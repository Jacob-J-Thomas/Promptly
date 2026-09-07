using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Promptly.Application.Interfaces;
using Promptly.Server.Models;
using Promptly.Server.Security;

namespace Promptly.Server.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class EnvironmentsController : ControllerBase
{
    private readonly IEnvironmentService _environmentService;
    private readonly ITenantAccessScopeAccessor _tenantAccessScopeAccessor;
    private readonly ILogger<EnvironmentsController> _logger;

    public EnvironmentsController(
        IEnvironmentService environmentService,
        ITenantAccessScopeAccessor tenantAccessScopeAccessor,
        ILogger<EnvironmentsController> logger)
    {
        _environmentService = environmentService;
        _tenantAccessScopeAccessor = tenantAccessScopeAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Get all environments for a project
    /// </summary>
    [HttpGet("projects/{projectId}/environments")]
    [ProducesResponseType(typeof(IEnumerable<EnvironmentResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEnvironments(Guid projectId)
    {
        if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
        {
            return Unauthorized();
        }

        var environments = await _environmentService.GetEnvironmentsByProjectAsync(
            projectId,
            scope);

        if (environments == null)
        {
            return NotFound(new { message = "Project not found" });
        }

        var response = environments.Select(e => new EnvironmentResponse
        {
            Id = e.Id,
            ProjectId = e.ProjectId,
            Name = e.Name,
            BaseUrl = e.BaseUrl,
            HasHeaders = !string.IsNullOrWhiteSpace(e.DefaultHeadersEncryptedJson),
            CreatedAt = e.CreatedAt
        });

        return Ok(response);
    }

    /// <summary>
    /// Get a specific environment by ID (with decrypted headers)
    /// </summary>
    [HttpGet("environments/{environmentId}")]
    [ProducesResponseType(typeof(EnvironmentDetailResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEnvironment(Guid environmentId)
    {
        if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
        {
            return Unauthorized();
        }

        var environment = await _environmentService.GetEnvironmentByIdAsync(environmentId, scope);

        if (environment == null)
        {
            return NotFound(new { message = "Environment not found" });
        }

        var headers = await _environmentService.GetDecryptedHeadersAsync(environmentId, scope);

        var response = new EnvironmentDetailResponse
        {
            Id = environment.Id,
            ProjectId = environment.ProjectId,
            Name = environment.Name,
            BaseUrl = environment.BaseUrl,
            HasHeaders = !string.IsNullOrWhiteSpace(environment.DefaultHeadersEncryptedJson),
            CreatedAt = environment.CreatedAt,
            Headers = headers
        };

        return Ok(response);
    }

    /// <summary>
    /// Create a new environment
    /// </summary>
    [HttpPost("projects/{projectId}/environments")]
    [ProducesResponseType(typeof(EnvironmentResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateEnvironment(Guid projectId, [FromBody] CreateEnvironmentRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
        {
            return Unauthorized();
        }

        var environment = await _environmentService.CreateEnvironmentAsync(
            projectId,
            request.Name,
            request.BaseUrl,
            request.Headers,
            scope);

        if (environment == null)
        {
            return NotFound(new { message = "Project not found" });
        }

        var response = new EnvironmentResponse
        {
            Id = environment.Id,
            ProjectId = environment.ProjectId,
            Name = environment.Name,
            BaseUrl = environment.BaseUrl,
            HasHeaders = !string.IsNullOrWhiteSpace(environment.DefaultHeadersEncryptedJson),
            CreatedAt = environment.CreatedAt
        };

        return CreatedAtAction(nameof(GetEnvironment), new { environmentId = environment.Id }, response);
    }

    /// <summary>
    /// Update an existing environment
    /// </summary>
    [HttpPut("environments/{environmentId}")]
    [ProducesResponseType(typeof(EnvironmentResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateEnvironment(Guid environmentId, [FromBody] UpdateEnvironmentRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
        {
            return Unauthorized();
        }

        var environment = await _environmentService.UpdateEnvironmentAsync(
            environmentId,
            request.Name,
            request.BaseUrl,
            request.Headers,
            scope);

        if (environment == null)
        {
            return NotFound(new { message = "Environment not found" });
        }

        var response = new EnvironmentResponse
        {
            Id = environment.Id,
            ProjectId = environment.ProjectId,
            Name = environment.Name,
            BaseUrl = environment.BaseUrl,
            HasHeaders = !string.IsNullOrWhiteSpace(environment.DefaultHeadersEncryptedJson),
            CreatedAt = environment.CreatedAt
        };

        return Ok(response);
    }

    /// <summary>
    /// Delete an environment
    /// </summary>
    [HttpDelete("environments/{environmentId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteEnvironment(Guid environmentId)
    {
        if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
        {
            return Unauthorized();
        }

        var deleted = await _environmentService.DeleteEnvironmentAsync(environmentId, scope);

        if (!deleted)
        {
            return NotFound(new { message = "Environment not found" });
        }

        return NoContent();
    }
}
