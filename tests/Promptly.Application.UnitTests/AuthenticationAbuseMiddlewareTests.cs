using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Promptly.Server.Models;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class AuthenticationAbuseMiddlewareTests
{
    [Fact]
    public async Task EndpointWithoutMetadataBypassesGuardAndInvokesNext()
    {
        var context = ContextWithEndpoint();
        var guard = new StubGuard
        {
            ClientDecision = AuthenticationThrottleDecision.RejectAfter(8)
        };
        var writer = new StubWriter();
        var nextCalled = false;
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, guard, new StubAdmissionGate(), writer);

        Assert.True(nextCalled);
        Assert.Equal(0, guard.ClientCalls);
        Assert.Equal(0, writer.WriteCalls);
    }

    [Fact]
    public async Task MissingEndpointAlsoBypassesGuard()
    {
        var context = new DefaultHttpContext();
        var guard = new StubGuard();
        var writer = new StubWriter();
        var nextCalled = false;
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, guard, new StubAdmissionGate(), writer);

        Assert.True(nextCalled);
        Assert.Equal(0, guard.ClientCalls);
    }

    [Theory]
    [InlineData(AuthenticationOperation.Login)]
    [InlineData(AuthenticationOperation.Registration)]
    public async Task MetadataConsumesMatchingClientGateAndAllowedRequestInvokesNext(
        AuthenticationOperation operation)
    {
        var context = ContextWithEndpoint(new AuthenticationAbuseOperationAttribute(operation));
        var guard = new StubGuard();
        var writer = new StubWriter();
        var nextCalled = false;
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, guard, new StubAdmissionGate(), writer);

        Assert.True(nextCalled);
        Assert.Equal(1, guard.ClientCalls);
        Assert.Equal(operation, guard.LastOperation);
        Assert.Same(context, guard.LastContext);
        Assert.Equal(0, writer.WriteCalls);
    }

    [Fact]
    public async Task ClientAdmissionRunsBeforeAggregateAdmissionAndDownstream()
    {
        var events = new List<string>();
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        var guard = new StubGuard { OnClientCall = () => events.Add("client") };
        var admissionGate = new StubAdmissionGate
        {
            OnTryAcquire = () => events.Add("aggregate")
        };
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            events.Add("downstream");
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, guard, admissionGate, new StubWriter());

        Assert.Equal(new[] { "client", "aggregate", "downstream" }, events);
        Assert.Equal(1, admissionGate.ReleaseCalls);
    }

    [Fact]
    public async Task AuthenticationEndpointAppliesTransportBodyLimitBeforeInvokingNext()
    {
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        var sizeFeature = new StubRequestSizeFeature();
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(sizeFeature);
        var guard = new StubGuard();
        var admissionGate = new StubAdmissionGate();
        var writer = new StubWriter();
        var nextCalled = false;
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, guard, admissionGate, writer);

        Assert.True(nextCalled);
        Assert.Equal(AuthenticationInputLimits.RequestBodyMaxBytes, sizeFeature.MaxRequestBodySize);
        Assert.Equal(1, guard.ClientCalls);
    }

    [Fact]
    public async Task BodyAtExactLimitInvokesNext()
    {
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        context.Request.ContentLength = AuthenticationInputLimits.RequestBodyMaxBytes;
        var guard = new StubGuard();
        var nextCalled = false;
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            context,
            guard,
            new StubAdmissionGate(),
            new StubWriter());

        Assert.True(nextCalled);
        Assert.Equal(1, guard.ClientCalls);
    }

    [Fact]
    public async Task ReadOnlyTransportBodyLimitIsPreserved()
    {
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        var sizeFeature = new StubRequestSizeFeature(
            isReadOnly: true,
            maxRequestBodySize: 16 * 1024);
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(sizeFeature);
        var middleware = new AuthenticationAbuseMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(
            context,
            new StubGuard(),
            new StubAdmissionGate(),
            new StubWriter());

        Assert.Equal(16 * 1024, sizeFeature.MaxRequestBodySize);
    }

    [Fact]
    public async Task StricterWritableTransportBodyLimitIsPreserved()
    {
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        var sizeFeature = new StubRequestSizeFeature(
            maxRequestBodySize: AuthenticationInputLimits.RequestBodyMaxBytes / 2);
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(sizeFeature);
        var middleware = new AuthenticationAbuseMiddleware(_ => Task.CompletedTask);

        await middleware.InvokeAsync(
            context,
            new StubGuard(),
            new StubAdmissionGate(),
            new StubWriter());

        Assert.Equal(
            AuthenticationInputLimits.RequestBodyMaxBytes / 2,
            sizeFeature.MaxRequestBodySize);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StricterTransportBodyLimitRejectsKnownAndUnknownLengths(
        bool isReadOnly,
        bool hasKnownLength)
    {
        var stricterLimit = AuthenticationInputLimits.RequestBodyMaxBytes / 2;
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        var sizeFeature = new StubRequestSizeFeature(
            isReadOnly: isReadOnly,
            maxRequestBodySize: stricterLimit);
        context.Features.Set<IHttpMaxRequestBodySizeFeature>(sizeFeature);
        if (hasKnownLength)
        {
            context.Request.ContentLength = stricterLimit + 1;
        }
        else
        {
            context.Request.Body = new NonSeekableReadStream(new byte[stricterLimit + 1]);
        }

        var guard = new StubGuard();
        var nextCalled = false;
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(
            context,
            guard,
            new StubAdmissionGate(),
            new StubWriter());

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal(stricterLimit, sizeFeature.MaxRequestBodySize);
        Assert.Equal(1, guard.ClientCalls);
    }

    [Theory]
    [InlineData(AuthenticationOperation.Login)]
    [InlineData(AuthenticationOperation.Registration)]
    public async Task OversizedKnownLengthReturns413AfterClientAdmissionWithoutInvokingNext(
        AuthenticationOperation operation)
    {
        var context = ContextWithEndpoint(new AuthenticationAbuseOperationAttribute(operation));
        context.Request.ContentLength = AuthenticationInputLimits.RequestBodyMaxBytes + 1;
        var guard = new StubGuard();
        var admissionGate = new StubAdmissionGate();
        var writer = new StubWriter();
        var nextCalled = false;
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, guard, admissionGate, writer);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal(1, guard.ClientCalls);
        Assert.Equal(1, admissionGate.ReleaseCalls);
        Assert.Equal(0, writer.WriteCalls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OversizedUnknownLengthReturns413IndependentlyOfTransportFeature(
        bool useReadOnlyFeature,
        bool useWritableFeature)
    {
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        context.Request.Body = new NonSeekableReadStream(
            new byte[AuthenticationInputLimits.RequestBodyMaxBytes + 1]);
        if (useReadOnlyFeature || useWritableFeature)
        {
            context.Features.Set<IHttpMaxRequestBodySizeFeature>(
                new StubRequestSizeFeature(
                    isReadOnly: useReadOnlyFeature,
                    maxRequestBodySize: 16 * 1024));
        }

        var guard = new StubGuard();
        var admissionGate = new StubAdmissionGate();
        var writer = new StubWriter();
        var nextCalled = false;
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, guard, admissionGate, writer);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal(1, guard.ClientCalls);
        Assert.Equal(1, admissionGate.ReleaseCalls);
        Assert.Equal(0, writer.WriteCalls);
    }

    [Fact]
    public async Task ThrottledUnknownLengthRequestIsRejectedWithoutReadingBody()
    {
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        var body = new ThrowOnReadStream();
        context.Request.Body = body;
        var decision = AuthenticationThrottleDecision.RejectAfter(4);
        var guard = new StubGuard { ClientDecision = decision };
        var admissionGate = new StubAdmissionGate();
        var writer = new StubWriter();
        var middleware = new AuthenticationAbuseMiddleware(_ =>
            throw new InvalidOperationException("Throttled request reached the next middleware."));

        await middleware.InvokeAsync(context, guard, admissionGate, writer);

        Assert.Equal(0, body.ReadCount);
        Assert.Equal(1, guard.ClientCalls);
        Assert.Equal(0, admissionGate.Calls);
        Assert.Equal(1, writer.WriteCalls);
        Assert.Equal(decision, writer.LastDecision);
    }

    [Theory]
    [InlineData(AuthenticationOperation.Login)]
    [InlineData(AuthenticationOperation.Registration)]
    public async Task AggregateRejectionReturnsGeneric429WithoutReadingBody(
        AuthenticationOperation operation)
    {
        var context = ContextWithEndpoint(new AuthenticationAbuseOperationAttribute(operation));
        var body = new ThrowOnReadStream();
        context.Request.Body = body;
        var admissionGate = new StubAdmissionGate { Reject = true };
        var writer = new StubWriter();
        var middleware = new AuthenticationAbuseMiddleware(_ =>
            throw new InvalidOperationException("Rejected request reached downstream."));

        await middleware.InvokeAsync(context, new StubGuard(), admissionGate, writer);

        Assert.Equal(0, body.ReadCount);
        Assert.Equal(1, admissionGate.Calls);
        Assert.Equal(operation, admissionGate.LastOperation);
        Assert.Equal(0, admissionGate.ReleaseCalls);
        Assert.Equal(1, writer.WriteCalls);
        Assert.False(writer.LastDecision.IsAllowed);
        Assert.Equal(1, writer.LastDecision.RetryAfterSeconds);
    }

    [Fact]
    public async Task AdmissionLeaseRemainsHeldUntilDownstreamResponseCompletes()
    {
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        context.Request.ContentLength = 0;
        context.Response.Body = new MemoryStream();
        var admissionGate = new StubAdmissionGate();
        var downstreamEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var completeResponse = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var middleware = new AuthenticationAbuseMiddleware(async downstreamContext =>
        {
            downstreamEntered.SetResult();
            await completeResponse.Task.WaitAsync(downstreamContext.RequestAborted);
            await downstreamContext.Response.WriteAsync("complete");
        });

        var invocation = middleware.InvokeAsync(
            context,
            new StubGuard(),
            admissionGate,
            new StubWriter());
        await downstreamEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, admissionGate.ReleaseCalls);
        completeResponse.SetResult();
        await invocation;
        Assert.Equal(1, admissionGate.ReleaseCalls);
        Assert.Equal("complete", await ReadResponseBodyAsync(context));
    }

    [Fact]
    public async Task ValidUnknownLengthBodyIsBufferedForDownstreamAndOriginalStreamIsRestored()
    {
        var payload = Encoding.UTF8.GetBytes("{\"email\":\"member@example.test\"}");
        var originalBody = new NonSeekableReadStream(payload);
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        context.Request.Body = originalBody;
        var guard = new StubGuard();
        string? downstreamBody = null;
        var middleware = new AuthenticationAbuseMiddleware(async downstreamContext =>
        {
            using var reader = new StreamReader(
                downstreamContext.Request.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            downstreamBody = await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
        });

        await middleware.InvokeAsync(
            context,
            guard,
            new StubAdmissionGate(),
            new StubWriter());

        Assert.Equal(Encoding.UTF8.GetString(payload), downstreamBody);
        Assert.Same(originalBody, context.Request.Body);
        Assert.Equal(1, guard.ClientCalls);
    }

    [Fact]
    public async Task Transport413WhileBufferingReturnsPrivateNoStore413()
    {
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        var originalBody = new BadRequestStream(
            new BadHttpRequestException(
                "Request body too large.",
                StatusCodes.Status413PayloadTooLarge));
        context.Request.Body = originalBody;
        var guard = new StubGuard();
        var admissionGate = new StubAdmissionGate();
        var nextCalled = false;
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, guard, admissionGate, new StubWriter());

        Assert.False(nextCalled);
        Assert.Same(originalBody, context.Request.Body);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal(1, guard.ClientCalls);
        Assert.Equal(1, admissionGate.ReleaseCalls);
    }

    [Fact]
    public async Task Non413TransportBadRequestPropagates()
    {
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        var exception = new BadHttpRequestException(
            "Malformed request body.",
            StatusCodes.Status400BadRequest);
        context.Request.Body = new BadRequestStream(exception);
        var middleware = new AuthenticationAbuseMiddleware(_ => Task.CompletedTask);
        var admissionGate = new StubAdmissionGate();

        var thrown = await Assert.ThrowsAsync<BadHttpRequestException>(() =>
            middleware.InvokeAsync(
                context,
                new StubGuard(),
                admissionGate,
                new StubWriter()));

        Assert.Same(exception, thrown);
        Assert.Equal(1, admissionGate.ReleaseCalls);
    }

    [Fact]
    public async Task DownstreamExceptionPropagatesAndReleasesAdmission()
    {
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        context.Request.ContentLength = 0;
        var exception = new InvalidOperationException("Downstream failure.");
        var admissionGate = new StubAdmissionGate();
        var middleware = new AuthenticationAbuseMiddleware(_ => Task.FromException(exception));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            middleware.InvokeAsync(
                context,
                new StubGuard(),
                admissionGate,
                new StubWriter()));

        Assert.Same(exception, thrown);
        Assert.Equal(1, admissionGate.ReleaseCalls);
    }

    [Fact]
    public async Task RequestCancellationWhileReadingBodyReleasesAdmission()
    {
        using var cancellation = new CancellationTokenSource();
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        context.Request.Body = new NonSeekableReadStream([1]);
        context.RequestAborted = cancellation.Token;
        cancellation.Cancel();
        var admissionGate = new StubAdmissionGate();
        var middleware = new AuthenticationAbuseMiddleware(_ => Task.CompletedTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            middleware.InvokeAsync(
                context,
                new StubGuard(),
                admissionGate,
                new StubWriter()));

        Assert.Equal(1, admissionGate.ReleaseCalls);
    }

    [Fact]
    public async Task RejectionShortCircuitsAndForwardsRequestAbortToWriter()
    {
        using var cancellation = new CancellationTokenSource();
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        context.RequestAborted = cancellation.Token;
        var decision = AuthenticationThrottleDecision.RejectAfter(12);
        var guard = new StubGuard { ClientDecision = decision };
        var writer = new StubWriter();
        var nextCalled = false;
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, guard, new StubAdmissionGate(), writer);

        Assert.False(nextCalled);
        Assert.Equal(1, writer.WriteCalls);
        Assert.Equal(decision, writer.LastDecision);
        Assert.Same(context, writer.LastContext);
        Assert.Equal(cancellation.Token, writer.LastCancellationToken);
    }

    [Fact]
    public async Task WriterCancellationPropagatesAndNextRemainsSkipped()
    {
        var context = ContextWithEndpoint(
            new AuthenticationAbuseOperationAttribute(AuthenticationOperation.Login));
        var guard = new StubGuard
        {
            ClientDecision = AuthenticationThrottleDecision.RejectAfter(1)
        };
        var writer = new StubWriter
        {
            Exception = new OperationCanceledException(context.RequestAborted)
        };
        var nextCalled = false;
        var middleware = new AuthenticationAbuseMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            middleware.InvokeAsync(context, guard, new StubAdmissionGate(), writer));

        Assert.False(nextCalled);
    }

    [Fact]
    public async Task NullAdmissionGateIsRejected()
    {
        var middleware = new AuthenticationAbuseMiddleware(_ => Task.CompletedTask);

        await Assert.ThrowsAsync<ArgumentNullException>(() => middleware.InvokeAsync(
            ContextWithEndpoint(),
            new StubGuard(),
            null!,
            new StubWriter()));
    }

    private static async Task<string> ReadResponseBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        return await reader.ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    private static DefaultHttpContext ContextWithEndpoint(params object[] metadata)
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(metadata),
            "test"));
        return context;
    }

    private sealed class StubGuard : IAuthenticationAbuseGuard
    {
        public AuthenticationThrottleDecision ClientDecision { get; set; } =
            AuthenticationThrottleDecision.Allowed;

        public int ClientCalls { get; private set; }

        public AuthenticationOperation LastOperation { get; private set; }

        public HttpContext? LastContext { get; private set; }

        public Action? OnClientCall { get; init; }

        public AuthenticationThrottleDecision TryAcquireClient(
            AuthenticationOperation operation,
            HttpContext httpContext)
        {
            OnClientCall?.Invoke();
            ClientCalls++;
            LastOperation = operation;
            LastContext = httpContext;
            return ClientDecision;
        }

        public ValueTask<AuthenticationAccountAttempt> BeginAccountAttemptAsync(
            AuthenticationOperation operation,
            HttpContext httpContext,
            string normalizedAccount,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public AuthenticationThrottleDecision RecordLoginFailure(
            AuthenticationAccountAttempt attempt) =>
            throw new NotSupportedException();
    }

    private sealed class StubAdmissionGate : IAuthenticationRequestAdmissionGate
    {
        private int _releaseCalls;

        public bool Reject { get; init; }

        public Action? OnTryAcquire { get; init; }

        public int Calls { get; private set; }

        public int ReleaseCalls => Volatile.Read(ref _releaseCalls);

        public AuthenticationOperation LastOperation { get; private set; }

        public IAuthenticationRequestAdmissionLease? TryAcquire(
            AuthenticationOperation operation)
        {
            OnTryAcquire?.Invoke();
            Calls++;
            LastOperation = operation;
            return Reject
                ? null
                : new StubAdmissionLease(() => Interlocked.Increment(ref _releaseCalls));
        }
    }

    private sealed class StubAdmissionLease(Action release) : IAuthenticationRequestAdmissionLease
    {
        public void Dispose() => release();
    }

    private sealed class StubWriter : IAuthenticationThrottleResponseWriter
    {
        public int WriteCalls { get; private set; }

        public HttpContext? LastContext { get; private set; }

        public AuthenticationThrottleDecision LastDecision { get; private set; }

        public CancellationToken LastCancellationToken { get; private set; }

        public Exception? Exception { get; init; }

        public Task WriteAsync(
            HttpContext httpContext,
            AuthenticationThrottleDecision decision,
            CancellationToken cancellationToken)
        {
            WriteCalls++;
            LastContext = httpContext;
            LastDecision = decision;
            LastCancellationToken = cancellationToken;
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }

        public Microsoft.AspNetCore.Mvc.IActionResult CreateActionResult(
            AuthenticationThrottleDecision decision) =>
            throw new NotSupportedException();
    }

    private sealed class StubRequestSizeFeature(
        bool isReadOnly = false,
        long? maxRequestBodySize = null) : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly { get; } = isReadOnly;

        public long? MaxRequestBodySize { get; set; } = maxRequestBodySize;
    }

    private sealed class NonSeekableReadStream(byte[] contents) : Stream
    {
        private readonly MemoryStream _inner = new(contents, writable: false);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowOnReadStream : Stream
    {
        public int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("Authentication body must not be read.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromException<int>(
                new InvalidOperationException("Authentication body must not be read."));
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class BadRequestStream(BadHttpRequestException exception) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw exception;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
