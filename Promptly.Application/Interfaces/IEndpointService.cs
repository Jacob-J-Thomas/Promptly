using Promptly.Application.Models;
using Promptly.Domain.Entities;

namespace Promptly.Application.Interfaces;

public interface IEndpointService
{
    Task<IReadOnlyList<Endpoint>?> GetEndpointsByEnvironmentAsync(
        Guid environmentId,
        TenantAccessScope scope);
    Task<Endpoint?> GetEndpointByIdAsync(Guid endpointId, TenantAccessScope scope);
    Task<Endpoint?> CreateEndpointAsync(
        Guid environmentId,
        string name,
        string path,
        string httpMethod,
        int timeoutSeconds,
        TenantAccessScope scope);
    Task<Endpoint?> UpdateEndpointAsync(
        Guid endpointId,
        string name,
        string path,
        string httpMethod,
        int timeoutSeconds,
        TenantAccessScope scope);
    Task<bool> DeleteEndpointAsync(Guid endpointId, TenantAccessScope scope);
}
