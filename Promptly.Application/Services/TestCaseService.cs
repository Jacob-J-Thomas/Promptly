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
    private readonly ITestSpecificationValidator _specificationValidator;

    public TestCaseService(
        PromptlyDbContext dbContext,
        ILogger<TestCaseService> logger,
        IExpectationValidator? expectationValidator = null,
        ITestSpecificationValidator? specificationValidator = null)
    {
        _dbContext = dbContext;
        _logger = logger;
        var expectation = expectationValidator ?? new ExpectationDslValidator();
        _specificationValidator = specificationValidator ?? new TestSpecificationValidator(expectation);
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

        EnsureValidTestSpecification(externalId, name, inputSpecJson, expectationsJson);

        var duplicate = await _dbContext.TestCases
            .ForTenant(scope)
            .AnyAsync(testCase => testCase.SuiteId == suiteId && testCase.ExternalId == externalId);
        if (duplicate)
        {
            throw Validation("duplicate_external_id", "externalId", "External ID already exists in this suite");
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

        EnsureValidTestSpecification(externalId, name, inputSpecJson, expectationsJson);

        var duplicate = await _dbContext.TestCases
            .ForTenant(scope)
            .AnyAsync(candidate => candidate.SuiteId == testCase.SuiteId
                && candidate.ExternalId == externalId
                && candidate.Id != id);
        if (duplicate)
        {
            throw Validation("duplicate_external_id", "externalId", "External ID already exists in this suite");
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

        var issues = new List<ExpectationValidationIssue>();
        var staged = new List<TestCase>(testCases.Count);
        var externalIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < testCases.Count; index++)
        {
            var source = testCases[index];
            var rowPath = $"rows[{index}]";
            if (string.IsNullOrWhiteSpace(source.ExternalId))
            {
                issues.Add(new("required", $"{rowPath}.id", "Test ID is required"));
            }
            else if (!externalIds.Add(source.ExternalId))
            {
                issues.Add(new("duplicate_external_id", $"{rowPath}.id", "External ID is duplicated in the import"));
            }

            if (string.IsNullOrWhiteSpace(source.Name))
            {
                issues.Add(new("required", $"{rowPath}.name", "Test name is required"));
            }

            AddRowIssues(issues, rowPath, _specificationValidator.Validate(source.InputSpecJson, source.ExpectationsJson).Issues);
            staged.Add(new TestCase
            {
                Id = Guid.NewGuid(),
                SuiteId = suiteId,
                ExternalId = source.ExternalId,
                Name = source.Name,
                Description = source.Description,
                InputSpecJson = source.InputSpecJson,
                ExpectationsJson = source.ExpectationsJson,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
        }

        var existingIds = await _dbContext.TestCases
            .ForTenant(scope)
            .Where(testCase => testCase.SuiteId == suiteId)
            .Select(testCase => testCase.ExternalId)
            .ToListAsync();
        foreach (var (externalId, index) in externalIds.Select((value, index) => (value, index)))
        {
            if (existingIds.Contains(externalId, StringComparer.Ordinal))
            {
                var rowIndex = testCases.FindIndex(testCase => testCase.ExternalId == externalId);
                issues.Add(new("duplicate_external_id", $"rows[{rowIndex}].id", "External ID already exists in this suite"));
            }
        }

        if (issues.Count > 0)
        {
            throw new TestSpecificationValidationException(issues);
        }

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync()
            : null;
        try
        {
            _dbContext.TestCases.AddRange(staged);
            await _dbContext.SaveChangesAsync();
            if (transaction is not null)
            {
                await transaction.CommitAsync();
            }
        }
        catch (DbUpdateException ex)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync();
            }

            _logger.LogWarning(ex, "Bulk test-case persistence failed for suite {SuiteId}", suiteId);
            throw Validation("persistence_error", "rows", "Test cases could not be imported");
        }

        _logger.LogInformation("Bulk created {Count} test cases for suite {SuiteId}", testCases.Count, suiteId);

        return staged;
    }

    private void EnsureValidTestSpecification(
        string externalId,
        string name,
        string inputSpecJson,
        string expectationsJson)
    {
        var issues = new List<ExpectationValidationIssue>();
        if (string.IsNullOrWhiteSpace(externalId))
        {
            issues.Add(new("required", "externalId", "External ID is required"));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new("required", "name", "Test name is required"));
        }

        issues.AddRange(_specificationValidator.Validate(inputSpecJson, expectationsJson).Issues);
        if (issues.Count > 0)
        {
            ThrowValidation(issues);
        }
    }

    private static void AddRowIssues(
        ICollection<ExpectationValidationIssue> target,
        string rowPath,
        IEnumerable<ExpectationValidationIssue> source)
    {
        foreach (var issue in source)
        {
            var path = issue.Path.StartsWith("inputSpecJson", StringComparison.Ordinal)
                ? $"{rowPath}.input{issue.Path["inputSpecJson".Length..]}"
                : issue.Path.StartsWith("expectationsJson", StringComparison.Ordinal)
                    ? $"{rowPath}.expectations{issue.Path["expectationsJson".Length..]}"
                    : $"{rowPath}.{issue.Path}";
            target.Add(issue with { Path = path });
        }
    }

    private static TestSpecificationValidationException Validation(string code, string path, string message) =>
        new([new ExpectationValidationIssue(code, path, message)]);

    private static void ThrowValidation(IReadOnlyList<ExpectationValidationIssue> issues)
    {
        if (issues.Any(issue => issue.Path.StartsWith("expectationsJson", StringComparison.Ordinal)))
        {
            throw new ExpectationValidationException(issues);
        }

        throw new TestSpecificationValidationException(issues);
    }
}
