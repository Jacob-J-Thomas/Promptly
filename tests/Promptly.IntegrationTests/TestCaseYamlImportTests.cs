using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Promptly.Application.Data;

namespace Promptly.IntegrationTests;

public sealed class SpecificationPersistenceIntegrationTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task Invalid_later_yaml_row_returns_safe_400_and_rolls_back_the_postgresql_batch()
    {
        using var user = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var projectId = await user.CreateProjectAsync();
        var suiteId = await user.CreateSuiteAsync(projectId);
        var yaml = ValidRow("atomic-first") + "\n" + """
            - id: atomic-invalid
              name: Invalid later row
              input:
                messages: []
              expectations: []
            """;

        using var response = await ImportAsync(user, suiteId, yaml);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("rows[1]", body, StringComparison.Ordinal);
        Assert.DoesNotContain("DbUpdateException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
        using var persistedResponse = await user.GetAsync($"/api/suites/{suiteId}/tests");
        using var persisted = await ReadJsonAsync(persistedResponse);
        Assert.Empty(persisted.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Existing_and_in_file_duplicate_external_ids_are_rejected_atomically()
    {
        using var user = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var projectId = await user.CreateProjectAsync();
        var suiteId = await user.CreateSuiteAsync(projectId);
        using (var initial = await ImportAsync(user, suiteId, ValidRow("duplicate-existing")))
        {
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
        }

        using (var existing = await ImportAsync(user, suiteId, ValidRow("duplicate-existing")))
        {
            var body = await existing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, existing.StatusCode);
            Assert.Contains("duplicate_external_id", body, StringComparison.Ordinal);
        }

        var duplicateInFile = ValidRow("duplicate-in-file") + "\n" + ValidRow("duplicate-in-file");
        using (var inFile = await ImportAsync(user, suiteId, duplicateInFile))
        {
            var body = await inFile.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, inFile.StatusCode);
            Assert.Contains("duplicate_external_id", body, StringComparison.Ordinal);
        }

        using var persistedResponse = await user.GetAsync($"/api/suites/{suiteId}/tests");
        using var persisted = await ReadJsonAsync(persistedResponse);
        var persistedIds = persisted.RootElement.EnumerateArray()
            .Select(test => test.GetProperty("externalId").GetString())
            .ToArray();
        Assert.Equal(["duplicate-existing"], persistedIds);
    }

    [Fact]
    public async Task Concurrent_same_external_id_imports_leave_one_row_and_safe_conflict_error()
    {
        var barrier = new ConcurrentBulkWriteBarrier();
        await fixture.RunWithHostAsync(
            fixture.DefaultWorkerBaseUrl,
            async host =>
            {
                using var user = await PromptlyApiClient.RegisterAsync(host.Factory);
                var projectId = await user.CreateProjectAsync();
                var suiteId = await user.CreateSuiteAsync(projectId);
                var yaml = ValidRow("concurrent-unique");

                barrier.Arm();
                var imports = new[]
                {
                    ImportAsync(user, suiteId, yaml),
                    ImportAsync(user, suiteId, yaml)
                };
                bool bothWritesReachedBarrier;
                try
                {
                    bothWritesReachedBarrier = await barrier.WaitForBothAsync(
                        TimeSpan.FromSeconds(20),
                        TestContext.Current.CancellationToken);
                }
                finally
                {
                    barrier.Release();
                }

                var responses = await Task.WhenAll(imports);
                try
                {
                    Assert.True(bothWritesReachedBarrier, "Both imports must reach the write barrier before release");
                    Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
                    var failures = responses.Where(response => response.StatusCode != HttpStatusCode.OK).ToArray();
                    Assert.Single(failures);
                    var failureBody = await failures[0].Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                    Assert.Equal(HttpStatusCode.BadRequest, failures[0].StatusCode);
                    Assert.Contains("persistence_error", failureBody, StringComparison.Ordinal);
                    Assert.DoesNotContain("DbUpdateException", failureBody, StringComparison.Ordinal);
                    Assert.DoesNotContain("Npgsql", failureBody, StringComparison.Ordinal);
                }
                finally
                {
                    foreach (var response in responses)
                    {
                        response.Dispose();
                    }
                }

                using var persistedResponse = await user.GetAsync($"/api/suites/{suiteId}/tests");
                using var persisted = await ReadJsonAsync(persistedResponse);
                Assert.Single(persisted.RootElement.EnumerateArray());
            },
            configureServices: services =>
            {
                services.AddSingleton(barrier);
                services.AddDbContext<PromptlyDbContext>((serviceProvider, options) =>
                    options.AddInterceptors(serviceProvider.GetRequiredService<ConcurrentBulkWriteBarrier>()));
            });
    }

    [Fact]
    public async Task Invalid_json_and_authentication_paths_are_safe_and_preserve_404_scoping()
    {
        using var owner = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var projectId = await owner.CreateProjectAsync();
        var suiteId = await owner.CreateSuiteAsync(projectId);

        using (var invalid = await owner.PostJsonAsync($"/api/suites/{suiteId}/tests", new
        {
            externalId = "invalid-json",
            name = "Invalid JSON",
            inputSpecJson = "{\"messages\":[]}",
            expectationsJson = "[]"
        }))
        {
            var body = await invalid.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("inputSpecJson.messages", body, StringComparison.Ordinal);
            Assert.DoesNotContain("DbUpdateException", body, StringComparison.Ordinal);
            Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
        }

        using var anonymous = fixture.PrimaryHost.Factory.CreateClient(new() { AllowAutoRedirect = false });
        using (var unauthorized = await anonymous.GetAsync($"/api/suites/{suiteId}/tests"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        }

        using (var missing = await owner.GetAsync($"/api/suites/{Guid.NewGuid()}/tests"))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }
    }

    [Fact]
    public async Task Export_reimport_round_trip_keeps_semantic_content_and_all_expectation_types()
    {
        using var user = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var projectId = await user.CreateProjectAsync();
        var suiteId = await user.CreateSuiteAsync(projectId);
        var yaml = """
            - id: round-trip
              name: Round trip
              description: null
              input:
                messages:
                  - role: user
                    content: hello
                ordered: [first, second]
                metadata:
                  enabled: true
                  missing: null
                  nested:
                    also_missing: null
                  wide: 1e100
              expectations:
                - type: contains_text
                  text: hello
                - type: banned_text
                  text: forbidden
                - type: regex_match
                  pattern: hello
                - type: link_pattern
                  pattern: https?://
                - type: tool_called
                  tool_name: search
                - type: tool_sequence
                  sequence: [search]
                - type: llm_judge
                  rubric: Be helpful
                - type: groundedness
            """;

        using (var imported = await ImportAsync(user, suiteId, yaml))
        {
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        }

        using var exported = await user.GetAsync($"/api/suites/{suiteId}/tests/export");
        var exportedYaml = await exported.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
        Assert.Contains("wide: 1e100", exportedYaml, StringComparison.Ordinal);
        Assert.Contains("missing: null", exportedYaml, StringComparison.Ordinal);

        var targetSuiteId = await user.CreateSuiteAsync(projectId);
        using (var reimported = await ImportAsync(user, targetSuiteId, exportedYaml))
        {
            Assert.Equal(HttpStatusCode.OK, reimported.StatusCode);
        }

        using var persistedResponse = await user.GetAsync($"/api/suites/{suiteId}/tests");
        using var persisted = await ReadJsonAsync(persistedResponse);
        var original = Assert.Single(persisted.RootElement.EnumerateArray());
        using var originalInput = JsonDocument.Parse(original.GetProperty("inputSpecJson").GetString()!);
        using var originalExpectations = JsonDocument.Parse(original.GetProperty("expectationsJson").GetString()!);

        using var reimportedResponse = await user.GetAsync($"/api/suites/{targetSuiteId}/tests");
        using var reimportedTests = await ReadJsonAsync(reimportedResponse);
        var roundTripped = Assert.Single(reimportedTests.RootElement.EnumerateArray());
        using var roundTrippedInput = JsonDocument.Parse(roundTripped.GetProperty("inputSpecJson").GetString()!);
        using var roundTrippedExpectations = JsonDocument.Parse(roundTripped.GetProperty("expectationsJson").GetString()!);

        Assert.True(JsonElement.DeepEquals(originalInput.RootElement, roundTrippedInput.RootElement));
        Assert.True(JsonElement.DeepEquals(originalExpectations.RootElement, roundTrippedExpectations.RootElement));
        Assert.Equal(original.GetProperty("description").GetString(), roundTripped.GetProperty("description").GetString());
        Assert.Equal(8, roundTrippedExpectations.RootElement.GetArrayLength());
        Assert.Equal(
            ["contains_text", "banned_text", "regex_match", "link_pattern", "tool_called", "tool_sequence", "llm_judge", "groundedness"],
            roundTrippedExpectations.RootElement.EnumerateArray()
                .Select(expectation => expectation.GetProperty("type").GetString())
                .ToArray());

        var metadata = roundTrippedInput.RootElement.GetProperty("metadata");
        Assert.Equal(JsonValueKind.Number, metadata.GetProperty("wide").ValueKind);
        Assert.Equal("1e100", metadata.GetProperty("wide").GetRawText());
        Assert.Equal(JsonValueKind.Null, metadata.GetProperty("nested").GetProperty("also_missing").ValueKind);
        Assert.Equal("first", roundTrippedInput.RootElement.GetProperty("ordered")[0].GetString());
        Assert.Equal("second", roundTrippedInput.RootElement.GetProperty("ordered")[1].GetString());
    }

    [Fact]
    public async Task Json_intake_enforces_message_scalar_duplicate_and_version_limits_before_persistence()
    {
        using var user = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var projectId = await user.CreateProjectAsync();
        var suiteId = await user.CreateSuiteAsync(projectId);
        var exactMessages = "{\"messages\":["
            + string.Join(",", Enumerable.Repeat("{\"role\":\"user\",\"content\":\"x\"}", 100))
            + "]}";
        var overMessages = "{\"messages\":["
            + string.Join(",", Enumerable.Repeat("{\"role\":\"user\",\"content\":\"x\"}", 101))
            + "]}";

        using (var exact = await CreateTestAsync(user, suiteId, "exact-messages", exactMessages))
        {
            Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
        }

        using (var over = await CreateTestAsync(user, suiteId, "over-messages", overMessages))
        {
            Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        }

        var scalarOver = "{\"messages\":[{\"role\":\"user\",\"content\":\""
            + new string('x', 16_385)
            + "\"}]}";
        using (var scalar = await CreateTestAsync(user, suiteId, "over-scalar", scalarOver))
        {
            Assert.Equal(HttpStatusCode.BadRequest, scalar.StatusCode);
        }

        using (var duplicate = await CreateTestAsync(
                   user,
                   suiteId,
                   "duplicate-properties",
                   "{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"enabled\":true,\"enabled\":false}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
        }

        using (var version = await CreateTestAsync(
                   user,
                   suiteId,
                   "unsupported-version",
                   "{\"messages\":[{\"role\":\"user\",\"content\":\"x\"}],\"version\":2}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, version.StatusCode);
        }
    }

    private static async Task<HttpResponseMessage> ImportAsync(
        PromptlyApiClient user,
        Guid suiteId,
        string yaml)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/suites/{suiteId}/tests/import");
        using var multipart = new MultipartFormDataContent();
        var file = new StringContent(yaml, Encoding.UTF8);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-yaml");
        multipart.Add(file, "file", "specification.yaml");
        request.Content = multipart;
        return await user.SendAsync(request);
    }

    private static Task<HttpResponseMessage> CreateTestAsync(
        PromptlyApiClient user,
        Guid suiteId,
        string externalId,
        string inputSpecJson) =>
        user.PostJsonAsync($"/api/suites/{suiteId}/tests", new
        {
            externalId,
            name = externalId,
            inputSpecJson,
            expectationsJson = "[{\"type\":\"contains_text\",\"text\":\"x\"}]"
        });

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

    private static string ValidRow(string externalId) => $$"""
        - id: {{externalId}}
          name: Test {{externalId}}
          description: deterministic fixture
          input:
            messages:
              - role: user
                content: hello
          expectations:
            - type: contains_text
              text: hello
        """;

    private sealed class ConcurrentBulkWriteBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource<bool> _bothArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;
        private int _arrivals;

        public void Arm() => Volatile.Write(ref _armed, 1);

        public async Task<bool> WaitForBothAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            var completed = await Task.WhenAny(
                _bothArrived.Task,
                Task.Delay(timeout, cancellationToken));
            return completed == _bothArrived.Task;
        }

        public void Release() => _release.TrySetResult(true);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _armed) != 0)
            {
                var arrival = Interlocked.Increment(ref _arrivals);
                if (arrival <= 2)
                {
                    if (arrival == 2)
                    {
                        _bothArrived.TrySetResult(true);
                    }

                    await _release.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                }
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
