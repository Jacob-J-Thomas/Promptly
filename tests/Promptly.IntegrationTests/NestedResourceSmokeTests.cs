using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Promptly.IntegrationTests;

public sealed class NestedResourceSmokeTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task OwnedSuite_ImportsRepresentativeYamlAndPersistsTestCase()
    {
        using var user = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var projectId = await user.CreateProjectAsync();
        var suiteId = await user.CreateSuiteAsync(projectId);
        var externalId = $"yaml-{Guid.NewGuid():N}";
        var yaml = $$"""
            - id: {{externalId}}
              name: YAML integration case
              description: Import persistence smoke
              input:
                prompt: hello
              expectations:
                - type: contains
                  value: hello
            """;

        using var multipart = new MultipartFormDataContent();
        var file = new StringContent(yaml, Encoding.UTF8);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-yaml");
        multipart.Add(file, "file", "integration-tests.yaml");
        using var importRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/suites/{suiteId}/tests/import")
        {
            Content = multipart
        };
        using var importResponse = await user.SendAsync(importRequest);

        Assert.Equal(HttpStatusCode.OK, importResponse.StatusCode);
        using (var document = JsonDocument.Parse(
                   await importResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)))
        {
            Assert.Equal(1, document.RootElement.GetProperty("importedCount").GetInt32());
            Assert.Contains(
                externalId,
                document.RootElement.GetProperty("importedTestIds")
                    .EnumerateArray()
                    .Select(value => value.GetString()));
        }

        using var persistedResponse = await user.GetAsync($"/api/suites/{suiteId}/tests");
        Assert.Equal(HttpStatusCode.OK, persistedResponse.StatusCode);
        using var persisted = JsonDocument.Parse(
            await persistedResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var imported = Assert.Single(persisted.RootElement.EnumerateArray());
        Assert.Equal(externalId, imported.GetProperty("externalId").GetString());
        Assert.Equal("YAML integration case", imported.GetProperty("name").GetString());
        Assert.Contains(
            "\"prompt\":\"hello\"",
            imported.GetProperty("inputSpecJson").GetString(),
            StringComparison.Ordinal);
    }
}
