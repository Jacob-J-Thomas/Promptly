using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;

namespace Promptly.Application.Services;

public class EndpointExecutor : IEndpointExecutor
{
    public const string HttpClientName = "Promptly.EndpointExecutor";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEncryptionService _encryptionService;
    private readonly IEndpointDestinationGuard _destinationGuard;
    private readonly ILogger<EndpointExecutor> _logger;
    private readonly bool _usesProxy;

    public EndpointExecutor(
        IHttpClientFactory httpClientFactory,
        IEncryptionService encryptionService,
        IEndpointDestinationGuard destinationGuard,
        ILogger<EndpointExecutor> logger,
        IOptions<EndpointEgressOptions>? egressOptions = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(encryptionService);
        ArgumentNullException.ThrowIfNull(destinationGuard);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _encryptionService = encryptionService;
        _destinationGuard = destinationGuard;
        _logger = logger;
        _usesProxy = !string.IsNullOrEmpty(egressOptions?.Value.ProxyUrl);
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
            if (!EndpointTargetPolicy.TryResolve(
                    environment.BaseUrl,
                    endpoint.Path,
                    out var resolvedTarget,
                    out var targetValidationError))
            {
                return new ExecutionResult
                {
                    Success = false,
                    ErrorMessage = $"Unsafe endpoint target: {targetValidationError}",
                    LatencyMs = stopwatch.ElapsedMilliseconds
                };
            }

            // Authorize before parsing tenant input, decrypting credentials, or creating a
            // client. Direct transports repeat this immediately before every socket dial.
            await _destinationGuard.AuthorizeAsync(resolvedTarget, cancellationToken);

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

            Dictionary<string, string>? headers = null;
            if (!string.IsNullOrWhiteSpace(environment.DefaultHeadersEncryptedJson))
            {
                try
                {
                    var headersJson = _encryptionService.Decrypt(environment.DefaultHeadersEncryptedJson);
                    headers = JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to decrypt headers for environment {EnvironmentId}",
                        environment.Id);
                }
            }

