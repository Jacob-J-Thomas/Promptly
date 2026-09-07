using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;

namespace Promptly.Application.Services;

public sealed class TestRunWorkerStore : ITestRunWorkerStore
{
    private readonly PromptlyDbContext _dbContext;
    private readonly ILogger<TestRunWorkerStore> _logger;

    public TestRunWorkerStore(
        PromptlyDbContext dbContext,
        ILogger<TestRunWorkerStore> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<TestRun?> GetRunByIdAsync(Guid runId)
    {
        return await _dbContext.TestRuns
            .Include(run => run.Suite)
            .Include(run => run.Environment)
            .Include(run => run.Endpoint)
            .Include(run => run.MappingSpec)
            .FirstOrDefaultAsync(run => run.Id == runId);
    }

    public async Task<TestRun?> ClaimNextQueuedRunAsync()
    {
        using var transaction = await _dbContext.Database.BeginTransactionAsync();

        try
        {
            var run = await _dbContext.TestRuns
                .Where(candidate => candidate.Status == TestRunStatus.Queued)
                .OrderBy(candidate => candidate.CreatedAt)
                .FirstOrDefaultAsync();

            if (run == null)
            {
                await transaction.CommitAsync();
                return null;
            }

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

    public async Task UpdateRunStatusAsync(
        Guid runId,
        TestRunStatus status,
        string? summaryJson = null,
        string? errorMessage = null)
    {
        var run = await _dbContext.TestRuns.FindAsync(runId);
        if (run == null)
        {
            throw new InvalidOperationException($"Test run with ID {runId} not found");
        }

        run.Status = status;
        run.SummaryJson = summaryJson;
        run.ErrorMessage = errorMessage;

        if (status is TestRunStatus.Completed or TestRunStatus.Failed)
        {
            run.CompletedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync();
        _logger.LogInformation("Updated test run {RunId} status to {Status}", runId, status);
    }
}
