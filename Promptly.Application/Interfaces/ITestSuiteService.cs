using Promptly.Application.Models;
using Promptly.Domain.Entities;

namespace Promptly.Application.Interfaces;

public interface ITestSuiteService
{
    Task<TestSuite?> CreateTestSuiteAsync(
        Guid projectId,
        string name,
        string? description,
        TenantAccessScope scope);
    Task<IReadOnlyList<TestSuite>?> GetTestSuitesByProjectAsync(
        Guid projectId,
        TenantAccessScope scope);
    Task<TestSuite?> GetTestSuiteByIdAsync(Guid id, TenantAccessScope scope);
    Task<TestSuite?> UpdateTestSuiteAsync(
        Guid id,
        string name,
        string? description,
        TenantAccessScope scope);
    Task<bool> DeleteTestSuiteAsync(Guid id, TenantAccessScope scope);
}
