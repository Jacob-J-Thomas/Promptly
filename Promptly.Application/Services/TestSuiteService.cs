using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
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

    public async Task<TestSuite?> CreateTestSuiteAsync(
        Guid projectId,
        string name,
        string? description,
        TenantAccessScope scope)
    {
        var ownsProject = await _dbContext.Projects
            .ForTenant(scope)
            .AnyAsync(project => project.Id == projectId);
        if (!ownsProject)
        {
            return null;
        }

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

    public async Task<IReadOnlyList<TestSuite>?> GetTestSuitesByProjectAsync(
        Guid projectId,
        TenantAccessScope scope)
    {
        var ownsProject = await _dbContext.Projects
            .ForTenant(scope)
            .AnyAsync(project => project.Id == projectId);
        if (!ownsProject)
        {
            return null;
        }

        return await _dbContext.TestSuites
            .ForTenant(scope)
            .Where(s => s.ProjectId == projectId)
            .Include(s => s.TestCases)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();
    }

    public async Task<TestSuite?> GetTestSuiteByIdAsync(Guid id, TenantAccessScope scope)
    {
        return await _dbContext.TestSuites
            .ForTenant(scope)
            .Include(s => s.TestCases)
            .FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<TestSuite?> UpdateTestSuiteAsync(
        Guid id,
        string name,
        string? description,
        TenantAccessScope scope)
    {
        var testSuite = await _dbContext.TestSuites
            .ForTenant(scope)
            .Include(s => s.TestCases)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (testSuite == null)
        {
            return null;
        }

        testSuite.Name = name;
        testSuite.Description = description;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated test suite {SuiteId}", id);

        return testSuite;
    }

    public async Task<bool> DeleteTestSuiteAsync(Guid id, TenantAccessScope scope)
    {
        var testSuite = await _dbContext.TestSuites
            .ForTenant(scope)
            .FirstOrDefaultAsync(s => s.Id == id);
        if (testSuite == null)
        {
            return false;
        }

        _dbContext.TestSuites.Remove(testSuite);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted test suite {SuiteId}", id);
        return true;
    }
}
