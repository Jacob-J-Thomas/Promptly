using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;

namespace Promptly.Application.Services;

public sealed class TestRunWorkerStore : ITestRunWorkerStore
{
    private const string InvalidExecutionGraphError =
        "Run failed resource-graph integrity validation before execution";
    private const string UnsafeEndpointTargetError =
        "Run failed endpoint-target validation before execution";

    private readonly PromptlyDbContext _dbContext;
    private readonly ILogger<TestRunWorkerStore> _logger;

    public TestRunWorkerStore(
        PromptlyDbContext dbContext,
        ILogger<TestRunWorkerStore> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<WorkerRunLoadResult> LoadRunForProcessingAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var validation = await ValidateRunForExecutionAsync(runId, cancellationToken);
        if (validation.Status is WorkerRunLoadStatus.Ready or WorkerRunLoadStatus.NotFound)
        {
            return validation;
        }

        var invalidRun = await _dbContext.TestRuns
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);
        // The validation query already proved the row exists for every
        // non-NotFound result. Throwing here avoids silently accepting a race.
        if (invalidRun == null)
        {
            throw new InvalidOperationException(
                $"Test run {runId} disappeared during execution validation");
        }

        MarkExecutionValidationFailed(invalidRun, validation.Status);
        await _dbContext.SaveChangesAsync(cancellationToken);
        _logger.LogWarning(
            "Rejected test run {RunId} during process-time {ValidationStatus} validation",
            runId,
            validation.Status);
        return validation;
    }

    public async Task<Guid?> ClaimNextQueuedRunAsync(
        CancellationToken cancellationToken = default)
    {
        using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var run = await _dbContext.TestRuns
                    .Where(candidate => candidate.Status == TestRunStatus.Queued)
                    .OrderBy(candidate => candidate.CreatedAt)
                    .FirstOrDefaultAsync(cancellationToken);

                if (run == null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return null;
                }

                var validation = await ValidateRunForExecutionAsync(
                    run.Id,
                    cancellationToken);
                if (validation.Status != WorkerRunLoadStatus.Ready)
                {
                    MarkExecutionValidationFailed(run, validation.Status);
                    await _dbContext.SaveChangesAsync(cancellationToken);
                    _logger.LogWarning(
                        "Rejected queued test run {RunId} during claim-time {ValidationStatus} validation",
                        run.Id,
                        validation.Status);
                    continue;
                }

                run.Status = TestRunStatus.Running;
                run.StartedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                _logger.LogInformation("Claimed test run {RunId} for processing", run.Id);
                return run.Id;
            }
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
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

    private async Task<WorkerRunLoadResult> ValidateRunForExecutionAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        var run = await _dbContext.TestRuns
            .AsNoTracking()
            .WhereExecutionGraphIsValid()
            .Include(candidate => candidate.Suite)
            .Include(candidate => candidate.Environment)
            .Include(candidate => candidate.Endpoint)
            .Include(candidate => candidate.MappingSpec)
            .FirstOrDefaultAsync(candidate => candidate.Id == runId, cancellationToken);

        if (run == null)
        {
            var exists = await _dbContext.TestRuns
                .AsNoTracking()
                .AnyAsync(candidate => candidate.Id == runId, cancellationToken);
            return exists
                ? WorkerRunLoadResult.InvalidGraph()
                : WorkerRunLoadResult.NotFound();
        }

        if (!EndpointTargetPolicy.TryResolve(
                run.Environment!.BaseUrl,
                run.Endpoint!.Path,
                out _,
                out _))
        {
            return WorkerRunLoadResult.UnsafeEndpointTarget();
        }

        return WorkerRunLoadResult.Ready(run);
    }

    private static void MarkExecutionValidationFailed(
        TestRun run,
        WorkerRunLoadStatus validationStatus)
    {
        run.Status = TestRunStatus.Failed;
        run.CompletedAt = DateTime.UtcNow;
        run.SummaryJson = null;
        run.ErrorMessage = validationStatus == WorkerRunLoadStatus.UnsafeEndpointTarget
            ? UnsafeEndpointTargetError
            : InvalidExecutionGraphError;
    }
}
