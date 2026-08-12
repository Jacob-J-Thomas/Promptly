using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Promptly.Domain.Entities;
using Promptly.Server.Security;

namespace Promptly.IntegrationTests;

internal sealed class TestAuthenticationPartitionKeyProvider
    : IAuthenticationPartitionKeyProvider
{
    public const string ClientHeaderName = "X-Promptly-Test-Client";
    public const string AccountKeyPrefix = "auth-integration-account:";
    private const string DefaultClient = "default-test-client";
    private int _clientKeyCallCount;
    private int _accountKeyCallCount;

    public int ClientKeyCallCount => Volatile.Read(ref _clientKeyCallCount);

    public int AccountKeyCallCount => Volatile.Read(ref _accountKeyCallCount);

    public string GetClientKey(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        Interlocked.Increment(ref _clientKeyCallCount);

        var values = httpContext.Request.Headers[ClientHeaderName];
        var client = values.Count == 1 && !string.IsNullOrWhiteSpace(values[0])
            ? values[0]!.Trim()
            : DefaultClient;
        return Hash("client", client);
    }

    public string GetAccountKey(string normalizedAccount)
    {
        ArgumentNullException.ThrowIfNull(normalizedAccount);
        Interlocked.Increment(ref _accountKeyCallCount);
        return string.Concat(AccountKeyPrefix, normalizedAccount);
    }

    private static string Hash(string kind, string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{value}")));
}

internal sealed class AuthenticationBodyReadProbe
{
    public const string HeaderName = "X-Promptly-Test-Probe-Authentication-Body";
    private int _readCount;

    public int ReadCount => Volatile.Read(ref _readCount);

    public Stream Wrap(Stream inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new CountingReadStream(inner, this);
    }

    private void RecordRead() => Interlocked.Increment(ref _readCount);

    private sealed class CountingReadStream(
        Stream inner,
        AuthenticationBodyReadProbe owner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count)
        {
            owner.RecordRead();
            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            owner.RecordRead();
            return inner.Read(buffer);
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            owner.RecordRead();
            return inner.ReadAsync(buffer, offset, count, cancellationToken);
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            owner.RecordRead();
            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) =>
            inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            // ASP.NET Core owns the underlying request body stream.
            base.Dispose(disposing);
        }
    }
}

internal sealed class AuthenticationBodyReadProbeStartupFilter(
    AuthenticationBodyReadProbe probe) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        applicationBuilder =>
        {
            applicationBuilder.Use(async (context, nextMiddleware) =>
            {
                if (!context.Request.Headers.ContainsKey(AuthenticationBodyReadProbe.HeaderName))
                {
                    await nextMiddleware();
                    return;
                }

                var originalBody = context.Request.Body;
                using var observedBody = probe.Wrap(originalBody);
                context.Request.Body = observedBody;
                try
                {
                    await nextMiddleware();
                }
                finally
                {
                    context.Request.Body = originalBody;
                }
            });
            next(applicationBuilder);
        };
}

internal sealed class FixedRemoteIpAddressStartupFilter(IPAddress remoteIpAddress)
    : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        applicationBuilder =>
        {
            applicationBuilder.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = remoteIpAddress;
                await nextMiddleware();
            });
            next(applicationBuilder);
        };
}

internal sealed class AuthenticationCredentialGate
{
    private readonly object _gate = new();
    private readonly HashSet<string> _targets = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _entered = new(StringComparer.OrdinalIgnoreCase);
    private readonly TaskCompletionSource _allEntered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private int _verificationCallCount;

    public int VerificationCallCount => Volatile.Read(ref _verificationCallCount);

    public void Hold(params string[] emails)
    {
        ArgumentNullException.ThrowIfNull(emails);
        lock (_gate)
        {
            if (_targets.Count != 0 || emails.Length == 0)
            {
                throw new InvalidOperationException("The credential gate can be configured once.");
            }

            if (emails.Distinct(StringComparer.OrdinalIgnoreCase).Count() != emails.Length)
            {
                throw new ArgumentException("Credential gate targets must be unique.", nameof(emails));
            }

            _targets.UnionWith(emails);
        }
    }

    public Task WaitUntilHeldAsync(CancellationToken cancellationToken) =>
        _allEntered.Task.WaitAsync(cancellationToken);

    public void Release() => _release.TrySetResult();

    public async Task BeforeVerifyAsync(string email, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _verificationCallCount);
        Task? release = null;
        lock (_gate)
        {
            if (_targets.Contains(email))
            {
                _entered.Add(email);
                if (_entered.Count == _targets.Count)
                {
                    _allEntered.TrySetResult();
                }

                release = _release.Task;
            }
        }

        if (release is not null)
        {
            await release.WaitAsync(cancellationToken);
        }
    }
}

internal sealed class GatedIdentityCredentialVerifier(
    AuthenticationCredentialGate gate,
    IdentityCredentialVerifier inner) : IIdentityCredentialVerifier
{
    public async Task<User?> VerifyAsync(
        string email,
        string password,
        CancellationToken cancellationToken)
    {
        await gate.BeforeVerifyAsync(email, cancellationToken);
        return await inner.VerifyAsync(email, password, cancellationToken);
    }
}
