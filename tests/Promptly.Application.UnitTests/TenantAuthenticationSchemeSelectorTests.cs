using Microsoft.AspNetCore.Http;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public class TenantAuthenticationSchemeSelectorTests
{
    [Fact]
    public void Select_WithoutApiKeyHeader_UsesJwtBearer()
    {
        var context = new DefaultHttpContext();

        var scheme = TenantAuthenticationSchemeSelector.Select(context);

        Assert.Equal(TenantAuthenticationSchemes.JwtBearer, scheme);
    }

    [Theory]
    [InlineData("")]
    [InlineData("api-key")]
    public void Select_WithApiKeyHeader_UsesApiKeyExclusively(string apiKey)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[TenantAuthenticationSchemeSelector.ApiKeyHeaderName] = apiKey;
        context.Request.Headers.Authorization = "Bearer otherwise-valid-token";

        var scheme = TenantAuthenticationSchemeSelector.Select(context);

        Assert.Equal(TenantAuthenticationSchemes.ApiKey, scheme);
    }

    [Fact]
    public void Select_WithNullContext_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TenantAuthenticationSchemeSelector.Select(null!));
    }
}
