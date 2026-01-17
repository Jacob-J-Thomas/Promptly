namespace Promptly.Domain.Entities;

public class TestSuite
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Project? Project { get; set; }
    public ICollection<TestCase> TestCases { get; set; } = new List<TestCase>();
    public ICollection<TestRun> TestRuns { get; set; } = new List<TestRun>();
}
