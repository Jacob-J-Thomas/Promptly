using Promptly.Domain.Entities;

namespace Promptly.Application.Interfaces;

public interface IEndpointExecutor
{
    /// <summary>
    /// Execute a test case against an endpoint
    /// </summary>
    Task<ExecutionResult> ExecuteAsync(Endpoint endpoint, Domain.Entities.Environment environment, TestCase testCase);
}

public record ExecutionResult
{
    public bool Success { get; init; }
    public string? ResponseJson { get; init; }
    public long LatencyMs { get; init; }
    public int? StatusCode { get; init; }
    public string? ErrorMessage { get; init; }
}
