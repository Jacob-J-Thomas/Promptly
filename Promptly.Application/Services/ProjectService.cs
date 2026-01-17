using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using Promptly.Application.Data;

namespace Promptly.Application.Services;

public class ProjectService : IProjectService
{
    private readonly PromptlyDbContext _dbContext;
    private readonly ILogger<ProjectService> _logger;

    public ProjectService(PromptlyDbContext dbContext, ILogger<ProjectService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<IEnumerable<Project>> GetProjectsByUserAsync(string userId)
    {
        return await _dbContext.Projects
            .Where(p => p.OwnerUserId == userId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync();
    }

    public async Task<Project?> GetProjectByIdAsync(Guid projectId, string userId)
    {
        var project = await _dbContext.Projects
            .FirstOrDefaultAsync(p => p.Id == projectId && p.OwnerUserId == userId);

        return project;
    }

    public async Task<Project> CreateProjectAsync(string userId, string name, string? description)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            OwnerUserId = userId,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Project {ProjectId} created by user {UserId}", project.Id, userId);

        return project;
    }

    public async Task<Project?> UpdateProjectAsync(Guid projectId, string userId, string name, string? description)
    {
        var project = await GetProjectByIdAsync(projectId, userId);
        if (project == null)
        {
            return null;
        }

        project.Name = name;
        project.Description = description;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Project {ProjectId} updated by user {UserId}", projectId, userId);

        return project;
    }

    public async Task<bool> DeleteProjectAsync(Guid projectId, string userId)
    {
        var project = await GetProjectByIdAsync(projectId, userId);
        if (project == null)
        {
            return false;
        }

        _dbContext.Projects.Remove(project);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Project {ProjectId} deleted by user {UserId}", projectId, userId);

        return true;
    }
}
