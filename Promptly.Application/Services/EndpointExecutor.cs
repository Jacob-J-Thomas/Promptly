using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;

namespace Promptly.Application.Services;

public class EndpointExecutor : IEndpointExecutor
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEncryptionService _encryptionService;
    private readonly ILogger<EndpointExecutor> _logger;

    public EndpointExecutor(
        IHttpClientFactory httpClientFactory,
        IEncryptionService encryptionService,
        ILogger<EndpointExecutor> logger)
    {
        _httpClientFactory = httpClientFactory;
        _encryptionService = encryptionService;
        _logger = logger;
    }

    public async Task<ExecutionResult> ExecuteAsync(
        Endpoint endpoint,
        Domain.Entities.Environment environment,
        TestCase testCase,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Parse test input
            var input = JsonSerializer.Deserialize<Dictionary<string, object>>(testCase.InputSpecJson);
            if (input == null)
            {
                return new ExecutionResult
                {
                    Success = false,
                    ErrorMessage = "Failed to parse test input",
                    LatencyMs = stopwatch.ElapsedMilliseconds
                };
            }

            // Build request payload (default: messages from input)
            var payload = new Dictionary<string, object>();
            if (input.ContainsKey("messages"))
            {
                payload["messages"] = input["messages"];
            }
            else
            {
                // If no messages, include all input fields
                payload = input;
            }

            var requestJson = JsonSerializer.Serialize(payload);

            // Create HTTP client
            var httpClient = _httpClientFactory.CreateClient();
            httpClient.BaseAddress = new Uri(environment.BaseUrl);
            httpClient.Timeout = TimeSpan.FromSeconds(endpoint.TimeoutSeconds);

            // Add headers (decrypt first)
            if (!string.IsNullOrWhiteSpace(environment.DefaultHeadersEncryptedJson))
            {
                try
                {
                    var headersJson = _encryptionService.Decrypt(environment.DefaultHeadersEncryptedJson);
                    var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                    if (headers != null)
                    {
                        foreach (var header in headers)
                        {
                            httpClient.DefaultRequestHeaders.Add(header.Key, header.Value);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to decrypt/apply headers for environment {EnvironmentId}", environment.Id);
                }
            }

            // Send request
            var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            var httpMethod = endpoint.HttpMethod.ToUpper() switch
            {
                "POST" => HttpMethod.Post,
                "GET" => HttpMethod.Get,
                "PUT" => HttpMethod.Put,
                "PATCH" => HttpMethod.Patch,
                "DELETE" => HttpMethod.Delete,
                _ => HttpMethod.Post
            };

            var request = new HttpRequestMessage(httpMethod, endpoint.Path)
            {
                Content = content
            };

            var response = await httpClient.SendAsync(request, cancellationToken);
            stopwatch.Stop();

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);

            return new ExecutionResult
            {
                Success = response.IsSuccessStatusCode,
                ResponseJson = responseJson,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                StatusCode = (int)response.StatusCode,
                ErrorMessage = response.IsSuccessStatusCode ? null : $"HTTP {response.StatusCode}: {responseJson}"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Request to endpoint {EndpointId} timed out", endpoint.Id);

            return new ExecutionResult
            {
                Success = false,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                ErrorMessage = $"Request timed out after {endpoint.TimeoutSeconds}s"
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogError(ex, "Failed to execute request to endpoint {EndpointId}", endpoint.Id);

            return new ExecutionResult
            {
                Success = false,
                LatencyMs = stopwatch.ElapsedMilliseconds,
                ErrorMessage = $"Execution failed: {ex.Message}"
            };
        }
    }
}
