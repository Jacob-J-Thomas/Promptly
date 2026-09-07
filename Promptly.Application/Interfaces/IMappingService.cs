using Promptly.Domain.Entities;
using Promptly.Domain.ValueObjects;
using Promptly.Application.Models;

namespace Promptly.Application.Interfaces;

public interface IMappingService
{
    Task<MappingResult> ApplyMappingAsync(string mappingSpecJson, string responseJson);
    Task<MappingResult> ValidateMappingAsync(string mappingSpecJson, string sampleResponseJson);

    // MappingSpec CRUD operations
    Task<MappingSpec?> SaveMappingSpecAsync(
        Guid endpointId,
        string name,
        string specJson,
        TenantAccessScope scope);
    Task<List<MappingSpec>?> GetMappingSpecsByEndpointAsync(
        Guid endpointId,
        TenantAccessScope scope);
    Task<MappingSpec?> GetMappingSpecByIdAsync(Guid id, TenantAccessScope scope);
    Task<MappingSpec?> GetDefaultMappingAsync(Guid endpointId, TenantAccessScope scope);
    Task<MappingSpec?> UpdateMappingSpecAsync(
        Guid id,
        string name,
        string specJson,
        TenantAccessScope scope);
    Task<bool> SetDefaultMappingAsync(Guid id, TenantAccessScope scope);
    Task<bool> DeleteMappingSpecAsync(Guid id, TenantAccessScope scope);
}

public record MappingResult
{
    public bool Success { get; init; }
    public CanonicalTrace? Trace { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorPath { get; init; }
}
