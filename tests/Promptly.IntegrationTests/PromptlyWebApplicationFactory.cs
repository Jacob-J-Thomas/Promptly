using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Promptly.IntegrationTests;

internal sealed class PromptlyWebApplicationFactory(
    string connectionString,
    string workerBaseUrl,
    string dataProtectionPath,
    string serverLogPath,
    IReadOnlyDictionary<string, string?>? additionalSettings = null,
    Action<IServiceCollection>? configureServices = null)
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
            .UseSetting("TestRunner:Enabled", "false")
            // The assembly fixture shares one long-lived primary host. Keep its
            // process-local abuse partitions well above the total test volume;
            // abuse-control scenarios use dedicated hosts with explicit limits.
            .UseSetting("AuthenticationAbuse:LoginIpPermitLimit", "100000")
            .UseSetting("AuthenticationAbuse:RegistrationIpPermitLimit", "100000")
            .UseSetting("AuthenticationAbuse:LoginAccountPermitLimit", "100000")
            .UseSetting("AuthenticationAbuse:RegistrationAccountPermitLimit", "100000")
            .UseSetting("AuthenticationAbuse:PasswordSprayDistinctAccountLimit", "10000")
            .UseSetting("AuthenticationAbuse:MaximumTrackedPartitions", "100000");
        foreach (var setting in additionalSettings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(setting.Key, setting.Value);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Information);
            logging.AddProvider(new IntegrationFileLoggerProvider(serverLogPath));
        });

        if (configureServices is not null)
        {
            builder.ConfigureServices(configureServices);
        }
    }
}
