using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;
using Promptly.Application.Data;

namespace Promptly.Application.Services;

public class TestRunService : ITestRunService
{
    private readonly PromptlyDbContext _dbContext;
    private readonly ILogger<TestRunService> _logger;

    public TestRunService(PromptlyDbContext dbContext, ILogger<TestRunService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<TestRun> QueueRunAsync(
        Guid suiteId,
        Guid environmentId,
        Guid endpointId,
        Guid mappingSpecId,
        string createdByUserId,
        string? gitCommitHash = null,
        string? configSnapshotJson = null)
    {
        var testRun = new TestRun
        {
            Id = Guid.NewGuid(),
            SuiteId = suiteId,
            EnvironmentId = environmentId,
            EndpointId = endpointId,
            MappingSpecId = mappingSpecId,
            CreatedByUserId = createdByUserId,
            Status = TestRunStatus.Queued,
            GitCommitHash = gitCommitHash,
            ConfigSnapshotJson = configSnapshotJson,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.TestRuns.Add(testRun);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Queued test run {RunId} for suite {SuiteId}", testRun.Id, suiteId);

        return testRun;
    }

    public async Task<TestRun?> GetRunByIdAsync(Guid runId)
    {
        return await _dbContext.TestRuns
            .Include(r => r.Suite)
            .Include(r => r.Environment)
            .Include(r => r.Endpoint)
            .Include(r => r.MappingSpec)
            .FirstOrDefaultAsync(r => r.Id == runId);
    }

    public async Task<List<TestRun>> GetRunsBySuiteAsync(Guid suiteId, TestRunStatus? status = null, int? limit = null)
    {
        var query = _dbContext.TestRuns
            .Where(r => r.SuiteId == suiteId);

        if (status.HasValue)
        {
            query = query.Where(r => r.Status == status.Value);
        }

        query = query.OrderByDescending(r => r.CreatedAt);

        if (limit.HasValue)
        {
            query = query.Take(limit.Value);
        }

        return await query.ToListAsync();
    }

    public async Task<TestRun?> ClaimNextQueuedRunAsync()
    {
        // Use a transaction to ensure atomicity
        using var transaction = await _dbContext.Database.BeginTransactionAsync();

        try
        {
            // Find the oldest queued run
            var run = await _dbContext.TestRuns
                .Where(r => r.Status == TestRunStatus.Queued)
                .OrderBy(r => r.CreatedAt)
                .FirstOrDefaultAsync();

            if (run == null)
            {
                await transaction.CommitAsync();
                return null;
            }

            // Mark as Running
            run.Status = TestRunStatus.Running;
            run.StartedAt = DateTime.UtcNow;

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            _logger.LogInformation("Claimed test run {RunId} for processing", run.Id);

            return run;
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to claim queued run");
            throw;
        }
    }

    public async Task UpdateRunStatusAsync(Guid runId, TestRunStatus status, string? summaryJson = null, string? errorMessage = null)
    {
        var run = await _dbContext.TestRuns.FindAsync(runId);
        if (run == null)
        {
            throw new InvalidOperationException($"Test run with ID {runId} not found");
        }

        run.Status = status;
        run.SummaryJson = summaryJson;
        run.ErrorMessage = errorMessage;

        if (status == TestRunStatus.Completed || status == TestRunStatus.Failed)
        {
            run.CompletedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated test run {RunId} status to {Status}", runId, status);
    }
}
