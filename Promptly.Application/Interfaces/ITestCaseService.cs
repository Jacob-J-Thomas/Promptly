using Promptly.Domain.Entities;

namespace Promptly.Application.Interfaces;

public interface ITestCaseService
{
    Task<TestCase> CreateTestCaseAsync(Guid suiteId, string externalId, string name, string? description, string inputSpecJson, string expectationsJson);
    Task<List<TestCase>> GetTestCasesBySuiteAsync(Guid suiteId);
    Task<TestCase?> GetTestCaseByIdAsync(Guid id);
    Task<TestCase> UpdateTestCaseAsync(Guid id, string externalId, string name, string? description, string inputSpecJson, string expectationsJson);
    Task DeleteTestCaseAsync(Guid id);
    Task<List<TestCase>> BulkCreateTestsAsync(Guid suiteId, List<TestCase> testCases);
}
