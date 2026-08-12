using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Options;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Infrastructure.Configuration;
using Promptly.Infrastructure.Networking;

namespace Promptly.Application.UnitTests;

public sealed class SystemDestinationAddressResolverTests
{
    [Fact]
    public async Task Default_resolver_uses_the_system_lookup_for_localhost()
    {
        var resolver = new SystemDestinationAddressResolver();

        var addresses = await resolver.ResolveAsync(
            "localhost",
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(addresses);
        Assert.All(addresses, address =>
        {
            Assert.True(IPAddress.IsLoopback(address));
            Assert.True(address.AddressFamily is
                AddressFamily.InterNetwork or AddressFamily.InterNetworkV6);
        });
    }

    [Fact]
    public async Task Resolution_uses_the_canonical_host_and_token_and_returns_normalized_addresses()
    {
        const string host = "api.example.test";
        using var cancellationSource = new CancellationTokenSource();
        string? observedHost = null;
        CancellationToken observedToken = default;
        var resolver = new SystemDestinationAddressResolver((candidate, cancellationToken) =>
        {
            observedHost = candidate;
            observedToken = cancellationToken;
            return Task.FromResult<IPAddress[]>(
            [
                IPAddress.Parse("::ffff:192.0.2.10"),
                IPAddress.Parse("192.0.2.10"),
                IPAddress.Parse("2001:db8::10"),
                IPAddress.Parse("2001:db8::10")
            ]);
        });

        var addresses = await resolver.ResolveAsync(host, cancellationSource.Token);

        Assert.Equal(host, observedHost);
        Assert.Equal(cancellationSource.Token, observedToken);
        Assert.Equal(
            [IPAddress.Parse("192.0.2.10"), IPAddress.Parse("2001:db8::10")],
            addresses);
        var mutableView = Assert.IsAssignableFrom<IList<IPAddress>>(addresses);
        Assert.Throws<NotSupportedException>(() => mutableView.Add(IPAddress.Loopback));
    }

    [Fact]
    public async Task Pre_cancelled_resolution_does_not_start_DNS()
    {
        var lookupCalled = false;
        var resolver = new SystemDestinationAddressResolver((_, _) =>
        {
            lookupCalled = true;
            return Task.FromResult(Array.Empty<IPAddress>());
        });
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync("api.example.test", cancellationSource.Token));

        Assert.False(lookupCalled);
    }

    [Fact]
    public async Task Cancellation_after_DNS_is_observed_before_addresses_are_returned()
    {
        using var cancellationSource = new CancellationTokenSource();
        var resolver = new SystemDestinationAddressResolver((_, _) =>
        {
            cancellationSource.Cancel();
            return Task.FromResult(new[] { IPAddress.Parse("192.0.2.10") });
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resolver.ResolveAsync("api.example.test", cancellationSource.Token));
    }

    [Fact]
    public async Task Invalid_resolver_output_fails_closed()
    {
        var resolver = new SystemDestinationAddressResolver((_, _) =>
            Task.FromResult<IPAddress[]>([null!]));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.ResolveAsync("api.example.test", CancellationToken.None));

        Assert.Contains("invalid null address", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolver_preserves_lookup_errors()
    {
        var expected = new SocketException((int)SocketError.HostNotFound);
        var resolver = new SystemDestinationAddressResolver((_, _) =>
            Task.FromException<IPAddress[]>(expected));

        var actual = await Assert.ThrowsAsync<SocketException>(() =>
            resolver.ResolveAsync("missing.example.test", CancellationToken.None));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task Resolver_rejects_invalid_construction_and_host_arguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SystemDestinationAddressResolver(null!));

        var resolver = new SystemDestinationAddressResolver((_, _) =>
            Task.FromResult(Array.Empty<IPAddress>()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            resolver.ResolveAsync(" ", CancellationToken.None));
    }
}

public sealed class EndpointSocketConnectorTests
{
    [Fact]
    public async Task Successful_connection_returns_an_owned_network_stream()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = Assert.IsType<IPEndPoint>(listener.LocalEndpoint);
        var acceptTask = listener.AcceptSocketAsync(TestContext.Current.CancellationToken).AsTask();
        var connector = new EndpointSocketConnector();

        var stream = await connector.ConnectAsync(
            IPAddress.Loopback,
            endpoint.Port,
            CancellationToken.None);
        using var acceptedSocket = await acceptTask;

        Assert.IsType<NetworkStream>(stream);
        Assert.True(acceptedSocket.Connected);

        await stream.DisposeAsync();
        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var received = await acceptedSocket.ReceiveAsync(
            new byte[1],
            SocketFlags.None,
            timeoutSource.Token);
        Assert.Equal(0, received);
    }

    [Fact]
    public async Task Socket_is_disposed_when_connection_setup_fails()
    {
        var socket = new TrackingSocket();
        socket.Dispose();
        socket.ResetDisposeCount();
        var connector = new EndpointSocketConnector(_ => socket);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await connector.ConnectAsync(
                IPAddress.Loopback,
                443,
                CancellationToken.None));

        Assert.True(socket.DisposeCount > 0);
    }

