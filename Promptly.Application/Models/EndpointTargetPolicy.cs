using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;

namespace Promptly.Application.Models;

/// <summary>
/// Validates and resolves persisted endpoint targets without allowing them to
/// escape the configured environment origin.
/// </summary>
public static class EndpointTargetPolicy
{
    public static bool TryValidateRelativeTarget(
        string? target,
        out string validationError)
    {
        if (string.IsNullOrEmpty(target))
        {
            validationError = "an endpoint target is required";
            return false;
        }

        if (target.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
        {
            validationError = "the endpoint target cannot contain whitespace or control characters";
            return false;
        }

        if (HasUnpairedSurrogate(target))
        {
            validationError = "the endpoint target cannot contain malformed Unicode";
            return false;
        }

        if (target.Contains('\\'))
        {
            validationError = "the endpoint target cannot contain backslashes";
            return false;
        }

        if (target.StartsWith("//", StringComparison.Ordinal))
        {
            validationError = "scheme-relative endpoint targets are not allowed";
            return false;
        }

        if (HasSchemeOrMalformedColonPrefix(target))
        {
            validationError = "absolute endpoint targets are not allowed";
            return false;
        }

        if (!HasValidPercentEncoding(target))
        {
            validationError = "the endpoint target contains malformed percent-encoding";
            return false;
        }

        if (!Uri.TryCreate(target, UriKind.Relative, out _))
        {
            validationError = "the endpoint target is not a valid relative URI";
            return false;
        }

        validationError = string.Empty;
        return true;
    }

    public static void EnsureRelativeTarget(string? target)
    {
        if (!TryValidateRelativeTarget(target, out var validationError))
        {
            throw new EndpointTargetValidationException(validationError);
        }
    }

    public static bool TryResolve(
        string? environmentBaseUrl,
        string? target,
        [NotNullWhen(true)] out Uri? resolvedTarget,
        out string validationError)
    {
        resolvedTarget = null;

        if (!TryValidateBaseUri(environmentBaseUrl, out var baseUri, out validationError)
            || !TryValidateRelativeTarget(target, out validationError))
        {
            return false;
        }

        var candidate = new Uri(baseUri, target);
        if (!IsAllowedResolvedTarget(baseUri, candidate))
        {
            validationError = "the endpoint target must resolve to the configured environment origin";
            return false;
        }

        resolvedTarget = candidate;
        validationError = string.Empty;
        return true;
    }

    public static bool TryValidateBaseUri(
        string? environmentBaseUrl,
        [NotNullWhen(true)] out Uri? baseUri,
        out string validationError)
    {
        baseUri = null;

        if (string.IsNullOrEmpty(environmentBaseUrl)
            || environmentBaseUrl.Any(character =>
                char.IsControl(character) || char.IsWhiteSpace(character))
            || HasUnpairedSurrogate(environmentBaseUrl)
            || environmentBaseUrl.Contains('\\')
            || !Uri.TryCreate(environmentBaseUrl, UriKind.Absolute, out var candidate)
            || !IsHttpScheme(candidate.Scheme)
            || string.IsNullOrEmpty(candidate.IdnHost)
            || !string.IsNullOrEmpty(candidate.UserInfo))
        {
            validationError = "the environment base URL must be an absolute HTTP(S) URL without user information";
            return false;
        }

        baseUri = candidate;
        validationError = string.Empty;
        return true;
    }

    public static Uri EnsureBaseUri(string? environmentBaseUrl)
    {
        if (!TryValidateBaseUri(environmentBaseUrl, out var baseUri, out var validationError))
        {
            throw new EnvironmentBaseUrlValidationException(validationError);
        }

        return baseUri;
    }

    public static bool IsAllowedResolvedTarget(Uri environmentBaseUri, Uri candidate) =>
        environmentBaseUri.IsAbsoluteUri
        && candidate.IsAbsoluteUri
        && IsHttpScheme(environmentBaseUri.Scheme)
        && IsHttpScheme(candidate.Scheme)
        && string.IsNullOrEmpty(environmentBaseUri.UserInfo)
        && string.IsNullOrEmpty(candidate.UserInfo)
        && string.Equals(
            environmentBaseUri.Scheme,
            candidate.Scheme,
            StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            environmentBaseUri.IdnHost,
            candidate.IdnHost,
            StringComparison.OrdinalIgnoreCase)
        && environmentBaseUri.Port == candidate.Port;

    private static bool IsHttpScheme(string scheme) =>
        string.Equals(scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);

    private static bool HasSchemeOrMalformedColonPrefix(string target)
    {
        var colonIndex = target.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex <= 0)
        {
            return false;
        }

        var delimiterIndex = target.IndexOfAny(['/', '?', '#']);
        if (delimiterIndex >= 0 && colonIndex > delimiterIndex)
        {
            return false;
        }

        return true;
    }

    private static bool HasValidPercentEncoding(string target)
    {
        for (var index = 0; index < target.Length; index++)
        {
            if (target[index] != '%')
            {
                continue;
            }

            if (index + 2 >= target.Length
                || !char.IsAsciiHexDigit(target[index + 1])
                || !char.IsAsciiHexDigit(target[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static bool HasUnpairedSurrogate(string target)
    {
        for (var index = 0; index < target.Length; index++)
        {
            if (char.IsHighSurrogate(target[index]))
            {
                if (index + 1 >= target.Length || !char.IsLowSurrogate(target[index + 1]))
                {
                    return true;
                }

                index++;
            }
            else if (char.IsLowSurrogate(target[index]))
            {
                return true;
            }
        }

        return false;
    }

}

public sealed class EndpointTargetValidationException : ArgumentException
{
    public EndpointTargetValidationException(string detail)
        : base($"Invalid endpoint target: {detail}", "path")
    {
    }
}

public sealed class EnvironmentBaseUrlValidationException : ArgumentException
{
    public EnvironmentBaseUrlValidationException(string detail)
        : base($"Invalid environment base URL: {detail}", "baseUrl")
    {
    }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class EndpointTargetAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return ValidationResult.Success;
        }

        if (value is not string target)
        {
            return new ValidationResult("Invalid endpoint target: the endpoint target must be text");
        }

        if (EndpointTargetPolicy.TryValidateRelativeTarget(target, out var validationError))
        {
            return ValidationResult.Success;
        }

        return new ValidationResult($"Invalid endpoint target: {validationError}");
    }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class EnvironmentBaseUrlAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value == null)
        {
            return ValidationResult.Success;
        }

        if (value is not string baseUrl)
        {
            return new ValidationResult(
                "Invalid environment base URL: the environment base URL must be text");
        }

        if (EndpointTargetPolicy.TryValidateBaseUri(baseUrl, out _, out var validationError))
        {
            return ValidationResult.Success;
        }

        return new ValidationResult($"Invalid environment base URL: {validationError}");
    }
}
