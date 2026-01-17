namespace Promptly.Domain.Entities;

public class MappingSpec
{
    public Guid Id { get; set; }
    public Guid EndpointId { get; set; }
    public required string Name { get; set; }
    public required string SpecJson { get; set; }
    public bool IsDefault { get; set; } = false;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public Endpoint? Endpoint { get; set; }
}
