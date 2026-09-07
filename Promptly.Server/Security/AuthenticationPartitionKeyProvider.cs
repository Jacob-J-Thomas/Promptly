using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Promptly.Server.Security;

public interface IAuthenticationPartitionKeyProvider
{
    string GetClientKey(HttpContext httpContext);

    string GetAccountKey(string normalizedAccount);
}

public sealed class AuthenticationPartitionKeyProvider :
    IAuthenticationPartitionKeyProvider,
    IDisposable
{
    private const string ForwardedForHeaderName = "X-Forwarded-For";
    private const string MissingPeerSentinel = "missing-peer";
    private readonly object _keyGate = new();
    private readonly byte[] _hmacKey = RandomNumberGenerator.GetBytes(32);
    private readonly IPNetwork[] _trustedProxyNetworks;
    private bool _disposed;

    public AuthenticationPartitionKeyProvider(IOptions<AuthenticationAbuseOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _trustedProxyNetworks = (options.Value.TrustedProxyNetworks ?? [])
            .Select(network => IPNetwork.TryParse(network, out var parsed)
                ? (IPNetwork?)parsed
                : null)
            .Where(network => network.HasValue)
            .Select(network => network.GetValueOrDefault())
            .ToArray();
    }

    public string GetClientKey(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var directPeer = Normalize(httpContext.Connection.RemoteIpAddress);
        var clientAddress = directPeer;
        if (directPeer is not null
            && IsTrustedProxy(directPeer)
            && TryGetSingleForwardedAddress(httpContext, out var forwardedAddress))
        {
            clientAddress = forwardedAddress;
        }

        clientAddress = CanonicalizeClientAddress(clientAddress);
        return Hash("client", clientAddress?.ToString() ?? MissingPeerSentinel);
    }

    public string GetAccountKey(string normalizedAccount)
    {
        ArgumentNullException.ThrowIfNull(normalizedAccount);
        return Hash("account", normalizedAccount);
    }

    public void Dispose()
    {
        lock (_keyGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            CryptographicOperations.ZeroMemory(_hmacKey);
        }
    }

    private bool IsTrustedProxy(IPAddress address) =>
        _trustedProxyNetworks.Any(network => network.Contains(address));

    private string Hash(string partitionKind, string value)
    {
        lock (_keyGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var payload = Encoding.UTF8.GetBytes(string.Concat(partitionKind, ":", value));
            try
            {
                return Convert.ToHexStringLower(HMACSHA256.HashData(_hmacKey, payload));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
    }

    private static bool TryGetSingleForwardedAddress(
        HttpContext httpContext,
        out IPAddress? address)
    {
        address = null;
        var values = httpContext.Request.Headers[ForwardedForHeaderName];
        if (values.Count != 1 || values[0] is not { } headerValue)
        {
            return false;
        }

        var candidate = headerValue.Trim();
        if (candidate.Length == 0
            || candidate.Contains(',', StringComparison.Ordinal)
            || candidate.Contains('%', StringComparison.Ordinal)
            || !TryParseStrictIpLiteral(candidate, out var parsed))
        {
            return false;
        }

        parsed = Normalize(parsed)!;
        if (!IsUnscopedUnicast(parsed))
        {
            return false;
        }

        address = parsed;
        return true;
    }

    private static bool TryParseStrictIpLiteral(string value, out IPAddress address)
    {
        address = IPAddress.None;

        if (value.Contains(':', StringComparison.Ordinal))
        {
            return !value.Contains('[', StringComparison.Ordinal)
                && !value.Contains(']', StringComparison.Ordinal)
                && IPAddress.TryParse(value, out address!);
        }

        var octets = value.Split('.');
        if (octets.Length != 4)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[4];
        for (var index = 0; index < octets.Length; index++)
        {
            var octet = octets[index];
            if (octet.Length is < 1 or > 3
                || octet.Any(character => character is < '0' or > '9')
                || !byte.TryParse(octet, NumberStyles.None, CultureInfo.InvariantCulture, out bytes[index]))
            {
                return false;
            }
        }

        address = new IPAddress(bytes);
        return true;
    }

    private static bool IsUnscopedUnicast(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return !address.Equals(IPAddress.Any)
                && !address.Equals(IPAddress.Broadcast)
                && bytes[0] is < 224 or > 239;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6
            || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        return address.GetAddressBytes()[0] != 0xff;
    }

    private static IPAddress? Normalize(IPAddress? address)
    {
        if (address?.IsIPv4MappedToIPv6 == true)
        {
            return address.MapToIPv4();
        }

        return address;
    }

    private static IPAddress? CanonicalizeClientAddress(IPAddress? address)
    {
        address = Normalize(address);
        if (address?.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }
}
