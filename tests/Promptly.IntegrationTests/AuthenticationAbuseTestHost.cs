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
    private int _accountKeyCallCount;

    public int AccountKeyCallCount => Volatile.Read(ref _accountKeyCallCount);

    public string GetClientKey(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

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

            foreach (var email in emails)
            {
                if (!_targets.Add(email))
                {
                    throw new ArgumentException("Credential gate targets must be unique.", nameof(emails));
                }
            }
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
