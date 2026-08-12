using System.Net;
using Microsoft.Extensions.Options;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;

namespace Promptly.Application.Services;

public sealed class EndpointDestinationGuard : IEndpointDestinationGuard
{
    private readonly IDestinationAddressResolver _addressResolver;
    private readonly IReadOnlyDictionary<(string Host, int Port), IReadOnlyList<IPNetwork>>
        _allowedNonPublicDestinations;
    private readonly TimeSpan _dnsTimeout;

    public EndpointDestinationGuard(
        IDestinationAddressResolver addressResolver,
        IOptions<EndpointEgressOptions> options)
    {
        ArgumentNullException.ThrowIfNull(addressResolver);
        ArgumentNullException.ThrowIfNull(options);

        _addressResolver = addressResolver;
        _dnsTimeout = TimeSpan.FromSeconds(options.Value.DnsTimeoutSeconds);
        _allowedNonPublicDestinations = BuildAllowedDestinations(
            options.Value.AllowedNonPublicDestinations);
    }

    public async Task<AuthorizedEndpointDestination> AuthorizeAsync(
        Uri destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        if (!destination.IsAbsoluteUri
            || (!string.Equals(
                    destination.Scheme,
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase)
                && !string.Equals(
                    destination.Scheme,
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase))
            || !string.IsNullOrEmpty(destination.UserInfo)
            || !EndpointDestinationPolicy.TryCanonicalizeHost(
                destination.IdnHost,
                out var canonicalHost))
        {
            throw new EndpointDestinationRejectedException(
                EndpointDestinationRejectionReason.InvalidDestination);
        }

        IReadOnlyList<IPAddress> resolvedAddresses;
        if (IPAddress.TryParse(canonicalHost, out var literalAddress))
        {
            resolvedAddresses = [EndpointDestinationPolicy.NormalizeAddress(literalAddress)];
        }
        else
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(_dnsTimeout);
            try
            {
                resolvedAddresses = await _addressResolver.ResolveAsync(
                        canonicalHost,
                        timeoutSource.Token)
                    .WaitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested)
            {
                throw new EndpointDestinationRejectedException(
                    EndpointDestinationRejectionReason.ResolutionFailed,
                    new TimeoutException(
                        "Endpoint destination DNS resolution exceeded its configured deadline.",
                        exception));
            }
            catch (Exception exception)
            {
                throw new EndpointDestinationRejectedException(
                    EndpointDestinationRejectionReason.ResolutionFailed,
                    exception);
            }
        }

        if (resolvedAddresses is null || resolvedAddresses.Count == 0)
        {
            throw new EndpointDestinationRejectedException(
                EndpointDestinationRejectionReason.NoAddresses);
        }

        var addresses = resolvedAddresses
            .Select(EndpointDestinationPolicy.NormalizeAddress)
            .Distinct()
            .ToArray();
        foreach (var address in addresses)
        {
            AuthorizeAddress(canonicalHost, destination.Port, address);
        }

        return new AuthorizedEndpointDestination(
            canonicalHost,
            destination.Port,
            Array.AsReadOnly(addresses));
    }

    private void AuthorizeAddress(string host, int port, IPAddress address)
    {
        var classification = EndpointDestinationPolicy.Classify(address);
        if (classification == EndpointAddressClassification.GloballyReachable)
        {
            return;
        }

        if (classification == EndpointAddressClassification.PermanentlyDenied)
        {
            throw new EndpointDestinationRejectedException(
                EndpointDestinationRejectionReason.PermanentlyDeniedDestination);
        }

        if (!_allowedNonPublicDestinations.TryGetValue((host, port), out var networks)
            || !networks.Any(network => network.Contains(address)))
        {
            throw new EndpointDestinationRejectedException(
                EndpointDestinationRejectionReason.NonPublicDestination);
        }
    }

    private static IReadOnlyDictionary<(string Host, int Port), IReadOnlyList<IPNetwork>>
        BuildAllowedDestinations(IReadOnlyList<AllowedNonPublicDestinationRule>? rules)
    {
        var result = new Dictionary<(string Host, int Port), IReadOnlyList<IPNetwork>>();
        foreach (var rule in rules ?? [])
        {
            if (!EndpointDestinationPolicy.TryCanonicalizeHost(rule.Host, out var canonicalHost))
            {
                throw new InvalidOperationException(
                    "Endpoint egress options contain a non-canonical destination host.");
            }

            var networks = rule.Cidrs.Select(IPNetwork.Parse).ToArray();
            if (!result.TryAdd(
                    (canonicalHost, rule.Port),
                    Array.AsReadOnly(networks)))
            {
                throw new InvalidOperationException(
                    "Endpoint egress options contain a duplicate destination origin.");
            }
        }

        return result;
    }
}
