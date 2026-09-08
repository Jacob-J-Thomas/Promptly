using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Promptly.Sdk.DotNet;

/// <summary>
/// Client for interacting with the Promptly API
/// </summary>
public class PromptlyClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;
    private readonly string _baseUrl;

    /// <summary>
    /// Creates a new Promptly client
    /// </summary>
    /// <param name="baseUrl">Base URL of the Promptly API (e.g., http://localhost:5000/api)</param>
    /// <param name="apiKey">API key for authentication</param>
    /// <param name="httpClient">Optional HttpClient to use (if not provided, one will be created)</param>
    public PromptlyClient(string baseUrl, string apiKey, HttpClient? httpClient = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');

        if (httpClient == null)
        {
            _httpClient = new HttpClient();
            _disposeClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _httpClient.Timeout = TimeSpan.FromMinutes(10);
    }

    /// <summary>
    /// Queue a new test run
    /// </summary>
    public async Task<TestRunResponse> TriggerRunAsync(
        Guid suiteId,
        Guid environmentId,
        Guid endpointId,
        Guid mappingSpecId,
        string? gitCommitHash = null,
        string? configSnapshotJson = null,
        CancellationToken cancellationToken = default)
    {
        var request = new QueueRunRequest
        {
            SuiteId = suiteId,
            EnvironmentId = environmentId,
            EndpointId = endpointId,
            MappingSpecId = mappingSpecId,
            GitCommitHash = gitCommitHash,
            ConfigSnapshotJson = configSnapshotJson
        };

        var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/runs", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TestRunResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Failed to deserialize response");
    }

    /// <summary>
    /// Get the status and results of a test run
    /// </summary>
    public async Task<TestRunResponse> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"{_baseUrl}/runs/{runId}", cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<TestRunResponse>(cancellationToken)
            ?? throw new InvalidOperationException("Failed to deserialize response");
    }

    /// <summary>
    /// Wait for a test run to complete
    /// </summary>
    /// <param name="runId">The run ID to wait for</param>
    /// <param name="timeout">Maximum time to wait (default: 10 minutes)</param>
    /// <param name="pollInterval">Interval between status checks (default: 5 seconds)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The completed test run</returns>
    public async Task<TestRunResponse> WaitForCompletionAsync(
        Guid runId,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default)
    {
        var maxWait = timeout ?? TimeSpan.FromMinutes(10);
        var interval = pollInterval ?? TimeSpan.FromSeconds(5);
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < maxWait)
        {
            var run = await GetRunAsync(runId, cancellationToken);

            if (run.Status == "Completed" || run.Status == "Failed")
            {
                return run;
            }

            await Task.Delay(interval, cancellationToken);
        }

        throw new TimeoutException($"Test run {runId} did not complete within {maxWait}");
    }

    /// <summary>
    /// Get all test results for a run
    /// </summary>
    public async Task<List<TestRunResultResponse>> GetRunResultsAsync(
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"{_baseUrl}/runs/{runId}/results", cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<TestRunResultResponse>>(cancellationToken)
            ?? throw new InvalidOperationException("Failed to deserialize response");
    }

    /// <summary>
    /// Get all test runs for a suite
    /// </summary>
    public async Task<List<TestRunResponse>> GetRunsBySuiteAsync(
        Guid suiteId,
        string? status = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        var queryParams = new List<string>();
        if (!string.IsNullOrWhiteSpace(status))
            queryParams.Add($"status={status}");
        if (limit.HasValue)
            queryParams.Add($"limit={limit.Value}");

        var query = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : "";
        var response = await _httpClient.GetAsync($"{_baseUrl}/suites/{suiteId}/runs{query}", cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<List<TestRunResponse>>(cancellationToken)
            ?? throw new InvalidOperationException("Failed to deserialize response");
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient?.Dispose();
        }
    }
}

// Request/Response models
public record QueueRunRequest
{
    public Guid SuiteId { get; init; }
    public Guid EnvironmentId { get; init; }
    public Guid EndpointId { get; init; }
    public Guid MappingSpecId { get; init; }
    public string? GitCommitHash { get; init; }
    public string? ConfigSnapshotJson { get; init; }
}

public record TestRunResponse
{
    public Guid Id { get; init; }
    public Guid SuiteId { get; init; }
    public Guid EnvironmentId { get; init; }
    public Guid EndpointId { get; init; }
    public Guid MappingSpecId { get; init; }
    [JsonConverter(typeof(TestRunStatusStringConverter))]
    public required string Status { get; init; }
    public string? SummaryJson { get; init; }
    public string? GitCommitHash { get; init; }
    public string? ConfigSnapshotJson { get; init; }
    public required string CreatedByUserId { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
}

public record TestRunResultResponse
{
    public Guid Id { get; init; }
    public Guid RunId { get; init; }
    public Guid TestCaseId { get; init; }
    [JsonConverter(typeof(TestResultStatusStringConverter))]
    public required string Status { get; init; }
    public string? TraceJson { get; init; }
    public string? MetricsJson { get; init; }
    public string? FailureReasonsJson { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? TestCaseName { get; init; }
    public string? TestCaseExternalId { get; init; }
}
