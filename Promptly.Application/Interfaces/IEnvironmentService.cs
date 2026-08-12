using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Environment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.Interfaces;

public interface IEnvironmentService
{
    Task<IReadOnlyList<Environment>?> GetEnvironmentsByProjectAsync(
        Guid projectId,
        TenantAccessScope scope);
    Task<Environment?> GetEnvironmentByIdAsync(Guid environmentId, TenantAccessScope scope);
    Task<Environment?> CreateEnvironmentAsync(
        Guid projectId,
        string name,
        string baseUrl,
        Dictionary<string, string>? headers,
        TenantAccessScope scope);
    Task<Environment?> UpdateEnvironmentAsync(
        Guid environmentId,
        string name,
        string baseUrl,
        Dictionary<string, string>? headers,
        TenantAccessScope scope);
    Task<bool> DeleteEnvironmentAsync(Guid environmentId, TenantAccessScope scope);
    Task<Dictionary<string, string>?> GetDecryptedHeadersAsync(
        Guid environmentId,
        TenantAccessScope scope);
}
