using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using Promptly.Domain.ValueObjects;
using Promptly.Infrastructure.Clients;
using Environment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.UnitTests;

public sealed class ExternalCallCancellationTests
{
    [Fact]
    public async Task EndpointExecutor_propagates_cancellation_to_the_outbound_request()
    {
        var handler = new BlockingHandler();
        var executor = new EndpointExecutor(
            new StubHttpClientFactory(new HttpClient(handler)),
            new StubEncryptionService(),
            NullLogger<EndpointExecutor>.Instance);
        using var cancellation = new CancellationTokenSource();

        var execution = executor.ExecuteAsync(
            new Endpoint
            {
                Id = Guid.NewGuid(),
                EnvironmentId = Guid.NewGuid(),
                Name = "chat",
                Path = "/chat",
                TimeoutSeconds = 30
            },
            new Environment
            {
                Id = Guid.NewGuid(),
                ProjectId = Guid.NewGuid(),
                Name = "test",
                BaseUrl = "https://example.test"
            },
            new TestCase
            {
                Id = Guid.NewGuid(),
                SuiteId = Guid.NewGuid(),
                ExternalId = "case-1",
                Name = "Cancellation",
                InputSpecJson = "{}",
                ExpectationsJson = "[]"
            },
            cancellation.Token);

        await handler.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(handler.ObservedCancellationToken.CanBeCanceled);
    }

    [Theory]
    [InlineData("llm_judge")]
    [InlineData("groundedness")]
    public async Task PythonEvalClient_propagates_cancellation_to_evaluation_requests(
        string evaluationType)
    {
        var handler = new BlockingHandler();
        var configuration = new ConfigurationManager
        {
            ["PROMPTLY_EVAL_BASE_URL"] = "https://worker.example.test"
        };
        var client = new PythonEvalClient(
            new HttpClient(handler),
            configuration,
            NullLogger<PythonEvalClient>.Instance);
        var trace = new CanonicalTrace();
        using var cancellation = new CancellationTokenSource();

        var evaluation = evaluationType == "llm_judge"
            ? client.EvaluateLlmJudgeAsync(
                "Be accurate",
                0.8,
                trace,
                cancellationToken: cancellation.Token)
            : client.EvaluateGroundednessAsync(
                0.8,
                trace,
                [],
                cancellationToken: cancellation.Token);

        await handler.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => evaluation);
        Assert.True(handler.ObservedCancellationToken.CanBeCanceled);
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedCancellationToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            ObservedCancellationToken = cancellationToken;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private sealed class StubHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StubEncryptionService : IEncryptionService
    {
        public string Encrypt(string plainText) => plainText;

        public string Decrypt(string cipherText) => cipherText;
    }
}
