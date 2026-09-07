using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;

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

    public async Task<IReadOnlyList<Project>> GetProjectsAsync(TenantAccessScope scope)
    {
        return await _dbContext.Projects
            .ForTenant(scope)
            .OrderByDescending(project => project.CreatedAt)
            .ToListAsync();
    }

    public async Task<Project?> GetProjectByIdAsync(Guid projectId, TenantAccessScope scope)
    {
        return await _dbContext.Projects
            .ForTenant(scope)
            .FirstOrDefaultAsync(project => project.Id == projectId);
    }

    public async Task<Project?> CreateProjectAsync(
        string name,
        string? description,
        TenantAccessScope scope)
    {
        if (scope.ProjectId is not null)
        {
            return null;
        }

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            OwnerUserId = scope.OwnerUserId,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.Projects.Add(project);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Project {ProjectId} created",
            project.Id);

        return project;
    }

    public async Task<Project?> UpdateProjectAsync(
        Guid projectId,
        string name,
        string? description,
        TenantAccessScope scope)
    {
        var project = await GetProjectByIdAsync(projectId, scope);
        if (project == null)
        {
            return null;
        }

        project.Name = name;
        project.Description = description;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Project {ProjectId} updated",
            projectId);

        return project;
    }

    public async Task<bool> DeleteProjectAsync(Guid projectId, TenantAccessScope scope)
    {
        var project = await GetProjectByIdAsync(projectId, scope);
        if (project == null)
        {
            return false;
        }

        _dbContext.Projects.Remove(project);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation(
            "Project {ProjectId} deleted",
            projectId);

        return true;
    }
}
