using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Data;
using Promptly.Domain.Enums;

namespace Promptly.Server.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public class RunsController : ControllerBase
{
    private readonly ITestRunService _testRunService;
    private readonly PromptlyDbContext _dbContext;
    private readonly ILogger<RunsController> _logger;

    public RunsController(
        ITestRunService testRunService,
        PromptlyDbContext dbContext,
        ILogger<RunsController> logger)
    {
        _testRunService = testRunService;
        _dbContext = dbContext;
        _logger = logger;
    }

    /// <summary>
    /// Queue a new test run
    /// </summary>
    [HttpPost("runs")]
    public async Task<IActionResult> QueueRun([FromBody] QueueRunRequest request)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

            var run = await _testRunService.QueueRunAsync(
                request.SuiteId,
                request.EnvironmentId,
                request.EndpointId,
                request.MappingSpecId,
                userId,
                request.GitCommitHash,
                request.ConfigSnapshotJson);

            var response = new TestRunResponse
            {
                Id = run.Id,
                SuiteId = run.SuiteId,
                EnvironmentId = run.EnvironmentId,
                EndpointId = run.EndpointId,
                MappingSpecId = run.MappingSpecId,
                Status = run.Status,
                SummaryJson = run.SummaryJson,
                GitCommitHash = run.GitCommitHash,
                ConfigSnapshotJson = run.ConfigSnapshotJson,
                CreatedByUserId = run.CreatedByUserId,
                ErrorMessage = run.ErrorMessage,
                CreatedAt = run.CreatedAt,
                StartedAt = run.StartedAt,
                CompletedAt = run.CompletedAt
            };

            return CreatedAtAction(nameof(GetRun), new { id = run.Id }, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to queue test run");
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Get a specific test run by ID
    /// </summary>
    [HttpGet("runs/{id:guid}")]
    public async Task<IActionResult> GetRun(Guid id)
    {
        try
        {
            var run = await _testRunService.GetRunByIdAsync(id);
            if (run == null)
            {
                return NotFound(new { message = "Test run not found" });
            }

            var response = new TestRunResponse
            {
                Id = run.Id,
                SuiteId = run.SuiteId,
                EnvironmentId = run.EnvironmentId,
                EndpointId = run.EndpointId,
                MappingSpecId = run.MappingSpecId,
                Status = run.Status,
                SummaryJson = run.SummaryJson,
                GitCommitHash = run.GitCommitHash,
                ConfigSnapshotJson = run.ConfigSnapshotJson,
                CreatedByUserId = run.CreatedByUserId,
                ErrorMessage = run.ErrorMessage,
                CreatedAt = run.CreatedAt,
                StartedAt = run.StartedAt,
                CompletedAt = run.CompletedAt
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get test run {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Get all runs for a suite
    /// </summary>
    [HttpGet("suites/{suiteId:guid}/runs")]
    public async Task<IActionResult> GetRunsBySuite(Guid suiteId, [FromQuery] string? status = null, [FromQuery] int? limit = null)
    {
        try
        {
            TestRunStatus? statusEnum = null;
            if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<TestRunStatus>(status, true, out var parsedStatus))
            {
                statusEnum = parsedStatus;
            }

            var runs = await _testRunService.GetRunsBySuiteAsync(suiteId, statusEnum, limit);

            var responses = runs.Select(r => new TestRunResponse
            {
                Id = r.Id,
                SuiteId = r.SuiteId,
                EnvironmentId = r.EnvironmentId,
                EndpointId = r.EndpointId,
                MappingSpecId = r.MappingSpecId,
                Status = r.Status,
                SummaryJson = r.SummaryJson,
                GitCommitHash = r.GitCommitHash,
                ConfigSnapshotJson = r.ConfigSnapshotJson,
                CreatedByUserId = r.CreatedByUserId,
                ErrorMessage = r.ErrorMessage,
                CreatedAt = r.CreatedAt,
                StartedAt = r.StartedAt,
                CompletedAt = r.CompletedAt
            }).ToList();

            return Ok(responses);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get runs for suite {SuiteId}", suiteId);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Get all results for a test run
    /// </summary>
    [HttpGet("runs/{id:guid}/results")]
    public async Task<IActionResult> GetRunResults(Guid id)
    {
        try
        {
            var results = await _dbContext.TestRunResults
                .Include(r => r.TestCase)
                .Where(r => r.RunId == id)
                .OrderBy(r => r.Status == TestResultStatus.Fail ? 0 : 1)  // Failed tests first
                .ThenBy(r => r.TestCase!.ExternalId)
                .ToListAsync();

            var responses = results.Select(r => new TestRunResultResponse
            {
                Id = r.Id,
                RunId = r.RunId,
                TestCaseId = r.TestCaseId,
                Status = r.Status,
                TraceJson = r.TraceJson,
                MetricsJson = r.MetricsJson,
                FailureReasonsJson = r.FailureReasonsJson,
                CreatedAt = r.CreatedAt,
                TestCaseName = r.TestCase?.Name,
                TestCaseExternalId = r.TestCase?.ExternalId
            }).ToList();

            return Ok(responses);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get results for run {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Get a specific test run result by ID
    /// </summary>
    [HttpGet("runs/{runId:guid}/results/{resultId:guid}")]
    public async Task<IActionResult> GetRunResult(Guid runId, Guid resultId)
    {
        try
        {
            var result = await _dbContext.TestRunResults
                .Include(r => r.TestCase)
                .FirstOrDefaultAsync(r => r.Id == resultId && r.RunId == runId);

            if (result == null)
            {
                return NotFound(new { message = "Test result not found" });
            }

            var response = new TestRunResultResponse
            {
                Id = result.Id,
                RunId = result.RunId,
                TestCaseId = result.TestCaseId,
                Status = result.Status,
                TraceJson = result.TraceJson,
                MetricsJson = result.MetricsJson,
                FailureReasonsJson = result.FailureReasonsJson,
                CreatedAt = result.CreatedAt,
                TestCaseName = result.TestCase?.Name,
                TestCaseExternalId = result.TestCase?.ExternalId
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get result {ResultId} for run {RunId}", resultId, runId);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }
}
