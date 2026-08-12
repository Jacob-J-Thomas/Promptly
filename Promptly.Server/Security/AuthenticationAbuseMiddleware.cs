using Microsoft.AspNetCore.Http.Features;
using Promptly.Server.Models;

namespace Promptly.Server.Security;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true)]
public sealed class AuthenticationAbuseOperationAttribute(AuthenticationOperation operation) :
    Attribute
{
    public AuthenticationOperation Operation { get; } = operation;
}

public sealed class AuthenticationAbuseMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext httpContext,
        IAuthenticationAbuseGuard abuseGuard,
        IAuthenticationThrottleResponseWriter responseWriter)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(abuseGuard);
        ArgumentNullException.ThrowIfNull(responseWriter);

        var operation = httpContext.GetEndpoint()
            ?.Metadata
            .GetMetadata<AuthenticationAbuseOperationAttribute>();
        if (operation is null)
        {
            await next(httpContext).ConfigureAwait(false);
            return;
        }

        var requestSizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        var effectiveBodyLimit = Math.Min(
            requestSizeFeature?.MaxRequestBodySize
                ?? AuthenticationInputLimits.RequestBodyMaxBytes,
            AuthenticationInputLimits.RequestBodyMaxBytes);
        if (requestSizeFeature is { IsReadOnly: false })
        {
            requestSizeFeature.MaxRequestBodySize = effectiveBodyLimit;
        }

        var decision = abuseGuard.TryAcquireClient(operation.Operation, httpContext);
        if (!decision.IsAllowed)
        {
            await responseWriter.WriteAsync(
                    httpContext,
                    decision,
                    httpContext.RequestAborted)
                .ConfigureAwait(false);
            return;
        }

        if (httpContext.Request.ContentLength > effectiveBodyLimit)
        {
            RejectOversizedRequest(httpContext);
            return;
        }

        var originalBody = httpContext.Request.Body;
        MemoryStream? bufferedBody = null;
        try
        {
            if (httpContext.Request.ContentLength is null)
            {
                try
                {
                    bufferedBody = await BufferWithinLimitAsync(
                            originalBody,
                            checked((int)effectiveBodyLimit),
                            httpContext.RequestAborted)
                        .ConfigureAwait(false);
                }
                catch (BadHttpRequestException exception)
                    when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    RejectOversizedRequest(httpContext);
                    return;
                }
                if (bufferedBody is null)
                {
                    RejectOversizedRequest(httpContext);
                    return;
                }

                httpContext.Request.Body = bufferedBody;
            }

            await next(httpContext).ConfigureAwait(false);
        }
        finally
        {
            if (bufferedBody is not null)
            {
                httpContext.Request.Body = originalBody;
                await bufferedBody.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<MemoryStream?> BufferWithinLimitAsync(
        Stream requestBody,
        int bodyLimit,
        CancellationToken cancellationToken)
    {
        var bytes = new byte[bodyLimit + 1];
        var bytesRead = 0;
        while (bytesRead < bytes.Length)
        {
            var read = await requestBody.ReadAsync(
                    bytes.AsMemory(bytesRead, bytes.Length - bytesRead),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return new MemoryStream(bytes, 0, bytesRead, writable: false, publiclyVisible: false);
            }

            bytesRead += read;
        }

        return null;
    }

    private static void RejectOversizedRequest(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        httpContext.Response.Headers.CacheControl = "no-store";
    }
}
