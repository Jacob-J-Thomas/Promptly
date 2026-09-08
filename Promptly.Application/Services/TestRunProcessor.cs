using System.Text.Json;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;
using Promptly.Application.Data;
using Microsoft.EntityFrameworkCore;
using Promptly.Domain.ValueObjects;
using ExpectationResult = Promptly.Domain.ValueObjects.ExpectationResult;

namespace Promptly.Application.Services;

public class TestRunProcessor : ITestRunProcessor
{
    private readonly PromptlyDbContext _dbContext;
    private readonly ITestRunWorkerStore _testRunWorkerStore;
    private readonly IEndpointExecutor _endpointExecutor;
    private readonly IMappingService _mappingService;
    private readonly IExpectationEvaluator _expectationEvaluator;
    private readonly IPythonEvalClient _pythonEvalClient;
    private readonly IExpectationValidator _expectationValidator;
    private readonly ILogger<TestRunProcessor> _logger;

    public TestRunProcessor(
        PromptlyDbContext dbContext,
        ITestRunWorkerStore testRunWorkerStore,
        IEndpointExecutor endpointExecutor,
        IMappingService mappingService,
        IExpectationEvaluator expectationEvaluator,
        IPythonEvalClient pythonEvalClient,
        ILogger<TestRunProcessor> logger,
        IExpectationValidator? expectationValidator = null)
    {
        _dbContext = dbContext;
        _testRunWorkerStore = testRunWorkerStore;
        _endpointExecutor = endpointExecutor;
        _mappingService = mappingService;
        _expectationEvaluator = expectationEvaluator;
        _pythonEvalClient = pythonEvalClient;
        _expectationValidator = expectationValidator ?? new ExpectationDslValidator();
        _logger = logger;
    }

    public async Task ProcessRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("Starting processing for run {RunId}", runId);

            var loadResult = await _testRunWorkerStore.LoadRunForProcessingAsync(
                runId,
                cancellationToken);
            if (loadResult.Status == WorkerRunLoadStatus.NotFound)
            {
                _logger.LogError("Run {RunId} not found", runId);
                return;
            }

            if (loadResult.Status is WorkerRunLoadStatus.InvalidGraph
                or WorkerRunLoadStatus.UnsafeEndpointTarget)
            {
                _logger.LogWarning(
                    "Run {RunId} failed process-time {ValidationStatus} validation",
                    runId,
                    loadResult.Status);
                return;
            }

            var run = loadResult.Run
                ?? throw new InvalidOperationException(
                    $"Worker run load for {runId} was ready without run data");

            // Load test cases
            var testCases = await _dbContext.TestCases
                .AsNoTracking()
                .Where(tc => tc.SuiteId == run.SuiteId)
                .ToListAsync(cancellationToken);

            if (testCases.Count == 0)
            {
                await _testRunWorkerStore.UpdateRunStatusAsync(
                    runId,
                    TestRunStatus.Failed,
                    errorMessage: "No test cases found in suite");
                return;
            }

            // Update status to Running (if not already)
            if (run.Status != TestRunStatus.Running)
            {
                await _testRunWorkerStore.UpdateRunStatusAsync(runId, TestRunStatus.Running);
            }

            // Process each test case
            var results = new List<TestRunResult>();
            var totalPassed = 0;
            var totalFailed = 0;
            var totalErrors = 0;
            var totalLatency = 0L;
            var totalTokens = 0;
            var totalCost = 0.0;

            foreach (var testCase in testCases)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var processedCase = await ProcessTestCaseAsync(run, testCase, cancellationToken);
                    var result = processedCase.Result;
                    results.Add(result);

                    if (result.Status == TestResultStatus.Pass)
                        totalPassed++;
                    else if (result.Status == TestResultStatus.Fail)
                        totalFailed++;
                    else
                        totalErrors++;

