using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;

namespace Promptly.Server.Security;

public sealed class AuthenticationAbuseOptions
{
    public const string SectionName = "AuthenticationAbuse";
    public const int MaximumTrustedProxyNetworkCount = 256;

    public int ApiReplicaCount { get; set; } = 1;
    public int IdentityMaxFailedAccessAttempts { get; set; } = 5;
    public int IdentityLockoutSeconds { get; set; } = 900;
    public int LoginIpPermitLimit { get; set; } = 30;
    public int LoginIpWindowSeconds { get; set; } = 60;
    public int RegistrationIpPermitLimit { get; set; } = 5;
    public int RegistrationIpWindowSeconds { get; set; } = 900;
    public int LoginAccountPermitLimit { get; set; } = 10;
    public int LoginAccountWindowSeconds { get; set; } = 900;
    public int RegistrationAccountPermitLimit { get; set; } = 3;
    public int RegistrationAccountWindowSeconds { get; set; } = 900;
    public int PasswordSprayDistinctAccountLimit { get; set; } = 10;
    public int PasswordSprayWindowSeconds { get; set; } = 600;
    public int PasswordSprayBlockSeconds { get; set; } = 900;
    public int MaximumTrackedPartitions { get; set; } = 10_000;
    public int MaximumTrackedSprayAccountEntries { get; set; } = 50_000;
    public int AccountLockStripeCount { get; set; } = 256;
    public int MaximumRetryAfterSeconds { get; set; } = 900;
    public List<string> TrustedProxyNetworks { get; set; } = [];
}

