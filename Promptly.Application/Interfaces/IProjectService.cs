using Promptly.Application.Models;
using Promptly.Domain.Entities;

namespace Promptly.Application.Interfaces;

public interface IProjectService
{
    Task<IReadOnlyList<Project>> GetProjectsAsync(TenantAccessScope scope);
    Task<Project?> GetProjectByIdAsync(Guid projectId, TenantAccessScope scope);
    Task<Project?> CreateProjectAsync(string name, string? description, TenantAccessScope scope);
    Task<Project?> UpdateProjectAsync(
        Guid projectId,
        string name,
        string? description,
        TenantAccessScope scope);
    Task<bool> DeleteProjectAsync(Guid projectId, TenantAccessScope scope);
}
