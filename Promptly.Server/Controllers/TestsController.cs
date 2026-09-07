using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Server.Security;

namespace Promptly.Server.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public class TestsController : ControllerBase
{
    private readonly ITestCaseService _testCaseService;
    private readonly ITenantAccessScopeAccessor _tenantAccessScopeAccessor;
    private readonly ILogger<TestsController> _logger;

    public TestsController(
        ITestCaseService testCaseService,
        ITenantAccessScopeAccessor tenantAccessScopeAccessor,
        ILogger<TestsController> logger)
    {
        _testCaseService = testCaseService;
        _tenantAccessScopeAccessor = tenantAccessScopeAccessor;
        _logger = logger;
    }

    /// <summary>
    /// Create a new test case
    /// </summary>
    [HttpPost("suites/{suiteId:guid}/tests")]
    public async Task<IActionResult> CreateTestCase(Guid suiteId, [FromBody] CreateTestCaseRequest request)
    {
        try
        {
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            var testCase = await _testCaseService.CreateTestCaseAsync(
                suiteId,
                request.ExternalId,
                request.Name,
                request.Description,
                request.InputSpecJson,
                request.ExpectationsJson,
                scope);
            if (testCase == null)
            {
                return NotFound(new { message = "Test suite not found" });
            }

            var response = new TestCaseResponse
            {
                Id = testCase.Id,
                SuiteId = testCase.SuiteId,
                ExternalId = testCase.ExternalId,
                Name = testCase.Name,
                Description = testCase.Description,
                InputSpecJson = testCase.InputSpecJson,
                ExpectationsJson = testCase.ExpectationsJson,
                CreatedAt = testCase.CreatedAt,
            };

            return CreatedAtAction(nameof(GetTestCase), new { id = testCase.Id }, response);
        }
        catch (TestSpecificationValidationException validationException)
        {
            return BadRequest(new
            {
                message = "Invalid test specification",
                errors = validationException.Issues
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create test case for suite {SuiteId}", suiteId);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Get all test cases for a suite
    /// </summary>
    [HttpGet("suites/{suiteId:guid}/tests")]
    public async Task<IActionResult> GetTestCases(Guid suiteId)
    {
        try
        {
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            var testCases = await _testCaseService.GetTestCasesBySuiteAsync(suiteId, scope);
            if (testCases == null)
            {
                return NotFound(new { message = "Test suite not found" });
            }

            var responses = testCases.Select(t => new TestCaseResponse
            {
                Id = t.Id,
                SuiteId = t.SuiteId,
                ExternalId = t.ExternalId,
                Name = t.Name,
                Description = t.Description,
                InputSpecJson = t.InputSpecJson,
                ExpectationsJson = t.ExpectationsJson,
                CreatedAt = t.CreatedAt,
            }).ToList();

            return Ok(responses);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get test cases for suite {SuiteId}", suiteId);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Get a specific test case by ID
    /// </summary>
    [HttpGet("tests/{id:guid}")]
    public async Task<IActionResult> GetTestCase(Guid id)
    {
        try
        {
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            var testCase = await _testCaseService.GetTestCaseByIdAsync(id, scope);
            if (testCase == null)
            {
                return NotFound(new { message = "Test case not found" });
            }

            var response = new TestCaseResponse
            {
                Id = testCase.Id,
                SuiteId = testCase.SuiteId,
                ExternalId = testCase.ExternalId,
                Name = testCase.Name,
                Description = testCase.Description,
                InputSpecJson = testCase.InputSpecJson,
                ExpectationsJson = testCase.ExpectationsJson,
                CreatedAt = testCase.CreatedAt,
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get test case {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Update a test case
    /// </summary>
    [HttpPut("tests/{id:guid}")]
    public async Task<IActionResult> UpdateTestCase(Guid id, [FromBody] UpdateTestCaseRequest request)
    {
        try
        {
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            var testCase = await _testCaseService.UpdateTestCaseAsync(
                id,
                request.ExternalId,
                request.Name,
                request.Description,
                request.InputSpecJson,
                request.ExpectationsJson,
                scope);
            if (testCase == null)
            {
                return NotFound(new { message = "Test case not found" });
            }

            var response = new TestCaseResponse
            {
                Id = testCase.Id,
                SuiteId = testCase.SuiteId,
                ExternalId = testCase.ExternalId,
                Name = testCase.Name,
                Description = testCase.Description,
                InputSpecJson = testCase.InputSpecJson,
                ExpectationsJson = testCase.ExpectationsJson,
                CreatedAt = testCase.CreatedAt,
            };

            return Ok(response);
        }
        catch (TestSpecificationValidationException validationException)
        {
            return BadRequest(new
            {
                message = "Invalid test specification",
                errors = validationException.Issues
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update test case {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Delete a test case
    /// </summary>
    [HttpDelete("tests/{id:guid}")]
    public async Task<IActionResult> DeleteTestCase(Guid id)
    {
        try
        {
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            return await _testCaseService.DeleteTestCaseAsync(id, scope)
                ? NoContent()
                : NotFound(new { message = "Test case not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete test case {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }
}
