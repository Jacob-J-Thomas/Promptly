using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;

namespace Promptly.Server.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public class TestsController : ControllerBase
{
    private readonly ITestCaseService _testCaseService;
    private readonly ITestSuiteService _testSuiteService;
    private readonly ILogger<TestsController> _logger;

    public TestsController(
        ITestCaseService testCaseService,
        ITestSuiteService testSuiteService,
        ILogger<TestsController> logger)
    {
        _testCaseService = testCaseService;
        _testSuiteService = testSuiteService;
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
            // Verify suite exists
            var suite = await _testSuiteService.GetTestSuiteByIdAsync(suiteId);
            if (suite == null)
            {
                return NotFound(new { message = "Test suite not found" });
            }

            var testCase = await _testCaseService.CreateTestCaseAsync(
                suiteId,
                request.ExternalId,
                request.Name,
                request.Description,
                request.InputSpecJson,
                request.ExpectationsJson);

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
            var testCases = await _testCaseService.GetTestCasesBySuiteAsync(suiteId);

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
            var testCase = await _testCaseService.GetTestCaseByIdAsync(id);
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
            var testCase = await _testCaseService.UpdateTestCaseAsync(
                id,
                request.ExternalId,
                request.Name,
                request.Description,
                request.InputSpecJson,
                request.ExpectationsJson);

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
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
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
            await _testCaseService.DeleteTestCaseAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete test case {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }
}
