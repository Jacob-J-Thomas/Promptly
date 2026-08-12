using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Promptly.Application.Models;

namespace Promptly.Server.Security;

public static class TenantAccessScopeFactory
{
    public const string ProjectIdClaimType = "ProjectId";
    public const string ApiKeyIdClaimType = "ApiKeyId";

    public static bool TryCreate(
        ClaimsPrincipal? principal,
        [NotNullWhen(true)] out TenantAccessScope? scope)
    {
        scope = null;

        var authenticatedIdentities = principal?.Identities
            .Where(identity => identity.IsAuthenticated)
            .ToArray();

        // Never assemble a scope from claims spread across identities. The
        // policy scheme should produce exactly one authenticated identity.
        if (authenticatedIdentities is not { Length: 1 })
        {
            return false;
        }

        var identity = authenticatedIdentities[0];
        if (!TryGetUniqueClaim(identity, ClaimTypes.NameIdentifier, out var ownerUserId))
        {
            return false;
        }

        if (string.Equals(
                identity.AuthenticationType,
                TenantAuthenticationSchemes.JwtBearer,
                StringComparison.Ordinal))
        {
            scope = new TenantAccessScope(ownerUserId, ProjectId: null);
            return true;
        }

        if (!string.Equals(
                identity.AuthenticationType,
                TenantAuthenticationSchemes.ApiKey,
                StringComparison.Ordinal)
            || !TryGetUniqueGuidClaim(identity, ProjectIdClaimType, out var projectId)
            || !TryGetUniqueGuidClaim(identity, ApiKeyIdClaimType, out _))
        {
            return false;
        }

        scope = new TenantAccessScope(ownerUserId, projectId);
        return true;
    }

    private static bool TryGetUniqueGuidClaim(
        ClaimsIdentity identity,
        string claimType,
        out Guid value)
    {
        value = Guid.Empty;
        return TryGetUniqueClaim(identity, claimType, out var claimValue)
            && Guid.TryParse(claimValue, out value)
            && value != Guid.Empty;
    }

    private static bool TryGetUniqueClaim(
        ClaimsIdentity identity,
        string claimType,
        [NotNullWhen(true)] out string? value)
    {
        value = null;
        var matchingClaims = identity.Claims
            .Where(claim => string.Equals(claim.Type, claimType, StringComparison.Ordinal))
            .ToArray();

        if (matchingClaims.Length != 1 || string.IsNullOrWhiteSpace(matchingClaims[0].Value))
        {
            return false;
        }

        value = matchingClaims[0].Value;
        return true;
    }
}
