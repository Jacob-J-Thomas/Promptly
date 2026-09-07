using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Promptly.Server.Security;

public interface IAuthenticationThrottleResponseWriter
{
    Task WriteAsync(
        HttpContext httpContext,
        AuthenticationThrottleDecision decision,
        CancellationToken cancellationToken);

    IActionResult CreateActionResult(AuthenticationThrottleDecision decision);
}

public sealed class AuthenticationThrottleResponseWriter(
    IOptions<AuthenticationAbuseOptions> options) : IAuthenticationThrottleResponseWriter
{
    public const string ProblemType = "urn:promptly:problem:authentication-rate-limited";
    public const string ProblemCode = "authentication_rate_limited";
    public const string ProblemTitle = "Too many authentication requests.";
    private const string ProblemContentType = "application/problem+json";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly AuthenticationThrottleProblem Problem = new(
        ProblemType,
        ProblemTitle,
        StatusCodes.Status429TooManyRequests,
        ProblemCode);
    private readonly int _maximumRetryAfterSeconds = GetMaximumRetryAfterSeconds(options);

    public async Task WriteAsync(
        HttpContext httpContext,
        AuthenticationThrottleDecision decision,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        EnsureRejected(decision);

        httpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        httpContext.Response.ContentType = ProblemContentType;
        httpContext.Response.Headers.CacheControl = "no-store";
        httpContext.Response.Headers.RetryAfter = Math.Min(
                decision.RetryAfterSeconds,
                _maximumRetryAfterSeconds)
            .ToString(
            CultureInfo.InvariantCulture);

        await JsonSerializer.SerializeAsync(
                httpContext.Response.Body,
                Problem,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public IActionResult CreateActionResult(AuthenticationThrottleDecision decision)
    {
        EnsureRejected(decision);
        return new AuthenticationThrottleActionResult(this, decision);
    }

    private static void EnsureRejected(AuthenticationThrottleDecision decision)
    {
        if (decision.IsAllowed || decision.RetryAfterSeconds <= 0)
        {
            throw new ArgumentException("A throttle response requires a rejected decision.", nameof(decision));
        }
    }

    private static int GetMaximumRetryAfterSeconds(IOptions<AuthenticationAbuseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Value.MaximumRetryAfterSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "MaximumRetryAfterSeconds must be positive.");
        }

        return options.Value.MaximumRetryAfterSeconds;
    }

    private sealed class AuthenticationThrottleActionResult(
        AuthenticationThrottleResponseWriter writer,
        AuthenticationThrottleDecision decision) : IActionResult
    {
        public Task ExecuteResultAsync(ActionContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            return writer.WriteAsync(
                context.HttpContext,
                decision,
                context.HttpContext.RequestAborted);
        }
    }

    private sealed record AuthenticationThrottleProblem(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("status")] int Status,
        [property: JsonPropertyName("code")] string Code);
}
