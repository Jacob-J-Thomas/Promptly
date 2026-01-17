namespace Promptly.Domain.Entities;

public class Project
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string OwnerUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User? Owner { get; set; }
    public ICollection<Environment> Environments { get; set; } = new List<Environment>();
    public ICollection<TestSuite> TestSuites { get; set; } = new List<TestSuite>();
    public ICollection<ProjectApiKey> ApiKeys { get; set; } = new List<ProjectApiKey>();
    public ProjectSettings? Settings { get; set; }
}
