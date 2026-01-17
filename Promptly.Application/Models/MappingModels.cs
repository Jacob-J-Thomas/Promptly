using Promptly.Domain.ValueObjects;

namespace Promptly.Application.Models;

// Request/Response models for Mapping API

public record ProposeMappingRequest
{
    public required string SampleResponseJson { get; init; }
    public string? SampleRequestJson { get; init; }
    public Dictionary<string, object>? Hints { get; init; }
}

public record ProposeMappingResponse
{
    public required string MappingSpecJson { get; init; }
    public string? Reason { get; init; }
}

public record ValidateMappingRequest
{
    public required string MappingSpecJson { get; init; }
    public required string SampleResponseJson { get; init; }
}

public record ValidateMappingResponse
{
    public bool Success { get; init; }
    public CanonicalTrace? PreviewTrace { get; init; }
    public string? ErrorMessage { get; init; }
}

public record CreateMappingSpecRequest
{
    public required string Name { get; init; }
    public required string SpecJson { get; init; }
}

public record UpdateMappingSpecRequest
{
    public required string Name { get; init; }
    public required string SpecJson { get; init; }
}

public record MappingSpecResponse
{
    public Guid Id { get; init; }
    public Guid EndpointId { get; init; }
    public required string Name { get; init; }
    public required string SpecJson { get; init; }
    public bool IsDefault { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
