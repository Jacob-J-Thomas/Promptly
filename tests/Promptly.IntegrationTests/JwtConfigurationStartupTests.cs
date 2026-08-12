using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Promptly.IntegrationTests;

public sealed class JwtConfigurationStartupTests
{
    private const string PublishedLegacyKey =
        "YourSuperSecretJWTKeyThatShouldBeAtLeast32CharactersLongForProduction";

    [Theory]
    [InlineData("Development", "missing", "JWT:Key is required")]
    [InlineData("Test", "legacy", "JWT:Key is the published legacy key")]
    [InlineData("Production", "low-entropy", "JWT:Key is low entropy")]
    [InlineData("Production", "retired", "JWT:Key matches a retired key fingerprint")]
    public void Insecure_key_fails_before_host_startup(
        string environment,
        string keyCase,
        string expectedFailure)
    {
        var secureKeyBytes = RandomNumberGenerator.GetBytes(48);
        var key = keyCase switch
        {
            "missing" => null,
            "legacy" => PublishedLegacyKey,
            "low-entropy" => Convert.ToBase64String(new byte[48]),
            "retired" => Convert.ToBase64String(secureKeyBytes),
            _ => throw new ArgumentOutOfRangeException(nameof(keyCase))
        };
        var retiredFingerprints = keyCase == "retired"
            ? Convert.ToHexString(SHA256.HashData(secureKeyBytes))
            : null;
        using var factory = new JwtConfigurationFactory(environment, key, retiredFingerprints);

        var exception = Record.Exception(() =>
        {
            using var client = factory.CreateClient();
        });

        Assert.NotNull(exception);
        Assert.Contains(
            Flatten(exception),
            failure => failure.Message.Contains(expectedFailure, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Test")]
    [InlineData("Production")]
    public async Task Secure_injected_key_starts_a_healthy_host(string environment)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
        using var factory = new JwtConfigurationFactory(environment, key, retiredFingerprints: null);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/health",
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        yield return exception;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions.SelectMany(Flatten))
            {
                yield return inner;
            }
        }
        else if (exception.InnerException is not null)
        {
            foreach (var inner in Flatten(exception.InnerException))
            {
                yield return inner;
            }
        }
    }

    private sealed class JwtConfigurationFactory : WebApplicationFactory<Program>
    {
        private readonly string _environment;
        private readonly string? _key;
        private readonly string? _retiredFingerprints;
        private readonly string _temporaryRoot = Path.Join(
            Path.GetTempPath(),
            "promptly-jwt-configuration-tests",
            Guid.NewGuid().ToString("N"));

        public JwtConfigurationFactory(
            string environment,
            string? key,
            string? retiredFingerprints)
        {
            _environment = environment;
            _key = key;
            _retiredFingerprints = retiredFingerprints;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder
                .UseEnvironment(_environment)
                .UseSetting("ConnectionStrings:Default", "Host=unused;Database=unused")
                .UseSetting("JWT:Issuer", $"Promptly.{_environment}.Tests")
                .UseSetting("JWT:Audience", $"Promptly.{_environment}.Tests")
                .UseSetting("JWT:Key", _key ?? string.Empty)
                .UseSetting("JWT:RetiredKeyFingerprints", _retiredFingerprints ?? string.Empty)
                .UseSetting("JWT:ExpiryMinutes", "10")
                .UseSetting("DATA_PROTECTION_PATH", Path.Join(_temporaryRoot, "keys"))
                .UseSetting("PROMPTLY_EVAL_BASE_URL", "http://127.0.0.1:1")
                .UseSetting("Startup:ApplyDatabaseMigrations", "false")
                .UseSetting("TestRunner:Enabled", "false");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(_temporaryRoot))
            {
                Directory.Delete(_temporaryRoot, recursive: true);
            }
        }
    }
}
