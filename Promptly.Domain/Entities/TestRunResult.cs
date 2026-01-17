using Promptly.Domain.Enums;

namespace Promptly.Domain.Entities;

public class TestRunResult
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid TestCaseId { get; set; }
    public TestResultStatus Status { get; set; }
    public string? MetricsJson { get; set; }
    public string? FailureReasonsJson { get; set; }
    public required string TraceJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TestRun? TestRun { get; set; }
    public TestCase? TestCase { get; set; }
}
