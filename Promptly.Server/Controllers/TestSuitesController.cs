using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Infrastructure.Services;
using Promptly.Server.Security;

namespace Promptly.Server.Controllers;

[ApiController]
[Authorize]
[Route("api")]
public class TestSuitesController : ControllerBase
{
    private readonly ITestSuiteService _testSuiteService;
    private readonly ITestCaseService _testCaseService;
    private readonly IYamlService _yamlService;
    private readonly ITenantAccessScopeAccessor _tenantAccessScopeAccessor;
    private readonly ILogger<TestSuitesController> _logger;

    public TestSuitesController(
        ITestSuiteService testSuiteService,
        ITestCaseService testCaseService,
        IYamlService yamlService,
        ITenantAccessScopeAccessor tenantAccessScopeAccessor,
        ILogger<TestSuitesController> logger)
    {
        _testSuiteService = testSuiteService;
        _testCaseService = testCaseService;
        _yamlService = yamlService;
        _tenantAccessScopeAccessor = tenantAccessScopeAccessor;
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
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            var testSuite = await _testSuiteService.CreateTestSuiteAsync(
                projectId,
                request.Name,
                request.Description,
                scope);
            if (testSuite == null)
            {
                return NotFound(new { message = "Project not found" });
            }

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
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            var testSuites = await _testSuiteService.GetTestSuitesByProjectAsync(projectId, scope);
            if (testSuites == null)
            {
                return NotFound(new { message = "Project not found" });
            }

            var responses = testSuites.Select(suite => new TestSuiteResponse
            {
                Id = suite.Id,
                ProjectId = suite.ProjectId,
                Name = suite.Name,
                Description = suite.Description,
                CreatedAt = suite.CreatedAt,
                TestCaseCount = suite.TestCases.Count
            }).ToList();

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
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            var testSuite = await _testSuiteService.GetTestSuiteByIdAsync(id, scope);
            if (testSuite == null)
            {
                return NotFound(new { message = "Test suite not found" });
            }

            var response = new TestSuiteResponse
            {
                Id = testSuite.Id,
                ProjectId = testSuite.ProjectId,
                Name = testSuite.Name,
                Description = testSuite.Description,
                CreatedAt = testSuite.CreatedAt,
                TestCaseCount = testSuite.TestCases.Count
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
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            var testSuite = await _testSuiteService.UpdateTestSuiteAsync(
                id,
                request.Name,
                request.Description,
                scope);
            if (testSuite == null)
            {
                return NotFound(new { message = "Test suite not found" });
            }

            var response = new TestSuiteResponse
            {
                Id = testSuite.Id,
                ProjectId = testSuite.ProjectId,
                Name = testSuite.Name,
                Description = testSuite.Description,
                CreatedAt = testSuite.CreatedAt,
                TestCaseCount = testSuite.TestCases.Count
            };

            return Ok(response);
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
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            return await _testSuiteService.DeleteTestSuiteAsync(id, scope)
                ? NoContent()
                : NotFound(new { message = "Test suite not found" });
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
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            var suite = await _testSuiteService.GetTestSuiteByIdAsync(id, scope);
            if (suite == null)
            {
                return NotFound(new { message = "Test suite not found" });
            }

            if (file == null || file.Length == 0)
            {
                return BadRequest(new
                {
                    message = "Invalid test specification",
                    errors = new[]
                    {
                        new ExpectationValidationIssue(
                            "required",
                            "file",
                            "A YAML file is required")
                    }
                });
            }

            if (file.Length > YamlService.MaxYamlBytes)
            {
                return BadRequest(new
                {
                    message = "Invalid test specification",
                    errors = new[]
                    {
                        new ExpectationValidationIssue(
                            "too_large",
                            "$",
                            "YAML content exceeds the maximum size")
                    }
                });
            }

            string yamlContent;
            await using (var stream = file.OpenReadStream())
            await using (var buffer = new MemoryStream())
            {
                var chunk = new byte[8192];
                var total = 0;
                int read;
                while ((read = await stream.ReadAsync(chunk, HttpContext.RequestAborted)) > 0)
                {
                    total += read;
                    if (total > YamlService.MaxYamlBytes)
                    {
                        return BadRequest(new
                        {
                            message = "Invalid test specification",
                            errors = new[]
                            {
                                new ExpectationValidationIssue(
                                    "too_large",
                                    "$",
                                    "YAML content exceeds the maximum size")
                            }
                        });
                    }

                    await buffer.WriteAsync(chunk.AsMemory(0, read), HttpContext.RequestAborted);
                }

                yamlContent = Encoding.UTF8.GetString(buffer.ToArray());
            }

            var testCases = _yamlService.DeserializeTests(yamlContent, id);
            var createdTests = await _testCaseService.BulkCreateTestsAsync(id, testCases, scope);
            if (createdTests == null)
            {
                return NotFound(new { message = "Test suite not found" });
            }

            var response = new ImportTestsResponse
            {
                ImportedCount = createdTests.Count,
                ImportedTestIds = createdTests.Select(t => t.ExternalId).ToList()
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
            if (!_tenantAccessScopeAccessor.TryGetScope(out var scope))
            {
                return Unauthorized();
            }

            var suite = await _testSuiteService.GetTestSuiteByIdAsync(id, scope);
            if (suite == null)
            {
                return NotFound(new { message = "Test suite not found" });
            }

            var testCases = await _testCaseService.GetTestCasesBySuiteAsync(id, scope) ?? [];
            var yaml = _yamlService.SerializeTests([.. testCases]);

            var fileName = $"{suite.Name.Replace(" ", "_")}_tests.yaml";
            var bytes = Encoding.UTF8.GetBytes(yaml);

            return File(bytes, "application/x-yaml", fileName);
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
            _logger.LogError(ex, "Failed to export tests for suite {Id}", id);
            return StatusCode(500, new { message = "Internal server error" });
        }
    }
}
