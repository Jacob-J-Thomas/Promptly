using System.Net;
using Microsoft.Extensions.Options;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;

namespace Promptly.Application.UnitTests;

public sealed class EndpointDestinationPolicyTests
{
    public static TheoryData<string, EndpointAddressClassification> AddressClassifications => new()
    {
        // Ordinary globally reachable IPv4 and IPv6 addresses.
        { "8.8.8.8", EndpointAddressClassification.GloballyReachable },
        { "1.1.1.1", EndpointAddressClassification.GloballyReachable },
        { "2606:4700:4700::1111", EndpointAddressClassification.GloballyReachable },

        // Operator-exception-eligible loopback, RFC1918, CGNAT, and ULA ranges.
        { "127.0.0.1", EndpointAddressClassification.AllowlistEligible },
        { "10.0.0.1", EndpointAddressClassification.AllowlistEligible },
        { "100.64.0.1", EndpointAddressClassification.AllowlistEligible },
        { "172.16.0.1", EndpointAddressClassification.AllowlistEligible },
        { "192.168.1.1", EndpointAddressClassification.AllowlistEligible },
        { "::1", EndpointAddressClassification.AllowlistEligible },
        { "fc00::1", EndpointAddressClassification.AllowlistEligible },
        { "fdff:ffff::1", EndpointAddressClassification.AllowlistEligible },

        // Permanently denied IPv4 special-purpose categories.
        { "0.0.0.0", EndpointAddressClassification.PermanentlyDenied },
        { "0.1.2.3", EndpointAddressClassification.PermanentlyDenied },
        { "169.254.1.1", EndpointAddressClassification.PermanentlyDenied },
        { "192.0.0.1", EndpointAddressClassification.PermanentlyDenied },
        { "192.0.2.1", EndpointAddressClassification.PermanentlyDenied },
        { "192.88.99.1", EndpointAddressClassification.PermanentlyDenied },
        { "198.18.0.1", EndpointAddressClassification.PermanentlyDenied },
        { "198.51.100.1", EndpointAddressClassification.PermanentlyDenied },
        { "203.0.113.1", EndpointAddressClassification.PermanentlyDenied },
        { "224.0.0.1", EndpointAddressClassification.PermanentlyDenied },
        { "239.255.255.255", EndpointAddressClassification.PermanentlyDenied },
        { "240.0.0.1", EndpointAddressClassification.PermanentlyDenied },
        { "255.255.255.255", EndpointAddressClassification.PermanentlyDenied },

        // Specific cloud/container metadata endpoints, including addresses that
        // would otherwise be public or exception-eligible.
        { "100.100.100.200", EndpointAddressClassification.PermanentlyDenied },
        { "168.63.129.16", EndpointAddressClassification.PermanentlyDenied },
        { "169.254.169.254", EndpointAddressClassification.PermanentlyDenied },
        { "169.254.170.2", EndpointAddressClassification.PermanentlyDenied },
        { "192.0.0.192", EndpointAddressClassification.PermanentlyDenied },
        { "fd00:ec2::254", EndpointAddressClassification.PermanentlyDenied },
        { "fd20:ce::254", EndpointAddressClassification.PermanentlyDenied },

        // Permanently denied IPv6 special-purpose, documentation, transition,
        // link-local, and multicast categories.
        { "::", EndpointAddressClassification.PermanentlyDenied },
        { "64:ff9b::808:808", EndpointAddressClassification.PermanentlyDenied },
        { "64:ff9b:1::1", EndpointAddressClassification.PermanentlyDenied },
        { "100::1", EndpointAddressClassification.PermanentlyDenied },
        { "100:0:0:1::1", EndpointAddressClassification.PermanentlyDenied },
        { "2001::1234", EndpointAddressClassification.PermanentlyDenied },
        { "2001:2::1", EndpointAddressClassification.PermanentlyDenied },
        { "2001:db8::1", EndpointAddressClassification.PermanentlyDenied },
        { "2002::1", EndpointAddressClassification.PermanentlyDenied },
        { "3fff::1", EndpointAddressClassification.PermanentlyDenied },
        { "4000::1", EndpointAddressClassification.PermanentlyDenied },
        { "5f00::1", EndpointAddressClassification.PermanentlyDenied },
        { "8000::1", EndpointAddressClassification.PermanentlyDenied },
        { "fec0::1", EndpointAddressClassification.PermanentlyDenied },
        { "fe80::1", EndpointAddressClassification.PermanentlyDenied },
        { "ff02::1", EndpointAddressClassification.PermanentlyDenied },

        // Globally reachable exceptions nested in broader IANA special blocks.
        { "192.0.0.9", EndpointAddressClassification.GloballyReachable },
        { "192.0.0.10", EndpointAddressClassification.GloballyReachable },
        { "2001:1::1", EndpointAddressClassification.GloballyReachable },
        { "2001:1::2", EndpointAddressClassification.GloballyReachable },
        { "2001:1::3", EndpointAddressClassification.GloballyReachable },
        { "2001:3::1", EndpointAddressClassification.GloballyReachable },
        { "2001:4:112::1", EndpointAddressClassification.GloballyReachable },
        { "2001:20::1", EndpointAddressClassification.GloballyReachable },
        { "2001:30::1", EndpointAddressClassification.GloballyReachable }
    };

