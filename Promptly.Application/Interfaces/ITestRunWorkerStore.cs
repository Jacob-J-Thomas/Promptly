using Promptly.Domain.Entities;
using Promptly.Domain.Enums;

namespace Promptly.Application.Interfaces;

/// <summary>
/// Persistence operations reserved for the trusted background run processor.
/// HTTP controllers must use <see cref="ITestRunService"/> instead.
/// </summary>
public interface ITestRunWorkerStore
{
    Task<WorkerRunLoadResult> LoadRunForProcessingAsync(
        Guid runId,
        CancellationToken cancellationToken = default);

    Task<Guid?> ClaimNextQueuedRunAsync(CancellationToken cancellationToken = default);

    Task UpdateRunStatusAsync(
        Guid runId,
        TestRunStatus status,
        string? summaryJson = null,
        string? errorMessage = null);
}

public enum WorkerRunLoadStatus
{
    Ready,
    NotFound,
    InvalidGraph,
    UnsafeEndpointTarget
}

public sealed record WorkerRunLoadResult
{
    private WorkerRunLoadResult(WorkerRunLoadStatus status, TestRun? run)
    {
        Status = status;
        Run = run;
    }

    public WorkerRunLoadStatus Status { get; }

    public TestRun? Run { get; }

    public static WorkerRunLoadResult Ready(TestRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return new(WorkerRunLoadStatus.Ready, run);
    }

    public static WorkerRunLoadResult NotFound() =>
        new(WorkerRunLoadStatus.NotFound, run: null);

    public static WorkerRunLoadResult InvalidGraph() =>
        new(WorkerRunLoadStatus.InvalidGraph, run: null);

    public static WorkerRunLoadResult UnsafeEndpointTarget() =>
        new(WorkerRunLoadStatus.UnsafeEndpointTarget, run: null);
}
