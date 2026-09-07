namespace Promptly.Application.Models;

public sealed class EndpointEgressOptions
{
    public const string SectionName = "EndpointEgress";

    public int DnsTimeoutSeconds { get; init; } = 5;

    public int ConnectTimeoutSeconds { get; init; } = 10;

    public int PooledConnectionLifetimeSeconds { get; init; } = 120;

    public string? ProxyUrl { get; init; }

    public bool RequireProxy { get; init; }

    /// <summary>
    /// Exact host, port, and CIDR exceptions for direct transport only. Proxy
    /// transports reject these rules because their dial boundary cannot enforce
    /// the same per-origin exception contract.
    /// </summary>
    public List<AllowedNonPublicDestinationRule> AllowedNonPublicDestinations { get; init; } = [];
}

public sealed class AllowedNonPublicDestinationRule
{
    public required string Host { get; init; }

    public int Port { get; init; }

    public List<string> Cidrs { get; init; } = [];
}
