using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Promptly.Application.Data;
using Promptly.Domain.Entities;
using Promptly.Infrastructure.Security;

namespace Promptly.Application.UnitTests;

public sealed class ApiKeyAuthenticationHandlerTests
{
    [Fact]
    public async Task Missing_header_returns_no_authentication_result()
    {
        var observation = await AuthenticateAsync(null);

        Assert.True(observation.Result.None);
        Assert.False(observation.Result.Succeeded);
        Assert.Null(observation.LastUsedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_header_is_rejected(string headerValue)
    {
        var observation = await AuthenticateAsync(headerValue);

        Assert.False(observation.Result.Succeeded);
        Assert.Equal("Invalid API Key", observation.Result.Failure?.Message);
        Assert.Null(observation.LastUsedAt);
    }

    [Fact]
    public async Task Unknown_key_is_rejected()
    {
        var observation = await AuthenticateAsync("unknown-key");

        Assert.False(observation.Result.Succeeded);
        Assert.Equal("Invalid API Key", observation.Result.Failure?.Message);
        Assert.Null(observation.LastUsedAt);
    }

    [Fact]
    public async Task Expired_key_is_rejected_without_updating_last_use()
    {
        var observation = await AuthenticateAsync(
            ValidApiKey,
            DateTime.UtcNow.Subtract(TimeSpan.FromMinutes(1)));

        Assert.False(observation.Result.Succeeded);
        Assert.Equal("API Key has expired", observation.Result.Failure?.Message);
        Assert.Null(observation.LastUsedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Valid_key_authenticates_project_claims_and_records_use(bool hasFutureExpiry)
    {
        var before = DateTime.UtcNow;
        var observation = await AuthenticateAsync(
            ValidApiKey,
            hasFutureExpiry ? DateTime.UtcNow.AddHours(1) : null);

        Assert.True(observation.Result.Succeeded);
        Assert.Equal(OwnerUserId, observation.Result.Principal?.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.Equal(ProjectId.ToString(), observation.Result.Principal?.FindFirstValue("ProjectId"));
        Assert.Equal(ApiKeyId.ToString(), observation.Result.Principal?.FindFirstValue("ApiKeyId"));
        Assert.InRange(Assert.IsType<DateTime>(observation.LastUsedAt), before, DateTime.UtcNow);
    }

    private const string ValidApiKey = "promptly-test-api-key";
    private const string OwnerUserId = "owner-123";
    private static readonly Guid ProjectId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ApiKeyId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static async Task<AuthenticationObservation> AuthenticateAsync(
        string? providedApiKey,
        DateTime? expiresAt = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(UrlEncoder.Default);
        services.AddDbContext<PromptlyDbContext>(options =>
            options.UseInMemoryDatabase($"api-key-tests-{Guid.NewGuid():N}"));
        services
            .AddAuthentication("ApiKey")
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                "ApiKey",
                _ => { });

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
        dbContext.Users.Add(new User
        {
            Id = OwnerUserId,
            UserName = "owner",
            NormalizedUserName = "OWNER"
        });
        dbContext.Projects.Add(new Project
        {
            Id = ProjectId,
            Name = "Project",
            OwnerUserId = OwnerUserId
        });
        dbContext.ProjectApiKeys.Add(new ProjectApiKey
        {
            Id = ApiKeyId,
            ProjectId = ProjectId,
            Name = "Automation",
            KeyHashSha256 = HashApiKey(ValidApiKey),
            KeyLastFourChars = "-key",
            ExpiresAt = expiresAt
        });
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var httpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider
        };
        if (providedApiKey != null)
        {
            httpContext.Request.Headers["X-API-Key"] = providedApiKey;
        }

        var result = await httpContext.AuthenticateAsync("ApiKey");
        var lastUsedAt = await dbContext.ProjectApiKeys
            .AsNoTracking()
            .Where(key => key.Id == ApiKeyId)
            .Select(key => key.LastUsedAt)
            .SingleAsync(TestContext.Current.CancellationToken);

        return new AuthenticationObservation(result, lastUsedAt);
    }

    private static string HashApiKey(string apiKey) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));

    private sealed record AuthenticationObservation(
        AuthenticateResult Result,
        DateTime? LastUsedAt);
}
