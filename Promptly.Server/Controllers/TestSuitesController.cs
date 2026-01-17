using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;

namespace Promptly.Server.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public class TestSuitesController : ControllerBase
{
    private readonly ITestSuiteService _testSuiteService;
    private readonly ITestCaseService _testCaseService;
    private readonly IYamlService _yamlService;
    private readonly IProjectService _projectService;
    private readonly ILogger<TestSuitesController> _logger;

    public TestSuitesController(
        ITestSuiteService testSuiteService,
        ITestCaseService testCaseService,
        IYamlService yamlService,
        IProjectService projectService,
        ILogger<TestSuitesController> logger)
    {
        _testSuiteService = testSuiteService;
        _testCaseService = testCaseService;
        _yamlService = yamlService;
        _projectService = projectService;
        _logger = logger;
    }

    /// <summary>
    /// Create a new test suite
    /// </summary>
    [HttpPost("suites")]
    public async Task<IActionResult> CreateTestSuite([FromBody] CreateTestSuiteRequest request, [FromQuery] Guid projectId)
    {
        try
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier)!;

            // Verify project exists (implicitly checks ownership via ProjectService)
            var project = await _projectService.GetProjectByIdAsync(projectId, userId);
            if (project == null)
            {
                return NotFound(new { message = "Project not found" });
            }

            var testSuite = await _testSuiteService.CreateTestSuiteAsync(
                projectId,
                request.Name,
                request.Description);

            var response = new TestSuiteResponse
            {
                Id = testSuite.Id,
                ProjectId = testSuite.ProjectId,
                Name = testSuite.Name,
                Description = testSuite.Description,
                CreatedAt = testSuite.CreatedAt,
                TestCaseCount = 0
            };

            return CreatedAtAction(nameof(GetTestSuite), new { id = testSuite.Id }, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create test suite for project {ProjectId}", projectId);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Get all test suites for a project
    /// </summary>
    [HttpGet("projects/{projectId:guid}/suites")]
    public async Task<IActionResult> GetTestSuitesByProject(Guid projectId)
    {
        try
        {
            var testSuites = await _testSuiteService.GetTestSuitesByProjectAsync(projectId);

            var responses = new List<TestSuiteResponse>();
            foreach (var suite in testSuites)
            {
                var testCases = await _testCaseService.GetTestCasesBySuiteAsync(suite.Id);

                responses.Add(new TestSuiteResponse
                {
                    Id = suite.Id,
                    ProjectId = suite.ProjectId,
                    Name = suite.Name,
                    Description = suite.Description,
                    CreatedAt = suite.CreatedAt,
                    TestCaseCount = testCases.Count
                });
            }

            return Ok(responses);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get test suites for project {ProjectId}", projectId);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Get a specific test suite by ID
    /// </summary>
    [HttpGet("suites/{id:guid}")]
    public async Task<IActionResult> GetTestSuite(Guid id)
    {
        try
        {
            var testSuite = await _testSuiteService.GetTestSuiteByIdAsync(id);
            if (testSuite == null)
            {
                return NotFound(new { message = "Test suite not found" });
            }

            var testCases = await _testCaseService.GetTestCasesBySuiteAsync(id);

            var response = new TestSuiteResponse
            {
                Id = testSuite.Id,
                ProjectId = testSuite.ProjectId,
                Name = testSuite.Name,
                Description = testSuite.Description,
                CreatedAt = testSuite.CreatedAt,
                TestCaseCount = testCases.Count
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get test suite {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Update a test suite
    /// </summary>
    [HttpPut("suites/{id:guid}")]
    public async Task<IActionResult> UpdateTestSuite(Guid id, [FromBody] UpdateTestSuiteRequest request)
    {
        try
        {
            var testSuite = await _testSuiteService.UpdateTestSuiteAsync(
                id,
                request.Name,
                request.Description);

            var testCases = await _testCaseService.GetTestCasesBySuiteAsync(id);

            var response = new TestSuiteResponse
            {
                Id = testSuite.Id,
                ProjectId = testSuite.ProjectId,
                Name = testSuite.Name,
                Description = testSuite.Description,
                CreatedAt = testSuite.CreatedAt,
                TestCaseCount = testCases.Count
            };

            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update test suite {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Delete a test suite
    /// </summary>
    [HttpDelete("suites/{id:guid}")]
    public async Task<IActionResult> DeleteTestSuite(Guid id)
    {
        try
        {
            await _testSuiteService.DeleteTestSuiteAsync(id);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete test suite {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Import tests from YAML file
    /// </summary>
    [HttpPost("suites/{id:guid}/tests/import")]
    public async Task<IActionResult> ImportTests(Guid id, IFormFile file)
    {
        try
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { message = "No file provided" });
            }

            // Verify suite exists
            var suite = await _testSuiteService.GetTestSuiteByIdAsync(id);
            if (suite == null)
            {
                return NotFound(new { message = "Test suite not found" });
            }

            // Read YAML content
            string yamlContent;
            using (var reader = new StreamReader(file.OpenReadStream()))
            {
                yamlContent = await reader.ReadToEndAsync();
            }

            // Deserialize and create tests
            var testCases = _yamlService.DeserializeTests(yamlContent, id);
            var createdTests = await _testCaseService.BulkCreateTestsAsync(id, testCases);

            var response = new ImportTestsResponse
            {
                ImportedCount = createdTests.Count,
                ImportedTestIds = createdTests.Select(t => t.ExternalId).ToList()
            };

            return Ok(response);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to import tests for suite {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }

    /// <summary>
    /// Export tests to YAML file
    /// </summary>
    [HttpGet("suites/{id:guid}/tests/export")]
    public async Task<IActionResult> ExportTests(Guid id)
    {
        try
        {
            // Verify suite exists
            var suite = await _testSuiteService.GetTestSuiteByIdAsync(id);
            if (suite == null)
            {
                return NotFound(new { message = "Test suite not found" });
            }

            var testCases = await _testCaseService.GetTestCasesBySuiteAsync(id);
            var yaml = _yamlService.SerializeTests(testCases);

            var fileName = $"{suite.Name.Replace(" ", "_")}_tests.yaml";
            var bytes = Encoding.UTF8.GetBytes(yaml);

            return File(bytes, "application/x-yaml", fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export tests for suite {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }
}
