using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Promptly.IntegrationTests;

public sealed class StartupAndAuthTests(IntegrationFixture fixture)
{
    [Fact]
    public async Task EmptyDatabase_StartsWithMigrationAndHealthyPipeline()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var response = await fixture.PrimaryHost.Client.GetAsync("/health", cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        Assert.Equal("healthy", document.RootElement.GetProperty("status").GetString());
        Assert.True(document.RootElement.GetProperty("timestamp").TryGetDateTime(out _));
        Assert.Contains("20260115023007_InitialCreate", fixture.AppliedMigrations);
        Assert.Equal(0, fixture.InitialUserCount);
        Assert.Equal(0, fixture.InitialProjectCount);
        Assert.False(fixture.BackgroundRunnerRegistered);
    }

    [Fact]
    public async Task Authentication_RegistersAndLogsIn_ButRejectsDuplicateAndWrongPassword()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var email = $"auth-{Guid.NewGuid():N}@example.test";
        const string password = "Integration1";
        using var registered = await PromptlyApiClient.RegisterAsync(
            fixture.PrimaryHost.Factory,
            email,
            password);

        using var successfulLogin = await fixture.PrimaryHost.Client.PostAsJsonAsync(
            "/api/auth/login",
            new
            {
                email,
                password
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.OK, successfulLogin.StatusCode);
        using (var document = JsonDocument.Parse(
                   await successfulLogin.Content.ReadAsStringAsync(cancellationToken)))
        {
            Assert.False(string.IsNullOrWhiteSpace(document.RootElement.GetProperty("token").GetString()));
        }

        using var wrongPassword = await fixture.PrimaryHost.Client.PostAsJsonAsync(
            "/api/auth/login",
            new
            {
                email,
                password = "WrongPassword1"
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);

        using var duplicate = await fixture.PrimaryHost.Client.PostAsJsonAsync(
            "/api/auth/register",
            new
            {
                email,
                password,
                name = "Duplicate"
            },
            cancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Fact]
    public async Task ProtectedRoute_DeniesAnonymousRequest()
    {
        using var response = await fixture.PrimaryHost.Client.GetAsync(
            "/api/projects",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
