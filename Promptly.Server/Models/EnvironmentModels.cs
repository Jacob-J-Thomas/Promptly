using System.ComponentModel.DataAnnotations;
using Promptly.Application.Models;

namespace Promptly.Server.Models;

public record CreateEnvironmentRequest
{
    [Required]
    public required string Name { get; init; }

    [Required]
    [EnvironmentBaseUrl]
    public required string BaseUrl { get; init; }

    public Dictionary<string, string>? Headers { get; init; }
}

public record UpdateEnvironmentRequest
{
    [Required]
    public required string Name { get; init; }

    [Required]
    [EnvironmentBaseUrl]
    public required string BaseUrl { get; init; }

    public Dictionary<string, string>? Headers { get; init; }
}

public record EnvironmentResponse
{
    public required Guid Id { get; init; }
    public required Guid ProjectId { get; init; }
    public required string Name { get; init; }
    public required string BaseUrl { get; init; }
    public bool HasHeaders { get; init; }
    public required DateTime CreatedAt { get; init; }
}

public record EnvironmentDetailResponse : EnvironmentResponse
{
    public Dictionary<string, string>? Headers { get; init; }
}
