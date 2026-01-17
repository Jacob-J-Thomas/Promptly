using Promptly.Domain.Entities;

namespace Promptly.Application.Interfaces;

public interface ITestSuiteService
{
    Task<TestSuite> CreateTestSuiteAsync(Guid projectId, string name, string? description = null);
    Task<List<TestSuite>> GetTestSuitesByProjectAsync(Guid projectId);
    Task<TestSuite?> GetTestSuiteByIdAsync(Guid id);
    Task<TestSuite> UpdateTestSuiteAsync(Guid id, string name, string? description);
    Task DeleteTestSuiteAsync(Guid id);
}