    [Fact]
    public async Task Cancellation_and_invalid_arguments_do_not_create_a_socket()
    {
        var factoryCalls = 0;
        var connector = new EndpointSocketConnector(_ =>
        {
            factoryCalls++;
            return new Socket(SocketType.Stream, ProtocolType.Tcp);
        });
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await connector.ConnectAsync(null!, 443, CancellationToken.None));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await connector.ConnectAsync(
                IPAddress.Loopback,
                443,
                cancellationSource.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await connector.ConnectAsync(
                IPAddress.IPv6Loopback,
                443,
                cancellationSource.Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await connector.ConnectAsync(IPAddress.Loopback, 0, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await connector.ConnectAsync(IPAddress.Loopback, 65_536, CancellationToken.None));

        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void Null_socket_factory_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => new EndpointSocketConnector(null!));
    }

    private sealed class TrackingSocket : Socket
    {
        public TrackingSocket()
            : base(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
        }

        public int DisposeCount { get; private set; }

        public void ResetDisposeCount() => DisposeCount = 0;

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }
}

public sealed class EndpointDestinationConnectorTests
{
    private static readonly Uri Destination = new("https://api.example.test/v1/run");

    [Fact]
    public async Task Every_physical_connection_reauthorizes_and_dials_only_that_result()
    {
        var firstAddress = IPAddress.Parse("203.0.113.10");
        var secondAddress = IPAddress.Parse("2001:db8::10");
        var authorizations = new Queue<AuthorizedEndpointDestination>(
        [
            Authorized("api.example.test", 443, firstAddress),
            Authorized("api.example.test", 443, secondAddress)
        ]);
        var guard = new RecordingDestinationGuard((_, _) =>
            Task.FromResult(authorizations.Dequeue()));
        var socket = new RecordingSocketConnector((_, _, _) =>
            ValueTask.FromResult(Stream.Null));
        var connector = CreateConnector(guard, socket);
        var logicalEndpoint = new DnsEndPoint("API.EXAMPLE.TEST.", 443);

        await using var firstStream = await connector.ConnectAuthorizedAsync(
            Destination,
            logicalEndpoint,
            TestContext.Current.CancellationToken);
        await using var secondStream = await connector.ConnectAuthorizedAsync(
            Destination,
            logicalEndpoint,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, guard.Calls.Count);
        Assert.All(guard.Calls, call => Assert.Equal(Destination, call.Destination));
        Assert.Equal([firstAddress, secondAddress], socket.Attempts.Select(attempt => attempt.Address));
        Assert.All(socket.Attempts, attempt => Assert.Equal(443, attempt.Port));
    }

    [Fact]
    public async Task Failed_authorized_literal_falls_back_to_the_next_authorized_literal()
    {
        var firstAddress = IPAddress.Parse("203.0.113.10");
        var secondAddress = IPAddress.Parse("2001:db8::10");
        var guard = new RecordingDestinationGuard((_, _) => Task.FromResult(
            Authorized("203.0.113.10", 8443, firstAddress, secondAddress)));
        var attempt = 0;
        var socket = new RecordingSocketConnector((_, _, _) =>
        {
            attempt++;
            return attempt == 1
                ? ValueTask.FromException<Stream>(
                    new SocketException((int)SocketError.ConnectionRefused))
                : ValueTask.FromResult(Stream.Null);
        });
        var connector = CreateConnector(guard, socket);

        await using var stream = await connector.ConnectAuthorizedAsync(
            new Uri("https://203.0.113.10:8443/run"),
            new DnsEndPoint("::ffff:203.0.113.10", 8443),
            TestContext.Current.CancellationToken);

        Assert.Equal([firstAddress, secondAddress], socket.Attempts.Select(item => item.Address));
        Assert.All(socket.Attempts, item => Assert.Equal(8443, item.Port));
    }

