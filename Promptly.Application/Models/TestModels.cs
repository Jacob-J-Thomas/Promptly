using Promptly.Application.Interfaces;

namespace Promptly.Application.Models;

// Test Suite models
public record CreateTestSuiteRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public record UpdateTestSuiteRequest
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public record TestSuiteResponse
{
    public Guid Id { get; init; }
    public Guid ProjectId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
    public int TestCaseCount { get; init; }
}

// Test Case models
public record CreateTestCaseRequest
{
    public required string ExternalId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string InputSpecJson { get; init; }
    public required string ExpectationsJson { get; init; }
}

public record UpdateTestCaseRequest
{
    public required string ExternalId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string InputSpecJson { get; init; }
    public required string ExpectationsJson { get; init; }
}

public record TestCaseResponse
{
    public Guid Id { get; init; }
    public Guid SuiteId { get; init; }
    public required string ExternalId { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string InputSpecJson { get; init; }
    public required string ExpectationsJson { get; init; }
    public DateTime CreatedAt { get; init; }
}

// Import/Export models
public record ImportTestsResponse
{
    public int ImportedCount { get; init; }
    public List<string> ImportedTestIds { get; init; } = new();
    public List<string> Errors { get; init; } = new();
}

/// <summary>
/// Safe structured validation failure shared by JSON and YAML test-spec writes.
/// </summary>
public class TestSpecificationValidationException : ArgumentException
{
    public TestSpecificationValidationException(IReadOnlyList<ExpectationValidationIssue> issues)
        : base(string.Join("; ", issues.Select(issue => $"{issue.Path}: {issue.Message}")))
    {
        Issues = issues;
    }

    public IReadOnlyList<ExpectationValidationIssue> Issues { get; }
}

/// <summary>
/// Backward-compatible name for expectation-only service validation failures.
/// </summary>
public sealed class ExpectationValidationException : TestSpecificationValidationException
{
    public ExpectationValidationException(IReadOnlyList<ExpectationValidationIssue> issues)
        : base(issues)
    {
    }
}
