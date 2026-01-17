using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Promptly.Application.Data;

namespace Promptly.Infrastructure.Security;

public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private const string ApiKeyHeaderName = "X-API-Key";
    private readonly PromptlyDbContext _dbContext;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        PromptlyDbContext dbContext)
        : base(options, logger, encoder)
    {
        _dbContext = dbContext;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeaderName, out var apiKeyHeaderValues))
        {
            return AuthenticateResult.NoResult();
        }

        var providedApiKey = apiKeyHeaderValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(providedApiKey))
        {
            return AuthenticateResult.Fail("Invalid API Key");
        }

        // Hash the provided API key
        var hashedKey = HashApiKey(providedApiKey);

        // Find the API key in database
        var apiKey = await _dbContext.ProjectApiKeys
            .Include(k => k.Project)
            .FirstOrDefaultAsync(k => k.KeyHashSha256 == hashedKey);

        if (apiKey == null)
        {
            return AuthenticateResult.Fail("Invalid API Key");
        }

        // Check if key has expired
        if (apiKey.ExpiresAt.HasValue && apiKey.ExpiresAt.Value < DateTime.UtcNow)
        {
            return AuthenticateResult.Fail("API Key has expired");
        }

        // Update last used timestamp
        apiKey.LastUsedAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        // Create claims
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, apiKey.Project!.OwnerUserId),
            new Claim("ProjectId", apiKey.ProjectId.ToString()),
            new Claim("ApiKeyId", apiKey.Id.ToString())
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }

    private static string HashApiKey(string apiKey)
    {
        using var sha256 = SHA256.Create();
        var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToBase64String(hashBytes);
    }
}