    [Theory]
    [InlineData("other.example.test", 443)]
    [InlineData("api.example.test", 8443)]
    public async Task Logical_host_and_port_must_match_the_authorized_destination(
        string logicalHost,
        int logicalPort)
    {
        var socket = SucceedingSocket();
        var connector = CreateConnector(
            GuardReturning(Authorized("api.example.test", 443, IPAddress.Parse("203.0.113.10"))),
            socket);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await connector.ConnectAuthorizedAsync(
                Destination,
                new DnsEndPoint(logicalHost, logicalPort),
                TestContext.Current.CancellationToken));

        Assert.Contains("does not match", exception.Message, StringComparison.Ordinal);
        Assert.Empty(socket.Attempts);
    }

    [Fact]
    public async Task Empty_authorization_fails_closed_before_socket_creation()
    {
        var socket = SucceedingSocket();
        var connector = CreateConnector(
            GuardReturning(new AuthorizedEndpointDestination("api.example.test", 443, [])),
            socket);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await connector.ConnectAuthorizedAsync(
                Destination,
                new DnsEndPoint("api.example.test", 443),
                TestContext.Current.CancellationToken));

        Assert.Contains("no approved addresses", exception.Message, StringComparison.Ordinal);
        Assert.Empty(socket.Attempts);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_without_becoming_a_timeout()
    {
        using var cancellationSource = new CancellationTokenSource();
        var socket = new RecordingSocketConnector((_, _, _) =>
        {
            cancellationSource.Cancel();
            return ValueTask.FromException<Stream>(
                new OperationCanceledException(cancellationSource.Token));
        });
        var connector = CreateConnector(
            GuardReturning(Authorized("api.example.test", 443, IPAddress.Parse("203.0.113.10"))),
            socket);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await connector.ConnectAuthorizedAsync(
                Destination,
                new DnsEndPoint("api.example.test", 443),
                cancellationSource.Token));

        Assert.Single(socket.Attempts);
    }

    [Fact]
    public async Task Connect_deadline_is_reported_as_a_timeout()
    {
        var socket = new RecordingSocketConnector(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new MemoryStream();
        });
        var connector = CreateConnector(
            GuardReturning(Authorized("api.example.test", 443, IPAddress.Parse("203.0.113.10"))),
            socket,
            connectTimeoutSeconds: 0);

        var exception = await Assert.ThrowsAsync<TimeoutException>(async () =>
            await connector.ConnectAuthorizedAsync(
                Destination,
                new DnsEndPoint("api.example.test", 443),
                TestContext.Current.CancellationToken));

        Assert.Contains("exceeded 0 seconds", exception.Message, StringComparison.Ordinal);
        Assert.IsAssignableFrom<OperationCanceledException>(exception.InnerException);
    }

    [Fact]
    public async Task Single_address_failure_is_preserved_as_the_inner_exception()
    {
        var failure = new IOException("connection failed");
        var socket = new RecordingSocketConnector((_, _, _) =>
            ValueTask.FromException<Stream>(failure));
        var connector = CreateConnector(
            GuardReturning(Authorized("203.0.113.10", 443, IPAddress.Parse("203.0.113.10"))),
            socket);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await connector.ConnectAuthorizedAsync(
                new Uri("https://203.0.113.10/run"),
                new DnsEndPoint("203.0.113.10", 443),
                TestContext.Current.CancellationToken));

        Assert.Same(failure, exception.InnerException);
        Assert.Single(socket.Attempts);
    }

    [Fact]
    public async Task Multiple_address_failures_are_preserved_as_an_aggregate()
    {
        var failures = new Exception[]
        {
            new SocketException((int)SocketError.ConnectionRefused),
            new IOException("network unavailable")
        };
        var attempt = 0;
        var socket = new RecordingSocketConnector((_, _, _) =>
            ValueTask.FromException<Stream>(failures[attempt++]));
        var connector = CreateConnector(
            GuardReturning(Authorized(
                "api.example.test",
                443,
                IPAddress.Parse("203.0.113.10"),
                IPAddress.Parse("2001:db8::10"))),
            socket);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await connector.ConnectAuthorizedAsync(
                Destination,
                new DnsEndPoint("api.example.test", 443),
                TestContext.Current.CancellationToken));

        var aggregate = Assert.IsType<AggregateException>(exception.InnerException);
        Assert.Equal(failures, aggregate.InnerExceptions);
        Assert.Equal(2, socket.Attempts.Count);
    }

    [Fact]
    public async Task Unexpected_socket_failure_is_not_reinterpreted()
    {
        var failure = new InvalidOperationException("socket invariant failed");
        var socket = new RecordingSocketConnector((_, _, _) =>
            ValueTask.FromException<Stream>(failure));
        var connector = CreateConnector(
            GuardReturning(Authorized("api.example.test", 443, IPAddress.Parse("203.0.113.10"))),
            socket);

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await connector.ConnectAuthorizedAsync(
                Destination,
                new DnsEndPoint("api.example.test", 443),
                TestContext.Current.CancellationToken));

        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task Guard_rejection_propagates_without_attempting_a_socket()
    {
        var rejection = new EndpointDestinationRejectedException(
            EndpointDestinationRejectionReason.NonPublicDestination);
        var guard = new RecordingDestinationGuard((_, _) =>
            Task.FromException<AuthorizedEndpointDestination>(rejection));
        var socket = SucceedingSocket();
        var connector = CreateConnector(guard, socket);

        var actual = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(async () =>
            await connector.ConnectAuthorizedAsync(
                Destination,
                new DnsEndPoint("api.example.test", 443),
                TestContext.Current.CancellationToken));

        Assert.Same(rejection, actual);
        Assert.Single(guard.Calls);
        Assert.Empty(socket.Attempts);
    }

    [Fact]
    public async Task Pre_cancelled_connection_never_calls_the_guard_or_socket()
    {
        var guard = GuardReturning(
            Authorized("api.example.test", 443, IPAddress.Parse("203.0.113.10")));
        var socket = SucceedingSocket();
        var connector = CreateConnector(guard, socket);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await connector.ConnectAuthorizedAsync(
                Destination,
                new DnsEndPoint("api.example.test", 443),
                cancellationSource.Token));

        Assert.Empty(guard.Calls);
        Assert.Empty(socket.Attempts);
    }

    [Fact]
    public async Task ConnectAuthorizedAsync_rejects_null_arguments()
    {
        var connector = CreateConnector(
            GuardReturning(Authorized("api.example.test", 443, IPAddress.Parse("203.0.113.10"))),
            SucceedingSocket());

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await connector.ConnectAuthorizedAsync(
                null!,
                new DnsEndPoint("api.example.test", 443),
                TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await connector.ConnectAuthorizedAsync(
                Destination,
                null!,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Malformed_framework_host_is_rejected_before_socket_creation()
    {
        var guard = GuardReturning(
            Authorized("api.example.test", 443, IPAddress.Parse("203.0.113.10")));
        var socket = SucceedingSocket();
        var connector = CreateConnector(guard, socket);
        var malformedHost = new string('\ud800', 1);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await connector.ConnectAuthorizedAsync(
                Destination,
                new DnsEndPoint(malformedHost, 443),
                TestContext.Current.CancellationToken));

        Assert.Contains("host is invalid", exception.Message, StringComparison.Ordinal);
        Assert.IsType<ArgumentException>(exception.InnerException);
        Assert.Single(guard.Calls);
        Assert.Empty(socket.Attempts);
    }

    [Fact]
    public void ConnectAsync_rejects_null_context_and_requests_without_an_absolute_URI()
    {
        var connector = CreateConnector(
            GuardReturning(Authorized("api.example.test", 443, IPAddress.Parse("203.0.113.10"))),
            SucceedingSocket());

        Assert.Throws<ArgumentNullException>(() =>
            connector.ConnectAsync(null!, TestContext.Current.CancellationToken));

        using var missingUriRequest = new HttpRequestMessage();
        var missingUriContext = CreateFrameworkContext(missingUriRequest);
        Assert.Throws<HttpRequestException>(() =>
            connector.ConnectAsync(missingUriContext, TestContext.Current.CancellationToken));

        using var relativeUriRequest = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri("relative", UriKind.Relative));
        var relativeUriContext = CreateFrameworkContext(relativeUriRequest);
        Assert.Throws<HttpRequestException>(() =>
            connector.ConnectAsync(relativeUriContext, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConnectAsync_forwards_the_framework_request_and_endpoint()
    {
        var guard = GuardReturning(
            Authorized("api.example.test", 443, IPAddress.Parse("203.0.113.10")));
        var socket = SucceedingSocket();
        var connector = CreateConnector(guard, socket);
        using var request = new HttpRequestMessage(HttpMethod.Get, Destination);
        var context = CreateFrameworkContext(request);

        await using var stream = await connector.ConnectAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(Destination, Assert.Single(guard.Calls).Destination);
        Assert.Equal(IPAddress.Parse("203.0.113.10"), Assert.Single(socket.Attempts).Address);
    }

    [Fact]
    public void Constructor_rejects_null_dependencies_and_accepts_the_default_socket_connector()
    {
        var guard = GuardReturning(
            Authorized("api.example.test", 443, IPAddress.Parse("203.0.113.10")));
        var options = Options.Create(new EndpointEgressOptions());

        Assert.Throws<ArgumentNullException>(() =>
            new EndpointDestinationConnector(null!, options));
        Assert.Throws<ArgumentNullException>(() =>
            new EndpointDestinationConnector(guard, null!));
        Assert.NotNull(new EndpointDestinationConnector(guard, options));
    }

    private static EndpointDestinationConnector CreateConnector(
        RecordingDestinationGuard guard,
        RecordingSocketConnector socket,
        int connectTimeoutSeconds = 10) =>
        new(
            guard,
            Options.Create(new EndpointEgressOptions
            {
                ConnectTimeoutSeconds = connectTimeoutSeconds
            }),
            socket);

    private static RecordingDestinationGuard GuardReturning(
        AuthorizedEndpointDestination authorized) =>
        new((_, _) => Task.FromResult(authorized));

    private static RecordingSocketConnector SucceedingSocket() =>
        new((_, _, _) => ValueTask.FromResult(Stream.Null));

    private static AuthorizedEndpointDestination Authorized(
        string canonicalHost,
        int port,
        params IPAddress[] addresses) =>
        new(canonicalHost, port, Array.AsReadOnly(addresses));

    private static SocketsHttpConnectionContext CreateFrameworkContext(
        HttpRequestMessage request)
    {
        var constructor = Assert.Single(
            typeof(SocketsHttpConnectionContext).GetConstructors(
                BindingFlags.Instance | BindingFlags.NonPublic));
        return Assert.IsType<SocketsHttpConnectionContext>(constructor.Invoke(
            [new DnsEndPoint("api.example.test", 443), request]));
    }

    private sealed class RecordingDestinationGuard(
        Func<Uri, CancellationToken, Task<AuthorizedEndpointDestination>> authorize)
        : IEndpointDestinationGuard
    {
        public List<(Uri Destination, CancellationToken CancellationToken)> Calls { get; } = [];

        public Task<AuthorizedEndpointDestination> AuthorizeAsync(
            Uri destination,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((destination, cancellationToken));
            return authorize(destination, cancellationToken);
        }
    }

    private sealed class RecordingSocketConnector(
        Func<IPAddress, int, CancellationToken, ValueTask<Stream>> connect)
        : IEndpointSocketConnector
    {
        public List<(IPAddress Address, int Port, CancellationToken CancellationToken)> Attempts
        {
            get;
        } = [];

        public ValueTask<Stream> ConnectAsync(
            IPAddress address,
            int port,
            CancellationToken cancellationToken)
        {
            Attempts.Add((address, port, cancellationToken));
            return connect(address, port, cancellationToken);
        }
    }
}

public sealed class EndpointEgressOptionsValidatorTests
{
    private readonly EndpointEgressOptionsValidator _validator = new();

    [Fact]
    public void Canonical_direct_and_proxy_configurations_succeed()
    {
        var proxy = ValidOptions(
            proxyUrl: "http://proxy.example:3128/",
            requireProxy: true,
            rules: []);
        Assert.True(_validator.Validate(Options.DefaultName, proxy).Succeeded);

        var direct = ValidOptions(
            rules:
            [
                ValidRule(host: "fd00::10", port: 8443, cidrs: ["fd00::/8"])
            ]);
        Assert.True(_validator.Validate(Options.DefaultName, direct).Succeeded);
    }

    [Fact]
    public void Invalid_timeouts_missing_proxy_and_null_rules_fail_together()
    {
        var options = ValidOptions(
            dnsTimeoutSeconds: 0,
            connectTimeoutSeconds: 0,
            pooledConnectionLifetimeSeconds: 86_401,
            proxyUrl: null,
            requireProxy: true,
            rules: null,
            preserveNullRules: true);

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(result, "DnsTimeoutSeconds must be between 1 and 120");
        AssertFailure(result, "ConnectTimeoutSeconds must be between 1 and 120");
        AssertFailure(result, "PooledConnectionLifetimeSeconds must be between 1 and 86400");
        AssertFailure(result, "ProxyUrl is required when RequireProxy is true");
        AssertFailure(result, "AllowedNonPublicDestinations cannot be null");

        var oppositeBounds = _validator.Validate(
            Options.DefaultName,
            ValidOptions(
                dnsTimeoutSeconds: 121,
                connectTimeoutSeconds: 121,
                pooledConnectionLifetimeSeconds: 0));
        AssertFailure(oppositeBounds, "DnsTimeoutSeconds must be between 1 and 120");
        AssertFailure(oppositeBounds, "ConnectTimeoutSeconds must be between 1 and 120");
        AssertFailure(oppositeBounds, "PooledConnectionLifetimeSeconds must be between 1 and 86400");
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(120, 120, 86_400)]
    public void Timeout_and_lifetime_boundary_values_succeed(
        int dnsTimeoutSeconds,
        int connectTimeoutSeconds,
        int pooledConnectionLifetimeSeconds)
    {
        var result = _validator.Validate(
            Options.DefaultName,
            ValidOptions(
                dnsTimeoutSeconds: dnsTimeoutSeconds,
                connectTimeoutSeconds: connectTimeoutSeconds,
                pooledConnectionLifetimeSeconds: pooledConnectionLifetimeSeconds));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://proxy.example:3128/\0")]
    [InlineData("http:\\proxy.example:3128")]
    [InlineData("not-a-proxy-origin")]
    [InlineData("http:///")]
    [InlineData("ftp://proxy.example:3128")]
    [InlineData("http://user:secret@proxy.example:3128")]
    [InlineData("http://proxy.example:3128/path")]
    [InlineData("http://proxy.example:3128/?query=true")]
    [InlineData("http://proxy.example:3128/#fragment")]
    [InlineData("HTTP://Proxy.Example:3128")]
    [InlineData("http://proxy.example:80")]
    [InlineData(" http://proxy.example:3128")]
    public void Proxy_must_be_a_canonical_HTTP_origin(string proxyUrl)
    {
        var result = _validator.Validate(
            Options.DefaultName,
            ValidOptions(proxyUrl: proxyUrl, requireProxy: true, rules: []));

        AssertFailure(result, "ProxyUrl must be an absolute HTTP(S) URL");
    }

    [Theory]
    [InlineData("http://proxy.example:3128")]
    [InlineData("http://proxy.example/")]
    [InlineData("http://[2001:db8::10]:3128/")]
    public void Canonical_proxy_origin_variants_succeed(string proxyUrl)
    {
        var result = _validator.Validate(
            Options.DefaultName,
            ValidOptions(proxyUrl: proxyUrl, requireProxy: true, rules: []));

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(true, "http://proxy.example:3128")]
    [InlineData(false, "http://proxy.example:3128")]
    [InlineData(true, null)]
    public void Proxy_transports_reject_non_public_destination_rules(
        bool requireProxy,
        string? proxyUrl)
    {
        var result = _validator.Validate(
            Options.DefaultName,
            ValidOptions(
                proxyUrl: proxyUrl,
                requireProxy: requireProxy,
                rules: [ValidRule()]));

        AssertFailure(
            result,
            "AllowedNonPublicDestinations cannot be configured when a proxy transport is selected");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*.internal.example")]
    [InlineData(".internal.example")]
    [InlineData("Internal.Example")]
    [InlineData("internal.example.")]
    [InlineData("bücher.example")]
    [InlineData("127.1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("fe80::1%4")]
    [InlineData("not_a_host")]
    [InlineData(" internal.example")]
    public void Rules_require_an_exact_canonical_host(string? host)
    {
        var result = _validator.Validate(
            Options.DefaultName,
            ValidOptions(rules: [ValidRule(host: host!)]));

        AssertFailure(result, "Host must be an exact canonical ASCII lowercase host");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65_536)]
    public void Rule_ports_are_bounded(int port)
    {
        var result = _validator.Validate(
            Options.DefaultName,
            ValidOptions(rules: [ValidRule(port: port)]));

        AssertFailure(result, "Port must be between 1 and 65535");
    }

    [Fact]
    public void Empty_noncanonical_and_duplicate_CIDRs_are_rejected()
    {
        var emptyResult = _validator.Validate(
            Options.DefaultName,
            ValidOptions(rules: [ValidRule(cidrs: [])]));
        AssertFailure(emptyResult, "Cidrs must contain at least one canonical IPv4 or IPv6 CIDR");

        var invalidResult = _validator.Validate(
            Options.DefaultName,
            ValidOptions(rules:
            [
                ValidRule(cidrs: ["10.0.0.1/8", "10.0.0.0/8", "10.0.0.0/8"])
            ]));
        AssertFailure(invalidResult, "must be a canonical IPv4 or IPv6 CIDR");
        AssertFailure(invalidResult, "duplicates an existing CIDR");
    }

    [Fact]
    public void Null_malformed_and_unsupported_CIDRs_are_rejected()
    {
        var nullResult = _validator.Validate(
            Options.DefaultName,
            ValidOptions(rules: [ValidRule(cidrs: null, preserveNullCidrs: true)]));
        AssertFailure(nullResult, "Cidrs must contain at least one canonical IPv4 or IPv6 CIDR");

        var invalidResult = _validator.Validate(
            Options.DefaultName,
            ValidOptions(rules:
            [
                ValidRule(cidrs: ["", "not-a-cidr", "8.8.8.0/24"])
            ]));
        AssertFailure(invalidResult, "must be a canonical IPv4 or IPv6 CIDR");
        AssertFailure(invalidResult, "must be contained within a supported");
    }

    [Fact]
    public void Duplicate_host_and_port_rules_are_rejected_but_distinct_ports_are_allowed()
    {
        var duplicateResult = _validator.Validate(
            Options.DefaultName,
            ValidOptions(rules:
            [
                ValidRule(),
                ValidRule(cidrs: ["192.168.0.0/16"])
            ]));
        AssertFailure(duplicateResult, "duplicates an existing exact host and port rule");

        var distinctResult = _validator.Validate(
            Options.DefaultName,
            ValidOptions(rules: [ValidRule(), ValidRule(port: 8443)]));
        Assert.True(distinctResult.Succeeded);
    }

    [Fact]
    public void Null_options_and_null_rule_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _validator.Validate(Options.DefaultName, null!));

        var result = _validator.Validate(
            Options.DefaultName,
            ValidOptions(rules: [null!]));
        AssertFailure(result, "AllowedNonPublicDestinations:0 cannot be null");
    }

    private static EndpointEgressOptions ValidOptions(
        int dnsTimeoutSeconds = 5,
        int connectTimeoutSeconds = 10,
        int pooledConnectionLifetimeSeconds = 300,
        string? proxyUrl = null,
        bool requireProxy = false,
        List<AllowedNonPublicDestinationRule>? rules = null,
        bool preserveNullRules = false) => new()
        {
            DnsTimeoutSeconds = dnsTimeoutSeconds,
            ConnectTimeoutSeconds = connectTimeoutSeconds,
            PooledConnectionLifetimeSeconds = pooledConnectionLifetimeSeconds,
            ProxyUrl = proxyUrl,
            RequireProxy = requireProxy,
            AllowedNonPublicDestinations = preserveNullRules ? rules! : rules ?? [ValidRule()]
        };

    private static AllowedNonPublicDestinationRule ValidRule(
        string host = "internal.example",
        int port = 443,
        List<string>? cidrs = null,
        bool preserveNullCidrs = false) => new()
        {
            Host = host,
            Port = port,
            Cidrs = preserveNullCidrs ? cidrs! : cidrs ?? ["10.0.0.0/8", "fd00::/8"]
        };

    private static void AssertFailure(ValidateOptionsResult result, string fragment)
    {
        Assert.True(result.Failed);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(fragment, StringComparison.Ordinal));
    }
}
