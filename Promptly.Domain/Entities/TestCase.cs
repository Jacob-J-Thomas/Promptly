namespace Promptly.Domain.Entities;

public class TestCase
{
    public Guid Id { get; set; }
    public Guid SuiteId { get; set; }
    public required string ExternalId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string InputSpecJson { get; set; }
    public required string ExpectationsJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public TestSuite? Suite { get; set; }
    public ICollection<TestRunResult> TestRunResults { get; set; } = new List<TestRunResult>();
}
