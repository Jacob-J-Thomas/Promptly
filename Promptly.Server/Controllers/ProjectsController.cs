using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Promptly.Application.Interfaces;
using Promptly.Server.Models;
using Promptly.Server.Security;

namespace Promptly.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProjectsController : ControllerBase
{
    private readonly IProjectService _projectService;
    private readonly ITenantAccessScopeAccessor _tenantAccessScopeAccessor;
    private readonly ILogger<ProjectsController> _logger;

    public ProjectsController(
        IProjectService projectService,
        ITenantAccessScopeAccessor tenantAccessScopeAccessor,
        ILogger<ProjectsController> logger)
    {
        _projectService = projectService;
        _tenantAccessScopeAccessor = tenantAccessScopeAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Get all projects for the authenticated user
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ProjectResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProjects()
    {
        if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
        {
            return Unauthorized();
        }

        var projects = await _projectService.GetProjectsAsync(scope);

        var response = projects.Select(p => new ProjectResponse
        {
            Id = p.Id,
            Name = p.Name,
            Description = p.Description,
            OwnerUserId = p.OwnerUserId,
            CreatedAt = p.CreatedAt
        });

        return Ok(response);
    }

    /// <summary>
    /// Get a specific project by ID
    /// </summary>
    [HttpGet("{projectId}")]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProject(Guid projectId)
    {
        if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
        {
            return Unauthorized();
        }

        var project = await _projectService.GetProjectByIdAsync(projectId, scope);

        if (project == null)
        {
            return NotFound(new { message = "Project not found" });
        }

        var response = new ProjectResponse
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            OwnerUserId = project.OwnerUserId,
            CreatedAt = project.CreatedAt
        };

        return Ok(response);
    }

    /// <summary>
    /// Create a new project
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateProject([FromBody] CreateProjectRequest request)
    {
        if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
        {
            return Unauthorized();
        }

        if (scope.ProjectId is not null)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var project = await _projectService.CreateProjectAsync(
            request.Name,
            request.Description,
            scope);
        if (project == null)
        {
            return Forbid();
        }

        var response = new ProjectResponse
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            OwnerUserId = project.OwnerUserId,
            CreatedAt = project.CreatedAt
        };

        return CreatedAtAction(nameof(GetProject), new { projectId = project.Id }, response);
    }

    /// <summary>
    /// Update an existing project
    /// </summary>
    [HttpPut("{projectId}")]
    [ProducesResponseType(typeof(ProjectResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateProject(Guid projectId, [FromBody] UpdateProjectRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
        {
            return Unauthorized();
        }

        var project = await _projectService.UpdateProjectAsync(
            projectId,
            request.Name,
            request.Description,
            scope);

        if (project == null)
        {
            return NotFound(new { message = "Project not found" });
        }

        var response = new ProjectResponse
        {
            Id = project.Id,
            Name = project.Name,
            Description = project.Description,
            OwnerUserId = project.OwnerUserId,
            CreatedAt = project.CreatedAt
        };

        return Ok(response);
    }

    /// <summary>
    /// Delete a project
    /// </summary>
    [HttpDelete("{projectId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteProject(Guid projectId)
    {
        if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
        {
            return Unauthorized();
        }

        var deleted = await _projectService.DeleteProjectAsync(projectId, scope);

        if (!deleted)
        {
            return NotFound(new { message = "Project not found" });
        }

        return NoContent();
    }
}
