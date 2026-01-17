using System.ComponentModel.DataAnnotations;

namespace Promptly.Server.Models;

public record CreateProjectRequest
{
    [Required]
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public record UpdateProjectRequest
{
    [Required]
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public record ProjectResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string OwnerUserId { get; init; }
    public required DateTime CreatedAt { get; init; }
}
