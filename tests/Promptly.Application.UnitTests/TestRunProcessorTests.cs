using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
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

        var runService = new StubTestRunService(CreateRun(runId, suiteId));
        var processor = CreateProcessor(
            dbContext,
            runService,
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
        Assert.Equal(TestRunStatus.Completed, runService.StatusUpdates[^1].Status);
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
        var runService = new StubTestRunService(CreateRun(runId, Guid.NewGuid()));
        var processor = CreateProcessor(
            dbContext,
            runService,
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

        var update = Assert.Single(runService.StatusUpdates);
        Assert.Equal(TestRunStatus.Failed, update.Status);
        Assert.Contains("cancelled", update.ErrorMessage, StringComparison.OrdinalIgnoreCase);
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
        ITestRunService testRunService,
        IExpectationEvaluator expectationEvaluator)
    {
        return new TestRunProcessor(
            dbContext,
            testRunService,
            new StubEndpointExecutor(),
            new StubMappingService(),
            expectationEvaluator,
            new StubPythonEvalClient(),
            NullLogger<TestRunProcessor>.Instance);
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

    private sealed class StubTestRunService(TestRun run) : ITestRunService
    {
        public List<(TestRunStatus Status, string? ErrorMessage)> StatusUpdates { get; } = [];

        public Task<TestRun?> GetRunByIdAsync(Guid runId) => Task.FromResult<TestRun?>(run);

        public Task UpdateRunStatusAsync(
            Guid runId,
            TestRunStatus status,
            string? summaryJson = null,
            string? errorMessage = null)
        {
            StatusUpdates.Add((status, errorMessage));
            return Task.CompletedTask;
        }

        public Task<TestRun> QueueRunAsync(
            Guid suiteId,
            Guid environmentId,
            Guid endpointId,
            Guid mappingSpecId,
            string createdByUserId,
            string? gitCommitHash = null,
            string? configSnapshotJson = null) => throw new NotSupportedException();

        public Task<List<TestRun>> GetRunsBySuiteAsync(
            Guid suiteId,
            TestRunStatus? status = null,
            int? limit = null) => throw new NotSupportedException();

        public Task<TestRun?> ClaimNextQueuedRunAsync() => throw new NotSupportedException();
    }

    private sealed class StubEndpointExecutor : IEndpointExecutor
    {
        public Task<ExecutionResult> ExecuteAsync(
            Endpoint endpoint,
            Environment environment,
            TestCase testCase) => Task.FromResult(new ExecutionResult
            {
                Success = true,
                ResponseJson = "{}"
            });
    }

    private sealed class StubMappingService : IMappingService
    {
        public Task<MappingResult> ApplyMappingAsync(string mappingSpecJson, string responseJson) =>
            Task.FromResult(new MappingResult
            {
                Success = true,
                Trace = new CanonicalTrace
                {
                    Messages = [new Message { Role = "assistant", Content = "response" }]
                }
            });

        public Task<MappingResult> ValidateMappingAsync(
            string mappingSpecJson,
            string sampleResponseJson) => throw new NotSupportedException();

        public Task<MappingSpec> SaveMappingSpecAsync(
            Guid endpointId,
            string name,
            string specJson) => throw new NotSupportedException();

        public Task<List<MappingSpec>> GetMappingSpecsByEndpointAsync(Guid endpointId) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> GetMappingSpecByIdAsync(Guid id) => throw new NotSupportedException();

        public Task<MappingSpec?> GetDefaultMappingAsync(Guid endpointId) =>
            throw new NotSupportedException();

        public Task<MappingSpec> UpdateMappingSpecAsync(
            Guid id,
            string name,
            string specJson) => throw new NotSupportedException();

        public Task SetDefaultMappingAsync(Guid id) => throw new NotSupportedException();

        public Task DeleteMappingSpecAsync(Guid id) => throw new NotSupportedException();
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
            Dictionary<string, object>? hints = null) => throw new NotSupportedException();

        public Task<EvaluationResult> EvaluateLlmJudgeAsync(
            string rubric,
            double minScore,
            CanonicalTrace trace,
            string? model = null,
            string? provider = null) => throw new NotSupportedException();

        public Task<EvaluationResult> EvaluateGroundednessAsync(
            double minScore,
            CanonicalTrace trace,
            List<RetrievedDoc> docs,
            string? model = null,
            string? provider = null) => throw new NotSupportedException();
    }
}
