using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Promptly.Infrastructure.Configuration;

namespace Promptly.Application.UnitTests;

public sealed class JwtSettingsValidatorTests
{
    private const string PublishedLegacyKey =
        "YourSuperSecretJWTKeyThatShouldBeAtLeast32CharactersLongForProduction";

    private readonly JwtSettingsValidator _validator = new();

    [Fact]
    public void Secure_configuration_succeeds_and_returns_the_decoded_key()
    {
        var keyBytes = DiverseKeyBytes();
        var settings = SecureSettings(keyBytes);

        var result = _validator.Validate(Options.DefaultName, settings);
        var decoded = JwtSettingsValidator.GetValidatedSigningKeyBytes(settings);

        Assert.True(result.Succeeded);
        Assert.Equal(keyBytes, decoded);
        Assert.Equal(16, JwtSettingsValidator.GetSigningKeyId(decoded).Length);
    }

    [Theory]
    [InlineData(32)]
    [InlineData(34)]
    public void Secure_padded_configuration_succeeds(int byteCount)
    {
        var settings = SecureSettings(DiverseKeyBytes()[..byteCount]);

        var result = _validator.Validate(Options.DefaultName, settings);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Missing_identity_key_and_invalid_expiry_fail_together()
    {
        var settings = new JwtSettings
        {
            Issuer = " ",
            Audience = string.Empty,
            Key = string.Empty,
            ExpiryMinutes = 0
        };

        var result = _validator.Validate(Options.DefaultName, settings);

        Assert.True(result.Failed);
        AssertFailure(result, "JWT:Issuer is required.");
        AssertFailure(result, "JWT:Audience is required.");
        AssertFailure(
            result,
            "JWT:Key is required and must be injected as canonical base64-encoded random bytes.");
        AssertFailure(result, "JWT:ExpiryMinutes must be between 1 and 1440.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void Expiry_outside_the_bounded_range_fails(int expiryMinutes)
    {
        var settings = SecureSettings();
        settings.ExpiryMinutes = expiryMinutes;

        var result = _validator.Validate(Options.DefaultName, settings);

        AssertFailure(result, "JWT:ExpiryMinutes must be between 1 and 1440.");
    }

    [Fact]
    public void Published_legacy_text_key_is_rejected()
    {
        var settings = SecureSettings();
        settings.Key = PublishedLegacyKey;

        var result = _validator.Validate(Options.DefaultName, settings);

        AssertFailure(result, "JWT:Key is the published legacy key and must be rotated.");
    }

    [Fact]
    public void Base64_encoded_legacy_key_material_is_rejected()
    {
        var settings = SecureSettings();
        settings.Key = Convert.ToBase64String(Encoding.UTF8.GetBytes(PublishedLegacyKey));

        var result = _validator.Validate(Options.DefaultName, settings);

        AssertFailure(
            result,
            "JWT:Key decodes to the published legacy key and must be rotated.");
    }

    [Theory]
    [InlineData("not-base64")]
    [InlineData("AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=\n")]
    public void Malformed_or_noncanonical_base64_is_rejected(string key)
    {
        var settings = SecureSettings();
        settings.Key = key;

        var result = _validator.Validate(Options.DefaultName, settings);

        AssertFailure(result, "JWT:Key must be canonical base64 without whitespace.");
    }

    [Fact]
    public void Key_shorter_than_256_bits_is_rejected()
    {
        var settings = SecureSettings();
        settings.Key = Convert.ToBase64String(DiverseKeyBytes()[..31]);

        var result = _validator.Validate(Options.DefaultName, settings);

        AssertFailure(result, "JWT:Key must decode to at least 32 random bytes.");
    }

    [Fact]
    public void Low_entropy_key_is_rejected_even_when_long_enough()
    {
        var settings = SecureSettings();
        settings.Key = Convert.ToBase64String(
            [.. Enumerable.Range(0, 48).Select(index => (byte)(index % 5))]);

        var result = _validator.Validate(Options.DefaultName, settings);

        AssertFailure(
            result,
            "JWT:Key is low entropy or predictably structured; " +
            "generate it with a cryptographically secure generator.");
    }

    [Theory]
    [MemberData(nameof(PredictableKeys))]
    public void Predictably_structured_key_is_rejected_despite_length_and_byte_diversity(
        byte[] keyBytes)
    {
        var settings = SecureSettings();
        settings.Key = Convert.ToBase64String(keyBytes);

        var result = _validator.Validate(Options.DefaultName, settings);

        AssertFailure(
            result,
            "JWT:Key is low entropy or predictably structured; " +
            "generate it with a cryptographically secure generator.");
    }

    [Theory]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/")]
    [InlineData("/+9876543210zyxwvutsrqponmlkjihgfedcbaZYXWVUTSRQPONMLKJIHGFEDCBA")]
    public void Predictable_base64_symbol_sequence_is_rejected(string key)
    {
        var settings = SecureSettings();
        settings.Key = key;

        var result = _validator.Validate(Options.DefaultName, settings);

        AssertFailure(
            result,
            "JWT:Key is low entropy or predictably structured; " +
            "generate it with a cryptographically secure generator.");
    }

    public static TheoryData<byte[]> PredictableKeys => new()
    {
        { [.. Enumerable.Range(0, 32).Select(index => (byte)index)] },
        { Encoding.UTF8.GetBytes("this human-readable signing key is not random material") },
        { [.. Enumerable.Repeat(new byte[] { 0x00, 0xff, 0x11, 0xee }, 12)
            .SelectMany(block => block)] }
    };

    [Fact]
    public void Retired_fingerprint_blocks_key_reuse_case_insensitively()
    {
        var keyBytes = DiverseKeyBytes();
        var settings = SecureSettings(keyBytes);
        settings.RetiredKeyFingerprints = string.Join(
            ", ",
            new string('a', 64),
            Convert.ToHexString(SHA256.HashData(keyBytes)).ToLowerInvariant());

        var result = _validator.Validate(Options.DefaultName, settings);

        AssertFailure(result, "JWT:Key matches a retired key fingerprint and must not be reused.");
    }

    [Fact]
    public void Invalid_retired_fingerprint_is_rejected_even_when_the_current_key_is_invalid()
    {
        var settings = SecureSettings();
        settings.Key = string.Empty;
        settings.RetiredKeyFingerprints = "not-a-sha256-fingerprint";

        var result = _validator.Validate(Options.DefaultName, settings);

        AssertFailure(
            result,
            "JWT:RetiredKeyFingerprints entry 0 must be a 64-character SHA-256 hex digest.");
    }

    [Fact]
    public void A_different_valid_retired_fingerprint_does_not_block_startup()
    {
        var settings = SecureSettings();
        settings.RetiredKeyFingerprints = new string('a', 64);

        var result = _validator.Validate(Options.DefaultName, settings);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ")]
    public void Blank_retired_fingerprint_list_is_treated_as_empty(string? fingerprints)
    {
        var settings = SecureSettings();
        settings.RetiredKeyFingerprints = fingerprints!;

        var result = _validator.Validate(Options.DefaultName, settings);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Invalid_configuration_throws_without_disclosing_key_material()
    {
        var settings = SecureSettings();
        settings.Key = "secret-but-invalid";

        var exception = Assert.Throws<OptionsValidationException>(() =>
            JwtSettingsValidator.GetValidatedSigningKeyBytes(settings));

        Assert.Contains("JWT:Key must be canonical base64", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(settings.Key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_options_are_rejected_by_both_entry_points()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _validator.Validate(Options.DefaultName, null!));
        Assert.Throws<ArgumentNullException>(() =>
            JwtSettingsValidator.GetValidatedSigningKeyBytes(null!));
    }

    private static JwtSettings SecureSettings(byte[]? keyBytes = null) => new()
    {
        Issuer = "promptly-tests",
        Audience = "promptly-clients",
        Key = Convert.ToBase64String(keyBytes ?? DiverseKeyBytes()),
        ExpiryMinutes = 60
    };

    private static byte[] DiverseKeyBytes() =>
        SHA512.HashData(Encoding.UTF8.GetBytes("Promptly JWT validator deterministic test vector"))[..48];

    private static void AssertFailure(ValidateOptionsResult result, string expected)
    {
        Assert.True(result.Failed);
        Assert.NotNull(result.Failures);
        Assert.Contains(expected, result.Failures);
    }
}
