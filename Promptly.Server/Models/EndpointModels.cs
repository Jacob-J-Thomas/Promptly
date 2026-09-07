using System.ComponentModel.DataAnnotations;
using Promptly.Application.Models;

namespace Promptly.Server.Models;

public record CreateEndpointRequest
{
    [Required]
    public required string Name { get; init; }

    [Required]
    [EndpointTarget]
    public required string Path { get; init; }

    public string HttpMethod { get; init; } = "POST";

    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 30;
}

public record UpdateEndpointRequest
{
    [Required]
    public required string Name { get; init; }

    [Required]
    [EndpointTarget]
    public required string Path { get; init; }

    public string HttpMethod { get; init; } = "POST";

    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 30;
}

public record EndpointResponse
{
    public required Guid Id { get; init; }
    public required Guid EnvironmentId { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string HttpMethod { get; init; }
    public required int TimeoutSeconds { get; init; }
}
