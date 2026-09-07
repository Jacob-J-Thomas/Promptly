using Promptly.Domain.Entities;
using Promptly.Domain.Enums;

namespace Promptly.Application.Interfaces;

/// <summary>
/// Persistence operations reserved for the trusted background run processor.
/// HTTP controllers must use <see cref="ITestRunService"/> instead.
/// </summary>
public interface ITestRunWorkerStore
{
    Task<TestRun?> GetRunByIdAsync(Guid runId);

    Task<TestRun?> ClaimNextQueuedRunAsync();

    Task UpdateRunStatusAsync(
        Guid runId,
        TestRunStatus status,
        string? summaryJson = null,
        string? errorMessage = null);
}
