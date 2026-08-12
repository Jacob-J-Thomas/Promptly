using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using Promptly.Application.Models;

namespace Promptly.Infrastructure.Configuration;

public sealed class EndpointEgressOptionsValidator : IValidateOptions<EndpointEgressOptions>
{
    public const int MinimumDnsTimeoutSeconds = 1;
    public const int MaximumDnsTimeoutSeconds = 120;
    public const int MinimumConnectTimeoutSeconds = 1;
    public const int MaximumConnectTimeoutSeconds = 120;
    public const int MinimumPooledConnectionLifetimeSeconds = 1;
    public const int MaximumPooledConnectionLifetimeSeconds = 86_400;

    public ValidateOptionsResult Validate(string? name, EndpointEgressOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (options.DnsTimeoutSeconds is < MinimumDnsTimeoutSeconds
            or > MaximumDnsTimeoutSeconds)
        {
            failures.Add(
                $"EndpointEgress:DnsTimeoutSeconds must be between " +
                $"{MinimumDnsTimeoutSeconds} and {MaximumDnsTimeoutSeconds}.");
        }

        if (options.ConnectTimeoutSeconds is < MinimumConnectTimeoutSeconds
            or > MaximumConnectTimeoutSeconds)
        {
            failures.Add(
                $"EndpointEgress:ConnectTimeoutSeconds must be between " +
                $"{MinimumConnectTimeoutSeconds} and {MaximumConnectTimeoutSeconds}.");
        }

        if (options.PooledConnectionLifetimeSeconds is < MinimumPooledConnectionLifetimeSeconds
            or > MaximumPooledConnectionLifetimeSeconds)
        {
            failures.Add(
                $"EndpointEgress:PooledConnectionLifetimeSeconds must be between " +
                $"{MinimumPooledConnectionLifetimeSeconds} and " +
                $"{MaximumPooledConnectionLifetimeSeconds}.");
        }

        ValidateProxy(options, failures);
        if ((options.RequireProxy || options.ProxyUrl is not null)
            && options.AllowedNonPublicDestinations is { Count: > 0 })
        {
            failures.Add(
                "EndpointEgress:AllowedNonPublicDestinations cannot be configured " +
                "when a proxy transport is selected.");
        }

        ValidateRules(options.AllowedNonPublicDestinations, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateProxy(
        EndpointEgressOptions options,
        ICollection<string> failures)
    {
        if (options.RequireProxy && string.IsNullOrWhiteSpace(options.ProxyUrl))
        {
            failures.Add("EndpointEgress:ProxyUrl is required when RequireProxy is true.");
        }

        if (options.ProxyUrl is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.ProxyUrl)
            || options.ProxyUrl.Any(character => char.IsControl(character) || char.IsWhiteSpace(character))
            || options.ProxyUrl.Contains('\\')
            || !Uri.TryCreate(options.ProxyUrl, UriKind.Absolute, out var proxyUri)
            || (proxyUri.Scheme != Uri.UriSchemeHttp && proxyUri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(proxyUri.IdnHost)
            || !string.IsNullOrEmpty(proxyUri.UserInfo)
            || proxyUri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(proxyUri.Query)
            || !string.IsNullOrEmpty(proxyUri.Fragment)
            || !IsCanonicalExactHost(proxyUri.IdnHost)
            || !IsCanonicalProxyOrigin(options.ProxyUrl, proxyUri))
        {
            failures.Add(
                "EndpointEgress:ProxyUrl must be an absolute HTTP(S) URL without user information.");
        }
    }

    private static void ValidateRules(
        IReadOnlyList<AllowedNonPublicDestinationRule>? rules,
        ICollection<string> failures)
    {
        if (rules is null)
        {
            failures.Add("EndpointEgress:AllowedNonPublicDestinations cannot be null.");
            return;
        }

        var ruleKeys = new HashSet<(string Host, int Port)>();
        for (var ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
        {
            var rule = rules[ruleIndex];
            if (rule is null)
            {
                failures.Add(
                    $"EndpointEgress:AllowedNonPublicDestinations:{ruleIndex} cannot be null.");
                continue;
            }

            var prefix = $"EndpointEgress:AllowedNonPublicDestinations:{ruleIndex}";
            var hostIsCanonical = IsCanonicalExactHost(rule.Host);
            if (!hostIsCanonical)
            {
                failures.Add(
                    $"{prefix}:Host must be an exact canonical ASCII lowercase host without " +
                    "wildcards, suffix rules, or a trailing dot.");
            }

            if (rule.Port is < 1 or > IPEndPoint.MaxPort)
            {
                failures.Add($"{prefix}:Port must be between 1 and 65535.");
            }

            if (hostIsCanonical && rule.Port is >= 1 and <= IPEndPoint.MaxPort
                && !ruleKeys.Add((rule.Host, rule.Port)))
            {
                failures.Add(
                    $"{prefix} duplicates an existing exact host and port rule.");
            }

            ValidateCidrs(rule.Cidrs, prefix, failures);
        }
    }

    private static void ValidateCidrs(
        IReadOnlyList<string>? cidrs,
        string prefix,
        ICollection<string> failures)
    {
        if (cidrs is null || cidrs.Count == 0)
        {
            failures.Add($"{prefix}:Cidrs must contain at least one canonical IPv4 or IPv6 CIDR.");
            return;
        }

        var uniqueCidrs = new HashSet<string>(StringComparer.Ordinal);
        for (var cidrIndex = 0; cidrIndex < cidrs.Count; cidrIndex++)
        {
            var cidr = cidrs[cidrIndex];
            if (string.IsNullOrEmpty(cidr)
                || !IPNetwork.TryParse(cidr, out var network)
                || !string.Equals(network.ToString(), cidr, StringComparison.Ordinal))
            {
                failures.Add(
                    $"{prefix}:Cidrs:{cidrIndex} must be a canonical IPv4 or IPv6 CIDR.");
                continue;
            }

            if (!EndpointDestinationPolicy.IsAllowlistEligibleNetwork(network))
            {
                failures.Add(
                    $"{prefix}:Cidrs:{cidrIndex} must be contained within a supported " +
                    "private, loopback, unique-local, or carrier-grade NAT range.");
            }

            if (!uniqueCidrs.Add(cidr))
            {
                failures.Add($"{prefix}:Cidrs:{cidrIndex} duplicates an existing CIDR.");
            }
        }
    }

    private static bool IsCanonicalExactHost(string? host)
    {
        if (string.IsNullOrEmpty(host)
            || !string.Equals(host, host.Trim(), StringComparison.Ordinal)
            || host.Contains('*')
            || host.StartsWith(".", StringComparison.Ordinal)
            || host.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                && address.ScopeId != 0)
            {
                return false;
            }

            var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            return string.Equals(normalized.ToString(), host, StringComparison.Ordinal);
        }

        if (!EndpointDestinationPolicy.TryCanonicalizeHost(host, out var canonicalHost)
            || !string.Equals(host, canonicalHost, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return string.Equals(new IdnMapping().GetAscii(host), host, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsCanonicalProxyOrigin(string configuredValue, Uri proxyUri)
    {
        var host = proxyUri.IdnHost;
        var formattedHost = IPAddress.TryParse(host, out var address)
            && address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{host}]"
            : host;
        var port = proxyUri.IsDefaultPort ? string.Empty : $":{proxyUri.Port}";
        var canonicalOrigin = $"{proxyUri.Scheme}://{formattedHost}{port}";

        return string.Equals(configuredValue, canonicalOrigin, StringComparison.Ordinal)
            || string.Equals(configuredValue, canonicalOrigin + "/", StringComparison.Ordinal);
    }
}
