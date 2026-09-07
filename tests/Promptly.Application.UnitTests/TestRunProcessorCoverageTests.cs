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

public sealed class TestRunProcessorCoverageTests
{
    [Fact]
    public async Task ProcessRunAsync_returns_without_side_effects_when_run_is_missing()
    {
        await using var dbContext = CreateDbContext();
        var runStore = new RecordingWorkerStore(WorkerRunLoadResult.NotFound());
        var endpointExecutor = new DelegateEndpointExecutor((_, _, _, _) =>
            throw new InvalidOperationException("egress must not occur"));
        var processor = CreateProcessor(dbContext, runStore, endpointExecutor: endpointExecutor);

        await processor.ProcessRunAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Empty(runStore.StatusUpdates);
        Assert.Equal(0, endpointExecutor.CallCount);
        Assert.Empty(dbContext.TestRunResults);
    }

    [Fact]
    public async Task ProcessRunAsync_fails_a_run_with_no_test_cases()
    {
        await using var dbContext = CreateDbContext();
        var runId = Guid.NewGuid();
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, Guid.NewGuid())));
        var processor = CreateProcessor(dbContext, runStore);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var update = Assert.Single(runStore.StatusUpdates);
        Assert.Equal(TestRunStatus.Failed, update.Status);
        Assert.Equal("No test cases found in suite", update.ErrorMessage);
    }

    [Fact]
    public async Task ProcessRunAsync_transitions_a_queued_run_and_records_a_passing_case()
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(suiteId, "[]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId, TestRunStatus.Queued)));
        var processor = CreateProcessor(dbContext, runStore);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        Assert.Collection(
            runStore.StatusUpdates,
            update => Assert.Equal(TestRunStatus.Running, update.Status),
            update =>
            {
                Assert.Equal(TestRunStatus.Completed, update.Status);
                using var summary = JsonDocument.Parse(Assert.IsType<string>(update.SummaryJson));
                Assert.Equal(1, summary.RootElement.GetProperty("total").GetInt32());
                Assert.Equal(0, summary.RootElement.GetProperty("passed").GetInt32());
                Assert.Equal(0, summary.RootElement.GetProperty("failed").GetInt32());
                Assert.Equal(1, summary.RootElement.GetProperty("errors").GetInt32());
                Assert.Equal(0, summary.RootElement.GetProperty("passRate").GetDouble());
                Assert.Equal(0, summary.RootElement.GetProperty("avgLatencyMs").GetInt64());
                Assert.Equal(0, summary.RootElement.GetProperty("totalTokens").GetInt32());
                Assert.Equal(0, summary.RootElement.GetProperty("totalCost").GetDouble());
            });
        var result = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Error, result.Status);
        Assert.Contains("no_expectations", result.FailureReasonsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessRunAsync_aggregates_typed_usage_from_mapped_traces()
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(
            suiteId,
            "[{\"type\":\"contains_text\",\"text\":\"response\"}]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var processor = CreateProcessor(
            dbContext,
            runStore,
            mappingService: new DelegateMappingService((_, _) =>
                Task.FromResult(new MappingResult
                {
                    Success = true,
                    Trace = new CanonicalTrace
                    {
                        Messages = [new Message { Role = "assistant", Content = "response" }],
                        Usage = new Usage
                        {
                            TotalTokens = 321,
                            Cost = 1.25m,
                            LatencyMs = 90
                        }
                    }
                })));

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var completed = Assert.Single(runStore.StatusUpdates);
        Assert.Equal(TestRunStatus.Completed, completed.Status);
        using var summary = JsonDocument.Parse(Assert.IsType<string>(completed.SummaryJson));
        Assert.Equal(321, summary.RootElement.GetProperty("totalTokens").GetInt32());
        Assert.Equal(1.25, summary.RootElement.GetProperty("totalCost").GetDouble());
        Assert.Equal(90, summary.RootElement.GetProperty("avgLatencyMs").GetInt64());
    }

    [Theory]
    [InlineData(false, "{}", "Provider returned 500")]
    [InlineData(true, "", null)]
    public async Task ProcessRunAsync_records_endpoint_failures_without_mapping(
        bool success,
        string responseJson,
        string? errorMessage)
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(suiteId, "[]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var mappingService = new DelegateMappingService((_, _) =>
            throw new InvalidOperationException("mapping must not occur"));
        var processor = CreateProcessor(
            dbContext,
            runStore,
            endpointExecutor: new DelegateEndpointExecutor((_, _, _, _) =>
                Task.FromResult(new ExecutionResult
                {
                    Success = success,
                    ResponseJson = responseJson,
                    ErrorMessage = errorMessage
                })),
            mappingService: mappingService);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var result = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Error, result.Status);
        Assert.Equal(0, mappingService.ApplyCallCount);
        Assert.Contains(
            errorMessage ?? "No response from endpoint",
            result.FailureReasonsJson,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, "invalid mapping")]
    [InlineData(true, true, null)]
    public async Task ProcessRunAsync_records_mapping_failures(
        bool success,
        bool nullTrace,
        string? errorMessage)
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(suiteId, "[]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var processor = CreateProcessor(
            dbContext,
            runStore,
            mappingService: new DelegateMappingService((_, _) =>
                Task.FromResult(new MappingResult
                {
                    Success = success,
                    Trace = nullTrace ? null : CreateTrace(),
                    ErrorMessage = errorMessage
                })));

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var result = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Error, result.Status);
        Assert.Contains("Mapping failed", result.FailureReasonsJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null", "invalid_expectations_json")]
    [InlineData("[]", "no_expectations")]
    public async Task ProcessRunAsync_records_an_error_for_a_case_without_expectations(
        string expectationsJson,
        string expectedErrorCode)
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(suiteId, expectationsJson));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var processor = CreateProcessor(dbContext, runStore);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var result = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Error, result.Status);
        using var metrics = JsonDocument.Parse(Assert.IsType<string>(result.MetricsJson));
        Assert.Equal(0, metrics.RootElement.GetProperty("passed").GetInt32());
        Assert.Equal(0, metrics.RootElement.GetProperty("failed").GetInt32());
        Assert.Equal(1, metrics.RootElement.GetProperty("errors").GetInt32());
        Assert.Contains(expectedErrorCode, result.FailureReasonsJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("contains_text")]
    [InlineData("banned_text")]
    [InlineData("regex_match")]
    [InlineData("link_pattern")]
    [InlineData("tool_called")]
    [InlineData("tool_sequence")]
    public async Task ProcessRunAsync_routes_each_deterministic_expectation_to_the_evaluator(
        string expectationType)
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(
            suiteId,
            ValidExpectationJson(expectationType)));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var evaluator = new DelegateExpectationEvaluator((_, _, _) =>
            Task.FromResult(new ExpectationResult
            {
                ExpectationType = expectationType,
                Passed = true,
                Score = 1,
                Reason = "matched"
            }));
        var processor = CreateProcessor(dbContext, runStore, expectationEvaluator: evaluator);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        Assert.Equal(1, evaluator.CallCount);
        Assert.Equal(
            TestResultStatus.Pass,
            (await dbContext.TestRunResults.SingleAsync(TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task ProcessRunAsync_records_failed_error_and_unknown_expectations()
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(
            suiteId,
            """
            [
              {"type":"contains_text","text":"missing"},
              {"type":"banned_text","text":"error"},
              {"type":"future_expectation"},
              {}
            ]
            """));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var evaluator = new DelegateExpectationEvaluator((expectation, _, _) =>
        {
            var serialized = JsonSerializer.Serialize(expectation);
            return Task.FromResult(serialized.Contains("error", StringComparison.Ordinal)
                ? new ExpectationResult
                {
                    ExpectationType = "banned_text",
                    Passed = false,
                    Score = 0,
                    Reason = "evaluator unavailable",
                    ErrorCode = "evaluation_error"
                }
                : new ExpectationResult
                {
                    ExpectationType = "contains_text",
                    Passed = false,
                    Score = 0,
                    Reason = "not found"
                });
        });
        var processor = CreateProcessor(dbContext, runStore, expectationEvaluator: evaluator);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var result = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Error, result.Status);
        using var metrics = JsonDocument.Parse(Assert.IsType<string>(result.MetricsJson));
        Assert.Equal(0, metrics.RootElement.GetProperty("passed").GetInt32());
        Assert.Equal(2, metrics.RootElement.GetProperty("failed").GetInt32());
        Assert.Equal(2, metrics.RootElement.GetProperty("errors").GetInt32());
        Assert.Equal(4, metrics.RootElement.GetProperty("expectationResults").GetArrayLength());
        Assert.Contains("unknown_expectation_field", result.FailureReasonsJson, StringComparison.Ordinal);
        Assert.Contains("unsupported_expectation_type", result.FailureReasonsJson, StringComparison.Ordinal);
        Assert.Contains("missing_expectation_type", result.FailureReasonsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessRunAsync_records_a_failed_result_when_no_expectation_errors_occur()
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(
            suiteId,
            "[{\"type\":\"contains_text\",\"text\":\"missing\"}]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var processor = CreateProcessor(
            dbContext,
            runStore,
            expectationEvaluator: new DelegateExpectationEvaluator((_, _, _) =>
                Task.FromResult(new ExpectationResult
                {
                    ExpectationType = "contains_text",
                    Passed = false,
                    Score = 0,
                    Reason = "missing"
                })));

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var result = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Fail, result.Status);
        Assert.Equal(TestRunStatus.Completed, runStore.StatusUpdates[^1].Status);
    }

    [Theory]
    [InlineData("llm_judge", 0.8, 0.9, true)]
    [InlineData("llm_judge", 0.8, 0.7, false)]
    [InlineData("groundedness", 0.8, 0.9, true)]
    [InlineData("groundedness", 0.8, 0.7, false)]
    public async Task ProcessRunAsync_records_python_evaluation_success(
        string expectationType,
        double minScore,
        double score,
        bool expectedPass)
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var expectation = new Dictionary<string, object>
        {
            ["type"] = expectationType,
            ["min_score"] = minScore,
            ["model"] = "judge-model",
            ["provider"] = "judge-provider"
        };
        if (expectationType == "llm_judge")
        {
            expectation["rubric"] = "Be correct";
        }

        dbContext.TestCases.Add(CreateTestCase(
            suiteId,
            JsonSerializer.Serialize(new[] { expectation })));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var pythonClient = new DelegatePythonEvalClient
        {
            LlmJudge = (_, _, _, _, _, _) => Task.FromResult(new EvaluationResult
            {
                Success = true,
                Score = score,
                Reason = "scored"
            }),
            Groundedness = (_, _, _, _, _, _) => Task.FromResult(new EvaluationResult
            {
                Success = true,
                Score = score,
                Reason = "scored"
            })
        };
        var processor = CreateProcessor(dbContext, runStore, pythonEvalClient: pythonClient);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var result = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(
            expectedPass ? TestResultStatus.Pass : TestResultStatus.Fail,
            result.Status);
        Assert.Equal(expectationType == "llm_judge" ? 1 : 0, pythonClient.LlmCallCount);
        Assert.Equal(expectationType == "groundedness" ? 1 : 0, pythonClient.GroundednessCallCount);
        Assert.Equal("judge-model", pythonClient.LastModel);
        Assert.Equal("judge-provider", pythonClient.LastProvider);
        Assert.Equal(minScore, pythonClient.LastMinScore);
        if (expectationType == "groundedness")
        {
            Assert.Single(pythonClient.LastDocs);
        }
    }

    [Fact]
    public async Task ProcessRunAsync_keeps_missing_grounding_evidence_as_a_failed_assertion_at_zero_threshold()
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(
            suiteId,
            "[{\"type\":\"groundedness\",\"min_score\":0}]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var pythonClient = new DelegatePythonEvalClient
        {
            Groundedness = (_, _, _, _, _, _) => Task.FromResult(new EvaluationResult
            {
                Success = true,
                Score = 1,
                Reason = "should not be called"
            })
        };
        var processor = CreateProcessor(
            dbContext,
            runStore,
            mappingService: new DelegateMappingService((_, _) =>
                Task.FromResult(new MappingResult
                {
                    Success = true,
                    Trace = new CanonicalTrace
                    {
                        Messages = [new Message { Role = "assistant", Content = "answer" }]
                    }
                })),
            pythonEvalClient: pythonClient);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var result = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Fail, result.Status);
        Assert.Equal(0, pythonClient.GroundednessCallCount);
        using var metrics = JsonDocument.Parse(Assert.IsType<string>(result.MetricsJson));
        Assert.Equal(0, metrics.RootElement.GetProperty("passed").GetInt32());
        Assert.Equal(1, metrics.RootElement.GetProperty("failed").GetInt32());
        Assert.Equal(0, metrics.RootElement.GetProperty("errors").GetInt32());
        Assert.Contains("No retrieved documents available", result.FailureReasonsJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("llm_judge")]
    [InlineData("groundedness")]
    public async Task ProcessRunAsync_uses_python_defaults_and_client_error_fallbacks(
        string expectationType)
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(
            suiteId,
            expectationType == "llm_judge"
                ? "[{\"type\":\"llm_judge\",\"rubric\":\"Be helpful\"}]"
                : "[{\"type\":\"groundedness\"}]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var pythonClient = new DelegatePythonEvalClient
        {
            LlmJudge = (_, _, _, _, _, _) => Task.FromResult(new EvaluationResult
            {
                Success = false,
                ErrorMessage = "judge failed"
            }),
            Groundedness = (_, _, _, _, _, _) => Task.FromResult(new EvaluationResult
            {
                Success = false,
                ErrorMessage = "grounding failed"
            })
        };
        var processor = CreateProcessor(dbContext, runStore, pythonEvalClient: pythonClient);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var result = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Error, result.Status);
        Assert.Equal(0.8, pythonClient.LastMinScore);
        Assert.Null(pythonClient.LastModel);
        Assert.Null(pythonClient.LastProvider);
        Assert.Contains(PythonWorkerErrorCodes.ClientError, result.FailureReasonsJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("llm_judge")]
    [InlineData("groundedness")]
    public async Task ProcessRunAsync_converts_python_client_exceptions_to_error_results(
        string expectationType)
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(
            suiteId,
            expectationType == "llm_judge"
                ? "[{\"type\":\"llm_judge\",\"rubric\":\"Be helpful\"}]"
                : "[{\"type\":\"groundedness\"}]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var pythonClient = new DelegatePythonEvalClient
        {
            LlmJudge = (_, _, _, _, _, _) => throw new HttpRequestException("network down"),
            Groundedness = (_, _, _, _, _, _) => throw new HttpRequestException("network down")
        };
        var processor = CreateProcessor(dbContext, runStore, pythonEvalClient: pythonClient);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var result = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Error, result.Status);
        Assert.Contains(PythonWorkerErrorCodes.ClientError, result.FailureReasonsJson, StringComparison.Ordinal);
        Assert.Contains("could not be completed", result.FailureReasonsJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("evaluator-exception")]
    [InlineData("mapping-exception")]
    [InlineData("endpoint-exception")]
    public async Task ProcessRunAsync_converts_case_processing_exceptions_to_error_results(
        string failurePoint)
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var expectationsJson = failurePoint == "invalid-json"
            ? "not json"
            : failurePoint == "evaluator-exception"
                ? "[{\"type\":\"contains_text\",\"text\":\"ok\"}]"
                : "[{\"type\":\"contains_text\"}]";
        dbContext.TestCases.Add(CreateTestCase(suiteId, expectationsJson));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId)));
        var endpoint = new DelegateEndpointExecutor((_, _, _, _) =>
            failurePoint == "endpoint-exception"
                ? throw new HttpRequestException("endpoint exploded")
                : Task.FromResult(new ExecutionResult { Success = true, ResponseJson = "{}" }));
        var mapping = new DelegateMappingService((_, _) =>
            failurePoint == "mapping-exception"
                ? throw new InvalidOperationException("mapping exploded")
                : Task.FromResult(new MappingResult { Success = true, Trace = CreateTrace() }));
        var evaluator = new DelegateExpectationEvaluator((_, _, _) =>
            failurePoint == "evaluator-exception"
                ? throw new InvalidOperationException("evaluator exploded")
                : Task.FromResult(PassingExpectation()));
        var processor = CreateProcessor(
            dbContext,
            runStore,
            endpointExecutor: endpoint,
            mappingService: mapping,
            expectationEvaluator: evaluator);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var result = await dbContext.TestRunResults.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TestResultStatus.Error, result.Status);
        Assert.Contains(
            failurePoint == "invalid-json" ? "invalid_expectations_json" : "Processing error",
            result.FailureReasonsJson,
            StringComparison.Ordinal);
        Assert.Equal(TestRunStatus.Completed, runStore.StatusUpdates[^1].Status);
    }

    [Fact]
    public async Task ProcessRunAsync_marks_a_run_failed_when_loading_throws()
    {
        await using var dbContext = CreateDbContext();
        var runId = Guid.NewGuid();
        var runStore = new RecordingWorkerStore(WorkerRunLoadResult.NotFound())
        {
            Load = (_, _) => throw new InvalidOperationException("database unavailable")
        };
        var processor = CreateProcessor(dbContext, runStore);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        var update = Assert.Single(runStore.StatusUpdates);
        Assert.Equal(TestRunStatus.Failed, update.Status);
        Assert.Contains("database unavailable", update.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProcessRunAsync_marks_a_run_failed_when_a_status_transition_throws()
    {
        await using var dbContext = CreateDbContext();
        var suiteId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        dbContext.TestCases.Add(CreateTestCase(suiteId, "[]"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var runStore = new RecordingWorkerStore(
            WorkerRunLoadResult.Ready(CreateRun(runId, suiteId, TestRunStatus.Queued)));
        var updateAttempts = 0;
        runStore.OnUpdate = update =>
        {
            updateAttempts++;
            return updateAttempts == 1
                ? Task.FromException(new InvalidOperationException("transition failed"))
                : Task.CompletedTask;
        };
        var processor = CreateProcessor(dbContext, runStore);

        await processor.ProcessRunAsync(runId, TestContext.Current.CancellationToken);

        Assert.Collection(
            runStore.StatusUpdates,
            update => Assert.Equal(TestRunStatus.Running, update.Status),
            update =>
            {
                Assert.Equal(TestRunStatus.Failed, update.Status);
                Assert.Contains("transition failed", update.ErrorMessage, StringComparison.Ordinal);
            });
    }

    [Fact]
    public async Task ProcessRunAsync_rethrows_cancellation_when_recording_failure_also_fails()
    {
        await using var dbContext = CreateDbContext();
        var runId = Guid.NewGuid();
        var runStore = new RecordingWorkerStore(WorkerRunLoadResult.NotFound())
        {
            OnUpdate = _ => Task.FromException(new InvalidOperationException("status unavailable"))
        };
        var processor = CreateProcessor(dbContext, runStore);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            processor.ProcessRunAsync(runId, cancellation.Token));

        Assert.Equal(TestRunStatus.Failed, Assert.Single(runStore.StatusUpdates).Status);
    }

    private static PromptlyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase($"processor-coverage-{Guid.NewGuid():N}")
            .Options;
        return new PromptlyDbContext(options);
    }

    private static TestRunProcessor CreateProcessor(
        PromptlyDbContext dbContext,
        ITestRunWorkerStore runStore,
        IEndpointExecutor? endpointExecutor = null,
        IMappingService? mappingService = null,
        IExpectationEvaluator? expectationEvaluator = null,
        IPythonEvalClient? pythonEvalClient = null)
    {
        return new TestRunProcessor(
            dbContext,
            runStore,
            endpointExecutor ?? new DelegateEndpointExecutor((_, _, _, _) =>
                Task.FromResult(new ExecutionResult { Success = true, ResponseJson = "{}" })),
            mappingService ?? new DelegateMappingService((_, _) =>
                Task.FromResult(new MappingResult { Success = true, Trace = CreateTrace() })),
            expectationEvaluator ?? new DelegateExpectationEvaluator((_, _, _) =>
                Task.FromResult(PassingExpectation())),
            pythonEvalClient ?? new DelegatePythonEvalClient(),
            NullLogger<TestRunProcessor>.Instance);
    }

    private static TestCase CreateTestCase(Guid suiteId, string expectationsJson)
    {
        return new TestCase
        {
            Id = Guid.NewGuid(),
            SuiteId = suiteId,
            ExternalId = Guid.NewGuid().ToString("N"),
            Name = "Processor coverage",
            InputSpecJson = "{}",
            ExpectationsJson = expectationsJson
        };
    }

    private static string ValidExpectationJson(string expectationType) => expectationType switch
    {
        "contains_text" => "[{\"type\":\"contains_text\",\"text\":\"response\"}]",
        "banned_text" => "[{\"type\":\"banned_text\",\"text\":\"forbidden\"}]",
        "regex_match" => "[{\"type\":\"regex_match\",\"pattern\":\"response\"}]",
        "link_pattern" => "[{\"type\":\"link_pattern\",\"pattern\":\"example\"}]",
        "tool_called" => "[{\"type\":\"tool_called\",\"tool_name\":\"search\"}]",
        "tool_sequence" => "[{\"type\":\"tool_sequence\",\"sequence\":[\"search\"]}]",
        _ => throw new ArgumentOutOfRangeException(nameof(expectationType), expectationType, null)
    };

    private static TestRun CreateRun(
        Guid runId,
        Guid suiteId,
        TestRunStatus status = TestRunStatus.Running)
    {
        var projectId = Guid.NewGuid();
        var environment = new Environment
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
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
            ProjectId = projectId,
            SuiteId = suiteId,
            EnvironmentId = environment.Id,
            EndpointId = endpoint.Id,
            MappingSpecId = mapping.Id,
            Status = status,
            CreatedByUserId = "processor-user",
            Environment = environment,
            Endpoint = endpoint,
            MappingSpec = mapping
        };
    }

    private static CanonicalTrace CreateTrace()
    {
        return new CanonicalTrace
        {
            Messages = [new Message { Role = "assistant", Content = "response" }],
            RetrievedDocs = [new RetrievedDoc { Id = "doc-1", Content = "source" }]
        };
    }

    private static ExpectationResult PassingExpectation()
    {
        return new ExpectationResult
        {
            ExpectationType = "contains_text",
            Passed = true,
            Score = 1,
            Reason = "matched"
        };
    }

    private sealed record StatusUpdate(
        TestRunStatus Status,
        string? SummaryJson,
        string? ErrorMessage);

    private sealed class RecordingWorkerStore(WorkerRunLoadResult loadResult)
        : ITestRunWorkerStore
    {
        public Func<Guid, CancellationToken, Task<WorkerRunLoadResult>>? Load { get; init; }

        public Func<StatusUpdate, Task>? OnUpdate { get; set; }

        public List<StatusUpdate> StatusUpdates { get; } = [];

        public Task<WorkerRunLoadResult> LoadRunForProcessingAsync(
            Guid runId,
            CancellationToken cancellationToken = default) =>
            Load?.Invoke(runId, cancellationToken) ?? Task.FromResult(loadResult);

        public Task<Guid?> ClaimNextQueuedRunAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateRunStatusAsync(
            Guid runId,
            TestRunStatus status,
            string? summaryJson = null,
            string? errorMessage = null)
        {
            var update = new StatusUpdate(status, summaryJson, errorMessage);
            StatusUpdates.Add(update);
            return OnUpdate?.Invoke(update) ?? Task.CompletedTask;
        }
    }

    private sealed class DelegateEndpointExecutor(
        Func<Endpoint, Environment, TestCase, CancellationToken, Task<ExecutionResult>> execute)
        : IEndpointExecutor
    {
        public int CallCount { get; private set; }

        public Task<ExecutionResult> ExecuteAsync(
            Endpoint endpoint,
            Environment environment,
            TestCase testCase,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return execute(endpoint, environment, testCase, cancellationToken);
        }
    }

    private sealed class DelegateMappingService(
        Func<string, string, Task<MappingResult>> apply)
        : IMappingService
    {
        public int ApplyCallCount { get; private set; }

        public Task<MappingResult> ApplyMappingAsync(string mappingSpecJson, string responseJson)
        {
            ApplyCallCount++;
            return apply(mappingSpecJson, responseJson);
        }

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
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<MappingSpec?> GetMappingSpecByIdAsync(Guid id, TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> GetDefaultMappingAsync(Guid endpointId, TenantAccessScope scope) =>
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

    private sealed class DelegateExpectationEvaluator(
        Func<object, CanonicalTrace, CancellationToken, Task<ExpectationResult>> evaluate)
        : IExpectationEvaluator
    {
        public int CallCount { get; private set; }

        public Task<ExpectationResult> EvaluateAsync(
            object expectation,
            CanonicalTrace trace,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return evaluate(expectation, trace, cancellationToken);
        }
    }

    private sealed class DelegatePythonEvalClient : IPythonEvalClient
    {
        public Func<
            string,
            double,
            CanonicalTrace,
            string?,
            string?,
            CancellationToken,
            Task<EvaluationResult>> LlmJudge
        { get; init; } = (_, _, _, _, _, _) =>
                throw new NotSupportedException();

        public Func<
            double,
            CanonicalTrace,
            List<RetrievedDoc>,
            string?,
            string?,
            CancellationToken,
            Task<EvaluationResult>> Groundedness
        { get; init; } = (_, _, _, _, _, _) =>
                throw new NotSupportedException();

        public int LlmCallCount { get; private set; }

        public int GroundednessCallCount { get; private set; }

        public double LastMinScore { get; private set; }

        public string? LastModel { get; private set; }

        public string? LastProvider { get; private set; }

        public List<RetrievedDoc> LastDocs { get; private set; } = [];

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
            LlmCallCount++;
            LastMinScore = minScore;
            LastModel = model;
            LastProvider = provider;
            return LlmJudge(rubric, minScore, trace, model, provider, cancellationToken);
        }

        public Task<EvaluationResult> EvaluateGroundednessAsync(
            double minScore,
            CanonicalTrace trace,
            List<RetrievedDoc> docs,
            string? model = null,
            string? provider = null,
            CancellationToken cancellationToken = default)
        {
            GroundednessCallCount++;
            LastMinScore = minScore;
            LastModel = model;
            LastProvider = provider;
            LastDocs = docs;
            return Groundedness(minScore, trace, docs, model, provider, cancellationToken);
        }
    }
}