            // Send request
            var httpMethod = endpoint.HttpMethod.ToUpper() switch
            {
                "POST" => HttpMethod.Post,
                "GET" => HttpMethod.Get,
                "PUT" => HttpMethod.Put,
                "PATCH" => HttpMethod.Patch,
                "DELETE" => HttpMethod.Delete,
                _ => HttpMethod.Post
            };

            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(httpMethod, resolvedTarget)
            {
                Content = content,
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            if (headers != null)
            {
                foreach (var header in headers)
                {
                    if (IsReservedOutboundHeader(header.Key))
                    {
                        _logger.LogWarning(
                            "Blocked reserved outbound header {HeaderName} for environment {EnvironmentId}",
                            header.Key,
                            environment.Id);
                        return Failed(
                            stopwatch,
                            "Unsafe endpoint headers");
                    }

                    try
                    {
                        request.Headers.Add(header.Key, header.Value);
                    }
                    catch (Exception exception) when (
                        exception is FormatException or InvalidOperationException)
                    {
                        _logger.LogWarning(
                            "Blocked invalid outbound header {HeaderName} for environment {EnvironmentId}",
                            header.Key,
                            environment.Id);
                        return Failed(
                            stopwatch,
                            "Unsafe endpoint headers");
                    }
                }
            }

            using var httpClient = _httpClientFactory.CreateClient(HttpClientName);
            httpClient.Timeout = TimeSpan.FromSeconds(endpoint.TimeoutSeconds);

            using var response = await httpClient.SendAsync(request, cancellationToken);
            stopwatch.Stop();

            if (_usesProxy
                && (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired
                    || response.Headers.Contains("X-Smokescreen-Error")))
            {
                _logger.LogWarning(
                    "Endpoint proxy denied destination for endpoint {EndpointId}",
                    endpoint.Id);
                return Failed(
                    stopwatch,
                    EndpointDestinationRejectedException.SafeMessage,
                    (int)response.StatusCode);
            }

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
        catch (EndpointDestinationRejectedException exception)
        {
            _logger.LogWarning(
                "Endpoint destination policy denied endpoint {EndpointId} with reason {Reason}",
                endpoint.Id,
                exception.Reason);
            return Failed(stopwatch, EndpointDestinationRejectedException.SafeMessage);
        }
        catch (HttpRequestException exception) when (
            FindDestinationRejection(exception) is not null)
        {
            var rejection = FindDestinationRejection(exception)!;
            _logger.LogWarning(
                "Endpoint connection policy denied endpoint {EndpointId} with reason {Reason}",
                endpoint.Id,
                rejection.Reason);
            return Failed(stopwatch, EndpointDestinationRejectedException.SafeMessage);
        }
        catch (HttpRequestException exception) when (_usesProxy && IsProxyDenial(exception))
        {
            _logger.LogWarning(
                "Endpoint proxy denied destination for endpoint {EndpointId}",
                endpoint.Id);
            return Failed(
                stopwatch,
                EndpointDestinationRejectedException.SafeMessage,
                (int)HttpStatusCode.ProxyAuthenticationRequired);
        }
        catch (HttpRequestException exception) when (_usesProxy && exception.StatusCode is not null)
        {
            // SocketsHttpHandler surfaces non-success HTTPS CONNECT responses as
            // exceptions whose message embeds the proxy origin. Preserve only
            // the status; never persist or return deployment details.
            _logger.LogWarning(
                "Endpoint proxy tunnel failed for endpoint {EndpointId} with status {StatusCode}",
                endpoint.Id,
                exception.StatusCode);
            return Failed(
                stopwatch,
                EndpointDestinationRejectedException.SafeMessage,
                (int)exception.StatusCode.Value);
        }
        catch (HttpRequestException exception)
        {
            // Framework messages may contain the configured proxy origin, the
            // selected literal address, or resolver details. Keep those only in
            // structured server logs.
            _logger.LogWarning(
                exception,
                "Endpoint transport failed for endpoint {EndpointId}",
                endpoint.Id);
            return Failed(stopwatch, "Endpoint transport failed");
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

    private static bool IsReservedOutboundHeader(string headerName) =>
        string.IsNullOrWhiteSpace(headerName)
        || string.Equals(headerName, "Host", StringComparison.OrdinalIgnoreCase)
        || string.Equals(headerName, "Connection", StringComparison.OrdinalIgnoreCase)
        || string.Equals(headerName, "Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || string.Equals(headerName, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || string.Equals(headerName, "TE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(headerName, "Trailer", StringComparison.OrdinalIgnoreCase)
        || string.Equals(headerName, "Upgrade", StringComparison.OrdinalIgnoreCase)
        || string.Equals(headerName, "Content-Length", StringComparison.OrdinalIgnoreCase)
        || string.Equals(
            headerName,
            "X-Upstream-Https-Proxy",
            StringComparison.OrdinalIgnoreCase)
        || headerName.StartsWith("Proxy-", StringComparison.OrdinalIgnoreCase)
        || headerName.StartsWith("X-Smokescreen-", StringComparison.OrdinalIgnoreCase);

    private static EndpointDestinationRejectedException? FindDestinationRejection(
        Exception exception)
    {
        if (exception is EndpointDestinationRejectedException rejection)
        {
            return rejection;
        }

        if (exception is AggregateException aggregateException)
        {
            foreach (var innerException in aggregateException.InnerExceptions)
            {
                var nestedRejection = FindDestinationRejection(innerException);
                if (nestedRejection != null)
                {
                    return nestedRejection;
                }
            }
        }

        return exception.InnerException == null
            ? null
            : FindDestinationRejection(exception.InnerException);
    }

    private static bool IsProxyDenial(Exception exception)
    {
        if (exception is HttpRequestException
            {
                StatusCode: HttpStatusCode.ProxyAuthenticationRequired
            })
        {
            return true;
        }

        if (exception is AggregateException aggregateException
            && aggregateException.InnerExceptions.Any(IsProxyDenial))
        {
            return true;
        }

        return exception.InnerException != null && IsProxyDenial(exception.InnerException);
    }

    private static ExecutionResult Failed(
        Stopwatch stopwatch,
        string errorMessage,
        int? statusCode = null)
    {
        stopwatch.Stop();
        return new ExecutionResult
        {
            Success = false,
            LatencyMs = stopwatch.ElapsedMilliseconds,
            StatusCode = statusCode,
            ErrorMessage = errorMessage
        };
    }
}
