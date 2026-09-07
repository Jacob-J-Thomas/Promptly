using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Application.Data;

namespace Promptly.Application.Services;

public class TestCaseService : ITestCaseService
{
    private readonly PromptlyDbContext _dbContext;
    private readonly ILogger<TestCaseService> _logger;

    public TestCaseService(PromptlyDbContext dbContext, ILogger<TestCaseService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<TestCase?> CreateTestCaseAsync(
        Guid suiteId,
        string externalId,
        string name,
        string? description,
        string inputSpecJson,
        string expectationsJson,
        TenantAccessScope scope)
    {
        var ownsSuite = await _dbContext.TestSuites
            .ForTenant(scope)
            .AnyAsync(suite => suite.Id == suiteId);
        if (!ownsSuite)
        {
            return null;
        }

        var testCase = new TestCase
        {
            Id = Guid.NewGuid(),
            SuiteId = suiteId,
            ExternalId = externalId,
            Name = name,
            Description = description,
            InputSpecJson = inputSpecJson,
            ExpectationsJson = expectationsJson,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.TestCases.Add(testCase);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Created test case {TestCaseId} for suite {SuiteId}", testCase.Id, suiteId);

        return testCase;
    }

    public async Task<IReadOnlyList<TestCase>?> GetTestCasesBySuiteAsync(
        Guid suiteId,
        TenantAccessScope scope)
    {
        var ownsSuite = await _dbContext.TestSuites
            .ForTenant(scope)
            .AnyAsync(suite => suite.Id == suiteId);
        if (!ownsSuite)
        {
            return null;
        }

        return await _dbContext.TestCases
            .ForTenant(scope)
            .Where(t => t.SuiteId == suiteId)
            .OrderBy(t => t.ExternalId)
            .ToListAsync();
    }

    public async Task<TestCase?> GetTestCaseByIdAsync(Guid id, TenantAccessScope scope)
    {
        return await _dbContext.TestCases
            .ForTenant(scope)
            .FirstOrDefaultAsync(testCase => testCase.Id == id);
    }

    public async Task<TestCase?> UpdateTestCaseAsync(
        Guid id,
        string externalId,
        string name,
        string? description,
        string inputSpecJson,
        string expectationsJson,
        TenantAccessScope scope)
    {
        var testCase = await _dbContext.TestCases
            .ForTenant(scope)
            .FirstOrDefaultAsync(testCase => testCase.Id == id);
        if (testCase == null)
        {
            return null;
        }

        testCase.ExternalId = externalId;
        testCase.Name = name;
        testCase.Description = description;
        testCase.InputSpecJson = inputSpecJson;
        testCase.ExpectationsJson = expectationsJson;
        testCase.UpdatedAt = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Updated test case {TestCaseId}", id);

        return testCase;
    }

    public async Task<bool> DeleteTestCaseAsync(Guid id, TenantAccessScope scope)
    {
        var testCase = await _dbContext.TestCases
            .ForTenant(scope)
            .FirstOrDefaultAsync(testCase => testCase.Id == id);
        if (testCase == null)
        {
            return false;
        }

        _dbContext.TestCases.Remove(testCase);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Deleted test case {TestCaseId}", id);
        return true;
    }

    public async Task<IReadOnlyList<TestCase>?> BulkCreateTestsAsync(
        Guid suiteId,
        List<TestCase> testCases,
        TenantAccessScope scope)
    {
        var ownsSuite = await _dbContext.TestSuites
            .ForTenant(scope)
            .AnyAsync(suite => suite.Id == suiteId);
        if (!ownsSuite)
        {
            return null;
        }

        // Ensure all test cases have the correct suite ID
        foreach (var testCase in testCases)
        {
            testCase.Id = Guid.NewGuid();
            testCase.SuiteId = suiteId;
            testCase.CreatedAt = DateTime.UtcNow;
            testCase.UpdatedAt = DateTime.UtcNow;
        }

        _dbContext.TestCases.AddRange(testCases);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Bulk created {Count} test cases for suite {SuiteId}", testCases.Count, suiteId);

        return testCases;
    }
}
