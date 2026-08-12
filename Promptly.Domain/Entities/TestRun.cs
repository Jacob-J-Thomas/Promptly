using Promptly.Domain.Enums;

namespace Promptly.Domain.Entities;

public class TestRun
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public Guid SuiteId { get; set; }
    public Guid EnvironmentId { get; set; }
    public Guid EndpointId { get; set; }
    public Guid MappingSpecId { get; set; }
    public TestRunStatus Status { get; set; } = TestRunStatus.Queued;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public required string CreatedByUserId { get; set; }
    public string? GitCommitHash { get; set; }
    public string? ConfigSnapshotJson { get; set; }
    public string? SummaryJson { get; set; }
    public string? ErrorMessage { get; set; }

    public TestSuite? Suite { get; set; }
    public Environment? Environment { get; set; }
    public Endpoint? Endpoint { get; set; }
    public MappingSpec? MappingSpec { get; set; }
    public User? CreatedBy { get; set; }
    public ICollection<TestRunResult> Results { get; set; } = new List<TestRunResult>();
}
