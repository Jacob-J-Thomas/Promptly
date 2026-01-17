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
        Dictionary<string, object>? hints = null)
    {
        try
        {
            var request = new
            {
                sample_response_json = sampleResponse,
                sample_request_json = sampleRequest,
                hints = hints
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/mapping/propose", content);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync();
                return new MappingProposalResult
                {
                    Success = false,
                    ErrorMessage = $"Python worker returned {response.StatusCode}: {error}"
                };
            }

            var responseJson = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize<MappingProposalResponse>(responseJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result == null)
            {
                return new MappingProposalResult
                {
                    Success = false,
                    ErrorMessage = "Failed to parse response from Python worker"
                };
            }

            return new MappingProposalResult
            {
                Success = true,
                MappingSpecJson = JsonSerializer.Serialize(result.MappingSpec),
                Reason = result.Reason
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Python worker for mapping proposal");
            return new MappingProposalResult
            {
                Success = false,
                ErrorMessage = $"Failed to call Python worker: {ex.Message}"
            };
        }
    }

    public async Task<EvaluationResult> EvaluateLlmJudgeAsync(
        string rubric,
        double minScore,
        CanonicalTrace trace,
        string? model = null,
        string? provider = null)
    {
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
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/eval/llm-judge", content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return new EvaluationResult
                {
                    Success = false,
                    Score = 0.0,
                    ErrorMessage = $"Python worker returned {response.StatusCode}: {responseJson}"
                };
            }

            var result = JsonSerializer.Deserialize<EvaluationResponse>(responseJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result == null)
            {
                return new EvaluationResult
                {
                    Success = false,
                    Score = 0.0,
                    ErrorMessage = "Failed to parse response from Python worker"
                };
            }

            return new EvaluationResult
            {
                Success = true,
                Score = result.Score,
                Reason = result.Reason
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Python worker for LLM judge evaluation");
            return new EvaluationResult
            {
                Success = false,
                Score = 0.0,
                ErrorMessage = $"Failed to call Python worker: {ex.Message}"
            };
        }
    }

    public async Task<EvaluationResult> EvaluateGroundednessAsync(
        double minScore,
        CanonicalTrace trace,
        List<RetrievedDoc> docs,
        string? model = null,
        string? provider = null)
    {
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
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync("/eval/groundedness", content);
            var responseJson = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return new EvaluationResult
                {
                    Success = false,
                    Score = 0.0,
                    ErrorMessage = $"Python worker returned {response.StatusCode}: {responseJson}"
                };
            }

            var result = JsonSerializer.Deserialize<EvaluationResponse>(responseJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result == null)
            {
                return new EvaluationResult
                {
                    Success = false,
                    Score = 0.0,
                    ErrorMessage = "Failed to parse response from Python worker"
                };
            }

            return new EvaluationResult
            {
                Success = true,
                Score = result.Score,
                Reason = result.Reason
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Python worker for groundedness evaluation");
            return new EvaluationResult
            {
                Success = false,
                Score = 0.0,
                ErrorMessage = $"Failed to call Python worker: {ex.Message}"
            };
        }
    }

    // Helper classes for deserialization
    private class MappingProposalResponse
    {
        public Dictionary<string, object>? MappingSpec { get; set; }
        public string? Reason { get; set; }
    }

    private class EvaluationResponse
    {
        public double Score { get; set; }
        public string? Reason { get; set; }
    }
}
