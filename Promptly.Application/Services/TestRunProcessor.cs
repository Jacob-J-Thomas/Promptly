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
    private readonly ITestRunService _testRunService;
    private readonly IEndpointExecutor _endpointExecutor;
    private readonly IMappingService _mappingService;
    private readonly IExpectationEvaluator _expectationEvaluator;
    private readonly IPythonEvalClient _pythonEvalClient;
    private readonly ILogger<TestRunProcessor> _logger;

    public TestRunProcessor(
        PromptlyDbContext dbContext,
        ITestRunService testRunService,
        IEndpointExecutor endpointExecutor,
        IMappingService mappingService,
        IExpectationEvaluator expectationEvaluator,
        IPythonEvalClient pythonEvalClient,
        ILogger<TestRunProcessor> logger)
    {
        _dbContext = dbContext;
        _testRunService = testRunService;
        _endpointExecutor = endpointExecutor;
        _mappingService = mappingService;
        _expectationEvaluator = expectationEvaluator;
        _pythonEvalClient = pythonEvalClient;
        _logger = logger;
    }

    public async Task ProcessRunAsync(Guid runId)
    {
        try
        {
            _logger.LogInformation("Starting processing for run {RunId}", runId);

            // Load run details
            var run = await _testRunService.GetRunByIdAsync(runId);
            if (run == null)
            {
                _logger.LogError("Run {RunId} not found", runId);
                return;
            }

            // Load test cases
            var testCases = await _dbContext.TestCases
                .Where(tc => tc.SuiteId == run.SuiteId)
                .ToListAsync();

            if (testCases.Count == 0)
            {
                await _testRunService.UpdateRunStatusAsync(
                    runId,
                    TestRunStatus.Failed,
                    errorMessage: "No test cases found in suite");
                return;
            }

            // Update status to Running (if not already)
            if (run.Status != TestRunStatus.Running)
            {
                await _testRunService.UpdateRunStatusAsync(runId, TestRunStatus.Running);
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
                    var result = await ProcessTestCaseAsync(run, testCase);
                    results.Add(result);

                    if (result.Status == TestResultStatus.Pass)
                        totalPassed++;
                    else if (result.Status == TestResultStatus.Fail)
                        totalFailed++;
                    else
                        totalErrors++;

                    // Aggregate metrics
                    if (!string.IsNullOrWhiteSpace(result.TraceJson))
                    {
                        try
                        {
                            var trace = JsonSerializer.Deserialize<Dictionary<string, object>>(result.TraceJson);
                            if (trace != null && trace.ContainsKey("usage"))
                            {
                                var usage = JsonSerializer.Deserialize<Dictionary<string, object>>(trace["usage"].ToString() ?? "{}");
                                if (usage != null)
                                {
                                    if (usage.ContainsKey("totalTokens") && int.TryParse(usage["totalTokens"].ToString(), out var tokens))
                                        totalTokens += tokens;
                                    if (usage.ContainsKey("cost") && double.TryParse(usage["cost"].ToString(), out var cost))
                                        totalCost += cost;
                                    if (usage.ContainsKey("latencyMs") && long.TryParse(usage["latencyMs"].ToString(), out var latency))
                                        totalLatency += latency;
                                }
                            }
                        }
                        catch
                        {
                            // Ignore parsing errors
                        }
                    }
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
            await _dbContext.SaveChangesAsync();

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
            await _testRunService.UpdateRunStatusAsync(runId, TestRunStatus.Completed, summaryJson);

            _logger.LogInformation(
                "Completed run {RunId}: {Passed}/{Total} passed, {Failed} failed, {Errors} errors",
                runId, totalPassed, testCases.Count, totalFailed, totalErrors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error processing run {RunId}", runId);
            await _testRunService.UpdateRunStatusAsync(
                runId,
                TestRunStatus.Failed,
                errorMessage: $"Fatal processing error: {ex.Message}");
        }
    }

    private async Task<TestRunResult> ProcessTestCaseAsync(TestRun run, TestCase testCase)
    {
        try
        {
            // Step 1: Execute HTTP request
            var executionResult = await _endpointExecutor.ExecuteAsync(run.Endpoint!, run.Environment!, testCase);

            if (!executionResult.Success || string.IsNullOrWhiteSpace(executionResult.ResponseJson))
            {
                return new TestRunResult
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    TestCaseId = testCase.Id,
                    Status = TestResultStatus.Error,
                    TraceJson = "{}",
                    FailureReasonsJson = JsonSerializer.Serialize(new[] { executionResult.ErrorMessage ?? "No response from endpoint" }),
                    CreatedAt = DateTime.UtcNow
                };
            }

            // Step 2: Apply mapping to get CanonicalTrace
            var mappingResult = await _mappingService.ApplyMappingAsync(
                run.MappingSpec!.SpecJson,
                executionResult.ResponseJson);

            if (!mappingResult.Success || mappingResult.Trace == null)
            {
                return new TestRunResult
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    TestCaseId = testCase.Id,
                    Status = TestResultStatus.Error,
                    TraceJson = "{}",
                    FailureReasonsJson = JsonSerializer.Serialize(new[] { $"Mapping failed: {mappingResult.ErrorMessage}" }),
                    CreatedAt = DateTime.UtcNow
                };
            }

            var trace = mappingResult.Trace;
            var traceJson = JsonSerializer.Serialize(trace);

            // Step 3: Evaluate expectations
            var expectations = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(testCase.ExpectationsJson);
            if (expectations == null || expectations.Count == 0)
            {
                return new TestRunResult
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    TestCaseId = testCase.Id,
                    Status = TestResultStatus.Pass,
                    TraceJson = traceJson,
                    MetricsJson = JsonSerializer.Serialize(new { passed = 0, failed = 0 }),
                    CreatedAt = DateTime.UtcNow
                };
            }

            var expectationResults = new List<ExpectationResult>();
            int passed = 0;
            int failed = 0;
            var failureReasons = new List<string>();

            foreach (var exp in expectations)
            {
                var expType = exp.ContainsKey("type") ? exp["type"].ToString() : "";
                ExpectationResult expResult;

                // Deterministic expectations
                if (expType == "contains_text" || expType == "banned_text" || expType == "regex_match" ||
                    expType == "link_pattern" || expType == "tool_called" || expType == "tool_sequence")
                {
                    expResult = await _expectationEvaluator.EvaluateAsync(exp, trace);
                }
                // LLM-based expectations
                else if (expType == "llm_judge")
                {
                    expResult = await EvaluateLlmJudgeAsync(exp, trace);
                }
                else if (expType == "groundedness")
                {
                    expResult = await EvaluateGroundednessAsync(exp, trace);
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
                    passed++;
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
                total = expectations.Count,
                expectationResults
            });

            return new TestRunResult
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                TestCaseId = testCase.Id,
                Status = failed == 0 ? TestResultStatus.Pass : TestResultStatus.Fail,
                TraceJson = traceJson,
                MetricsJson = metricsJson,
                FailureReasonsJson = failureReasons.Count > 0 ? JsonSerializer.Serialize(failureReasons) : null,
                CreatedAt = DateTime.UtcNow
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing test case {TestCaseId}", testCase.Id);
            return new TestRunResult
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                TestCaseId = testCase.Id,
                Status = TestResultStatus.Error,
                TraceJson = "{}",
                FailureReasonsJson = JsonSerializer.Serialize(new[] { $"Processing error: {ex.Message}" }),
                CreatedAt = DateTime.UtcNow
            };
        }
    }

    private async Task<ExpectationResult> EvaluateLlmJudgeAsync(Dictionary<string, object> exp, Domain.ValueObjects.CanonicalTrace trace)
    {
        try
        {
            var rubric = exp.ContainsKey("rubric") ? exp["rubric"].ToString() : "";
            var minScore = exp.ContainsKey("min_score") && double.TryParse(exp["min_score"].ToString(), out var ms) ? ms : 0.7;
            var model = exp.ContainsKey("model") ? exp["model"].ToString() : null;
            var provider = exp.ContainsKey("provider") ? exp["provider"].ToString() : null;

            var result = await _pythonEvalClient.EvaluateLlmJudgeAsync(
                rubric ?? "",
                minScore,
                trace,
                model,
                provider);

            if (!result.Success)
            {
                return new ExpectationResult
                {
                    ExpectationType = "llm_judge",
                    Passed = false,
                    Score = 0.0,
                    Reason = $"LLM judge evaluation failed: {result.ErrorMessage}"
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate LLM judge expectation");
            return new ExpectationResult
            {
                ExpectationType = "llm_judge",
                Passed = false,
                Score = 0.0,
                Reason = $"LLM judge error: {ex.Message}"
            };
        }
    }

    private async Task<ExpectationResult> EvaluateGroundednessAsync(Dictionary<string, object> exp, Domain.ValueObjects.CanonicalTrace trace)
    {
        try
        {
            var minScore = exp.ContainsKey("min_score") && double.TryParse(exp["min_score"].ToString(), out var ms) ? ms : 0.7;
            var model = exp.ContainsKey("model") ? exp["model"].ToString() : null;
            var provider = exp.ContainsKey("provider") ? exp["provider"].ToString() : null;

            var result = await _pythonEvalClient.EvaluateGroundednessAsync(
                minScore,
                trace,
                trace.RetrievedDocs.ToList(),
                model,
                provider);

            if (!result.Success)
            {
                return new ExpectationResult
                {
                    ExpectationType = "groundedness",
                    Passed = false,
                    Score = 0.0,
                    Reason = $"Groundedness evaluation failed: {result.ErrorMessage}"
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to evaluate groundedness expectation");
            return new ExpectationResult
            {
                ExpectationType = "groundedness",
                Passed = false,
                Score = 0.0,
                Reason = $"Groundedness error: {ex.Message}"
            };
        }
    }
}
