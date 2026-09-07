using Promptly.Domain.Entities;
using Promptly.Domain.ValueObjects;

namespace Promptly.Application.Interfaces;

public interface IMappingService
{
    Task<MappingResult> ApplyMappingAsync(string mappingSpecJson, string responseJson);
    Task<MappingResult> ValidateMappingAsync(string mappingSpecJson, string sampleResponseJson);

    // MappingSpec CRUD operations
    Task<MappingSpec> SaveMappingSpecAsync(Guid endpointId, string name, string specJson);
    Task<List<MappingSpec>> GetMappingSpecsByEndpointAsync(Guid endpointId);
    Task<MappingSpec?> GetMappingSpecByIdAsync(Guid id);
    Task<MappingSpec?> GetDefaultMappingAsync(Guid endpointId);
    Task<MappingSpec> UpdateMappingSpecAsync(Guid id, string name, string specJson);
    Task SetDefaultMappingAsync(Guid id);
    Task DeleteMappingSpecAsync(Guid id);
}

public record MappingResult
{
    public bool Success { get; init; }
    public CanonicalTrace? Trace { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorPath { get; init; }
}
