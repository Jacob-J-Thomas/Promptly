using Promptly.Domain.ValueObjects;

namespace Promptly.Application.Interfaces;

public interface IPythonEvalClient
{
    Task<MappingProposalResult> ProposeMappingAsync(
        string sampleResponse,
        string? sampleRequest = null,
        Dictionary<string, object>? hints = null,
        CancellationToken cancellationToken = default);
    Task<EvaluationResult> EvaluateLlmJudgeAsync(
        string rubric,
        double minScore,
        CanonicalTrace trace,
        string? model = null,
        string? provider = null,
        CancellationToken cancellationToken = default);

    Task<EvaluationResult> EvaluateGroundednessAsync(
        double minScore,
        CanonicalTrace trace,
        List<RetrievedDoc> docs,
        string? model = null,
        string? provider = null,
        CancellationToken cancellationToken = default);
}

public record MappingProposalResult
{
    public bool Success { get; init; }
    public string? MappingSpecJson { get; init; }
    public string? Reason { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public int? WorkerStatusCode { get; init; }
}

public record EvaluationResult
{
    public bool Success { get; init; }
    public double Score { get; init; }
    public string? Reason { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ErrorCode { get; init; }
    public int? WorkerStatusCode { get; init; }
}

public static class PythonWorkerErrorCodes
{
    public const string BadRequest = "python_worker_bad_request";
    public const string UpstreamFailure = "python_worker_upstream_failure";
    public const string Unavailable = "python_worker_unavailable";
    public const string HttpError = "python_worker_http_error";
    public const string InvalidResponse = "python_worker_invalid_response";
    public const string Timeout = "python_worker_timeout";
    public const string TransportError = "python_worker_transport_error";
    public const string ClientError = "python_worker_client_error";

    public static string FromStatusCode(int statusCode) => statusCode switch
    {
        400 or 422 => BadRequest,
        502 => UpstreamFailure,
        503 => Unavailable,
        _ => HttpError
    };
}
