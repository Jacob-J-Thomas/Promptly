using Promptly.Application.Models;
using Promptly.Domain.Entities;

namespace Promptly.Application.Interfaces;

public interface ITestCaseService
{
    Task<TestCase?> CreateTestCaseAsync(
        Guid suiteId,
        string externalId,
        string name,
        string? description,
        string inputSpecJson,
        string expectationsJson,
        TenantAccessScope scope);
    Task<IReadOnlyList<TestCase>?> GetTestCasesBySuiteAsync(Guid suiteId, TenantAccessScope scope);
    Task<TestCase?> GetTestCaseByIdAsync(Guid id, TenantAccessScope scope);
    Task<TestCase?> UpdateTestCaseAsync(
        Guid id,
        string externalId,
        string name,
        string? description,
        string inputSpecJson,
        string expectationsJson,
        TenantAccessScope scope);
    Task<bool> DeleteTestCaseAsync(Guid id, TenantAccessScope scope);
    Task<IReadOnlyList<TestCase>?> BulkCreateTestsAsync(
        Guid suiteId,
        List<TestCase> testCases,
        TenantAccessScope scope);
}
