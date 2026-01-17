using Promptly.Domain.Enums;

namespace Promptly.Application.Models;

// Test Run models
public record QueueRunRequest
{
    public Guid SuiteId { get; init; }
    public Guid EnvironmentId { get; init; }
    public Guid EndpointId { get; init; }
    public Guid MappingSpecId { get; init; }
    public string? GitCommitHash { get; init; }
    public string? ConfigSnapshotJson { get; init; }
}

public record TestRunResponse
{
    public Guid Id { get; init; }
    public Guid SuiteId { get; init; }
    public Guid EnvironmentId { get; init; }
    public Guid EndpointId { get; init; }
    public Guid MappingSpecId { get; init; }
    public TestRunStatus Status { get; init; }
    public string? SummaryJson { get; init; }
    public string? GitCommitHash { get; init; }
    public string? ConfigSnapshotJson { get; init; }
    public string? CreatedByUserId { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

public record TestRunResultResponse
{
    public Guid Id { get; init; }
    public Guid RunId { get; init; }
    public Guid TestCaseId { get; init; }
    public TestResultStatus Status { get; init; }
    public string? TraceJson { get; init; }
    public string? MetricsJson { get; init; }
    public string? FailureReasonsJson { get; init; }
    public DateTime CreatedAt { get; init; }

    // Include test case details for convenience
    public string? TestCaseName { get; init; }
    public string? TestCaseExternalId { get; init; }
}
