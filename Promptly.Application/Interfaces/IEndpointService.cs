using Promptly.Domain.Entities;

namespace Promptly.Application.Interfaces;

public interface IEndpointService
{
    Task<IEnumerable<Endpoint>> GetEndpointsByEnvironmentAsync(Guid environmentId);
    Task<Endpoint?> GetEndpointByIdAsync(Guid endpointId);
    Task<Endpoint> CreateEndpointAsync(Guid environmentId, string name, string path, string httpMethod, int timeoutSeconds);
    Task<Endpoint?> UpdateEndpointAsync(Guid endpointId, string name, string path, string httpMethod, int timeoutSeconds);
    Task<bool> DeleteEndpointAsync(Guid endpointId);
}
