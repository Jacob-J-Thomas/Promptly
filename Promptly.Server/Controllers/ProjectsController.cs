using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Promptly.Application.Interfaces;
using Promptly.Server.Models;

namespace Promptly.Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProjectsController : ControllerBase
{
    private readonly IProjectService _projectService;
    private readonly ILogger<ProjectsController> _logger;

    public ProjectsController(IProjectService projectService, ILogger<ProjectsController> logger)
    {
        _projectService = projectService;
        _logger = logger;
    }

    private string GetUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>
    /// Get all projects for the authenticated user
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<ProjectResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetProjects()
    {
        var userId = GetUserId();
        var projects = await _projectService.GetProjectsByUserAsync(userId);

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
        var userId = GetUserId();
        var project = await _projectService.GetProjectByIdAsync(projectId, userId);

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
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userId = GetUserId();
        var project = await _projectService.CreateProjectAsync(userId, request.Name, request.Description);

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

        var userId = GetUserId();
        var project = await _projectService.UpdateProjectAsync(projectId, userId, request.Name, request.Description);

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
        var userId = GetUserId();
        var deleted = await _projectService.DeleteProjectAsync(projectId, userId);

        if (!deleted)
        {
            return NotFound(new { message = "Project not found" });
        }

        return NoContent();
    }
}
