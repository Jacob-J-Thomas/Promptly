using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class AuthenticationPartitionKeyProviderTests
{
    [Fact]
    public void KeysAreStablePrivateLowercaseDigests()
    {
        using var provider = CreateProvider();
        var context = Context("203.0.113.9");

        var clientKey = provider.GetClientKey(context);
        var repeatedClientKey = provider.GetClientKey(context);
        var accountKey = provider.GetAccountKey("USER@EXAMPLE.COM");

        Assert.Equal(clientKey, repeatedClientKey);
        Assert.Matches("^[0-9a-f]{64}$", clientKey);
        Assert.Matches("^[0-9a-f]{64}$", accountKey);
        Assert.DoesNotContain("203.0.113.9", clientKey, StringComparison.Ordinal);
        Assert.DoesNotContain("USER", accountKey, StringComparison.Ordinal);
        Assert.NotEqual(clientKey, accountKey);
    }

    [Fact]
    public void ProvidersUseIndependentProcessRandomKeys()
    {
        using var first = CreateProvider();
        using var second = CreateProvider();
        var context = Context("203.0.113.10");

        Assert.NotEqual(first.GetClientKey(context), second.GetClientKey(context));
        Assert.NotEqual(
            first.GetAccountKey("USER@EXAMPLE.COM"),
            second.GetAccountKey("USER@EXAMPLE.COM"));
    }

    [Fact]
    public void MissingPeersShareOnePrivateSentinelPartition()
    {
        using var provider = CreateProvider();

        var first = provider.GetClientKey(new DefaultHttpContext());
        var second = provider.GetClientKey(new DefaultHttpContext());

        Assert.Equal(first, second);
        Assert.NotEqual(first, provider.GetClientKey(Context("127.0.0.1")));
        Assert.DoesNotContain("missing", first, StringComparison.Ordinal);
    }

    [Fact]
    public void DirectIpv4AndMappedIpv4HaveTheSameCanonicalPartition()
    {
        using var provider = CreateProvider();

        var ipv4 = provider.GetClientKey(Context("192.0.2.44"));
        var mapped = provider.GetClientKey(Context("::ffff:192.0.2.44"));

        Assert.Equal(ipv4, mapped);
    }

    [Fact]
    public void Ipv6ClientPartitionsAggregateAtSixtyFourBits()
    {
        using var provider = CreateProvider();

        var first = provider.GetClientKey(Context("2001:db8:1234:5678::1"));
        var samePrefix = provider.GetClientKey(Context("2001:db8:1234:5678:ffff::99"));
        var differentPrefix = provider.GetClientKey(Context("2001:db8:1234:5679::1"));

        Assert.Equal(first, samePrefix);
        Assert.NotEqual(first, differentPrefix);
    }

    [Fact]
    public void TrustedProxyUsesExactlyOneCanonicalForwardedLiteral()
    {
        using var provider = CreateProvider("10.20.0.0/16", "2001:db8:55::/48");

        var ipv4Forwarded = Context("10.20.3.4", "203.0.113.77");
        var mappedForwarded = Context("10.20.3.4", "::ffff:203.0.113.77");
        var ipv6Forwarded = Context("2001:db8:55::a", "2001:0db8:99::7");

        Assert.Equal(
            provider.GetClientKey(Context("203.0.113.77")),
            provider.GetClientKey(ipv4Forwarded));
        Assert.Equal(
            provider.GetClientKey(ipv4Forwarded),
            provider.GetClientKey(mappedForwarded));
        Assert.Equal(
            provider.GetClientKey(Context("2001:db8:99::7")),
            provider.GetClientKey(ipv6Forwarded));
    }

    [Fact]
    public void UntrustedPeerCannotOverrideItsPartitionWithForwardedHeader()
    {
        using var provider = CreateProvider("10.20.0.0/16");

        var supplied = Context("198.51.100.12", "203.0.113.77");
        var direct = Context("198.51.100.12");

        Assert.Equal(provider.GetClientKey(direct), provider.GetClientKey(supplied));
        Assert.NotEqual(
            provider.GetClientKey(Context("203.0.113.77")),
            provider.GetClientKey(supplied));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("203.0.113.7, 198.51.100.8")]
    [InlineData("203.0.113")]
    [InlineData("0xCB.0.0.1")]
    [InlineData("[2001:db8::1]")]
    [InlineData("fe80::1%4")]
    [InlineData("0.0.0.0")]
    [InlineData("255.255.255.255")]
    [InlineData("224.0.0.1")]
    [InlineData("::")]
    [InlineData("ff02::1")]
    public void TrustedProxyIgnoresMalformedOrNonUnicastForwardedValues(string value)
    {
        using var provider = CreateProvider("10.20.0.0/16");
        var supplied = Context("10.20.4.5", value);
        var direct = Context("10.20.4.5");

        Assert.Equal(provider.GetClientKey(direct), provider.GetClientKey(supplied));
    }

    [Fact]
    public void TrustedProxyIgnoresMultipleForwardedHeaderValues()
    {
        using var provider = CreateProvider("10.20.0.0/16");
        var context = Context("10.20.4.5");
        context.Request.Headers["X-Forwarded-For"] = new StringValues(
            ["203.0.113.7", "198.51.100.8"]);

        Assert.Equal(
            provider.GetClientKey(Context("10.20.4.5")),
            provider.GetClientKey(context));
    }

    [Fact]
    public void NonByteAlignedTrustedNetworkMatchesOnlyItsAddressFamilyAndPrefix()
    {
        using var provider = CreateProvider("10.20.32.0/19");

        var inside = Context("10.20.63.255", "203.0.113.9");
        var outside = Context("10.20.64.1", "203.0.113.9");
        var otherFamily = Context("2001:db8::1", "203.0.113.9");

        Assert.Equal(
            provider.GetClientKey(Context("203.0.113.9")),
            provider.GetClientKey(inside));
        Assert.Equal(
            provider.GetClientKey(Context("10.20.64.1")),
            provider.GetClientKey(outside));
        Assert.Equal(
            provider.GetClientKey(Context("2001:db8::1")),
            provider.GetClientKey(otherFamily));
    }

    [Fact]
    public void InvalidConfiguredNetworksAreIgnoredAndNullListFailsClosed()
    {
        using var invalid = CreateProvider("not-a-network", "10.0.0.0/99");
        using var nullList = new AuthenticationPartitionKeyProvider(
            Options.Create(new AuthenticationAbuseOptions { TrustedProxyNetworks = null! }));
        var forwarded = Context("10.0.0.1", "203.0.113.9");

        Assert.Equal(
            invalid.GetClientKey(Context("10.0.0.1")),
            invalid.GetClientKey(forwarded));
        Assert.Equal(
            nullList.GetClientKey(Context("10.0.0.1")),
            nullList.GetClientKey(forwarded));
    }

    [Theory]
    [InlineData("1.2.3.1234")]
    [InlineData("1.2.3.999")]
    [InlineData("1.2.3.+4")]
    [InlineData("1.2.3.")]
    [InlineData("2001:db8::1]")]
    public void AdditionalNonLiteralForwardedFormsFailClosed(string forwardedFor)
    {
        using var provider = CreateProvider("10.0.0.0/8");
        var context = Context("10.0.0.1", forwardedFor);

        Assert.Equal(
            provider.GetClientKey(Context("10.0.0.1")),
            provider.GetClientKey(context));
    }

    [Fact]
    public void PublicMethodsValidateNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new AuthenticationPartitionKeyProvider(null!));
        using var provider = CreateProvider();
        Assert.Throws<ArgumentNullException>(() => provider.GetClientKey(null!));
        Assert.Throws<ArgumentNullException>(() => provider.GetAccountKey(null!));
    }

    [Fact]
    public void DisposeIsIdempotentAndPreventsFurtherKeyUse()
    {
        using var provider = CreateProvider();

        provider.Dispose();
        provider.Dispose();

        Assert.Throws<ObjectDisposedException>(() => provider.GetAccountKey("USER@EXAMPLE.COM"));
        Assert.Throws<ObjectDisposedException>(() => provider.GetClientKey(Context("192.0.2.1")));
    }

    private static AuthenticationPartitionKeyProvider CreateProvider(params string[] trustedNetworks) =>
        new(Options.Create(new AuthenticationAbuseOptions
        {
            TrustedProxyNetworks = [.. trustedNetworks]
        }));

    private static DefaultHttpContext Context(string remoteIp, string? forwardedFor = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        return context;
    }
}
