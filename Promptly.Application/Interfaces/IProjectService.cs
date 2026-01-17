using Promptly.Domain.Entities;

namespace Promptly.Application.Interfaces;

public interface IProjectService
{
    Task<IEnumerable<Project>> GetProjectsByUserAsync(string userId);
    Task<Project?> GetProjectByIdAsync(Guid projectId, string userId);
    Task<Project> CreateProjectAsync(string userId, string name, string? description);
    Task<Project?> UpdateProjectAsync(Guid projectId, string userId, string name, string? description);
    Task<bool> DeleteProjectAsync(Guid projectId, string userId);
}
