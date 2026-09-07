using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.ValueObjects;

namespace Promptly.Infrastructure.Clients;

public class PythonEvalClient : IPythonEvalClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<PythonEvalClient> _logger;
    private readonly string _baseUrl;

    public PythonEvalClient(HttpClient httpClient, IConfiguration configuration, ILogger<PythonEvalClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _baseUrl = configuration["PROMPTLY_EVAL_BASE_URL"] ?? "http://localhost:8000";
        _httpClient.BaseAddress = new Uri(_baseUrl);
        _httpClient.Timeout = TimeSpan.FromMinutes(2);
    }

    public async Task<MappingProposalResult> ProposeMappingAsync(
        string sampleResponse,
        string? sampleRequest = null,
        Dictionary<string, object>? hints = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var request = new
            {
                sample_response_json = sampleResponse,
                sample_request_json = sampleRequest,
                hints = hints
            };

            var json = JsonSerializer.Serialize(request);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(
                "/mapping/propose",
                content,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                return new MappingProposalResult
                {
                    Success = false,
                    ErrorMessage = GetSafeWorkerErrorMessage((int)response.StatusCode, error),
                    ErrorCode = PythonWorkerErrorCodes.FromStatusCode((int)response.StatusCode),
                    WorkerStatusCode = (int)response.StatusCode
                };
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<MappingProposalResponse>(responseJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result?.MappingSpec == null)
            {
                return new MappingProposalResult
                {
                    Success = false,
                    ErrorMessage = "Python worker returned an invalid response",
                    ErrorCode = PythonWorkerErrorCodes.InvalidResponse
                };
            }

            return new MappingProposalResult
            {
                Success = true,
                MappingSpecJson = JsonSerializer.Serialize(result.MappingSpec),
                Reason = result.Reason
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Python worker for mapping proposal");
            return new MappingProposalResult
            {
                Success = false,
                ErrorMessage = GetSafeExceptionMessage(ex),
                ErrorCode = GetExceptionErrorCode(ex)
            };
        }
    }

    public async Task<EvaluationResult> EvaluateLlmJudgeAsync(
        string rubric,
        double minScore,
        CanonicalTrace trace,
        string? model = null,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var request = new
            {
                rubric,
                min_score = minScore,
                trace,
                model,
                provider
            };

            var json = JsonSerializer.Serialize(request, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(
                "/eval/llm-judge",
                content,
                cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new EvaluationResult
                {
                    Success = false,
                    Score = 0.0,
                    ErrorMessage = GetSafeWorkerErrorMessage((int)response.StatusCode, responseJson),
                    ErrorCode = PythonWorkerErrorCodes.FromStatusCode((int)response.StatusCode),
                    WorkerStatusCode = (int)response.StatusCode
                };
            }

            var result = JsonSerializer.Deserialize<EvaluationResponse>(responseJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result?.Score is not double score || result.Reason == null || score is < 0.0 or > 1.0)
            {
                return new EvaluationResult
                {
                    Success = false,
                    Score = 0.0,
                    ErrorMessage = "Python worker returned an invalid response",
                    ErrorCode = PythonWorkerErrorCodes.InvalidResponse
                };
            }

            return new EvaluationResult
            {
                Success = true,
                Score = score,
                Reason = result.Reason
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Python worker for LLM judge evaluation");
            return new EvaluationResult
            {
                Success = false,
                Score = 0.0,
                ErrorMessage = GetSafeExceptionMessage(ex),
                ErrorCode = GetExceptionErrorCode(ex)
            };
        }
    }

    public async Task<EvaluationResult> EvaluateGroundednessAsync(
        double minScore,
        CanonicalTrace trace,
        List<RetrievedDoc> docs,
        string? model = null,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var request = new
            {
                min_score = minScore,
                trace,
                docs,
                model,
                provider
            };

            var json = JsonSerializer.Serialize(request, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            using var content = new StringContent(json, Encoding.UTF8, "application/json");

            using var response = await _httpClient.PostAsync(
                "/eval/groundedness",
                content,
                cancellationToken);
            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new EvaluationResult
                {
                    Success = false,
                    Score = 0.0,
                    ErrorMessage = GetSafeWorkerErrorMessage((int)response.StatusCode, responseJson),
                    ErrorCode = PythonWorkerErrorCodes.FromStatusCode((int)response.StatusCode),
                    WorkerStatusCode = (int)response.StatusCode
                };
            }

            var result = JsonSerializer.Deserialize<EvaluationResponse>(responseJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result?.Score is not double score || result.Reason == null || score is < 0.0 or > 1.0)
            {
                return new EvaluationResult
                {
                    Success = false,
                    Score = 0.0,
                    ErrorMessage = "Python worker returned an invalid response",
                    ErrorCode = PythonWorkerErrorCodes.InvalidResponse
                };
            }

            return new EvaluationResult
            {
                Success = true,
                Score = score,
                Reason = result.Reason
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Python worker for groundedness evaluation");
            return new EvaluationResult
            {
                Success = false,
                Score = 0.0,
                ErrorMessage = GetSafeExceptionMessage(ex),
                ErrorCode = GetExceptionErrorCode(ex)
            };
        }
    }

    private static string GetExceptionErrorCode(Exception exception) => exception switch
    {
        TaskCanceledException => PythonWorkerErrorCodes.Timeout,
        HttpRequestException => PythonWorkerErrorCodes.TransportError,
        JsonException => PythonWorkerErrorCodes.InvalidResponse,
        _ => PythonWorkerErrorCodes.ClientError
    };

    private static string GetSafeExceptionMessage(Exception exception) => exception switch
    {
        TaskCanceledException => "Python worker request timed out",
        HttpRequestException => "Python worker could not be reached",
        JsonException => "Python worker returned an invalid response",
        _ => "Python worker request failed"
    };

    private static string GetSafeWorkerErrorMessage(int statusCode, string responseJson)
    {
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            if (document.RootElement.TryGetProperty("detail", out var detail))
            {
                if (detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString()!;
                }

                if (detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString()!;
                }

                if (detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("message", out var detailMessage)
                    && detailMessage.ValueKind == JsonValueKind.String)
                {
                    return detailMessage.GetString()!;
                }
            }
        }
        catch (JsonException)
        {
            // Non-contract bodies (for example a proxy HTML response) are never echoed.
        }

        return $"Python worker returned HTTP {statusCode}";
    }

    // Helper classes for deserialization
    private class MappingProposalResponse
    {
        public Dictionary<string, object>? MappingSpec { get; set; }
        public string? Reason { get; set; }
    }

    private class EvaluationResponse
    {
        public double? Score { get; set; }
        public string? Reason { get; set; }
    }
}
