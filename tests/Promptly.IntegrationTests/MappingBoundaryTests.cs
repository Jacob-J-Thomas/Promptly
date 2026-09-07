using System.Net;
using System.Text.Json;

namespace Promptly.IntegrationTests;

public sealed class MappingBoundaryTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task MappingProposal_TraversesServerFastApiAndDeterministicProvider()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var user = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var endpointId = await CreateOwnedEndpointAsync(user);
        var before = await fixture.ReadProviderEvidenceAsync(cancellationToken);
        var readiness = before.LastOrDefault(record =>
            record.Kind == "models"
            && record.Method == "GET"
            && record.Path == "/v1/models"
            && record.Authorized);
        Assert.NotNull(readiness);
        var baselineSequence = before.Max(record => record.Sequence);
        var correlationId = Guid.NewGuid().ToString("N");

        using var response = await user.PostJsonAsync(
            $"/api/endpoints/{endpointId}/mapping/propose",
            new
            {
                sampleResponseJson = "{\"answer\":\"integration works\"}",
                sampleRequestJson = "{\"prompt\":\"hello\"}",
                hints = new Dictionary<string, object>
                {
                    ["integrationCorrelation"] = correlationId,
                    ["purpose"] = "integration-test"
                }
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "Mapping spec generated successfully based on sample structure",
            document.RootElement.GetProperty("reason").GetString());
        var mappingJson = document.RootElement.GetProperty("mappingSpecJson").GetString();
        Assert.False(string.IsNullOrWhiteSpace(mappingJson));
        using var mapping = JsonDocument.Parse(mappingJson);
        Assert.Equal(1, mapping.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(
            "$.answer",
            mapping.RootElement.GetProperty("fallback")
                .GetProperty("singleAssistantContentPath")
                .GetString());

        var providerRequest = await fixture.WaitForProviderChatCompletionAsync(
            correlationId,
            baselineSequence,
            cancellationToken);
        Assert.True(providerRequest.Sequence > readiness.Sequence);
        Assert.Equal("POST", providerRequest.Method);
        Assert.Equal("/v1/chat/completions", providerRequest.Path);
        Assert.True(providerRequest.Authorized);
        Assert.True(providerRequest.ValidJson);
        Assert.Equal("verification-model", providerRequest.Model);
        Assert.Equal(2, providerRequest.MessageCount);
        Assert.Equal(correlationId, providerRequest.CorrelationId);
    }

    [Fact]
    public async Task MappingProposal_ReturnsSafeBadGatewayWhenWorkerIsUnavailable()
    {
        using var primaryUser = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var endpointId = await CreateOwnedEndpointAsync(primaryUser);
        await fixture.RunWithHostAsync("http://127.0.0.1:1", async unavailableHost =>
        {
            using var user = PromptlyApiClient.FromToken(
                unavailableHost.Factory,
                primaryUser.User);
            using var response = await user.PostJsonAsync(
                $"/api/endpoints/{endpointId}/mapping/propose",
                new { sampleResponseJson = "{\"answer\":\"ignored\"}" });

            Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
            await AssertWorkerErrorAsync(
                response,
                "python_worker_transport_error",
                "Python worker could not be reached");
        });
    }

    [Fact]
    public async Task MappingProposal_ReturnsSafeBadGatewayForMalformedWorkerJson()
    {
        using var primaryUser = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var endpointId = await CreateOwnedEndpointAsync(primaryUser);
        var malformedWorker = new MalformedWorkerStub();
        malformedWorker.Start();
        await malformedWorker.RunAndDisposeAsync(async runningWorker =>
        {
            await fixture.RunWithHostAsync(runningWorker.BaseUrl, async malformedHost =>
            {
                using var user = PromptlyApiClient.FromToken(
                    malformedHost.Factory,
                    primaryUser.User);
                using var response = await user.PostJsonAsync(
                    $"/api/endpoints/{endpointId}/mapping/propose",
                    new { sampleResponseJson = "{\"answer\":\"ignored\"}" });

                Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
                await AssertWorkerErrorAsync(
                    response,
                    "python_worker_invalid_response",
                    "Python worker returned an invalid response");
            });
        });
    }

    private static async Task<Guid> CreateOwnedEndpointAsync(PromptlyApiClient user)
    {
        var projectId = await user.CreateProjectAsync();
        var environmentId = await user.CreateEnvironmentAsync(projectId);
        return await user.CreateEndpointAsync(environmentId);
    }

    private static async Task AssertWorkerErrorAsync(
        HttpResponseMessage response,
        string expectedCode,
        string expectedMessage)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedCode, document.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal(expectedMessage, document.RootElement.GetProperty("message").GetString());
    }
}
