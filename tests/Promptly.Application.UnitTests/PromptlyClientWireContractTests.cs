using System.Net;
using System.Text;
using System.Text.Json;
using Promptly.Cli;
using Promptly.Sdk.DotNet;

namespace Promptly.Application.UnitTests;

public sealed class PromptlyClientWireContractTests
{
    [Fact]
    public async Task Client_accepts_numeric_api_enum_statuses_and_policy_returns_success()
    {
        var runId = Guid.NewGuid();
        using var client = CreateClient(new Dictionary<string, string>
        {
            [$"/api/runs/{runId}"] = $$"""
                {"id":"{{runId}}","status":2,"createdByUserId":"runner"}
                """,
            [$"/api/runs/{runId}/results"] = "[{\"id\":\"00000000-0000-0000-0000-000000000001\",\"runId\":\"" + runId + "\",\"testCaseId\":\"00000000-0000-0000-0000-000000000002\",\"status\":0}]"
        });

        var run = await client.GetRunAsync(runId, TestContext.Current.CancellationToken);
        var results = await client.GetRunResultsAsync(runId, TestContext.Current.CancellationToken);

        Assert.Equal("Completed", run.Status);
        Assert.Equal("Pass", Assert.Single(results).Status);
        Assert.Equal(RunExitCodePolicy.Success, RunExitCodePolicy.GetExitCode(results, run));
    }