    [Theory]
    [MemberData(nameof(AddressClassifications))]
    public void Classify_applies_the_complete_address_policy(
        string address,
        EndpointAddressClassification expected)
    {
        Assert.Equal(expected, EndpointDestinationPolicy.Classify(IPAddress.Parse(address)));
    }

    [Fact]
    public void Classify_normalizes_ipv4_mapped_ipv6_before_applying_policy()
    {
        Assert.Equal(
            EndpointAddressClassification.GloballyReachable,
            EndpointDestinationPolicy.Classify(IPAddress.Parse("::ffff:8.8.8.8")));
        Assert.Equal(
            EndpointAddressClassification.AllowlistEligible,
            EndpointDestinationPolicy.Classify(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.Equal(
            EndpointAddressClassification.PermanentlyDenied,
            EndpointDestinationPolicy.Classify(IPAddress.Parse("::ffff:169.254.169.254")));
    }

    [Fact]
    public void Scoped_ipv6_is_permanently_denied()
    {
        var scoped = IPAddress.Parse("fe80::1");
        scoped.ScopeId = 4;

        Assert.Equal(
            EndpointAddressClassification.PermanentlyDenied,
            EndpointDestinationPolicy.Classify(scoped));
    }

    [Fact]
    public void Null_addresses_are_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => EndpointDestinationPolicy.Classify(null!));
        Assert.Throws<ArgumentNullException>(() => EndpointDestinationPolicy.NormalizeAddress(null!));
    }

