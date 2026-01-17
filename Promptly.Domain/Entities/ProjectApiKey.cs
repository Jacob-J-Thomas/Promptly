namespace Promptly.Domain.Entities;

public class ProjectApiKey
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public required string KeyHashSha256 { get; set; }
    public required string KeyLastFourChars { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }

    public Project? Project { get; set; }
}
