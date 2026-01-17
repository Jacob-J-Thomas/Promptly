namespace Promptly.Domain.Entities;

public class Environment
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public required string BaseUrl { get; set; }
    public string? DefaultHeadersEncryptedJson { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Project? Project { get; set; }
    public ICollection<Endpoint> Endpoints { get; set; } = new List<Endpoint>();
}
