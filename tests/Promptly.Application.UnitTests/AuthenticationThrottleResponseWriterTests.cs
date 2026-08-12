using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class AuthenticationThrottleResponseWriterTests
{
    [Fact]
    public async Task WriteAsyncProducesStablePrivateProblemContract()
    {
        var writer = CreateWriter(maximumRetryAfterSeconds: 900);
        var context = WritableContext();

        await writer.WriteAsync(
            context,
            AuthenticationThrottleDecision.RejectAfter(17),
            TestContext.Current.CancellationToken);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal("17", context.Response.Headers.RetryAfter);
        var json = await ReadJson(context);
        Assert.Equal(
            AuthenticationThrottleResponseWriter.ProblemType,
            json.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            AuthenticationThrottleResponseWriter.ProblemTitle,
            json.RootElement.GetProperty("title").GetString());
        Assert.Equal(429, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(
            AuthenticationThrottleResponseWriter.ProblemCode,
            json.RootElement.GetProperty("code").GetString());
        Assert.Equal(4, json.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain("identifier", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("account", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", json.RootElement.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EveryThrottleCauseUsesIdenticalBodyAndOnlyRetryVaries()
    {
        var writer = CreateWriter(maximumRetryAfterSeconds: 900);
        var first = WritableContext();
        var second = WritableContext();

        await writer.WriteAsync(
            first,
            AuthenticationThrottleDecision.RejectAfter(2),
            TestContext.Current.CancellationToken);
        await writer.WriteAsync(
            second,
            AuthenticationThrottleDecision.RejectAfter(300),
            TestContext.Current.CancellationToken);

        Assert.Equal(await ReadBody(first), await ReadBody(second));
        Assert.Equal("2", first.Response.Headers.RetryAfter);
        Assert.Equal("300", second.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task WriterDefensivelyClampsRetryAfterAtPublicBoundary()
    {
        var writer = CreateWriter(maximumRetryAfterSeconds: 9);
        var context = WritableContext();

        await writer.WriteAsync(
            context,
            AuthenticationThrottleDecision.RejectAfter(int.MaxValue),
            TestContext.Current.CancellationToken);

        Assert.Equal("9", context.Response.Headers.RetryAfter);
    }

    [Fact]
    public async Task ActionResultExecutesTheSameResponseContractAndHonorsAbortToken()
    {
        var writer = CreateWriter(maximumRetryAfterSeconds: 900);
        using var cancellation = new CancellationTokenSource();
        var context = WritableContext();
        context.RequestAborted = cancellation.Token;
        var actionContext = new ActionContext { HttpContext = context };

        var result = writer.CreateActionResult(AuthenticationThrottleDecision.RejectAfter(11));
        await result.ExecuteResultAsync(actionContext);

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.Equal("11", context.Response.Headers.RetryAfter);
        using var json = await ReadJson(context);
        Assert.Equal(
            AuthenticationThrottleResponseWriter.ProblemCode,
            json.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task CanceledWritePropagatesCancellation()
    {
        var writer = CreateWriter(maximumRetryAfterSeconds: 900);
        var context = WritableContext();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.WriteAsync(
            context,
            AuthenticationThrottleDecision.RejectAfter(1),
            cancellation.Token));
    }

    [Fact]
    public void AllowedOrDefaultDecisionCannotProduceThrottleResponse()
    {
        var writer = CreateWriter(maximumRetryAfterSeconds: 900);

        Assert.Throws<ArgumentException>(() =>
            writer.CreateActionResult(AuthenticationThrottleDecision.Allowed));
        Assert.Throws<ArgumentException>(() =>
            writer.CreateActionResult(default));
    }

    [Fact]
    public void ConstructorValidatesOptionsBoundary()
    {
        Assert.Throws<ArgumentNullException>(() => new AuthenticationThrottleResponseWriter(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CreateWriter(maximumRetryAfterSeconds: 0));
    }

    private static AuthenticationThrottleResponseWriter CreateWriter(int maximumRetryAfterSeconds) =>
        new(Options.Create(new AuthenticationAbuseOptions
        {
            MaximumRetryAfterSeconds = maximumRetryAfterSeconds
        }));

    private static DefaultHttpContext WritableContext()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonDocument> ReadJson(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        return await JsonDocument.ParseAsync(
            context.Response.Body,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<string> ReadBody(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(
            context.Response.Body,
            leaveOpen: true);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
