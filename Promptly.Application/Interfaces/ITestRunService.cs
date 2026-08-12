using Promptly.Domain.Entities;
using Promptly.Domain.Enums;
using Promptly.Application.Models;

namespace Promptly.Application.Interfaces;

public interface ITestRunService
{
    /// <summary>
    /// Queue a new test run
    /// </summary>
    Task<TestRun?> QueueRunAsync(
        Guid suiteId,
        Guid environmentId,
        Guid endpointId,
        Guid mappingSpecId,
        string? gitCommitHash,
        string? configSnapshotJson,
        TenantAccessScope scope);

    /// <summary>
    /// Get run by ID
    /// </summary>
    Task<TestRun?> GetRunByIdAsync(Guid runId, TenantAccessScope scope);

    /// <summary>
    /// Get all runs for a suite with optional filters
    /// </summary>
    Task<List<TestRun>?> GetRunsBySuiteAsync(
        Guid suiteId,
        TestRunStatus? status,
        int? limit,
        TenantAccessScope scope);

    /// <summary>
    /// Get all results for an accessible test run
    /// </summary>
    Task<List<TestRunResult>?> GetRunResultsAsync(Guid runId, TenantAccessScope scope);

    /// <summary>
    /// Get an accessible result constrained to its parent run
    /// </summary>
    Task<TestRunResult?> GetRunResultAsync(
        Guid runId,
        Guid resultId,
        TenantAccessScope scope);
}
