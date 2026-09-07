using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;

namespace Promptly.Infrastructure.Networking;

/// <summary>
/// Opens a TCP connection to one literal address which has already passed destination policy.
/// </summary>
public interface IEndpointSocketConnector
{
    ValueTask<Stream> ConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken);
}

public sealed class EndpointSocketConnector : IEndpointSocketConnector
{
    private readonly Func<AddressFamily, Socket> _socketFactory;

    public EndpointSocketConnector()
        : this(addressFamily =>
            new Socket(addressFamily, SocketType.Stream, ProtocolType.Tcp))
    {
    }

    public EndpointSocketConnector(Func<AddressFamily, Socket> socketFactory)
    {
        ArgumentNullException.ThrowIfNull(socketFactory);
        _socketFactory = socketFactory;
    }

    public async ValueTask<Stream> ConnectAsync(
        IPAddress address,
        int port,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        cancellationToken.ThrowIfCancellationRequested();

        var socket = _socketFactory(address.AddressFamily);

        try
        {
            socket.NoDelay = true;
            await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Reauthorizes an HTTP destination when a physical connection is created, then pins the
/// connection to an address from that exact authorization result. Returning the raw network
/// stream lets <see cref="SocketsHttpHandler"/> retain the logical host for TLS and SNI.
/// </summary>
public sealed class EndpointDestinationConnector
{
    private readonly IEndpointDestinationGuard _destinationGuard;
    private readonly IEndpointSocketConnector _socketConnector;
    private readonly TimeSpan _connectTimeout;

    public EndpointDestinationConnector(
        IEndpointDestinationGuard destinationGuard,
        IOptions<EndpointEgressOptions> options,
        IEndpointSocketConnector? socketConnector = null)
    {
        ArgumentNullException.ThrowIfNull(destinationGuard);
        ArgumentNullException.ThrowIfNull(options);

        _destinationGuard = destinationGuard;
        _socketConnector = socketConnector ?? new EndpointSocketConnector();
        _connectTimeout = TimeSpan.FromSeconds(options.Value.ConnectTimeoutSeconds);
    }

    public ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var destination = context.InitialRequestMessage.RequestUri;
        if (destination is null || !destination.IsAbsoluteUri)
        {
            throw new HttpRequestException("An absolute endpoint destination is required.");
        }

        return ConnectAuthorizedAsync(
            destination,
            context.DnsEndPoint,
            cancellationToken);
    }

    /// <summary>
    /// Public deterministic seam for the framework callback. Production callers should normally
    /// register <see cref="ConnectAsync"/> directly as <see cref="SocketsHttpHandler.ConnectCallback"/>.
    /// </summary>
    public async ValueTask<Stream> ConnectAuthorizedAsync(
        Uri destination,
        DnsEndPoint logicalEndpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(logicalEndpoint);
        cancellationToken.ThrowIfCancellationRequested();

        // Authorization deliberately happens here, immediately before each physical connection.
        // The returned addresses are the only addresses the socket layer may dial.
        var authorized = await _destinationGuard.AuthorizeAsync(destination, cancellationToken)
            .ConfigureAwait(false);
        EnsureLogicalEndpointMatchesAuthorization(logicalEndpoint, authorized);

        if (authorized.Addresses.Count == 0)
        {
            throw new HttpRequestException(
                "Endpoint destination authorization returned no approved addresses.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_connectTimeout);

        var failures = new List<Exception>();
        foreach (var address in authorized.Addresses)
        {
            try
            {
                return await _socketConnector.ConnectAsync(
                        address,
                        authorized.EffectivePort,
                        timeoutSource.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception) when (timeoutSource.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Connecting to endpoint destination exceeded {_connectTimeout.TotalSeconds:g} seconds.",
                    exception);
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                failures.Add(exception);
            }
        }

        throw new HttpRequestException(
            "Unable to connect to any approved endpoint destination address.",
            failures.Count == 1 ? failures[0] : new AggregateException(failures));
    }

    private static void EnsureLogicalEndpointMatchesAuthorization(
        DnsEndPoint logicalEndpoint,
        AuthorizedEndpointDestination authorized)
    {
        var logicalHost = CanonicalizeLogicalHost(logicalEndpoint.Host);
        if (logicalEndpoint.Port != authorized.EffectivePort
            || !string.Equals(logicalHost, authorized.CanonicalHost, StringComparison.Ordinal))
        {
            throw new HttpRequestException(
                "The HTTP connection endpoint does not match the authorized destination.");
        }
    }

    private static string CanonicalizeLogicalHost(string host)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        }

        var withoutRootLabel = host.EndsWith(".", StringComparison.Ordinal)
            ? host[..^1]
            : host;
        try
        {
            return new IdnMapping().GetAscii(withoutRootLabel).ToLowerInvariant();
        }
        catch (ArgumentException exception)
        {
            throw new HttpRequestException("The HTTP connection host is invalid.", exception);
        }
    }
}