    [Fact]
    public async Task Client_accepts_string_statuses_for_backward_compatibility()
    {
        var runId = Guid.NewGuid();
        using var client = CreateClient(new Dictionary<string, string>
        {
            [$"/api/runs/{runId}"] = $$"""{"id":"{{runId}}","status":"Completed","createdByUserId":"runner"}""",
            [$"/api/runs/{runId}/results"] = "[{\"status\":\"Fail\"}]"
        });

        var run = await client.GetRunAsync(runId, TestContext.Current.CancellationToken);
        var results = await client.GetRunResultsAsync(runId, TestContext.Current.CancellationToken);

        Assert.Equal("Completed", run.Status);
        Assert.Equal("Fail", Assert.Single(results).Status);
        Assert.Equal(RunExitCodePolicy.AssertionFailure, RunExitCodePolicy.GetExitCode(results, run));
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Running")]
    [InlineData("Completed")]
    [InlineData("Failed")]
    public async Task Client_accepts_every_named_run_status(string expectedStatus)
    {
        var runId = Guid.NewGuid();
        using var client = CreateClient(new Dictionary<string, string>
        {
            [$"/api/runs/{runId}"] = RunJson(runId, JsonSerializer.Serialize(expectedStatus))
        });

        var run = await client.GetRunAsync(runId, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, run.Status);
    }

    [Theory]
    [InlineData("Pass")]
    [InlineData("Fail")]
    [InlineData("Error")]
    public async Task Client_accepts_every_named_result_status(string expectedStatus)
    {
        var runId = Guid.NewGuid();
        using var client = CreateClient(new Dictionary<string, string>
        {
            [$"/api/runs/{runId}/results"] = ResultsJson(JsonSerializer.Serialize(expectedStatus), runId)
        });

        var results = await client.GetRunResultsAsync(runId, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, Assert.Single(results).Status);
    }

    [Theory]
    [InlineData(0, "Queued")]
    [InlineData(1, "Running")]
    [InlineData(2, "Completed")]
    [InlineData(3, "Failed")]
    public async Task Client_maps_every_numeric_run_status(int wireValue, string expectedStatus)
    {
        var runId = Guid.NewGuid();
        using var client = CreateClient(new Dictionary<string, string>
        {
            [$"/api/runs/{runId}"] = RunJson(runId, wireValue.ToString())
        });

        var run = await client.GetRunAsync(runId, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, run.Status);
    }

    [Theory]
    [InlineData(0, "Pass")]
    [InlineData(1, "Fail")]
    [InlineData(2, "Error")]
    public async Task Client_maps_every_numeric_result_status(int wireValue, string expectedStatus)
    {
        var runId = Guid.NewGuid();
        using var client = CreateClient(new Dictionary<string, string>
        {
            [$"/api/runs/{runId}/results"] = ResultsJson(wireValue.ToString(), runId)
        });

        var results = await client.GetRunResultsAsync(runId, TestContext.Current.CancellationToken);

        Assert.Equal(expectedStatus, Assert.Single(results).Status);
    }

    [Theory]
    [InlineData("\"Unknown\"")]
    [InlineData("99")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    public async Task Client_rejects_invalid_run_status_wire_values(string wireValue)
    {
        var runId = Guid.NewGuid();
        using var client = CreateClient(new Dictionary<string, string>
        {
            [$"/api/runs/{runId}"] = RunJson(runId, wireValue)
        });

        await Assert.ThrowsAsync<JsonException>(() => client.GetRunAsync(runId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("\"Unknown\"")]
    [InlineData("99")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{}")]
    public async Task Client_rejects_invalid_result_status_wire_values(string wireValue)
    {
        var runId = Guid.NewGuid();
        using var client = CreateClient(new Dictionary<string, string>
        {
            [$"/api/runs/{runId}/results"] = ResultsJson(wireValue, runId)
        });

        await Assert.ThrowsAsync<JsonException>(() => client.GetRunResultsAsync(runId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Client_waits_through_numeric_nonterminal_status_to_completed()
    {
        var runId = Guid.NewGuid();
        using var client = CreateSequentialClient(
            $"/api/runs/{runId}",
            RunJson(runId, "1"),
            RunJson(runId, "2"));

        var run = await client.WaitForCompletionAsync(
            runId,
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.Zero,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Completed", run.Status);
    }

    [Fact]
    public async Task Numeric_assertion_failure_reaches_cli_policy_as_exit_one()
    {
        var runId = Guid.NewGuid();
        using var client = CreateClient(new Dictionary<string, string>
        {
            [$"/api/runs/{runId}"] = RunJson(runId, "2"),
            [$"/api/runs/{runId}/results"] = ResultsJson("1", runId)
        });

        var run = await client.GetRunAsync(runId, TestContext.Current.CancellationToken);
        var results = await client.GetRunResultsAsync(runId, TestContext.Current.CancellationToken);

        Assert.Equal(RunExitCodePolicy.AssertionFailure, RunExitCodePolicy.GetExitCode(results, run));
    }

    [Fact]
    public async Task Numeric_result_error_reaches_cli_policy_as_exit_two()
    {
        var runId = Guid.NewGuid();
        using var client = CreateClient(new Dictionary<string, string>
        {
            [$"/api/runs/{runId}"] = RunJson(runId, "2"),
            [$"/api/runs/{runId}/results"] = ResultsJson("2", runId)
        });

        var run = await client.GetRunAsync(runId, TestContext.Current.CancellationToken);
        var results = await client.GetRunResultsAsync(runId, TestContext.Current.CancellationToken);

        Assert.Equal(RunExitCodePolicy.InfrastructureError, RunExitCodePolicy.GetExitCode(results, run));
    }

    private static PromptlyClient CreateClient(IReadOnlyDictionary<string, string> responses) =>
        new("https://promptly.test/api", "test-key", new HttpClient(new RoutingHandler(responses)));

    private static PromptlyClient CreateSequentialClient(string path, params string[] responses) =>
        new("https://promptly.test/api", "test-key", new HttpClient(new SequentialHandler(path, responses)));

    private static string RunJson(Guid runId, string wireStatus) =>
        $$"""{"id":"{{runId}}","status":{{wireStatus}},"createdByUserId":"runner"}""";

    private static string ResultsJson(string wireStatus, Guid runId) =>
        $$"""[{"id":"00000000-0000-0000-0000-000000000001","runId":"{{runId}}","testCaseId":"00000000-0000-0000-0000-000000000002","status":{{wireStatus}}}]""";

    private sealed class RoutingHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (!responses.TryGetValue(path, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class SequentialHandler(string path, IReadOnlyList<string> responses) : HttpMessageHandler
    {
        private int _nextResponse;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath != path)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var index = Interlocked.Increment(ref _nextResponse) - 1;
            var body = responses[Math.Min(index, responses.Count - 1)];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
