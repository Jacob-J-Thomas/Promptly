using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Promptly.Application.Models;

public enum EndpointAddressClassification
{
    GloballyReachable,
    AllowlistEligible,
    PermanentlyDenied
}

public enum EndpointDestinationRejectionReason
{
    InvalidDestination,
    ResolutionFailed,
    NoAddresses,
    NonPublicDestination,
    PermanentlyDeniedDestination
}

public sealed class EndpointDestinationRejectedException : InvalidOperationException
{
    public const string SafeMessage = "Unsafe endpoint destination";

    public EndpointDestinationRejectedException(
        EndpointDestinationRejectionReason reason,
        Exception? innerException = null)
        : base(SafeMessage, innerException)
    {
        Reason = reason;
    }

    public EndpointDestinationRejectionReason Reason { get; }
}

/// <summary>
/// Classifies endpoint addresses using the IANA IPv4 and IPv6 special-purpose
/// registries as retrieved 2026-08-12. Non-public exceptions are deliberately
/// limited to exact operator-owned origin and CIDR rules.
/// </summary>
public static class EndpointDestinationPolicy
{
    private static readonly IPNetwork[] AllowlistEligibleNetworks = ParseNetworks(
        "10.0.0.0/8",
        "100.64.0.0/10",
        "127.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "::1/128",
        "fc00::/7");

    // Specific globally reachable assignments inside the otherwise special
    // 192.0.0.0/24 and 2001::/23 registry blocks are evaluated first.
    private static readonly IPNetwork[] SpecialGloballyReachableNetworks = ParseNetworks(
        "192.0.0.9/32",
        "192.0.0.10/32",
        "2001:1::1/128",
        "2001:1::2/128",
        "2001:1::3/128",
        "2001:3::/32",
        "2001:4:112::/48",
        "2001:20::/28",
        "2001:30::/28");

    private static readonly IPNetwork[] PermanentlyDeniedNetworks = ParseNetworks(
        "0.0.0.0/8",
        "169.254.0.0/16",
        "192.0.0.0/24",
        "192.0.2.0/24",
        "192.88.99.0/24",
        "198.18.0.0/15",
        "198.51.100.0/24",
        "203.0.113.0/24",
        "224.0.0.0/4",
        "240.0.0.0/4",
        "::/3",
        "::/128",
        "64:ff9b::/96",
        "64:ff9b:1::/48",
        "100::/64",
        "100:0:0:1::/64",
        "2001::/23",
        "2001:db8::/32",
        "2002::/16",
        "3fff::/20",
        "4000::/2",
        "5f00::/16",
        "8000::/1",
        "fe80::/10",
        "ff00::/8");

    private static readonly HashSet<IPAddress> PermanentlyDeniedAddresses = new(
        new[]
        {
            "100.100.100.200",
            "168.63.129.16",
            "169.254.169.254",
            "169.254.170.2",
            "192.0.0.192",
            "fd00:ec2::254",
            "fd20:ce::254"
        }.Select(IPAddress.Parse));

    public static EndpointAddressClassification Classify(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = NormalizeAddress(address);
        if (normalized.AddressFamily == AddressFamily.InterNetworkV6 && normalized.ScopeId != 0)
        {
            return EndpointAddressClassification.PermanentlyDenied;
        }

        if (PermanentlyDeniedAddresses.Contains(normalized))
        {
            return EndpointAddressClassification.PermanentlyDenied;
        }

        if (SpecialGloballyReachableNetworks.Any(network => network.Contains(normalized)))
        {
            return EndpointAddressClassification.GloballyReachable;
        }

        if (AllowlistEligibleNetworks.Any(network => network.Contains(normalized)))
        {
            return EndpointAddressClassification.AllowlistEligible;
        }

        return PermanentlyDeniedNetworks.Any(network => network.Contains(normalized))
            ? EndpointAddressClassification.PermanentlyDenied
            : EndpointAddressClassification.GloballyReachable;
    }

    public static bool IsAllowlistEligibleNetwork(IPNetwork network)
    {
        return AllowlistEligibleNetworks.Any(allowed =>
            allowed.BaseAddress.AddressFamily == network.BaseAddress.AddressFamily
            && network.PrefixLength >= allowed.PrefixLength
            && allowed.Contains(network.BaseAddress));
    }

    public static IPAddress NormalizeAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    public static bool TryCanonicalizeHost(
        string? host,
        out string canonicalHost)
    {
        canonicalHost = string.Empty;
        if (string.IsNullOrEmpty(host)
            || host.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || host.Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            var normalized = NormalizeAddress(address);
            if (normalized.AddressFamily == AddressFamily.InterNetworkV6
                && normalized.ScopeId != 0)
            {
                return false;
            }

            canonicalHost = normalized.ToString();
            return true;
        }

        var withoutRootLabel = host.EndsWith(".", StringComparison.Ordinal)
            ? host[..^1]
            : host;
        if (string.IsNullOrEmpty(withoutRootLabel))
        {
            return false;
        }

        try
        {
            canonicalHost = new IdnMapping().GetAscii(withoutRootLabel).ToLowerInvariant();
            if (canonicalHost.All(character =>
                    char.IsAsciiLetterOrDigit(character)
                    || character is '-' or '.'))
            {
                return true;
            }

            canonicalHost = string.Empty;
            return false;
        }
        catch (ArgumentException)
        {
            canonicalHost = string.Empty;
            return false;
        }
    }

    private static IPNetwork[] ParseNetworks(params string[] networks) =>
        networks.Select(IPNetwork.Parse).ToArray();

}
