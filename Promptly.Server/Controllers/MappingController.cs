using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;

namespace Promptly.Server.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public class MappingController : ControllerBase
{
    private readonly IMappingService _mappingService;
    private readonly IEndpointService _endpointService;
    private readonly IPythonEvalClient _pythonEvalClient;
    private readonly ILogger<MappingController> _logger;

    public MappingController(
        IMappingService mappingService,
        IEndpointService endpointService,
        IPythonEvalClient pythonEvalClient,
        ILogger<MappingController> logger)
    {
        _mappingService = mappingService;
        _endpointService = endpointService;
        _pythonEvalClient = pythonEvalClient;
        _logger = logger;
    }

    /// <summary>
    /// Propose a mapping spec using the Python worker's LLM-based analysis
    /// </summary>
    [HttpPost("endpoints/{endpointId:guid}/mapping/propose")]
    public async Task<IActionResult> ProposeMapping(Guid endpointId, [FromBody] ProposeMappingRequest request)
    {
        try
        {
            // Verify endpoint exists
            var endpoint = await _endpointService.GetEndpointByIdAsync(endpointId);
            if (endpoint == null)
            {
                return NotFound(new { message = "Endpoint not found" });
            }

            // Call Python worker
            var result = await _pythonEvalClient.ProposeMappingAsync(
                request.SampleResponseJson,
                request.SampleRequestJson,
                request.Hints);

            if (!result.Success)
            {
                return BadRequest(new { message = result.ErrorMessage });
            }

            return Ok(new ProposeMappingResponse
            {
                MappingSpecJson = result.MappingSpecJson!,
                Reason = result.Reason
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to propose mapping for endpoint {EndpointId}", endpointId);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Validate a mapping spec against sample response JSON
    /// </summary>
    [HttpPost("endpoints/{endpointId:guid}/mapping/validate")]
    public async Task<IActionResult> ValidateMapping(Guid endpointId, [FromBody] ValidateMappingRequest request)
    {
        try
        {
            // Verify endpoint exists
            var endpoint = await _endpointService.GetEndpointByIdAsync(endpointId);
            if (endpoint == null)
            {
                return NotFound(new { message = "Endpoint not found" });
            }

            // Validate mapping
            var result = await _mappingService.ValidateMappingAsync(
                request.MappingSpecJson,
                request.SampleResponseJson);

            return Ok(new ValidateMappingResponse
            {
                Success = result.Success,
                PreviewTrace = result.Trace,
                ErrorMessage = result.ErrorMessage
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate mapping for endpoint {EndpointId}", endpointId);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Save a new mapping spec for an endpoint
    /// </summary>
    [HttpPost("endpoints/{endpointId:guid}/mapping")]
    public async Task<IActionResult> CreateMappingSpec(Guid endpointId, [FromBody] CreateMappingSpecRequest request)
    {
        try
        {
            // Verify endpoint exists
            var endpoint = await _endpointService.GetEndpointByIdAsync(endpointId);
            if (endpoint == null)
            {
                return NotFound(new { message = "Endpoint not found" });
            }

            // Save mapping spec
            var mappingSpec = await _mappingService.SaveMappingSpecAsync(
                endpointId,
                request.Name,
                request.SpecJson);

            var response = new MappingSpecResponse
            {
                Id = mappingSpec.Id,
                EndpointId = mappingSpec.EndpointId,
                Name = mappingSpec.Name,
                SpecJson = mappingSpec.SpecJson,
                IsDefault = mappingSpec.IsDefault,
                CreatedAt = mappingSpec.CreatedAt,
                UpdatedAt = mappingSpec.UpdatedAt
            };

            return CreatedAtAction(nameof(GetMappingSpec), new { id = mappingSpec.Id }, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create mapping spec for endpoint {EndpointId}", endpointId);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Get all mapping specs for an endpoint
    /// </summary>
    [HttpGet("endpoints/{endpointId:guid}/mapping")]
    public async Task<IActionResult> GetMappingSpecs(Guid endpointId)
    {
        try
        {
            // Verify endpoint exists
            var endpoint = await _endpointService.GetEndpointByIdAsync(endpointId);
            if (endpoint == null)
            {
                return NotFound(new { message = "Endpoint not found" });
            }

            var mappingSpecs = await _mappingService.GetMappingSpecsByEndpointAsync(endpointId);

            var responses = mappingSpecs.Select(m => new MappingSpecResponse
            {
                Id = m.Id,
                EndpointId = m.EndpointId,
                Name = m.Name,
                SpecJson = m.SpecJson,
                IsDefault = m.IsDefault,
                CreatedAt = m.CreatedAt,
                UpdatedAt = m.UpdatedAt
            }).ToList();

            return Ok(responses);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get mapping specs for endpoint {EndpointId}", endpointId);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Get a specific mapping spec by ID
    /// </summary>
    [HttpGet("mapping/{id:guid}")]
    public async Task<IActionResult> GetMappingSpec(Guid id)
    {
        try
        {
            var mappingSpec = await _mappingService.GetMappingSpecByIdAsync(id);
            if (mappingSpec == null)
            {
                return NotFound(new { message = "Mapping spec not found" });
            }

            var response = new MappingSpecResponse
            {
                Id = mappingSpec.Id,
                EndpointId = mappingSpec.EndpointId,
                Name = mappingSpec.Name,
                SpecJson = mappingSpec.SpecJson,
                IsDefault = mappingSpec.IsDefault,
                CreatedAt = mappingSpec.CreatedAt,
                UpdatedAt = mappingSpec.UpdatedAt
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get mapping spec {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Update a mapping spec
    /// </summary>
    [HttpPut("mapping/{id:guid}")]
    public async Task<IActionResult> UpdateMappingSpec(Guid id, [FromBody] UpdateMappingSpecRequest request)
    {
        try
        {
            var mappingSpec = await _mappingService.UpdateMappingSpecAsync(
                id,
                request.Name,
                request.SpecJson);

            var response = new MappingSpecResponse
            {
                Id = mappingSpec.Id,
                EndpointId = mappingSpec.EndpointId,
                Name = mappingSpec.Name,
                SpecJson = mappingSpec.SpecJson,
                IsDefault = mappingSpec.IsDefault,
                CreatedAt = mappingSpec.CreatedAt,
                UpdatedAt = mappingSpec.UpdatedAt
            };

            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update mapping spec {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Set a mapping spec as the default for its endpoint
    /// </summary>
    [HttpPost("mapping/{id:guid}/set-default")]
    public async Task<IActionResult> SetDefaultMapping(Guid id)
    {
        try
        {
            await _mappingService.SetDefaultMappingAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to set default mapping {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Delete a mapping spec
    /// </summary>
    [HttpDelete("mapping/{id:guid}")]
    public async Task<IActionResult> DeleteMappingSpec(Guid id)
    {
        try
        {
            await _mappingService.DeleteMappingSpecAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete mapping spec {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }
}
