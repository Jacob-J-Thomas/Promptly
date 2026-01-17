using Promptly.Domain.Entities;
using Environment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.Interfaces;

public interface IEnvironmentService
{
    Task<IEnumerable<Environment>> GetEnvironmentsByProjectAsync(Guid projectId);
    Task<Environment?> GetEnvironmentByIdAsync(Guid environmentId);
    Task<Environment> CreateEnvironmentAsync(Guid projectId, string name, string baseUrl, Dictionary<string, string>? headers);
    Task<Environment?> UpdateEnvironmentAsync(Guid environmentId, string name, string baseUrl, Dictionary<string, string>? headers);
    Task<bool> DeleteEnvironmentAsync(Guid environmentId);
    Task<Dictionary<string, string>?> GetDecryptedHeadersAsync(Guid environmentId);
}
