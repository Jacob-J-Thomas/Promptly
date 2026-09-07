using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Domain.ValueObjects;
using Promptly.Infrastructure.Clients;

namespace Promptly.Application.UnitTests;

public sealed class PythonEvalClientContractTests
{
    [Fact]
    public async Task ProposeMappingAsync_matches_the_FastAPI_wire_contract()
    {
        var handler = new RecordingHandler(() => JsonResponse(HttpStatusCode.OK, """
            {
              "mappingSpec": {
                "version": 1,
                "fallback": { "singleAssistantContentPath": "$.answer" }
              },
              "reason": "Mapped"
            }
            """));
        var client = CreateClient(handler);

        var result = await client.ProposeMappingAsync(
            "{\"answer\":\"hello\"}",
            "{\"prompt\":\"hi\"}",
            new Dictionary<string, object> { ["selection"] = "first" },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("Mapped", result.Reason);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/mapping/propose", handler.RequestUri?.AbsolutePath);
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal(
            "{\"answer\":\"hello\"}",
            request.RootElement.GetProperty("sample_response_json").GetString());
        Assert.Equal(
            "{\"prompt\":\"hi\"}",
            request.RootElement.GetProperty("sample_request_json").GetString());
        Assert.Equal(
            "first",
            request.RootElement.GetProperty("hints").GetProperty("selection").GetString());
        using var mapping = JsonDocument.Parse(Assert.IsType<string>(result.MappingSpecJson));
        Assert.Equal(
            "$.answer",
            mapping.RootElement
                .GetProperty("fallback")
                .GetProperty("singleAssistantContentPath")
                .GetString());
    }

    [Fact]
    public async Task EvaluateLlmJudgeAsync_matches_CanonicalTrace_aliases()
    {
        var handler = new RecordingHandler(() => JsonResponse(
            HttpStatusCode.OK,
            """{"score":0.85,"reason":"Accurate"}"""));
        var client = CreateClient(handler);
        var trace = CreateTrace();

        var result = await client.EvaluateLlmJudgeAsync(
            "Be accurate",
            0.8,
            trace,
            "judge-model",
            "openai",
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0.85, result.Score);
        Assert.Equal("Accurate", result.Reason);
        Assert.Equal("/eval/llm-judge", handler.RequestUri?.AbsolutePath);
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal(0.8, request.RootElement.GetProperty("min_score").GetDouble());
        Assert.Equal("judge-model", request.RootElement.GetProperty("model").GetString());
        Assert.Equal("openai", request.RootElement.GetProperty("provider").GetString());
        var traceJson = request.RootElement.GetProperty("trace");
        Assert.Equal("lookup", traceJson.GetProperty("toolCalls")[0].GetProperty("name").GetString());
        Assert.Equal(
            "{\"id\":1}",
            traceJson.GetProperty("toolCalls")[0].GetProperty("argumentsJson").GetString());
        Assert.Equal(
            "doc-1",
            traceJson.GetProperty("retrievedDocs")[0].GetProperty("id").GetString());
        Assert.Equal("raw", traceJson.GetProperty("rawResponse").GetString());
    }

    [Fact]
    public async Task EvaluateGroundednessAsync_matches_the_FastAPI_wire_contract()
    {
        var handler = new RecordingHandler(() => JsonResponse(
            HttpStatusCode.OK,
            """{"score":0.75,"reason":"Grounded"}"""));
        var client = CreateClient(handler);
        var trace = CreateTrace();

        var result = await client.EvaluateGroundednessAsync(
            0.7,
            trace,
            trace.RetrievedDocs,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(0.75, result.Score);
        Assert.Equal("/eval/groundedness", handler.RequestUri?.AbsolutePath);
        using var request = JsonDocument.Parse(Assert.IsType<string>(handler.Body));
        Assert.Equal(0.7, request.RootElement.GetProperty("min_score").GetDouble());
        Assert.Equal(
            "Source",
            request.RootElement.GetProperty("docs")[0].GetProperty("title").GetString());
    }

    [Theory]
    [InlineData(400, PythonWorkerErrorCodes.BadRequest)]
    [InlineData(502, PythonWorkerErrorCodes.UpstreamFailure)]
    [InlineData(503, PythonWorkerErrorCodes.Unavailable)]
    public async Task Evaluation_preserves_FastAPI_failure_status_and_kind(
        int statusCode,
        string expectedErrorCode)
    {
        var handler = new RecordingHandler(() => JsonResponse(
            (HttpStatusCode)statusCode,
            """{"detail":{"error":{"message":"safe failure","details":"secret provider detail"}}}"""));
        var client = CreateClient(handler);

        var result = await client.EvaluateLlmJudgeAsync(
            "Be accurate",
            0.8,
            CreateTrace(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(statusCode, result.WorkerStatusCode);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Contains("safe failure", result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret provider detail", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_contract_worker_error_body_is_not_exposed()
    {
        var handler = new RecordingHandler(() => new HttpResponseMessage(
            HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>secret proxy detail</html>")
        });
        var client = CreateClient(handler);

        var result = await client.EvaluateLlmJudgeAsync(
            "Be accurate",
            0.8,
            CreateTrace(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(PythonWorkerErrorCodes.UpstreamFailure, result.ErrorCode);
        Assert.Equal("Python worker returned HTTP 502", result.ErrorMessage);
        Assert.DoesNotContain("secret proxy detail", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_but_malformed_worker_response_is_a_typed_error()
    {
        var handler = new RecordingHandler(() => JsonResponse(
            HttpStatusCode.OK,
            """{"reason":"score is missing"}"""));
        var client = CreateClient(handler);

        var result = await client.EvaluateLlmJudgeAsync(
            "Be accurate",
            0.8,
            CreateTrace(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(PythonWorkerErrorCodes.InvalidResponse, result.ErrorCode);
        Assert.Null(result.WorkerStatusCode);
    }

    [Fact]
    public async Task Transport_exception_is_typed_without_exposing_exception_details()
    {
        var handler = new RecordingHandler(
            () => throw new HttpRequestException("secret upstream detail"));
        var client = CreateClient(handler);

        var result = await client.EvaluateLlmJudgeAsync(
            "Be accurate",
            0.8,
            CreateTrace(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(PythonWorkerErrorCodes.TransportError, result.ErrorCode);
        Assert.DoesNotContain("secret upstream detail", result.ErrorMessage, StringComparison.Ordinal);
    }

    private static PythonEvalClient CreateClient(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationManager
        {
            ["PROMPTLY_EVAL_BASE_URL"] = "https://worker.example.test"
        };
        return new PythonEvalClient(
            new HttpClient(handler),
            configuration,
            NullLogger<PythonEvalClient>.Instance);
    }

    private static CanonicalTrace CreateTrace() => new()
    {
        Messages =
        [
            new Message { Role = "user", Content = "Question" },
            new Message { Role = "assistant", Content = "Answer" }
        ],
        ToolCalls = [new ToolCall { Name = "lookup", ArgumentsJson = "{\"id\":1}" }],
        Usage = new Usage { PromptTokens = 2, CompletionTokens = 1, TotalTokens = 3 },
        RetrievedDocs =
        [
            new RetrievedDoc
            {
                Id = "doc-1",
                Title = "Source",
                Content = "Fact",
                Metadata = new Dictionary<string, object> { ["kind"] = "reference" }
            }
        ],
        RawResponse = "raw"
    };

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            Body = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return responseFactory();
        }
    }
}
