using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using Promptly.Application.Data;

namespace Promptly.Application.Services;

public class TestSuiteService : ITestSuiteService
{
    private readonly PromptlyDbContext _dbContext;
    private readonly ILogger<TestSuiteService> _logger;

    public TestSuiteService(PromptlyDbContext dbContext, ILogger<TestSuiteService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<TestSuite> CreateTestSuiteAsync(Guid projectId, string name, string? description = null)
    {
        var testSuite = new TestSuite
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = name,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.TestSuites.Add(testSuite);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created test suite {SuiteId} for project {ProjectId}", testSuite.Id, projectId);

        return testSuite;
    }

    public async Task<List<TestSuite>> GetTestSuitesByProjectAsync(Guid projectId)
    {
        return await _dbContext.TestSuites
            .Where(s => s.ProjectId == projectId)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<TestSuite?> GetTestSuiteByIdAsync(Guid id)
    {
        return await _dbContext.TestSuites
            .Include(s => s.TestCases)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<TestSuite> UpdateTestSuiteAsync(Guid id, string name, string? description)
    {
        var testSuite = await _dbContext.TestSuites.FindAsync(id);
        if (testSuite == null)
        {
            throw new InvalidOperationException($"Test suite with ID {id} not found");
        }

        testSuite.Name = name;
        testSuite.Description = description;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated test suite {SuiteId}", id);

        return testSuite;
    }

    public async Task DeleteTestSuiteAsync(Guid id)
    {
        var testSuite = await _dbContext.TestSuites.FindAsync(id);
        if (testSuite == null)
        {
            throw new InvalidOperationException($"Test suite with ID {id} not found");
        }

        _dbContext.TestSuites.Remove(testSuite);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted test suite {SuiteId}", id);
    }
}
