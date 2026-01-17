using Promptly.Domain.Entities;
using Promptly.Domain.Enums;

namespace Promptly.Application.Interfaces;

public interface ITestRunService
{
    /// <summary>
    /// Queue a new test run
    /// </summary>
    Task<TestRun> QueueRunAsync(
        Guid suiteId,
        Guid environmentId,
        Guid endpointId,
        Guid mappingSpecId,
        string createdByUserId,
        string? gitCommitHash = null,
        string? configSnapshotJson = null);

    /// <summary>
    /// Get run by ID
    /// </summary>
    Task<TestRun?> GetRunByIdAsync(Guid runId);

    /// <summary>
    /// Get all runs for a suite with optional filters
    /// </summary>
    Task<List<TestRun>> GetRunsBySuiteAsync(Guid suiteId, TestRunStatus? status = null, int? limit = null);

    /// <summary>
    /// Atomically claim the next queued run for processing
    /// </summary>
    Task<TestRun?> ClaimNextQueuedRunAsync();

    /// <summary>
    /// Update run status and summary
    /// </summary>
    Task UpdateRunStatusAsync(Guid runId, TestRunStatus status, string? summaryJson = null, string? errorMessage = null);
}
