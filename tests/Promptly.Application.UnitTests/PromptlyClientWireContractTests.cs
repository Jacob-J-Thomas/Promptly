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

    [Fact]
    public async Task Client_rejects_unknown_numeric_statuses_safely()
    {
        var runId = Guid.NewGuid();
        using var client = CreateClient(new Dictionary<string, string>
        {
            [$"/api/runs/{runId}"] = $$"""{"id":"{{runId}}","status":99,"createdByUserId":"runner"}"""
        });

        await Assert.ThrowsAsync<JsonException>(() => client.GetRunAsync(runId, TestContext.Current.CancellationToken));
    }

    private static PromptlyClient CreateClient(IReadOnlyDictionary<string, string> responses) =>
        new("https://promptly.test/api", "test-key", new HttpClient(new RoutingHandler(responses)));

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
}