    [Fact]
    public void NormalizeAddress_only_maps_ipv4_mapped_ipv6()
    {
        var ipv4 = IPAddress.Parse("8.8.8.8");
        var ipv6 = IPAddress.Parse("2606:4700:4700::1111");

        Assert.Same(ipv4, EndpointDestinationPolicy.NormalizeAddress(ipv4));
        Assert.Same(ipv6, EndpointDestinationPolicy.NormalizeAddress(ipv6));
        Assert.Equal(
            IPAddress.Parse("8.8.8.8"),
            EndpointDestinationPolicy.NormalizeAddress(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Theory]
    [InlineData("10.0.0.0/8", true)]
    [InlineData("10.20.0.0/16", true)]
    [InlineData("100.64.0.0/10", true)]
    [InlineData("127.0.0.1/32", true)]
    [InlineData("172.16.0.0/12", true)]
    [InlineData("192.168.1.0/24", true)]
    [InlineData("::1/128", true)]
    [InlineData("fd00:1234::/48", true)]
    [InlineData("0.0.0.0/0", false)]
    [InlineData("10.0.0.0/7", false)]
    [InlineData("11.0.0.0/8", false)]
    [InlineData("8.8.8.0/24", false)]
    [InlineData("fc00::/6", false)]
    [InlineData("fe80::/10", false)]
    public void Only_supported_non_public_subnets_are_allowlist_eligible(
        string cidr,
        bool expected)
    {
        Assert.Equal(
            expected,
            EndpointDestinationPolicy.IsAllowlistEligibleNetwork(IPNetwork.Parse(cidr)));
    }

    [Theory]
    [InlineData("Example.COM", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("bücher.example", "xn--bcher-kva.example")]
    [InlineData("XN--BCHER-KVA.EXAMPLE.", "xn--bcher-kva.example")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "::1")]
    [InlineData("::ffff:192.168.1.2", "192.168.1.2")]
    public void Host_canonicalization_handles_dns_idn_root_labels_and_literals(
        string host,
        string expected)
    {
        Assert.True(EndpointDestinationPolicy.TryCanonicalizeHost(host, out var canonical));
        Assert.Equal(expected, canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("example..com")]
    [InlineData("bad_host.example")]
    [InlineData("-bad.example")]
    [InlineData("bad-.example")]
    [InlineData("*.example.com")]
    [InlineData("example .com")]
    [InlineData("example\t.com")]
    [InlineData("example\r\n.com")]
    [InlineData("\ud800.example")]
    public void Invalid_hosts_fail_canonicalization_without_leaking_partial_output(string? host)
    {
        Assert.False(EndpointDestinationPolicy.TryCanonicalizeHost(host, out var canonical));
        Assert.Empty(canonical);
    }

    [Fact]
    public void Overlong_dns_names_and_labels_fail_canonicalization()
    {
        var overlongLabel = $"{new string('a', 64)}.example";
        var overlongHost = string.Join('.', Enumerable.Repeat(new string('a', 63), 4));

        Assert.False(EndpointDestinationPolicy.TryCanonicalizeHost(
            overlongLabel,
            out var labelCanonical));
        Assert.Empty(labelCanonical);
        Assert.False(EndpointDestinationPolicy.TryCanonicalizeHost(
            overlongHost,
            out var hostCanonical));
        Assert.Empty(hostCanonical);
    }

    [Fact]
    public void Scoped_ipv6_literal_is_not_a_canonical_destination_host()
    {
        Assert.False(EndpointDestinationPolicy.TryCanonicalizeHost(
            "fe80::1%4",
            out var canonical));
        Assert.Empty(canonical);
    }
}

public sealed class EndpointDestinationGuardTests
{
    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var resolver = new RecordingResolver([IPAddress.Parse("8.8.8.8")]);
        var options = Options.Create(new EndpointEgressOptions());

        Assert.Throws<ArgumentNullException>(() => new EndpointDestinationGuard(null!, options));
        Assert.Throws<ArgumentNullException>(() => new EndpointDestinationGuard(resolver, null!));
    }

    [Fact]
    public void Constructor_rejects_invalid_rule_hosts_cidrs_and_duplicate_origins()
    {
        var resolver = new RecordingResolver([IPAddress.Loopback]);

        Assert.Throws<InvalidOperationException>(() => CreateGuard(
            resolver,
            Rule("*.example.com", 443, "127.0.0.0/8")));
        Assert.ThrowsAny<Exception>(() => CreateGuard(
            resolver,
            Rule("example.com", 443, "not-a-cidr")));
        Assert.Throws<InvalidOperationException>(() => CreateGuard(
            resolver,
            Rule("EXAMPLE.com.", 443, "127.0.0.0/8"),
            Rule("example.com", 443, "10.0.0.0/8")));
    }

    [Fact]
    public async Task Null_rule_collection_is_treated_as_no_exceptions()
    {
        var guard = new EndpointDestinationGuard(
            new RecordingResolver([IPAddress.Loopback]),
            Options.Create(new EndpointEgressOptions
            {
                AllowedNonPublicDestinations = null!
            }));

        var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
            guard.AuthorizeAsync(
                new Uri("https://private.example/"),
                TestContext.Current.CancellationToken));

        Assert.Equal(EndpointDestinationRejectionReason.NonPublicDestination, exception.Reason);
    }

    [Fact]
    public async Task Constructor_snapshots_rules_and_cidrs()
    {
        var rule = Rule("private.example", 443, "10.0.0.0/8");
        var options = new EndpointEgressOptions
        {
            AllowedNonPublicDestinations = [rule]
        };
        var guard = new EndpointDestinationGuard(
            new RecordingResolver([IPAddress.Parse("10.20.30.40")]),
            Options.Create(options));

        rule.Cidrs.Clear();
        options.AllowedNonPublicDestinations.Clear();

        var authorized = await guard.AuthorizeAsync(
            new Uri("https://private.example/path"),
            TestContext.Current.CancellationToken);

        Assert.Equal(IPAddress.Parse("10.20.30.40"), Assert.Single(authorized.Addresses));
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://example.com/resource")]
    [InlineData("https://user:secret@example.com/resource")]
    public async Task Invalid_destinations_fail_closed(string destination)
    {
        var guard = CreateGuard(new RecordingResolver([IPAddress.Parse("8.8.8.8")]));

        var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
            guard.AuthorizeAsync(
                new Uri(destination, UriKind.RelativeOrAbsolute),
                TestContext.Current.CancellationToken));

        Assert.Equal(EndpointDestinationRejectionReason.InvalidDestination, exception.Reason);
        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, exception.Message);
    }

