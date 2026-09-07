using Microsoft.AspNetCore.Http;

namespace Promptly.Server.Security;

public static class TenantAuthenticationSchemeSelector
{
    public const string ApiKeyHeaderName = "X-API-Key";

    public static string Select(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The presence of an API-key header is authoritative, even when its value
        // is empty or invalid. This prevents a bearer identity from being used as
        // a fallback when a request supplies both credential types.
        return context.Request.Headers.ContainsKey(ApiKeyHeaderName)
            ? TenantAuthenticationSchemes.ApiKey
            : TenantAuthenticationSchemes.JwtBearer;
    }
}
