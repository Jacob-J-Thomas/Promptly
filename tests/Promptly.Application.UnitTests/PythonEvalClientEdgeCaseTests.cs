using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Domain.ValueObjects;
using Promptly.Infrastructure.Clients;

namespace Promptly.Application.UnitTests;

public sealed class PythonEvalClientEdgeCaseTests
{
    [Fact]
    public void Constructor_uses_the_documented_default_worker_address_and_timeout()
    {
        var httpClient = new HttpClient(new FixedHandler(() => JsonResponse(HttpStatusCode.OK, "{}")));
        using var configuration = new ConfigurationManager();

        _ = new PythonEvalClient(
            httpClient,
            configuration,
            NullLogger<PythonEvalClient>.Instance);

        Assert.Equal(new Uri("http://localhost:8000"), httpClient.BaseAddress);
        Assert.Equal(TimeSpan.FromMinutes(2), httpClient.Timeout);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"reason\":\"mapping is missing\"}")]
    public async Task Mapping_proposal_rejects_success_responses_without_a_mapping(string responseJson)
    {
        var client = CreateClient(new FixedHandler(() => JsonResponse(HttpStatusCode.OK, responseJson)));

        var result = await client.ProposeMappingAsync(
            "{}",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(PythonWorkerErrorCodes.InvalidResponse, result.ErrorCode);
        Assert.Equal("Python worker returned an invalid response", result.ErrorMessage);
    }

    [Fact]
    public async Task Invalid_mapping_json_is_a_typed_error_without_exposing_parser_details()
    {
        var client = CreateClient(new FixedHandler(() => JsonResponse(HttpStatusCode.OK, "not-json")));

        var result = await client.ProposeMappingAsync(
            "{}",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(PythonWorkerErrorCodes.InvalidResponse, result.ErrorCode);
        Assert.Equal("Python worker returned an invalid response", result.ErrorMessage);
    }

    [Theory]
    [InlineData("{\"detail\":\"safe string detail\"}", "safe string detail")]
    [InlineData("{\"detail\":{\"message\":\"safe object detail\"}}", "safe object detail")]
    [InlineData("{}", "Python worker returned HTTP 502")]
    public async Task Groundedness_preserves_only_supported_safe_worker_errors(
        string responseJson,
        string expectedMessage)
    {
        var client = CreateClient(new FixedHandler(() => JsonResponse(HttpStatusCode.BadGateway, responseJson)));

        var result = await client.EvaluateGroundednessAsync(
            0.7,
            new CanonicalTrace(),
            [],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(502, result.WorkerStatusCode);
        Assert.Equal(PythonWorkerErrorCodes.UpstreamFailure, result.ErrorCode);
        Assert.Equal(expectedMessage, result.ErrorMessage);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"reason\":\"missing score\"}")]
    [InlineData("{\"score\":0.5,\"reason\":null}")]
    [InlineData("{\"score\":-0.01,\"reason\":\"invalid\"}")]
    [InlineData("{\"score\":1.01,\"reason\":\"invalid\"}")]
    public async Task Groundedness_rejects_malformed_or_out_of_range_scores(string responseJson)
    {
        var client = CreateClient(new FixedHandler(() => JsonResponse(HttpStatusCode.OK, responseJson)));

        var result = await client.EvaluateGroundednessAsync(
            0.7,
            new CanonicalTrace(),
            [],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(0, result.Score);
        Assert.Equal(PythonWorkerErrorCodes.InvalidResponse, result.ErrorCode);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"score\":0.5,\"reason\":null}")]
    [InlineData("{\"score\":-0.01,\"reason\":\"invalid\"}")]
    [InlineData("{\"score\":1.01,\"reason\":\"invalid\"}")]
    public async Task Llm_judge_rejects_missing_reasons_and_out_of_range_scores(string responseJson)
    {
        var client = CreateClient(new FixedHandler(() => JsonResponse(HttpStatusCode.OK, responseJson)));

        var result = await client.EvaluateLlmJudgeAsync(
            "Be accurate",
            0.7,
            new CanonicalTrace(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(PythonWorkerErrorCodes.InvalidResponse, result.ErrorCode);
    }

    [Theory]
    [InlineData("timeout", PythonWorkerErrorCodes.Timeout, "Python worker request timed out")]
    [InlineData("client", PythonWorkerErrorCodes.ClientError, "Python worker request failed")]
    public async Task Groundedness_maps_non_caller_cancellation_and_unexpected_client_failures(
        string exceptionKind,
        string expectedCode,
        string expectedMessage)
    {
        var client = CreateClient(new FixedHandler(() => exceptionKind == "timeout"
            ? throw new TaskCanceledException("secret timeout detail")
            : throw new InvalidOperationException("secret client detail")));

        var result = await client.EvaluateGroundednessAsync(
            0.7,
            new CanonicalTrace(),
            [],
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Equal(expectedMessage, result.ErrorMessage);
    }

    private static PythonEvalClient CreateClient(HttpMessageHandler handler)
    {
        using var configuration = new ConfigurationManager
        {
            ["PROMPTLY_EVAL_BASE_URL"] = "https://worker.example.test"
        };
        return new PythonEvalClient(
            new HttpClient(handler),
            configuration,
            NullLogger<PythonEvalClient>.Instance);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FixedHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory());
    }
}