    [Fact]
    public async Task Null_destination_is_rejected_and_precanceled_calls_do_not_resolve()
    {
        var resolver = new RecordingResolver([IPAddress.Parse("8.8.8.8")]);
        var guard = CreateGuard(resolver);

        await Assert.ThrowsAsync<ArgumentNullException>(() => guard.AuthorizeAsync(
            null!,
            TestContext.Current.CancellationToken));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            guard.AuthorizeAsync(new Uri("https://example.com"), cancellation.Token));
        Assert.Equal(0, resolver.CallCount);
    }

    [Theory]
    [InlineData("http://8.8.8.8/path", "8.8.8.8", 80)]
    [InlineData("https://1.1.1.1:8443/path", "1.1.1.1", 8443)]
    [InlineData("https://[2606:4700:4700::1111]/path", "2606:4700:4700::1111", 443)]
    [InlineData("https://[::ffff:8.8.8.8]/path", "8.8.8.8", 443)]
    public async Task Public_ip_literals_bypass_dns_and_return_the_exact_authorization(
        string uri,
        string expectedAddress,
        int expectedPort)
    {
        var resolver = new RecordingResolver(
            exception: new InvalidOperationException("must not resolve literals"));
        var guard = CreateGuard(resolver);

        var result = await guard.AuthorizeAsync(
            new Uri(uri),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedAddress, result.CanonicalHost);
        Assert.Equal(expectedPort, result.EffectivePort);
        Assert.Equal(IPAddress.Parse(expectedAddress), Assert.Single(result.Addresses));
        Assert.Equal(0, resolver.CallCount);
    }

    [Fact]
    public async Task Dns_hosts_are_canonicalized_and_all_unique_normalized_answers_are_returned()
    {
        var resolver = new RecordingResolver(
        [
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("::ffff:8.8.8.8"),
            IPAddress.Parse("2606:4700:4700::1111")
        ]);
        var guard = CreateGuard(resolver);

        var result = await guard.AuthorizeAsync(
            new Uri("https://BÜCHER.example.:444/path"),
            TestContext.Current.CancellationToken);

        Assert.Equal("xn--bcher-kva.example", resolver.LastHost);
        Assert.Equal("xn--bcher-kva.example", result.CanonicalHost);
        Assert.Equal(444, result.EffectivePort);
        Assert.Equal(
            [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("2606:4700:4700::1111")],
            result.Addresses);
    }

    [Theory]
    [InlineData("169.254.169.254", EndpointDestinationRejectionReason.PermanentlyDeniedDestination)]
    [InlineData("192.0.2.1", EndpointDestinationRejectionReason.PermanentlyDeniedDestination)]
    [InlineData("fe80::1", EndpointDestinationRejectionReason.PermanentlyDeniedDestination)]
    [InlineData("127.0.0.1", EndpointDestinationRejectionReason.NonPublicDestination)]
    [InlineData("10.0.0.1", EndpointDestinationRejectionReason.NonPublicDestination)]
    [InlineData("100.64.0.1", EndpointDestinationRejectionReason.NonPublicDestination)]
    [InlineData("fd00::1", EndpointDestinationRejectionReason.NonPublicDestination)]
    public async Task Unsafe_literals_are_denied_without_dns(
        string address,
        EndpointDestinationRejectionReason expectedReason)
    {
        var resolver = new RecordingResolver([IPAddress.Parse("8.8.8.8")]);
        var guard = CreateGuard(resolver);
        var literal = IPAddress.Parse(address).AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"https://[{address}]/"
            : $"https://{address}/";

        var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
            guard.AuthorizeAsync(new Uri(literal), TestContext.Current.CancellationToken));

        Assert.Equal(expectedReason, exception.Reason);
        Assert.Equal(0, resolver.CallCount);
    }

    [Theory]
    [InlineData("http://2130706433/")]
    [InlineData("http://0x7f000001/")]
    [InlineData("http://0177.0.0.1/")]
    [InlineData("http://127.1/")]
    [InlineData("http://[::ffff:7f00:1]/")]
    public async Task Alternate_numeric_loopback_encodings_are_canonicalized_and_denied(
        string destination)
    {
        var resolver = new RecordingResolver(
            exception: new InvalidOperationException("numeric literals must not reach DNS"));
        var guard = CreateGuard(resolver);

        var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
            guard.AuthorizeAsync(
                new Uri(destination),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            EndpointDestinationRejectionReason.NonPublicDestination,
            exception.Reason);
        Assert.Equal(0, resolver.CallCount);
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.0/8")]
    [InlineData("10.20.30.40", "10.0.0.0/8")]
    [InlineData("100.64.10.20", "100.64.0.0/10")]
    [InlineData("172.20.30.40", "172.16.0.0/12")]
    [InlineData("192.168.1.2", "192.168.0.0/16")]
    [InlineData("::1", "::1/128")]
    [InlineData("fd12:3456::1", "fc00::/7")]
    public async Task Exact_origin_and_cidr_rules_allow_supported_non_public_destinations(
        string address,
        string cidr)
    {
        var guard = CreateGuard(
            new RecordingResolver([IPAddress.Parse(address)]),
            Rule("PRIVATE.example.", 8443, cidr));

        var result = await guard.AuthorizeAsync(
            new Uri("https://private.example:8443/path"),
            TestContext.Current.CancellationToken);

        Assert.Equal("private.example", result.CanonicalHost);
        Assert.Equal(8443, result.EffectivePort);
        Assert.Equal(IPAddress.Parse(address), Assert.Single(result.Addresses));
    }

    [Theory]
    [InlineData("https://other.example:8443/", "private.example", 8443, "10.0.0.0/8")]
    [InlineData("https://private.example:8444/", "private.example", 8443, "10.0.0.0/8")]
    [InlineData("https://private.example:8443/", "private.example", 8443, "192.168.0.0/16")]
    public async Task Rules_require_exact_canonical_host_effective_port_and_matching_cidr(
        string destination,
        string ruleHost,
        int rulePort,
        string cidr)
    {
        var guard = CreateGuard(
            new RecordingResolver([IPAddress.Parse("10.20.30.40")]),
            Rule(ruleHost, rulePort, cidr));

        var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
            guard.AuthorizeAsync(new Uri(destination), TestContext.Current.CancellationToken));

        Assert.Equal(EndpointDestinationRejectionReason.NonPublicDestination, exception.Reason);
    }

    [Fact]
    public async Task Default_effective_port_participates_in_exact_rule_matching()
    {
        var guard = CreateGuard(
            new RecordingResolver([IPAddress.Loopback]),
            Rule("private.example", 443, "127.0.0.0/8"));

        var result = await guard.AuthorizeAsync(
            new Uri("https://private.example/path"),
            TestContext.Current.CancellationToken);

        Assert.Equal(443, result.EffectivePort);
    }

    [Theory]
    [InlineData("169.254.169.254", "0.0.0.0/0")]
    [InlineData("168.63.129.16", "168.63.129.16/32")]
    [InlineData("fd00:ec2::254", "fc00::/7")]
    [InlineData("2001:db8::1", "::/0")]
    public async Task Operator_rules_cannot_override_permanent_denials(
        string address,
        string cidr)
    {
        var guard = CreateGuard(
            new RecordingResolver([IPAddress.Parse(address)]),
            Rule("metadata.example", 443, cidr));

        var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
            guard.AuthorizeAsync(
                new Uri("https://metadata.example/"),
                TestContext.Current.CancellationToken));

        Assert.Equal(
            EndpointDestinationRejectionReason.PermanentlyDeniedDestination,
            exception.Reason);
        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, exception.Message);
        Assert.DoesNotContain(address, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mixed_public_and_unsafe_dns_answers_fail_closed_regardless_of_order()
    {
        var answerSets = new[]
        {
            new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Loopback },
            new[] { IPAddress.Loopback, IPAddress.Parse("8.8.8.8") },
            new[] { IPAddress.Parse("8.8.8.8"), IPAddress.Parse("169.254.169.254") }
        };

        foreach (var answers in answerSets)
        {
            var guard = CreateGuard(new RecordingResolver(answers));
            await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
                guard.AuthorizeAsync(
                    new Uri("https://mixed.example/"),
                    TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Every_dns_answer_must_match_the_same_exact_rule()
    {
        var guard = CreateGuard(
            new RecordingResolver(
            [
                IPAddress.Parse("10.1.2.3"),
                IPAddress.Parse("192.168.1.2")
            ]),
            Rule("private.example", 443, "10.0.0.0/8"));

        var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
            guard.AuthorizeAsync(
                new Uri("https://private.example/"),
                TestContext.Current.CancellationToken));

        Assert.Equal(EndpointDestinationRejectionReason.NonPublicDestination, exception.Reason);
    }

    [Fact]
    public async Task Null_and_empty_dns_results_are_denied()
    {
        foreach (var addresses in new IReadOnlyList<IPAddress>?[] { null, [] })
        {
            var guard = CreateGuard(new RecordingResolver(addresses));
            var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
                guard.AuthorizeAsync(
                    new Uri("https://empty.example/"),
                    TestContext.Current.CancellationToken));

            Assert.Equal(EndpointDestinationRejectionReason.NoAddresses, exception.Reason);
        }
    }

    [Fact]
    public async Task Resolver_failures_are_wrapped_with_a_safe_error_and_inner_exception()
    {
        var resolverFailure = new InvalidOperationException("secret resolver detail");
        var guard = CreateGuard(new RecordingResolver(exception: resolverFailure));

        var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
            guard.AuthorizeAsync(
                new Uri("https://failure.example/"),
                TestContext.Current.CancellationToken));

        Assert.Equal(EndpointDestinationRejectionReason.ResolutionFailed, exception.Reason);
        Assert.Same(resolverFailure, exception.InnerException);
        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, exception.Message);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_cancellation_from_resolver_propagates_unchanged()
    {
        using var cancellation = new CancellationTokenSource();
        var canceled = new OperationCanceledException(cancellation.Token);
        var guard = CreateGuard(new RecordingResolver(
            exception: canceled,
            onResolve: _ => cancellation.Cancel()));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            guard.AuthorizeAsync(new Uri("https://cancel.example/"), cancellation.Token));

        Assert.Same(canceled, exception);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Hanging_resolution_is_bounded_and_sanitized_in_every_transport_mode(
        bool proxyConfigured)
    {
        var resolver = new RecordingResolver(resolve: async cancellationToken =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });
        var guard = CreateGuard(
            resolver,
            dnsTimeoutSeconds: 0,
            proxyConfigured: proxyConfigured);

        var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
            guard.AuthorizeAsync(
                new Uri("https://hanging.example/"),
                TestContext.Current.CancellationToken));

        Assert.Equal(EndpointDestinationRejectionReason.ResolutionFailed, exception.Reason);
        var timeout = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.IsAssignableFrom<OperationCanceledException>(timeout.InnerException);
        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, exception.Message);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task Resolution_deadline_does_not_depend_on_resolver_honoring_cancellation()
    {
        var neverCompletes = new TaskCompletionSource<IReadOnlyList<IPAddress>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new RecordingResolver(resolve: _ => neverCompletes.Task);
        var guard = CreateGuard(resolver, dnsTimeoutSeconds: 0);

        var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
            guard.AuthorizeAsync(
                new Uri("https://noncooperative-resolver.example/"),
                TestContext.Current.CancellationToken));

        Assert.Equal(EndpointDestinationRejectionReason.ResolutionFailed, exception.Reason);
        var timeout = Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.IsAssignableFrom<OperationCanceledException>(timeout.InnerException);
        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, exception.Message);
        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_wins_a_race_with_the_dns_deadline()
    {
        using var cancellation = new CancellationTokenSource();
        var resolver = new RecordingResolver(resolve: async cancellationToken =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        });
        var guard = CreateGuard(resolver, dnsTimeoutSeconds: 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            guard.AuthorizeAsync(new Uri("https://cancel-race.example/"), cancellation.Token));

        Assert.Equal(1, resolver.CallCount);
    }

    [Fact]
    public async Task Noncaller_cancellation_is_treated_as_a_resolution_failure()
    {
        var resolverCancellation = new OperationCanceledException("resolver timeout");
        var guard = CreateGuard(new RecordingResolver(exception: resolverCancellation));

        var exception = await Assert.ThrowsAsync<EndpointDestinationRejectedException>(() =>
            guard.AuthorizeAsync(new Uri("https://timeout.example/"), CancellationToken.None));

        Assert.Equal(EndpointDestinationRejectionReason.ResolutionFailed, exception.Reason);
        Assert.Same(resolverCancellation, exception.InnerException);
    }

    [Fact]
    public void Rejected_exception_exposes_only_the_stable_safe_message_and_reason()
    {
        var inner = new Exception("sensitive host and address");
        var exception = new EndpointDestinationRejectedException(
            EndpointDestinationRejectionReason.PermanentlyDeniedDestination,
            inner);

        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, exception.Message);
        Assert.Equal(
            EndpointDestinationRejectionReason.PermanentlyDeniedDestination,
            exception.Reason);
        Assert.Same(inner, exception.InnerException);
        Assert.DoesNotContain("sensitive", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static EndpointDestinationGuard CreateGuard(
        IDestinationAddressResolver resolver,
        params AllowedNonPublicDestinationRule[] rules) =>
        CreateGuard(resolver, 5, proxyConfigured: false, rules);

    private static EndpointDestinationGuard CreateGuard(
        IDestinationAddressResolver resolver,
        int dnsTimeoutSeconds,
        bool proxyConfigured = false,
        params AllowedNonPublicDestinationRule[] rules) =>
        new(
            resolver,
            Options.Create(new EndpointEgressOptions
            {
                DnsTimeoutSeconds = dnsTimeoutSeconds,
                ProxyUrl = proxyConfigured ? "http://proxy.example:3128" : null,
                RequireProxy = proxyConfigured,
                AllowedNonPublicDestinations = [.. rules]
            }));

    private static AllowedNonPublicDestinationRule Rule(
        string host,
        int port,
        params string[] cidrs) =>
        new()
        {
            Host = host,
            Port = port,
            Cidrs = [.. cidrs]
        };

    private sealed class RecordingResolver : IDestinationAddressResolver
    {
        private readonly IReadOnlyList<IPAddress>? _addresses;
        private readonly Exception? _exception;
        private readonly Action<CancellationToken>? _onResolve;
        private readonly Func<CancellationToken, Task<IReadOnlyList<IPAddress>>>? _resolve;

        public RecordingResolver(
            IReadOnlyList<IPAddress>? addresses = null,
            Exception? exception = null,
            Action<CancellationToken>? onResolve = null,
            Func<CancellationToken, Task<IReadOnlyList<IPAddress>>>? resolve = null)
        {
            _addresses = addresses;
            _exception = exception;
            _onResolve = onResolve;
            _resolve = resolve;
        }

        public int CallCount { get; private set; }

        public string? LastHost { get; private set; }

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(
            string canonicalHost,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastHost = canonicalHost;
            _onResolve?.Invoke(cancellationToken);
            if (_resolve != null)
            {
                return _resolve(cancellationToken);
            }

            if (_exception != null)
            {
                return Task.FromException<IReadOnlyList<IPAddress>>(_exception);
            }

            return Task.FromResult(_addresses!);
        }
    }
}
