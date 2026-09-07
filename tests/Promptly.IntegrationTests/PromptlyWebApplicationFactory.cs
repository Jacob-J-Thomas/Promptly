using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Promptly.IntegrationTests;

internal sealed class PromptlyWebApplicationFactory(
    string connectionString,
    string workerBaseUrl,
    string dataProtectionPath,
    string serverLogPath,
    IReadOnlyDictionary<string, string?>? additionalSettings = null)
    : WebApplicationFactory<Program>
{
    private static readonly string JwtKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Use host settings because Program consumes these values while it is
        // registering services, before late app-configuration callbacks run.
        builder
            .UseEnvironment("IntegrationTest")
            .UseSetting("ConnectionStrings:Default", connectionString)
            .UseSetting("JWT:Issuer", "Promptly.IntegrationTests")
            .UseSetting("JWT:Audience", "Promptly.IntegrationTests")
            .UseSetting("JWT:Key", JwtKey)
            .UseSetting("JWT:ExpiryMinutes", "10")
            .UseSetting("DATA_PROTECTION_PATH", dataProtectionPath)
            .UseSetting("PROMPTLY_EVAL_BASE_URL", workerBaseUrl)
            .UseSetting("Startup:ApplyDatabaseMigrations", "true")
            .UseSetting("TestRunner:Enabled", "false");
        foreach (var setting in additionalSettings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(setting.Key, setting.Value);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddProvider(new IntegrationFileLoggerProvider(serverLogPath));
        });
    }
}
