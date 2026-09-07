using System.Net;
using System.Net.Sockets;
using Promptly.Application.Interfaces;

namespace Promptly.Infrastructure.Networking;

/// <summary>
/// Resolves both IPv4 and IPv6 destination addresses while preserving caller cancellation.
/// </summary>
public sealed class SystemDestinationAddressResolver : IDestinationAddressResolver
{
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _lookup;

    public SystemDestinationAddressResolver()
        : this((host, cancellationToken) =>
            Dns.GetHostAddressesAsync(host, AddressFamily.Unspecified, cancellationToken))
    {
    }

    public SystemDestinationAddressResolver(
        Func<string, CancellationToken, Task<IPAddress[]>> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        _lookup = lookup;
    }

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string canonicalHost,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalHost);
        cancellationToken.ThrowIfCancellationRequested();

        var resolved = await _lookup(canonicalHost, cancellationToken).ConfigureAwait(false);
        var addresses = new List<IPAddress>(resolved.Length);
        foreach (var address in resolved)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (address is null)
            {
                throw new InvalidOperationException(
                    "DNS resolution returned an invalid null address.");
            }

            var normalized = address.IsIPv4MappedToIPv6
                ? address.MapToIPv4()
                : address;
            if (addresses.Contains(normalized))
            {
                continue;
            }

            addresses.Add(normalized);
        }

        return addresses.AsReadOnly();
    }
}
