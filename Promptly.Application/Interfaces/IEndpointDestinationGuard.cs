using System.Net;

namespace Promptly.Application.Interfaces;

public interface IDestinationAddressResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(
        string canonicalHost,
        CancellationToken cancellationToken);
}

public interface IEndpointDestinationGuard
{
    Task<AuthorizedEndpointDestination> AuthorizeAsync(
        Uri destination,
        CancellationToken cancellationToken = default);
}

public sealed record AuthorizedEndpointDestination(
    string CanonicalHost,
    int EffectivePort,
    IReadOnlyList<IPAddress> Addresses);