public sealed class AuthenticationAbuseOptionsValidator
    : IValidateOptions<AuthenticationAbuseOptions>
{
    private const string SectionName = AuthenticationAbuseOptions.SectionName;
    private static readonly IPNetwork[] BroadTrustedProxyEligibleNetworks =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10")
    ];

    public ValidateOptionsResult Validate(string? name, AuthenticationAbuseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.ApiReplicaCount != 1)
        {
            failures.Add(
                $"{SectionName}:ApiReplicaCount must equal 1 because process-local abuse " +
                "controls support exactly one API replica.");
        }

        ValidateRange(
            options.IdentityMaxFailedAccessAttempts,
            nameof(options.IdentityMaxFailedAccessAttempts),
            minimum: 2,
            maximum: 100,
            failures);
        ValidateRange(
            options.IdentityLockoutSeconds,
            nameof(options.IdentityLockoutSeconds),
            minimum: 1,
            maximum: 86_400,
            failures);

        ValidatePermitLimit(
            options.LoginIpPermitLimit,
            nameof(options.LoginIpPermitLimit),
            failures);
        ValidateWindow(
            options.LoginIpWindowSeconds,
            nameof(options.LoginIpWindowSeconds),
            failures);
        ValidatePermitLimit(
            options.RegistrationIpPermitLimit,
            nameof(options.RegistrationIpPermitLimit),
            failures);
        ValidateWindow(
            options.RegistrationIpWindowSeconds,
            nameof(options.RegistrationIpWindowSeconds),
            failures);
        ValidatePermitLimit(
            options.LoginAccountPermitLimit,
            nameof(options.LoginAccountPermitLimit),
            failures);
        ValidateWindow(
            options.LoginAccountWindowSeconds,
            nameof(options.LoginAccountWindowSeconds),
            failures);
        ValidatePermitLimit(
            options.RegistrationAccountPermitLimit,
            nameof(options.RegistrationAccountPermitLimit),
            failures);
        ValidateWindow(
            options.RegistrationAccountWindowSeconds,
            nameof(options.RegistrationAccountWindowSeconds),
            failures);

        ValidateRange(
            options.PasswordSprayDistinctAccountLimit,
            nameof(options.PasswordSprayDistinctAccountLimit),
            minimum: 2,
            maximum: 10_000,
            failures);
        ValidateWindow(
            options.PasswordSprayWindowSeconds,
            nameof(options.PasswordSprayWindowSeconds),
            failures);
        ValidateWindow(
            options.PasswordSprayBlockSeconds,
            nameof(options.PasswordSprayBlockSeconds),
            failures);
        ValidateRange(
            options.MaximumTrackedPartitions,
            nameof(options.MaximumTrackedPartitions),
            minimum: 64,
            maximum: 1_000_000,
            failures);
        ValidateRange(
            options.MaximumTrackedSprayAccountEntries,
            nameof(options.MaximumTrackedSprayAccountEntries),
            minimum: 64,
            maximum: 1_000_000,
            failures);
        ValidateRange(
            options.AccountLockStripeCount,
            nameof(options.AccountLockStripeCount),
            minimum: 16,
            maximum: 4_096,
            failures);
        ValidateWindow(
            options.MaximumRetryAfterSeconds,
            nameof(options.MaximumRetryAfterSeconds),
            failures);

        if (options.AccountLockStripeCount > 0
            && !IsPowerOfTwo(options.AccountLockStripeCount))
        {
            failures.Add($"{SectionName}:AccountLockStripeCount must be a power of two.");
        }

        if (options.LoginAccountPermitLimit < options.IdentityMaxFailedAccessAttempts)
        {
            failures.Add(
                $"{SectionName}:LoginAccountPermitLimit must be greater than or equal to " +
                $"{SectionName}:IdentityMaxFailedAccessAttempts.");
        }

        if (options.MaximumTrackedSprayAccountEntries < options.PasswordSprayDistinctAccountLimit)
        {
            failures.Add(
                $"{SectionName}:MaximumTrackedSprayAccountEntries must be greater than or equal to " +
                $"{SectionName}:PasswordSprayDistinctAccountLimit.");
        }

        if (MaximumConfiguredRetryPeriod(options) > options.MaximumRetryAfterSeconds)
        {
            failures.Add(
                $"{SectionName}:MaximumRetryAfterSeconds must be greater than or equal to " +
                "every limiter window and password-spray block period.");
        }

        ValidateTrustedProxyNetworks(options.TrustedProxyNetworks, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    public static AuthenticationAbuseOptions GetValidatedSettings(
        AuthenticationAbuseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var validation = new AuthenticationAbuseOptionsValidator().Validate(
            Options.DefaultName,
            options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(AuthenticationAbuseOptions),
                validation.Failures);
        }

        return options;
    }

    private static int MaximumConfiguredRetryPeriod(AuthenticationAbuseOptions options) =>
        new[]
        {
            options.LoginIpWindowSeconds,
            options.RegistrationIpWindowSeconds,
            options.LoginAccountWindowSeconds,
            options.RegistrationAccountWindowSeconds,
            options.PasswordSprayWindowSeconds,
            options.PasswordSprayBlockSeconds
        }.Max();

    private static bool IsPowerOfTwo(int value) => (value & (value - 1)) == 0;

    private static void ValidatePermitLimit(
        int value,
        string propertyName,
        ICollection<string> failures) =>
        ValidateRange(value, propertyName, minimum: 1, maximum: 100_000, failures);

    private static void ValidateWindow(
        int value,
        string propertyName,
        ICollection<string> failures) =>
        ValidateRange(value, propertyName, minimum: 1, maximum: 3_600, failures);

    private static void ValidateRange(
        int value,
        string propertyName,
        int minimum,
        int maximum,
        ICollection<string> failures)
    {
        if (value < minimum || value > maximum)
        {
            failures.Add(
                $"{SectionName}:{propertyName} must be between {minimum} and {maximum}.");
        }
    }

    private static void ValidateTrustedProxyNetworks(
        IReadOnlyList<string>? networks,
        ICollection<string> failures)
    {
        if (networks is null)
        {
            failures.Add($"{SectionName}:TrustedProxyNetworks cannot be null.");
            return;
        }

        if (networks.Count > AuthenticationAbuseOptions.MaximumTrustedProxyNetworkCount)
        {
            failures.Add(
                $"{SectionName}:TrustedProxyNetworks cannot contain more than " +
                $"{AuthenticationAbuseOptions.MaximumTrustedProxyNetworkCount} entries.");
        }

        var uniqueNetworks = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < networks.Count; index++)
        {
            var configuredNetwork = networks[index];
            var path = $"{SectionName}:TrustedProxyNetworks:{index}";
            if (string.IsNullOrEmpty(configuredNetwork)
                || !IPNetwork.TryParse(configuredNetwork, out var network)
                || !string.Equals(network.ToString(), configuredNetwork, StringComparison.Ordinal))
            {
                failures.Add($"{path} must be a canonical IPv4 or IPv6 CIDR.");
                continue;
            }

            if (!uniqueNetworks.Add(network.ToString()))
            {
                failures.Add($"{path} duplicates an existing trusted proxy CIDR.");
            }

            var address = network.BaseAddress;
            if (address.IsIPv4MappedToIPv6)
            {
                failures.Add($"{path} must not use an IPv4-mapped IPv6 address.");
                continue;
            }

            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0)
            {
                failures.Add($"{path} must not use a scoped IPv6 address.");
                continue;
            }

            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                failures.Add($"{path} must not use an unspecified network address.");
            }

            if (IsMulticast(address))
            {
                failures.Add($"{path} must not use a multicast network.");
            }

            var minimumPrefixLength = address.AddressFamily == AddressFamily.InterNetwork
                ? 8
                : 32;
            if (network.PrefixLength < minimumPrefixLength)
            {
                failures.Add(
                    $"{path} must use a prefix length of at least {minimumPrefixLength} bits.");
            }

            var hostPrefixLength = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            if (network.PrefixLength < hostPrefixLength
                && !IsBroadTrustedProxyNetworkEligible(network))
            {
                failures.Add(
                    $"{path} is publicly reachable and must use an exact /{hostPrefixLength} " +
                    "host route; broad CIDRs are limited to supported private or " +
                    "special-use ranges.");
            }
        }
    }

    private static bool IsBroadTrustedProxyNetworkEligible(IPNetwork network) =>
        BroadTrustedProxyEligibleNetworks.Any(eligible =>
            eligible.BaseAddress.AddressFamily == network.BaseAddress.AddressFamily
            && network.PrefixLength >= eligible.PrefixLength
            && eligible.Contains(network.BaseAddress));

    private static bool IsMulticast(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6Multicast;
        }

        var firstOctet = address.GetAddressBytes()[0];
        return firstOctet is >= 224 and <= 239;
    }
}