                    // Aggregate the typed mapping output rather than reparsing persisted JSON.
                    if (processedCase.Usage is { } usage)
                    {
                        totalTokens += usage.TotalTokens ?? 0;
                        totalCost += (double)(usage.Cost ?? 0m);
                        totalLatency += usage.LatencyMs ?? 0;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to process test case {TestCaseId} in run {RunId}", testCase.Id, runId);

                    // Create error result
                    var errorResult = new TestRunResult
                    {
                        Id = Guid.NewGuid(),
                        RunId = runId,
                        TestCaseId = testCase.Id,
                        Status = TestResultStatus.Error,
                        TraceJson = "{}",
                        FailureReasonsJson = JsonSerializer.Serialize(new[] { $"Processing error: {ex.Message}" }),
                        CreatedAt = DateTime.UtcNow
                    };

                    results.Add(errorResult);
                    totalErrors++;
                }
            }

            // Save all results
            _dbContext.TestRunResults.AddRange(results);
            await _dbContext.SaveChangesAsync(cancellationToken);

            // Compute summary
            var summary = new
            {
                total = testCases.Count,
                passed = totalPassed,
                failed = totalFailed,
                errors = totalErrors,
                passRate = testCases.Count > 0 ? (double)totalPassed / testCases.Count : 0.0,
                avgLatencyMs = testCases.Count > 0 ? totalLatency / testCases.Count : 0,
                totalTokens,
                totalCost
            };

            var summaryJson = JsonSerializer.Serialize(summary);

            // Update run status
            await _testRunWorkerStore.UpdateRunStatusAsync(
                runId,
                TestRunStatus.Completed,
                summaryJson);

            _logger.LogInformation(
                "Completed run {RunId}: {Passed}/{Total} passed, {Failed} failed, {Errors} errors",
                runId, totalPassed, testCases.Count, totalFailed, totalErrors);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Run {RunId} was cancelled before completion", runId);
            try
            {
                await _testRunWorkerStore.UpdateRunStatusAsync(
                    runId,
                    TestRunStatus.Failed,
                    errorMessage: "Run processing was cancelled before completion");
            }
            catch (Exception statusException)
            {
                _logger.LogError(
                    statusException,
                    "Failed to record cancellation for run {RunId}; manual recovery may be required",
                    runId);
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error processing run {RunId}", runId);
            await _testRunWorkerStore.UpdateRunStatusAsync(
                runId,
                TestRunStatus.Failed,
                errorMessage: $"Fatal processing error: {ex.Message}");
        }
    }

    private async Task<ProcessedTestCase> ProcessTestCaseAsync(
        TestRun run,
        TestCase testCase,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Step 1: Execute HTTP request
            var executionResult = await _endpointExecutor.ExecuteAsync(
                run.Endpoint!,
                run.Environment!,
                testCase,
                cancellationToken);

            if (!executionResult.Success || string.IsNullOrWhiteSpace(executionResult.ResponseJson))
            {
                return new ProcessedTestCase(
                    new TestRunResult
                    {
                        Id = Guid.NewGuid(),
                        RunId = run.Id,
                        TestCaseId = testCase.Id,
                        Status = TestResultStatus.Error,
                        TraceJson = "{}",
                        FailureReasonsJson = JsonSerializer.Serialize(new[] { executionResult.ErrorMessage ?? "No response from endpoint" }),
                        CreatedAt = DateTime.UtcNow
                    },
                    Usage: null);
            }

            // Step 2: Apply mapping to get CanonicalTrace
            var mappingResult = await _mappingService.ApplyMappingAsync(
                run.MappingSpec!.SpecJson,
                executionResult.ResponseJson);

            if (!mappingResult.Success || mappingResult.Trace == null)
            {
                return new ProcessedTestCase(
                    new TestRunResult
                    {
                        Id = Guid.NewGuid(),
                        RunId = run.Id,
                        TestCaseId = testCase.Id,
                        Status = TestResultStatus.Error,
                        TraceJson = "{}",
                        FailureReasonsJson = JsonSerializer.Serialize(new[] { $"Mapping failed: {mappingResult.ErrorMessage}" }),
                        CreatedAt = DateTime.UtcNow
                    },
                    Usage: null);
            }

            var trace = mappingResult.Trace;
            var traceJson = JsonSerializer.Serialize(trace);

            // Step 3: Parse the persisted array, then validate each expectation
            // independently. API intake rejects an invalid document as a whole,
            // but legacy rows can contain a mix of valid assertions and bad
            // entries; those entries must remain visible as durable Errors while
            // valid assertions still produce their real Pass/Fail outcomes.
            List<ExpectationEntry> expectationEntries;
            try
            {
                using var expectationsDocument = JsonDocument.Parse(testCase.ExpectationsJson);
                if (expectationsDocument.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return InvalidTestCase(
                        run.Id,
                        testCase.Id,
                        [new ExpectationValidationIssue(
                            "invalid_expectations_json",
                            "$",
                            "Expectations JSON must be an array")],
                        traceJson,
                        trace.Usage);
                }

                expectationEntries = [];
                var index = 0;
                foreach (var element in expectationsDocument.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.Object)
                    {
                        expectationEntries.Add(new(
                            null,
                            [new ExpectationValidationIssue(
                                "invalid_expectation",
                                $"$[{index}]",
                                "Each expectation must be an object")]));
                    }
                    else
                    {
                        var expectation = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            element.GetRawText());
                        expectationEntries.Add(expectation is null
                            ? new(
                                null,
                                [new ExpectationValidationIssue(
                                    "invalid_expectation",
                                    $"$[{index}]",
                                    "Each expectation must be an object")])
                            : new(expectation, []));
                    }

                    index++;
                }
            }
            catch (JsonException)
            {
                return InvalidTestCase(
                    run.Id,
                    testCase.Id,
                    [new ExpectationValidationIssue(
                        "invalid_expectations_json",
                        "$",
                        "Expectations JSON is invalid")],
                    traceJson,
                    trace.Usage);
            }

            if (expectationEntries.Count == 0)
            {
                return InvalidTestCase(
                    run.Id,
                    testCase.Id,
                    [new ExpectationValidationIssue(
                        "no_expectations",
                        "$",
                        "At least one expectation is required")],
                    traceJson,
                    trace.Usage);
            }

            var expectationResults = new List<ExpectationResult>(expectationEntries.Count);
            int passed = 0;
            int failed = 0;
            int errors = 0;
            var failureReasons = new List<string>();

            foreach (var entry in expectationEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var exp = entry.Expectation;
                if (exp is null)
                {
                    var invalidResult = InvalidExpectationResult("invalid", entry.Issues);
                    expectationResults.Add(invalidResult);
                    errors++;
                    failureReasons.Add(
                        $"[{invalidResult.ExpectationType}:{invalidResult.ErrorCode}] {invalidResult.Reason}");
                    continue;
                }

                var validation = entry.Issues.Count > 0
                    ? new ExpectationValidationResult(entry.Issues)
                    : _expectationValidator.ValidateExpectation(exp);
                if (!validation.IsValid)
                {
                    var invalidResult = InvalidExpectationResult(
                        GetExpectationType(exp),
                        validation.Issues);
                    expectationResults.Add(invalidResult);
                    errors++;
                    failureReasons.Add(
                        $"[{invalidResult.ExpectationType}:{invalidResult.ErrorCode}] {invalidResult.Reason}");
                    continue;
                }

                var expType = GetExpectationType(exp);
                ExpectationResult expResult;

                // Deterministic expectations
                if (expType == "contains_text" || expType == "banned_text" || expType == "regex_match" ||
                    expType == "link_pattern" || expType == "tool_called" || expType == "tool_sequence")
                {
                    expResult = await _expectationEvaluator.EvaluateAsync(exp, trace, cancellationToken);
                }
                // LLM-based expectations
                else if (expType == "llm_judge")
                {
                    expResult = await EvaluateLlmJudgeAsync(exp, trace, cancellationToken);
                }
                else if (expType == "groundedness")
                {
                    expResult = await EvaluateGroundednessAsync(exp, trace, cancellationToken);
                }
                else
                {
                    expResult = new ExpectationResult
                    {
                        ExpectationType = expType ?? "unknown",
                        Passed = false,
                        Score = 0.0,
                        Reason = $"Unknown expectation type: {expType}"
                    };
                }

                expectationResults.Add(expResult);

                if (expResult.Passed)
                {
                    passed++;
                }
                else if (expResult.ErrorCode != null)
                {
                    errors++;
                    failureReasons.Add($"[{expType}:{expResult.ErrorCode}] {expResult.Reason}");
                }
                else
                {
                    failed++;
                    failureReasons.Add($"[{expType}] {expResult.Reason}");
                }
            }

            var metricsJson = JsonSerializer.Serialize(new
            {
                passed,
                failed,
                errors,
                total = expectationEntries.Count,
                expectationResults
            });

            return new ProcessedTestCase(
                new TestRunResult
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    TestCaseId = testCase.Id,
                    Status = errors > 0
                        ? TestResultStatus.Error
                        : failed > 0
                            ? TestResultStatus.Fail
                            : TestResultStatus.Pass,
                    TraceJson = traceJson,
                    MetricsJson = metricsJson,
                    FailureReasonsJson = failureReasons.Count > 0 ? JsonSerializer.Serialize(failureReasons) : null,
                    CreatedAt = DateTime.UtcNow
                },
                trace.Usage);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing test case {TestCaseId}", testCase.Id);
            return new ProcessedTestCase(
                new TestRunResult
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    TestCaseId = testCase.Id,
                    Status = TestResultStatus.Error,
                    TraceJson = "{}",
                    FailureReasonsJson = JsonSerializer.Serialize(new[] { $"Processing error: {ex.Message}" }),
                    CreatedAt = DateTime.UtcNow
                },
                Usage: null);
        }
    }

    private static ProcessedTestCase InvalidTestCase(
        Guid runId,
        Guid testCaseId,
        IReadOnlyList<ExpectationValidationIssue> issues,
        string traceJson,
        Usage? usage)
    {
        var expectationResults = issues.Select(issue => new ExpectationResult
        {
            ExpectationType = "invalid",
            Passed = false,
            Score = 0,
            ErrorCode = issue.Code,
            Reason = $"{issue.Path}: {issue.Message}"
        }).ToList();

        var failureReasons = expectationResults
            .Select(result => $"[{result.ExpectationType}:{result.ErrorCode}] {result.Reason}")
            .ToList();

        return new ProcessedTestCase(
            new TestRunResult
            {
                Id = Guid.NewGuid(),
                RunId = runId,
                TestCaseId = testCaseId,
                Status = TestResultStatus.Error,
                TraceJson = traceJson,
                MetricsJson = JsonSerializer.Serialize(new
                {
                    passed = 0,
                    failed = 0,
                    errors = expectationResults.Count,
                    total = expectationResults.Count,
                    expectationResults
                }),
                FailureReasonsJson = JsonSerializer.Serialize(failureReasons),
                CreatedAt = DateTime.UtcNow
            },
            Usage: usage);
    }

    private static ExpectationResult InvalidExpectationResult(
        string expectationType,
        IReadOnlyList<ExpectationValidationIssue> issues)
    {
        var firstIssue = issues[0];
        return new ExpectationResult
        {
            ExpectationType = expectationType,
            Passed = false,
            Score = 0,
            ErrorCode = firstIssue.Code,
            Reason = string.Join(
                "; ",
                issues.Select(issue => $"{issue.Path}: {issue.Message}"))
        };
    }

    private static string GetExpectationType(IReadOnlyDictionary<string, object> expectation)
    {
        return expectation.TryGetValue("type", out var value)
            && value is JsonElement { ValueKind: JsonValueKind.String } element
                ? element.GetString()!
                : "invalid";
    }

    private sealed record ExpectationEntry(
        Dictionary<string, object>? Expectation,
        IReadOnlyList<ExpectationValidationIssue> Issues);

    private sealed record ProcessedTestCase(TestRunResult Result, Usage? Usage);

    private async Task<ExpectationResult> EvaluateLlmJudgeAsync(
        Dictionary<string, object> exp,
        Domain.ValueObjects.CanonicalTrace trace,
        CancellationToken cancellationToken)
    {
        try
        {
            var rubric = exp.ContainsKey("rubric") ? exp["rubric"].ToString() : "";
            var minScore = exp.ContainsKey("min_score") && double.TryParse(exp["min_score"]?.ToString(), out var ms) ? ms : 0.8;
            var model = exp.ContainsKey("model") ? exp["model"].ToString() : null;
            var provider = exp.ContainsKey("provider") ? exp["provider"].ToString() : null;

            var result = await _pythonEvalClient.EvaluateLlmJudgeAsync(
                rubric ?? "",
                minScore,
                trace,
                model,
                provider,
                cancellationToken);

            if (!result.Success)
            {
                return new ExpectationResult
                {
                    ExpectationType = "llm_judge",
                    Passed = false,
                    Score = 0.0,
                    Reason = $"LLM judge evaluation failed: {result.ErrorMessage}",
                    ErrorCode = result.ErrorCode ?? PythonWorkerErrorCodes.ClientError
                };
            }

            return new ExpectationResult
            {
                ExpectationType = "llm_judge",
                Passed = result.Score >= minScore,
                Score = result.Score,
                Reason = result.Reason ?? "No reason provided"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate LLM judge expectation");
            return new ExpectationResult
            {
                ExpectationType = "llm_judge",
                Passed = false,
                Score = 0.0,
                Reason = "LLM judge evaluation could not be completed",
                ErrorCode = PythonWorkerErrorCodes.ClientError
            };
        }
    }

    private async Task<ExpectationResult> EvaluateGroundednessAsync(
        Dictionary<string, object> exp,
        Domain.ValueObjects.CanonicalTrace trace,
        CancellationToken cancellationToken)
    {
        try
        {
            var minScore = exp.ContainsKey("min_score") && double.TryParse(exp["min_score"]?.ToString(), out var ms) ? ms : 0.8;
            var model = exp.ContainsKey("model") ? exp["model"].ToString() : null;
            var provider = exp.ContainsKey("provider") ? exp["provider"].ToString() : null;

            // A score of zero from the worker is a truthful assertion failure when
            // no evidence is available. Guard this locally as well so a legacy
            // row cannot turn the absence of documents into Pass at min_score=0.
            if (trace.RetrievedDocs is null || trace.RetrievedDocs.Count == 0)
            {
                return new ExpectationResult
                {
                    ExpectationType = "groundedness",
                    Passed = false,
                    Score = 0.0,
                    Reason = "No retrieved documents available for groundedness evaluation"
                };
            }

            var result = await _pythonEvalClient.EvaluateGroundednessAsync(
                minScore,
                trace,
                trace.RetrievedDocs.ToList(),
                model,
                provider,
                cancellationToken);

            if (!result.Success)
            {
                return new ExpectationResult
                {
                    ExpectationType = "groundedness",
                    Passed = false,
                    Score = 0.0,
                    Reason = $"Groundedness evaluation failed: {result.ErrorMessage}",
                    ErrorCode = result.ErrorCode ?? PythonWorkerErrorCodes.ClientError
                };
            }

            return new ExpectationResult
            {
                ExpectationType = "groundedness",
                Passed = result.Score >= minScore,
                Score = result.Score,
                Reason = result.Reason ?? "No reason provided"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate groundedness expectation");
            return new ExpectationResult
            {
                ExpectationType = "groundedness",
                Passed = false,
                Score = 0.0,
                Reason = "Groundedness evaluation could not be completed",
                ErrorCode = PythonWorkerErrorCodes.ClientError
            };
        }
    }
}
