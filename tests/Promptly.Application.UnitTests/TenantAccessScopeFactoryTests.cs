using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public class TenantAccessScopeFactoryTests
{
    private const string OwnerUserId = "owner-123";
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ApiKeyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void TryCreate_JwtIdentity_ReturnsTenantWideScope()
    {
        var principal = CreatePrincipal(
            TenantAuthenticationSchemes.JwtBearer,
            new Claim(ClaimTypes.NameIdentifier, OwnerUserId),
            new Claim(TenantAccessScopeFactory.ProjectIdClaimType, ProjectId.ToString()),
            new Claim(TenantAccessScopeFactory.ApiKeyIdClaimType, ApiKeyId.ToString()));

        var succeeded = TenantAccessScopeFactory.TryCreate(principal, out var scope);

        Assert.True(succeeded);
        Assert.NotNull(scope);
        Assert.Equal(OwnerUserId, scope.OwnerUserId);
        Assert.Null(scope.ProjectId);
    }

    [Fact]
    public void TryCreate_ApiKeyIdentity_ReturnsExactProjectScope()
    {
        var principal = CreatePrincipal(
            TenantAuthenticationSchemes.ApiKey,
            new Claim(ClaimTypes.NameIdentifier, OwnerUserId),
            new Claim(TenantAccessScopeFactory.ProjectIdClaimType, ProjectId.ToString()),
            new Claim(TenantAccessScopeFactory.ApiKeyIdClaimType, ApiKeyId.ToString()));

        var succeeded = TenantAccessScopeFactory.TryCreate(principal, out var scope);

        Assert.True(succeeded);
        Assert.NotNull(scope);
        Assert.Equal(OwnerUserId, scope.OwnerUserId);
        Assert.Equal(ProjectId, scope.ProjectId);
    }

    public static TheoryData<ClaimsPrincipal?> MalformedPrincipals => new()
    {
        null!,
        new ClaimsPrincipal(),
        CreatePrincipal(TenantAuthenticationSchemes.JwtBearer),
        CreatePrincipal(
            TenantAuthenticationSchemes.JwtBearer,
            new Claim(ClaimTypes.NameIdentifier, " ")),
        CreatePrincipal(
            "Unknown",
            new Claim(ClaimTypes.NameIdentifier, OwnerUserId)),
        CreatePrincipal(
            TenantAuthenticationSchemes.ApiKey,
            new Claim(ClaimTypes.NameIdentifier, OwnerUserId),
            new Claim(TenantAccessScopeFactory.ProjectIdClaimType, "not-a-guid"),
            new Claim(TenantAccessScopeFactory.ApiKeyIdClaimType, ApiKeyId.ToString())),
        CreatePrincipal(
            TenantAuthenticationSchemes.ApiKey,
            new Claim(ClaimTypes.NameIdentifier, OwnerUserId),
            new Claim(TenantAccessScopeFactory.ProjectIdClaimType, ProjectId.ToString())),
        CreatePrincipal(
            TenantAuthenticationSchemes.ApiKey,
            new Claim(ClaimTypes.NameIdentifier, OwnerUserId),
            new Claim(TenantAccessScopeFactory.ProjectIdClaimType, ProjectId.ToString()),
            new Claim(TenantAccessScopeFactory.ApiKeyIdClaimType, "not-a-guid")),
        CreatePrincipal(
            TenantAuthenticationSchemes.ApiKey,
            new Claim(ClaimTypes.NameIdentifier, OwnerUserId),
            new Claim(ClaimTypes.NameIdentifier, "another-owner"),
            new Claim(TenantAccessScopeFactory.ProjectIdClaimType, ProjectId.ToString()),
            new Claim(TenantAccessScopeFactory.ApiKeyIdClaimType, ApiKeyId.ToString())),
        new ClaimsPrincipal(
            new[]
            {
                CreateIdentity(
                    TenantAuthenticationSchemes.JwtBearer,
                    new Claim(ClaimTypes.NameIdentifier, OwnerUserId)),
                CreateIdentity(
                    TenantAuthenticationSchemes.ApiKey,
                    new Claim(ClaimTypes.NameIdentifier, OwnerUserId),
                    new Claim(TenantAccessScopeFactory.ProjectIdClaimType, ProjectId.ToString()),
                    new Claim(TenantAccessScopeFactory.ApiKeyIdClaimType, ApiKeyId.ToString()))
            })
    };

    [Theory]
    [MemberData(nameof(MalformedPrincipals))]
    public void TryCreate_MalformedOrMixedPrincipal_FailsClosed(ClaimsPrincipal? principal)
    {
        var succeeded = TenantAccessScopeFactory.TryCreate(principal, out var scope);

        Assert.False(succeeded);
        Assert.Null(scope);
    }

    [Fact]
    public void Accessor_UsesCurrentHttpContextPrincipal()
    {
        var principal = CreatePrincipal(
            TenantAuthenticationSchemes.JwtBearer,
            new Claim(ClaimTypes.NameIdentifier, OwnerUserId));
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = principal }
        };
        var accessor = new HttpContextTenantAccessScopeAccessor(httpContextAccessor);

        var succeeded = accessor.TryGetScope(out var scope);

        Assert.True(succeeded);
        Assert.NotNull(scope);
        Assert.Equal(OwnerUserId, scope.OwnerUserId);
        Assert.Null(scope.ProjectId);
    }

    [Fact]
    public void Accessor_WithoutHttpContext_FailsClosed()
    {
        var accessor = new HttpContextTenantAccessScopeAccessor(new HttpContextAccessor());

        var succeeded = accessor.TryGetScope(out var scope);

        Assert.False(succeeded);
        Assert.Null(scope);
    }

    private static ClaimsPrincipal CreatePrincipal(
        string authenticationType,
        params Claim[] claims) =>
        new(CreateIdentity(authenticationType, claims));

    private static ClaimsIdentity CreateIdentity(
        string authenticationType,
        params Claim[] claims) =>
        new(claims, authenticationType);
}
