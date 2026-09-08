using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;
using Promptly.Domain.ValueObjects;
using Environment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.UnitTests;

public sealed class TestRunProcessorTests
{
    [Fact]
    public async Task ProcessRunAsync_preserves_structured_evaluator_errors()
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var testCase = new TestCase
        {
            Id = Guid.NewGuid(),
            SuiteId = suiteId,
            ExternalId = "case-1",
            Name = "Regex safety",
            InputSpecJson = "{}",
            ExpectationsJson = """
                [{"type":"regex_match","pattern":"(a+)+$"}]
                """
        };
        dbContext.TestCases.Add(testCase);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var runStore = new StubTestRunWorkerStore(CreateRun(runId, suiteId));
        var processor = CreateProcessor(
            dbContext,
            runStore,
            new StubExpectationEvaluator(new ExpectationResult
            {
                ExpectationType = "regex_match",
                Passed = false,
                Score = 0,
                Reason = "Regex evaluation timed out",
                ErrorCode = "regex_timeout"
            }));

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var storedResult = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Error, storedResult.Status);
        Assert.NotNull(storedResult.MetricsJson);
        using var metrics = JsonDocument.Parse(storedResult.MetricsJson);
        Assert.Equal(0, metrics.RootElement.GetProperty("passed").GetInt32());
        Assert.Equal(0, metrics.RootElement.GetProperty("failed").GetInt32());
        Assert.Equal(1, metrics.RootElement.GetProperty("errors").GetInt32());
        Assert.Equal(
            "regex_timeout",
            metrics.RootElement
                .GetProperty("expectationResults")[0]
                .GetProperty("ErrorCode")
                .GetString());
        Assert.Contains("regex_timeout", storedResult.FailureReasonsJson, StringComparison.Ordinal);
        Assert.Equal(TestRunStatus.Completed, runStore.StatusUpdates[^1].Status);
    }

    [Fact]
    public void ExpectationResult_omits_a_null_error_code_from_json()
    {
        var result = new ExpectationResult
        {
            ExpectationType = "contains_text",
            Passed = true,
            Score = 1,
            Reason = "matched"
        };

        var json = JsonSerializer.Serialize(result);

        Assert.DoesNotContain("ErrorCode", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessRunAsync_marks_a_claimed_run_failed_when_cancellation_is_observed()
    {
        await using var dbContext = CreateDbContext();
        var runId = Guid.NewGuid();
        var runStore = new StubTestRunWorkerStore(CreateRun(runId, Guid.NewGuid()));
        var processor = CreateProcessor(
            dbContext,
            runStore,
            new StubExpectationEvaluator(new ExpectationResult
            {
                ExpectationType = "contains_text",
                Passed = true,
                Score = 1,
                Reason = "unused"
            }));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessWithCancellationAsync(processor, runId, cancellation.Token));

        var update = Assert.Single(runStore.StatusUpdates);
        Assert.Equal(TestRunStatus.Failed, update.Status);
        Assert.Contains("cancelled", update.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessRunAsync_propagates_cancellation_into_endpoint_execution()
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(suiteId, "[]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new StubTestRunWorkerStore(CreateRun(runId, suiteId));
        var endpointExecutor = new BlockingEndpointExecutor();
        var processor = CreateProcessor(
            dbContext,
            runStore,
            new StubExpectationEvaluator(UnusedExpectationResult()),
            endpointExecutor: endpointExecutor);
        using var cancellation = new CancellationTokenSource();

        var processing = processor.ProcessRunAsync(runId, cancellation.Token);
        await endpointExecutor.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.Equal(cancellation.Token, endpointExecutor.ObservedCancellationToken);
        Assert.Equal(TestRunStatus.Failed, Assert.Single(runStore.StatusUpdates).Status);
    }

    [Theory]
    [InlineData("llm_judge")]
    [InlineData("groundedness")]
    public async Task ProcessRunAsync_propagates_cancellation_into_python_evaluation(
        string expectationType)
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(
            suiteId,
            ValidExpectationJson(expectationType)));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new StubTestRunWorkerStore(CreateRun(runId, suiteId));
        var pythonEvalClient = new BlockingPythonEvalClient(expectationType);
        var processor = CreateProcessor(
            dbContext,
            runStore,
            new StubExpectationEvaluator(UnusedExpectationResult()),
            pythonEvalClient: pythonEvalClient,
            includeRetrievedDocs: expectationType == "groundedness");
        using var cancellation = new CancellationTokenSource();

        var processing = processor.ProcessRunAsync(runId, cancellation.Token);
        try
        {
            await pythonEvalClient.Entered.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        Assert.Equal(cancellation.Token, pythonEvalClient.ObservedCancellationToken);
        Assert.Equal(TestRunStatus.Failed, Assert.Single(runStore.StatusUpdates).Status);
    }

    [Theory]
    [InlineData("llm_judge")]
    [InlineData("groundedness")]
    public async Task ProcessRunAsync_records_python_worker_failures_as_errors(
        string expectationType)
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(
            suiteId,
            ValidExpectationJson(expectationType)));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new StubTestRunWorkerStore(CreateRun(runId, suiteId));
        var processor = CreateProcessor(
            dbContext,
            runStore,
            new StubExpectationEvaluator(UnusedExpectationResult()),
            pythonEvalClient: new FailingPythonEvalClient(
                expectationType,
                new EvaluationResult
                {
                    Success = false,
                    ErrorMessage = "Provider unavailable",
                    ErrorCode = PythonWorkerErrorCodes.Unavailable,
                    WorkerStatusCode = 503
                }),
            includeRetrievedDocs: expectationType == "groundedness");

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var storedResult = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Error, storedResult.Status);
        using var metrics = JsonDocument.Parse(Assert.IsType<string>(storedResult.MetricsJson));
        Assert.Equal(0, metrics.RootElement.GetProperty("failed").GetInt32());
        Assert.Equal(1, metrics.RootElement.GetProperty("errors").GetInt32());
        Assert.Equal(
            PythonWorkerErrorCodes.Unavailable,
            metrics.RootElement
                .GetProperty("expectationResults")[0]
                .GetProperty("ErrorCode")
                .GetString());
        Assert.Contains(
            PythonWorkerErrorCodes.Unavailable,
            storedResult.FailureReasonsJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessRunAsync_stops_before_test_cases_and_egress_for_invalid_graph()
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(suiteId, "[]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new StubTestRunWorkerStore(WorkerRunLoadResult.InvalidGraph());
        var endpointExecutor = new RecordingEndpointExecutor();
        var processor = CreateProcessor(
            dbContext,
            runStore,
            new StubExpectationEvaluator(UnusedExpectationResult()),
            endpointExecutor: endpointExecutor);

        await processor.ProcessRunAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        Assert.False(endpointExecutor.WasCalled);
        Assert.Empty(dbContext.TestRunResults);
        Assert.Empty(runStore.StatusUpdates);
    }

    [Fact]
    public async Task ProcessRunAsync_stops_before_test_cases_and_egress_for_unsafe_endpoint_target()
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(suiteId, "[]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new StubTestRunWorkerStore(
            WorkerRunLoadResult.UnsafeEndpointTarget());
        var endpointExecutor = new RecordingEndpointExecutor();
        var processor = CreateProcessor(
            dbContext,
            runStore,
            new StubExpectationEvaluator(UnusedExpectationResult()),
            endpointExecutor: endpointExecutor);

        await processor.ProcessRunAsync(
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        Assert.False(endpointExecutor.WasCalled);
        Assert.Empty(dbContext.TestRunResults);
        Assert.Empty(runStore.StatusUpdates);
    }

    private static PromptlyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase($"promptly-tests-{Guid.NewGuid():N}")
            .Options;
        return new PromptlyDbContext(options);
    }

    private static TestRunProcessor CreateProcessor(
        PromptlyDbContext dbContext,
        ITestRunWorkerStore testRunWorkerStore,
        IExpectationEvaluator expectationEvaluator,
        IEndpointExecutor? endpointExecutor = null,
        IPythonEvalClient? pythonEvalClient = null,
        bool includeRetrievedDocs = false)
    {
        return new TestRunProcessor(
            dbContext,
            testRunWorkerStore,
            endpointExecutor ?? new StubEndpointExecutor(),
            new StubMappingService(includeRetrievedDocs),
            expectationEvaluator,
            pythonEvalClient ?? new StubPythonEvalClient(),
            NullLogger<TestRunProcessor>.Instance);
    }

    private static TestCase CreateTestCase(Guid suiteId, string expectationsJson)
    {
        return new TestCase
        {
            Id = Guid.NewGuid(),
            SuiteId = suiteId,
            ExternalId = "case-1",
            Name = "Cancellation",
            InputSpecJson = "{}",
            ExpectationsJson = expectationsJson
        };
    }

    private static string ValidExpectationJson(string expectationType) => expectationType switch
    {
        "llm_judge" => "[{\"type\":\"llm_judge\",\"rubric\":\"Be helpful\"}]",
        _ => JsonSerializer.Serialize(new[] { new { type = expectationType } })
    };

    private static ExpectationResult UnusedExpectationResult()
    {
        return new ExpectationResult
        {
            ExpectationType = "contains_text",
            Passed = true,
            Score = 1,
            Reason = "unused"
        };
    }

    private static TestRun CreateRun(Guid runId, Guid suiteId)
    {
        var environment = new Environment
        {
            Id = Guid.NewGuid(),
            ProjectId = Guid.NewGuid(),
            Name = "test",
            BaseUrl = "https://example.test"
        };
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = environment.Id,
            Name = "chat",
            Path = "/chat"
        };
        var mapping = new MappingSpec
        {
            Id = Guid.NewGuid(),
            EndpointId = endpoint.Id,
            Name = "default",
            SpecJson = "{}"
        };

        return new TestRun
        {
            Id = runId,
            SuiteId = suiteId,
            EnvironmentId = environment.Id,
            EndpointId = endpoint.Id,
            MappingSpecId = mapping.Id,
            Status = TestRunStatus.Running,
            CreatedByUserId = "test-user",
            Environment = environment,
            Endpoint = endpoint,
            MappingSpec = mapping
        };
    }

    private static Task ProcessWithCancellationAsync(
        TestRunProcessor processor,
        Guid runId,
        CancellationToken cancellationToken)
    {
        return processor.ProcessRunAsync(runId, cancellationToken);
    }

    private sealed class StubTestRunWorkerStore : ITestRunWorkerStore
    {
        private readonly WorkerRunLoadResult _loadResult;

        public StubTestRunWorkerStore(TestRun run)
            : this(WorkerRunLoadResult.Ready(run))
        {
        }

        public StubTestRunWorkerStore(WorkerRunLoadResult loadResult)
        {
            _loadResult = loadResult;
        }

        public List<(TestRunStatus Status, string? ErrorMessage)> StatusUpdates { get; } = [];

        public Task<WorkerRunLoadResult> LoadRunForProcessingAsync(
            Guid runId,
            CancellationToken cancellationToken = default) => Task.FromResult(_loadResult);

        public Task UpdateRunStatusAsync(
            Guid runId,
            TestRunStatus status,
            string? summaryJson = null,
            string? errorMessage = null)
        {
            StatusUpdates.Add((status, errorMessage));
            return Task.CompletedTask;
        }

        public Task<Guid?> ClaimNextQueuedRunAsync(
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubEndpointExecutor : IEndpointExecutor
    {
        public Task<ExecutionResult> ExecuteAsync(
            Endpoint endpoint,
            Environment environment,
            TestCase testCase,
            CancellationToken cancellationToken = default) => Task.FromResult(new ExecutionResult
            {
                Success = true,
                ResponseJson = "{}"
            });
    }

    private sealed class BlockingEndpointExecutor : IEndpointExecutor
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedCancellationToken { get; private set; }

        public async Task<ExecutionResult> ExecuteAsync(
            Endpoint endpoint,
            Environment environment,
            TestCase testCase,
            CancellationToken cancellationToken = default)
        {
            ObservedCancellationToken = cancellationToken;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ExecutionResult();
        }
    }

    private sealed class RecordingEndpointExecutor : IEndpointExecutor
    {
        public bool WasCalled { get; private set; }

        public Task<ExecutionResult> ExecuteAsync(
            Endpoint endpoint,
            Environment environment,
            TestCase testCase,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(new ExecutionResult
            {
                Success = true,
                ResponseJson = "{}"
            });
        }
    }

    private sealed class StubMappingService(bool includeRetrievedDocs) : IMappingService
    {
        public Task<MappingResult> ApplyMappingAsync(string mappingSpecJson, string responseJson) =>
            Task.FromResult(new MappingResult
            {
                Success = true,
                Trace = new CanonicalTrace
                {
                    Messages = [new Message { Role = "assistant", Content = "response" }],
                    RetrievedDocs = includeRetrievedDocs
                        ? [new RetrievedDoc { Id = "doc-1", Content = "source" }]
                        : []
                }
            });

        public Task<MappingResult> ValidateMappingAsync(
            string mappingSpecJson,
            string sampleResponseJson) => throw new NotSupportedException();

        public Task<MappingSpec?> SaveMappingSpecAsync(
            Guid endpointId,
            string name,
            string specJson,
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<List<MappingSpec>?> GetMappingSpecsByEndpointAsync(
            Guid endpointId,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> GetMappingSpecByIdAsync(Guid id, TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> GetDefaultMappingAsync(
            Guid endpointId,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> UpdateMappingSpecAsync(
            Guid id,
            string name,
            string specJson,
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<bool> SetDefaultMappingAsync(Guid id, TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<bool> DeleteMappingSpecAsync(Guid id, TenantAccessScope scope) =>
            throw new NotSupportedException();
    }

    private sealed class StubExpectationEvaluator(ExpectationResult result) : IExpectationEvaluator
    {
        public Task<ExpectationResult> EvaluateAsync(
            object expectation,
            CanonicalTrace trace,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class StubPythonEvalClient : IPythonEvalClient
    {
        public Task<MappingProposalResult> ProposeMappingAsync(
            string sampleResponse,
            string? sampleRequest = null,
            Dictionary<string, object>? hints = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EvaluationResult> EvaluateLlmJudgeAsync(
            string rubric,
            double minScore,
            CanonicalTrace trace,
            string? model = null,
            string? provider = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EvaluationResult> EvaluateGroundednessAsync(
            double minScore,
            CanonicalTrace trace,
            List<RetrievedDoc> docs,
            string? model = null,
            string? provider = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class BlockingPythonEvalClient(string expectationType) : IPythonEvalClient
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ObservedCancellationToken { get; private set; }

        public Task<MappingProposalResult> ProposeMappingAsync(
            string sampleResponse,
            string? sampleRequest = null,
            Dictionary<string, object>? hints = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EvaluationResult> EvaluateLlmJudgeAsync(
            string rubric,
            double minScore,
            CanonicalTrace trace,
            string? model = null,
            string? provider = null,
            CancellationToken cancellationToken = default)
        {
            if (expectationType != "llm_judge")
            {
                throw new InvalidOperationException("Unexpected LLM judge evaluation");
            }

            return WaitForCancellationAsync(cancellationToken);
        }

        public Task<EvaluationResult> EvaluateGroundednessAsync(
            double minScore,
            CanonicalTrace trace,
            List<RetrievedDoc> docs,
            string? model = null,
            string? provider = null,
            CancellationToken cancellationToken = default)
        {
            if (expectationType != "groundedness")
            {
                throw new InvalidOperationException("Unexpected groundedness evaluation");
            }

            return WaitForCancellationAsync(cancellationToken);
        }

        private async Task<EvaluationResult> WaitForCancellationAsync(
            CancellationToken cancellationToken)
        {
            ObservedCancellationToken = cancellationToken;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new EvaluationResult();
        }
    }

    private sealed class FailingPythonEvalClient(
        string expectationType,
        EvaluationResult failure) : IPythonEvalClient
    {
        public Task<MappingProposalResult> ProposeMappingAsync(
            string sampleResponse,
            string? sampleRequest = null,
            Dictionary<string, object>? hints = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EvaluationResult> EvaluateLlmJudgeAsync(
            string rubric,
            double minScore,
            CanonicalTrace trace,
            string? model = null,
            string? provider = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("llm_judge", expectationType);
            return Task.FromResult(failure);
        }

        public Task<EvaluationResult> EvaluateGroundednessAsync(
            double minScore,
            CanonicalTrace trace,
            List<RetrievedDoc> docs,
            string? model = null,
            string? provider = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("groundedness", expectationType);
            return Task.FromResult(failure);
        }
    }
}
