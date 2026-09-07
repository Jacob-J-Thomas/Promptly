using Microsoft.Extensions.Options;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class AuthenticationAbuseOptionsValidatorTests
{
    private readonly AuthenticationAbuseOptionsValidator _validator = new();

    [Fact]
    public void Defaults_are_exact_valid_and_returned_by_the_startup_helper()
    {
        var options = new AuthenticationAbuseOptions();

        var result = _validator.Validate(Options.DefaultName, options);
        var validated = AuthenticationAbuseOptionsValidator.GetValidatedSettings(options);

        Assert.True(result.Succeeded);
        Assert.Same(options, validated);
        Assert.Equal("AuthenticationAbuse", AuthenticationAbuseOptions.SectionName);
        Assert.Equal(1, options.ApiReplicaCount);
        Assert.Equal(5, options.IdentityMaxFailedAccessAttempts);
        Assert.Equal(900, options.IdentityLockoutSeconds);
        Assert.Equal(30, options.LoginIpPermitLimit);
        Assert.Equal(60, options.LoginIpWindowSeconds);
        Assert.Equal(5, options.RegistrationIpPermitLimit);
        Assert.Equal(900, options.RegistrationIpWindowSeconds);
        Assert.Equal(10, options.LoginAccountPermitLimit);
        Assert.Equal(900, options.LoginAccountWindowSeconds);
        Assert.Equal(3, options.RegistrationAccountPermitLimit);
        Assert.Equal(900, options.RegistrationAccountWindowSeconds);
        Assert.Equal(10, options.PasswordSprayDistinctAccountLimit);
        Assert.Equal(600, options.PasswordSprayWindowSeconds);
        Assert.Equal(900, options.PasswordSprayBlockSeconds);
        Assert.Equal(16, options.MaximumConcurrentAuthenticationRequests);
        Assert.Equal(10_000, options.MaximumTrackedPartitions);
        Assert.Equal(50_000, options.MaximumTrackedSprayAccountEntries);
        Assert.Equal(256, options.AccountLockStripeCount);
        Assert.Equal(900, options.MaximumRetryAfterSeconds);
        Assert.Equal(256, AuthenticationAbuseOptions.MaximumTrustedProxyNetworkCount);
        Assert.Empty(options.TrustedProxyNetworks);
    }

    [Theory]
    [InlineData(nameof(AuthenticationAbuseOptions.ApiReplicaCount), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.ApiReplicaCount), 2)]
    [InlineData(nameof(AuthenticationAbuseOptions.IdentityMaxFailedAccessAttempts), 1)]
    [InlineData(nameof(AuthenticationAbuseOptions.IdentityMaxFailedAccessAttempts), 101)]
    [InlineData(nameof(AuthenticationAbuseOptions.IdentityLockoutSeconds), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.IdentityLockoutSeconds), 86_401)]
    [InlineData(nameof(AuthenticationAbuseOptions.LoginIpPermitLimit), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.LoginIpPermitLimit), 100_001)]
    [InlineData(nameof(AuthenticationAbuseOptions.RegistrationIpPermitLimit), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.RegistrationIpPermitLimit), 100_001)]
    [InlineData(nameof(AuthenticationAbuseOptions.LoginAccountPermitLimit), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.LoginAccountPermitLimit), 100_001)]
    [InlineData(nameof(AuthenticationAbuseOptions.RegistrationAccountPermitLimit), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.RegistrationAccountPermitLimit), 100_001)]
    [InlineData(nameof(AuthenticationAbuseOptions.LoginIpWindowSeconds), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.LoginIpWindowSeconds), 3_601)]
    [InlineData(nameof(AuthenticationAbuseOptions.RegistrationIpWindowSeconds), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.RegistrationIpWindowSeconds), 3_601)]
    [InlineData(nameof(AuthenticationAbuseOptions.LoginAccountWindowSeconds), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.LoginAccountWindowSeconds), 3_601)]
    [InlineData(nameof(AuthenticationAbuseOptions.RegistrationAccountWindowSeconds), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.RegistrationAccountWindowSeconds), 3_601)]
    [InlineData(nameof(AuthenticationAbuseOptions.PasswordSprayDistinctAccountLimit), 1)]
    [InlineData(nameof(AuthenticationAbuseOptions.PasswordSprayDistinctAccountLimit), 10_001)]
    [InlineData(nameof(AuthenticationAbuseOptions.PasswordSprayWindowSeconds), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.PasswordSprayWindowSeconds), 3_601)]
    [InlineData(nameof(AuthenticationAbuseOptions.PasswordSprayBlockSeconds), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.PasswordSprayBlockSeconds), 3_601)]
    [InlineData(nameof(AuthenticationAbuseOptions.MaximumConcurrentAuthenticationRequests), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.MaximumConcurrentAuthenticationRequests), 257)]
    [InlineData(nameof(AuthenticationAbuseOptions.MaximumTrackedPartitions), 63)]
    [InlineData(nameof(AuthenticationAbuseOptions.MaximumTrackedPartitions), 1_000_001)]
    [InlineData(nameof(AuthenticationAbuseOptions.MaximumTrackedSprayAccountEntries), 63)]
    [InlineData(nameof(AuthenticationAbuseOptions.MaximumTrackedSprayAccountEntries), 1_000_001)]
    [InlineData(nameof(AuthenticationAbuseOptions.AccountLockStripeCount), 15)]
    [InlineData(nameof(AuthenticationAbuseOptions.AccountLockStripeCount), 4_097)]
    [InlineData(nameof(AuthenticationAbuseOptions.MaximumRetryAfterSeconds), 0)]
    [InlineData(nameof(AuthenticationAbuseOptions.MaximumRetryAfterSeconds), 3_601)]
    public void Values_outside_each_inclusive_boundary_fail(string propertyName, int value)
    {
        var options = new AuthenticationAbuseOptions();
        SetValue(options, propertyName, value);

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(result, $"AuthenticationAbuse:{propertyName} must");
    }

    [Fact]
    public void All_compatible_lower_boundaries_are_valid()
    {
        var options = new AuthenticationAbuseOptions
        {
            ApiReplicaCount = 1,
            IdentityMaxFailedAccessAttempts = 2,
            IdentityLockoutSeconds = 1,
            LoginIpPermitLimit = 1,
            LoginIpWindowSeconds = 1,
            RegistrationIpPermitLimit = 1,
            RegistrationIpWindowSeconds = 1,
            LoginAccountPermitLimit = 2,
            LoginAccountWindowSeconds = 1,
            RegistrationAccountPermitLimit = 1,
            RegistrationAccountWindowSeconds = 1,
            PasswordSprayDistinctAccountLimit = 2,
            PasswordSprayWindowSeconds = 1,
            PasswordSprayBlockSeconds = 1,
            MaximumConcurrentAuthenticationRequests = 1,
            MaximumTrackedPartitions = 64,
            MaximumTrackedSprayAccountEntries = 64,
            AccountLockStripeCount = 16,
            MaximumRetryAfterSeconds = 1
        };

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void All_upper_boundaries_are_valid()
    {
        var options = new AuthenticationAbuseOptions
        {
            ApiReplicaCount = 1,
            IdentityMaxFailedAccessAttempts = 100,
            IdentityLockoutSeconds = 86_400,
            LoginIpPermitLimit = 100_000,
            LoginIpWindowSeconds = 3_600,
            RegistrationIpPermitLimit = 100_000,
            RegistrationIpWindowSeconds = 3_600,
            LoginAccountPermitLimit = 100_000,
            LoginAccountWindowSeconds = 3_600,
            RegistrationAccountPermitLimit = 100_000,
            RegistrationAccountWindowSeconds = 3_600,
            PasswordSprayDistinctAccountLimit = 10_000,
            PasswordSprayWindowSeconds = 3_600,
            PasswordSprayBlockSeconds = 3_600,
            MaximumConcurrentAuthenticationRequests = 256,
            MaximumTrackedPartitions = 1_000_000,
            MaximumTrackedSprayAccountEntries = 1_000_000,
            AccountLockStripeCount = 4_096,
            MaximumRetryAfterSeconds = 3_600
        };

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(255)]
    [InlineData(4_095)]
    public void In_range_non_power_of_two_stripe_counts_fail(int stripeCount)
    {
        var options = new AuthenticationAbuseOptions
        {
            AccountLockStripeCount = stripeCount
        };

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(result, "AuthenticationAbuse:AccountLockStripeCount must be a power of two.");
    }

    [Fact]
    public void Login_account_permit_limit_must_cover_identity_failure_threshold()
    {
        var options = new AuthenticationAbuseOptions
        {
            IdentityMaxFailedAccessAttempts = 6,
            LoginAccountPermitLimit = 5
        };

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(
            result,
            "LoginAccountPermitLimit must be greater than or equal to " +
            "AuthenticationAbuse:IdentityMaxFailedAccessAttempts");

        options.LoginAccountPermitLimit = options.IdentityMaxFailedAccessAttempts;
        Assert.True(_validator.Validate(Options.DefaultName, options).Succeeded);
    }

    [Theory]
    [InlineData(nameof(AuthenticationAbuseOptions.LoginIpWindowSeconds))]
    [InlineData(nameof(AuthenticationAbuseOptions.RegistrationIpWindowSeconds))]
    [InlineData(nameof(AuthenticationAbuseOptions.LoginAccountWindowSeconds))]
    [InlineData(nameof(AuthenticationAbuseOptions.RegistrationAccountWindowSeconds))]
    [InlineData(nameof(AuthenticationAbuseOptions.PasswordSprayWindowSeconds))]
    [InlineData(nameof(AuthenticationAbuseOptions.PasswordSprayBlockSeconds))]
    public void Retry_after_cap_must_cover_each_limiter_period(string propertyName)
    {
        var options = new AuthenticationAbuseOptions();
        SetValue(options, propertyName, 901);

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(
            result,
            "MaximumRetryAfterSeconds must be greater than or equal to every limiter window");
    }

    [Fact]
    public void Maximum_tracked_spray_entries_must_cover_spray_cardinality()
    {
        var options = new AuthenticationAbuseOptions
        {
            PasswordSprayDistinctAccountLimit = 65,
            MaximumTrackedSprayAccountEntries = 64
        };

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(
            result,
            "MaximumTrackedSprayAccountEntries must be greater than or equal to " +
            "AuthenticationAbuse:PasswordSprayDistinctAccountLimit");

        options.MaximumTrackedSprayAccountEntries = options.PasswordSprayDistinctAccountLimit;
        Assert.True(_validator.Validate(Options.DefaultName, options).Succeeded);
    }

    [Fact]
    public void Canonical_bounded_IPv4_and_IPv6_proxy_networks_are_valid()
    {
        var options = new AuthenticationAbuseOptions
        {
            TrustedProxyNetworks =
            [
                "10.0.0.0/8",
                "192.168.1.0/24",
                "2001:db8::/32",
                "fd00::/48",
                "8.8.8.8/32",
                "2606:4700:4700::1111/128"
            ]
        };

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("8.0.0.0/8", 32)]
    [InlineData("2606:4700::/32", 128)]
    public void Broad_public_proxy_networks_require_exact_host_routes(
        string network,
        int hostPrefixLength)
    {
        var options = new AuthenticationAbuseOptions
        {
            TrustedProxyNetworks = [network]
        };

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(result, $"publicly reachable and must use an exact /{hostPrefixLength}");
        Assert.DoesNotContain(
            result.Failures!,
            failure => failure.Contains(network, StringComparison.Ordinal));
    }

    [Fact]
    public void Trusted_proxy_network_count_is_bounded()
    {
        var options = new AuthenticationAbuseOptions
        {
            TrustedProxyNetworks = Enumerable.Range(
                    0,
                    AuthenticationAbuseOptions.MaximumTrustedProxyNetworkCount + 1)
                .Select(index => $"10.{index / 256}.{index % 256}.1/32")
                .ToList()
        };

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(result, "TrustedProxyNetworks cannot contain more than 256 entries");
    }

    [Fact]
    public void Null_proxy_network_collection_fails_closed()
    {
        var options = new AuthenticationAbuseOptions
        {
            TrustedProxyNetworks = null!
        };

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(result, "AuthenticationAbuse:TrustedProxyNetworks cannot be null.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cidr")]
    [InlineData(" 10.0.0.0/8")]
    [InlineData("10.0.0.1/8")]
    [InlineData("2001:0DB8::/32")]
    public void Null_malformed_and_noncanonical_proxy_CIDRs_fail_without_echoing_values(
        string? network)
    {
        var options = new AuthenticationAbuseOptions
        {
            TrustedProxyNetworks = [network!]
        };

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(result, "TrustedProxyNetworks:0 must be a canonical IPv4 or IPv6 CIDR");
        if (!string.IsNullOrEmpty(network))
        {
            Assert.DoesNotContain(
                result.Failures!,
                failure => failure.Contains(network, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Duplicate_proxy_CIDRs_are_rejected_by_index()
    {
        var options = new AuthenticationAbuseOptions
        {
            TrustedProxyNetworks = ["10.0.0.0/8", "10.0.0.0/8"]
        };

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(result, "TrustedProxyNetworks:1 duplicates an existing trusted proxy CIDR");
    }

    [Theory]
    [InlineData("::ffff:192.0.2.0/120", "must not use an IPv4-mapped IPv6 address")]
    [InlineData("fe80::1%1/128", "must not use a scoped IPv6 address")]
    [InlineData("0.0.0.0/32", "must not use an unspecified network address")]
    [InlineData("::/128", "must not use an unspecified network address")]
    [InlineData("224.0.0.0/8", "must not use a multicast network")]
    [InlineData("ff00::/32", "must not use a multicast network")]
    [InlineData("10.0.0.0/7", "must use a prefix length of at least 8 bits")]
    [InlineData("2001:db8::/31", "must use a prefix length of at least 32 bits")]
    public void Unsafe_proxy_network_categories_are_rejected(string network, string failure)
    {
        var options = new AuthenticationAbuseOptions
        {
            TrustedProxyNetworks = [network]
        };

        var result = _validator.Validate(Options.DefaultName, options);

        AssertFailure(result, failure);
        Assert.DoesNotContain(
            result.Failures!,
            message => message.Contains(network, StringComparison.Ordinal));
    }

    [Fact]
    public void Independent_failures_are_aggregated_without_raw_configuration_values()
    {
        const string RawConfigurationValue = "operator-secret-proxy-value";
        var options = new AuthenticationAbuseOptions
        {
            ApiReplicaCount = 2,
            IdentityMaxFailedAccessAttempts = 101,
            LoginAccountPermitLimit = 0,
            PasswordSprayDistinctAccountLimit = 10_001,
            MaximumTrackedPartitions = 63,
            AccountLockStripeCount = 17,
            MaximumRetryAfterSeconds = 0,
            TrustedProxyNetworks = [RawConfigurationValue]
        };

        var result = _validator.Validate(Options.DefaultName, options);

        Assert.True(result.Failed);
        Assert.NotNull(result.Failures);
        Assert.True(result.Failures.Count() >= 10);
        Assert.DoesNotContain(
            result.Failures,
            failure => failure.Contains(RawConfigurationValue, StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_settings_helper_throws_without_disclosing_raw_values()
    {
        const string RawConfigurationValue = "operator-secret-proxy-value";
        var options = new AuthenticationAbuseOptions
        {
            TrustedProxyNetworks = [RawConfigurationValue]
        };

        var exception = Assert.Throws<OptionsValidationException>(() =>
            AuthenticationAbuseOptionsValidator.GetValidatedSettings(options));

        Assert.Contains("TrustedProxyNetworks:0", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(RawConfigurationValue, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_options_are_rejected_by_both_entry_points()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _validator.Validate(Options.DefaultName, null!));
        Assert.Throws<ArgumentNullException>(() =>
            AuthenticationAbuseOptionsValidator.GetValidatedSettings(null!));
    }

    private static void SetValue(
        AuthenticationAbuseOptions options,
        string propertyName,
        int value)
    {
        switch (propertyName)
        {
            case nameof(AuthenticationAbuseOptions.ApiReplicaCount):
                options.ApiReplicaCount = value;
                break;
            case nameof(AuthenticationAbuseOptions.IdentityMaxFailedAccessAttempts):
                options.IdentityMaxFailedAccessAttempts = value;
                break;
            case nameof(AuthenticationAbuseOptions.IdentityLockoutSeconds):
                options.IdentityLockoutSeconds = value;
                break;
            case nameof(AuthenticationAbuseOptions.LoginIpPermitLimit):
                options.LoginIpPermitLimit = value;
                break;
            case nameof(AuthenticationAbuseOptions.LoginIpWindowSeconds):
                options.LoginIpWindowSeconds = value;
                break;
            case nameof(AuthenticationAbuseOptions.RegistrationIpPermitLimit):
                options.RegistrationIpPermitLimit = value;
                break;
            case nameof(AuthenticationAbuseOptions.RegistrationIpWindowSeconds):
                options.RegistrationIpWindowSeconds = value;
                break;
            case nameof(AuthenticationAbuseOptions.LoginAccountPermitLimit):
                options.LoginAccountPermitLimit = value;
                break;
            case nameof(AuthenticationAbuseOptions.LoginAccountWindowSeconds):
                options.LoginAccountWindowSeconds = value;
                break;
            case nameof(AuthenticationAbuseOptions.RegistrationAccountPermitLimit):
                options.RegistrationAccountPermitLimit = value;
                break;
            case nameof(AuthenticationAbuseOptions.RegistrationAccountWindowSeconds):
                options.RegistrationAccountWindowSeconds = value;
                break;
            case nameof(AuthenticationAbuseOptions.PasswordSprayDistinctAccountLimit):
                options.PasswordSprayDistinctAccountLimit = value;
                break;
            case nameof(AuthenticationAbuseOptions.PasswordSprayWindowSeconds):
                options.PasswordSprayWindowSeconds = value;
                break;
            case nameof(AuthenticationAbuseOptions.PasswordSprayBlockSeconds):
                options.PasswordSprayBlockSeconds = value;
                break;
            case nameof(AuthenticationAbuseOptions.MaximumConcurrentAuthenticationRequests):
                options.MaximumConcurrentAuthenticationRequests = value;
                break;
            case nameof(AuthenticationAbuseOptions.MaximumTrackedPartitions):
                options.MaximumTrackedPartitions = value;
                break;
            case nameof(AuthenticationAbuseOptions.MaximumTrackedSprayAccountEntries):
                options.MaximumTrackedSprayAccountEntries = value;
                break;
            case nameof(AuthenticationAbuseOptions.AccountLockStripeCount):
                options.AccountLockStripeCount = value;
                break;
            case nameof(AuthenticationAbuseOptions.MaximumRetryAfterSeconds):
                options.MaximumRetryAfterSeconds = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(propertyName));
        }
    }

    private static void AssertFailure(ValidateOptionsResult result, string fragment)
    {
        Assert.True(result.Failed);
        Assert.NotNull(result.Failures);
        Assert.Contains(
            result.Failures,
            failure => failure.Contains(fragment, StringComparison.Ordinal));
    }
}
