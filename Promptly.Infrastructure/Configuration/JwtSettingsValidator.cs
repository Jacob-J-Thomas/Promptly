using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Promptly.Infrastructure.Configuration;

public sealed class JwtSettingsValidator : IValidateOptions<JwtSettings>
{
    public const int MinimumSigningKeyBytes = 32;
    public const int MaximumExpiryMinutes = 1440;
    public const double MinimumEstimatedEntropyBitsPerByte = 4.0;

    private const string PublishedLegacyKey =
        "YourSuperSecretJWTKeyThatShouldBeAtLeast32CharactersLongForProduction";
    private const string Base64Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    private const int PredictableBase64RunLength = 8;
    private static readonly byte[] PublishedLegacyKeyBytes = Encoding.UTF8.GetBytes(PublishedLegacyKey);

    public ValidateOptionsResult Validate(string? name, JwtSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Issuer))
        {
            failures.Add("JWT:Issuer is required.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add("JWT:Audience is required.");
        }

        if (options.ExpiryMinutes is < 1 or > MaximumExpiryMinutes)
        {
            failures.Add($"JWT:ExpiryMinutes must be between 1 and {MaximumExpiryMinutes}.");
        }

        var keyBytes = ValidateSigningKey(options.Key, failures);
        ValidateRetiredKeyFingerprints(options.RetiredKeyFingerprints, keyBytes, failures);

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    public static byte[] GetValidatedSigningKeyBytes(JwtSettings options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var validation = new JwtSettingsValidator().Validate(Options.DefaultName, options);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                Options.DefaultName,
                typeof(JwtSettings),
                validation.Failures);
        }

        return Convert.FromBase64String(options.Key);
    }

    public static string GetSigningKeyId(ReadOnlySpan<byte> signingKey) =>
        Convert.ToHexString(SHA256.HashData(signingKey)).ToLowerInvariant()[..16];

    private static byte[]? ValidateSigningKey(string key, ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            failures.Add(
                "JWT:Key is required and must be injected as canonical base64-encoded random bytes.");
            return null;
        }

        if (string.Equals(key, PublishedLegacyKey, StringComparison.Ordinal))
        {
            failures.Add("JWT:Key is the published legacy key and must be rotated.");
            return null;
        }

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(key);
        }
        catch (FormatException)
        {
            failures.Add("JWT:Key must be canonical base64 without whitespace.");
            return null;
        }

        if (!string.Equals(Convert.ToBase64String(keyBytes), key, StringComparison.Ordinal))
        {
            failures.Add("JWT:Key must be canonical base64 without whitespace.");
            return null;
        }

        if (CryptographicOperations.FixedTimeEquals(keyBytes, PublishedLegacyKeyBytes))
        {
            failures.Add("JWT:Key decodes to the published legacy key and must be rotated.");
            return null;
        }

        if (keyBytes.Length < MinimumSigningKeyBytes)
        {
            failures.Add(
                $"JWT:Key must decode to at least {MinimumSigningKeyBytes} random bytes.");
            return null;
        }

        if (HasPredictableEncodedPattern(key)
            || HasPredictablePattern(keyBytes)
            || EstimateEntropyBitsPerByte(keyBytes) < MinimumEstimatedEntropyBitsPerByte)
        {
            failures.Add(
                "JWT:Key is low entropy or predictably structured; " +
                "generate it with a cryptographically secure generator.");
            return null;
        }

        return keyBytes;
    }

    private static void ValidateRetiredKeyFingerprints(
        string? retiredFingerprints,
        byte[]? currentKey,
        ICollection<string> failures)
    {
        var currentFingerprint = currentKey is null
            ? null
            : Convert.ToHexString(SHA256.HashData(currentKey));
        var fingerprints = (retiredFingerprints ?? string.Empty).Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < fingerprints.Length; index++)
        {
            var fingerprint = fingerprints[index];
            if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            {
                failures.Add(
                    $"JWT:RetiredKeyFingerprints entry {index} must be a 64-character " +
                    "SHA-256 hex digest.");
            }
            else if (string.Equals(fingerprint, currentFingerprint, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add("JWT:Key matches a retired key fingerprint and must not be reused.");
            }
        }
    }

    private static bool HasPredictablePattern(byte[] bytes) =>
        bytes.All(value => value is >= 0x20 and <= 0x7e)
        || IsArithmeticProgression(bytes)
        || HasRepeatedBlock(bytes);

    private static bool HasPredictableEncodedPattern(string key)
    {
        var symbolCount = key.EndsWith("==", StringComparison.Ordinal)
            ? key.Length - 2
            : key.EndsWith('=')
                ? key.Length - 1
                : key.Length;
        var previous = Base64Alphabet.IndexOf(key[0], StringComparison.Ordinal);
        int? delta = null;
        var runLength = 1;
        for (var index = 1; index < symbolCount; index++)
        {
            var current = Base64Alphabet.IndexOf(key[index], StringComparison.Ordinal);
            var currentDelta = (current - previous + Base64Alphabet.Length) % Base64Alphabet.Length;
            if (delta == currentDelta)
            {
                runLength++;
                if (runLength >= PredictableBase64RunLength)
                {
                    return true;
                }
            }
            else
            {
                delta = currentDelta;
                runLength = 2;
            }

            previous = current;
        }

        return false;
    }

    private static bool IsArithmeticProgression(byte[] bytes)
    {
        var delta = unchecked((byte)(bytes[1] - bytes[0]));
        for (var index = 2; index < bytes.Length; index++)
        {
            if (unchecked((byte)(bytes[index] - bytes[index - 1])) != delta)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasRepeatedBlock(byte[] bytes)
    {
        for (var blockLength = 1; blockLength <= bytes.Length / 2; blockLength++)
        {
            if (bytes.Length % blockLength != 0)
            {
                continue;
            }

            var repeats = true;
            for (var index = blockLength; index < bytes.Length; index++)
            {
                if (bytes[index] == bytes[index % blockLength])
                {
                    continue;
                }

                repeats = false;
                break;
            }

            if (repeats)
            {
                return true;
            }
        }

        return false;
    }

    private static double EstimateEntropyBitsPerByte(byte[] bytes)
    {
        Span<int> frequencies = stackalloc int[256];
        foreach (var value in bytes)
        {
            frequencies[value]++;
        }

        var entropy = 0.0;
        foreach (var frequency in frequencies)
        {
            if (frequency == 0)
            {
                continue;
            }

            var probability = (double)frequency / bytes.Length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }
}
