using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Promptly.Application.Interfaces;
using Promptly.Server.Models;

namespace Promptly.Server.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class EndpointsController : ControllerBase
{
    private readonly IEndpointService _endpointService;
    private readonly ILogger<EndpointsController> _logger;

    public EndpointsController(IEndpointService endpointService, ILogger<EndpointsController> logger)
    {
        _endpointService = endpointService;
        _logger = logger;
    }

    /// <summary>
    /// Get all endpoints for an environment
    /// </summary>
    [HttpGet("environments/{environmentId}/endpoints")]
    [ProducesResponseType(typeof(IEnumerable<EndpointResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEndpoints(Guid environmentId)
    {
        var endpoints = await _endpointService.GetEndpointsByEnvironmentAsync(environmentId);

        var response = endpoints.Select(e => new EndpointResponse
        {
            Id = e.Id,
            EnvironmentId = e.EnvironmentId,
            Name = e.Name,
            Path = e.Path,
            HttpMethod = e.HttpMethod,
            TimeoutSeconds = e.TimeoutSeconds
        });

        return Ok(response);
    }

    /// <summary>
    /// Get a specific endpoint by ID
    /// </summary>
    [HttpGet("endpoints/{endpointId}")]
    [ProducesResponseType(typeof(EndpointResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEndpoint(Guid endpointId)
    {
        var endpoint = await _endpointService.GetEndpointByIdAsync(endpointId);

        if (endpoint == null)
        {
            return NotFound(new { message = "Endpoint not found" });
        }

        var response = new EndpointResponse
        {
            Id = endpoint.Id,
            EnvironmentId = endpoint.EnvironmentId,
            Name = endpoint.Name,
            Path = endpoint.Path,
            HttpMethod = endpoint.HttpMethod,
            TimeoutSeconds = endpoint.TimeoutSeconds
        };

        return Ok(response);
    }

    /// <summary>
    /// Create a new endpoint
    /// </summary>
    [HttpPost("environments/{environmentId}/endpoints")]
    [ProducesResponseType(typeof(EndpointResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateEndpoint(Guid environmentId, [FromBody] CreateEndpointRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var endpoint = await _endpointService.CreateEndpointAsync(
            environmentId,
            request.Name,
            request.Path,
            request.HttpMethod,
            request.TimeoutSeconds);

        var response = new EndpointResponse
        {
            Id = endpoint.Id,
            EnvironmentId = endpoint.EnvironmentId,
            Name = endpoint.Name,
            Path = endpoint.Path,
            HttpMethod = endpoint.HttpMethod,
            TimeoutSeconds = endpoint.TimeoutSeconds
        };

        return CreatedAtAction(nameof(GetEndpoint), new { endpointId = endpoint.Id }, response);
    }

    /// <summary>
    /// Update an existing endpoint
    /// </summary>
    [HttpPut("endpoints/{endpointId}")]
    [ProducesResponseType(typeof(EndpointResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateEndpoint(Guid endpointId, [FromBody] UpdateEndpointRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var endpoint = await _endpointService.UpdateEndpointAsync(
            endpointId,
            request.Name,
            request.Path,
            request.HttpMethod,
            request.TimeoutSeconds);

        if (endpoint == null)
        {
            return NotFound(new { message = "Endpoint not found" });
        }

        var response = new EndpointResponse
        {
            Id = endpoint.Id,
            EnvironmentId = endpoint.EnvironmentId,
            Name = endpoint.Name,
            Path = endpoint.Path,
            HttpMethod = endpoint.HttpMethod,
            TimeoutSeconds = endpoint.TimeoutSeconds
        };

        return Ok(response);
    }

    /// <summary>
    /// Delete an endpoint
    /// </summary>
    [HttpDelete("endpoints/{endpointId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteEndpoint(Guid endpointId)
    {
        var deleted = await _endpointService.DeleteEndpointAsync(endpointId);

        if (!deleted)
        {
            return NotFound(new { message = "Endpoint not found" });
        }

        return NoContent();
    }
}
