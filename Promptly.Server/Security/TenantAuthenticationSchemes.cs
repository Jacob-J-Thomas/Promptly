using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Promptly.Server.Security;

public static class TenantAuthenticationSchemes
{
    public const string Policy = "PromptlyAuthentication";
    public const string ApiKey = "ApiKey";
    public const string JwtBearer = JwtBearerDefaults.AuthenticationScheme;
}
