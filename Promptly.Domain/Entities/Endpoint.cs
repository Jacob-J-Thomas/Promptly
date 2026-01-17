namespace Promptly.Domain.Entities;

public class Endpoint
{
    public Guid Id { get; set; }
    public Guid EnvironmentId { get; set; }
    public required string Name { get; set; }
    public required string Path { get; set; }
    public string HttpMethod { get; set; } = "POST";
    public int TimeoutSeconds { get; set; } = 30;

    public Environment? Environment { get; set; }
    public ICollection<MappingSpec> MappingSpecs { get; set; } = new List<MappingSpec>();
}
