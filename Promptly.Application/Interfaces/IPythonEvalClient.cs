using Promptly.Domain.ValueObjects;

namespace Promptly.Application.Interfaces;

public interface IPythonEvalClient
{
    Task<MappingProposalResult> ProposeMappingAsync(string sampleResponse, string? sampleRequest = null, Dictionary<string, object>? hints = null);
    Task<EvaluationResult> EvaluateLlmJudgeAsync(string rubric, double minScore, CanonicalTrace trace, string? model = null, string? provider = null);
    Task<EvaluationResult> EvaluateGroundednessAsync(double minScore, CanonicalTrace trace, List<RetrievedDoc> docs, string? model = null, string? provider = null);
}

public record MappingProposalResult
{
    public bool Success { get; init; }
    public string? MappingSpecJson { get; init; }
    public string? Reason { get; init; }
    public string? ErrorMessage { get; init; }
}

public record EvaluationResult
{
    public bool Success { get; init; }
    public double Score { get; init; }
    public string? Reason { get; init; }
    public string? ErrorMessage { get; init; }
}
