using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Promptly.Server.Security;

namespace Promptly.IntegrationTests;

public sealed class AuthenticationAbuseConfigurationStartupTests
{
    private const string SensitiveMalformedProxy = "raw-proxy-secret.invalid/24";

    [Theory]
    [InlineData("replicas", "ApiReplicaCount must equal 1")]
    [InlineData("malformed-proxy", "TrustedProxyNetworks:0 must be a canonical IPv4 or IPv6 CIDR")]
    [InlineData("unsafe-proxy", "TrustedProxyNetworks:0 must not use an unspecified network address")]
    [InlineData("broad-public-proxy", "publicly reachable and must use an exact /32 host route")]
    [InlineData("too-many-proxies", "TrustedProxyNetworks cannot contain more than 256 entries")]
    [InlineData(
        "aggregate-concurrency",
        "MaximumConcurrentAuthenticationRequests must be between 1 and 256")]
    public void Unsafe_configuration_fails_before_host_startup(
        string configurationCase,
        string expectedFailure)
    {
        var settings = configurationCase switch
        {
            "replicas" => new Dictionary<string, string?>
            {
                ["AuthenticationAbuse:ApiReplicaCount"] = "2"
            },
            "malformed-proxy" => new Dictionary<string, string?>
            {
                ["AuthenticationAbuse:TrustedProxyNetworks:0"] = SensitiveMalformedProxy
            },
            "unsafe-proxy" => new Dictionary<string, string?>
            {
                ["AuthenticationAbuse:TrustedProxyNetworks:0"] = "0.0.0.0/0"
            },
            "broad-public-proxy" => new Dictionary<string, string?>
            {
                ["AuthenticationAbuse:TrustedProxyNetworks:0"] = "8.0.0.0/8"
            },
            "too-many-proxies" => Enumerable.Range(
                    0,
                    AuthenticationAbuseOptions.MaximumTrustedProxyNetworkCount + 1)
                .ToDictionary<int, string, string?>(
                    index => $"AuthenticationAbuse:TrustedProxyNetworks:{index}",
                    index => $"10.{index / 256}.{index % 256}.1/32"),
            "aggregate-concurrency" => new Dictionary<string, string?>
            {
                ["AuthenticationAbuse:MaximumConcurrentAuthenticationRequests"] = "257"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(configurationCase))
        };
        using var factory = new AuthenticationAbuseConfigurationFactory(settings);

        var exception = Record.Exception(() =>
        {
            using var client = factory.CreateClient();
        });

        Assert.NotNull(exception);
        var failures = Flatten(exception).Select(failure => failure.Message).ToArray();
        Assert.Contains(
            failures,
            failure => failure.Contains(expectedFailure, StringComparison.Ordinal));
        var configuredProxy = configurationCase switch
        {
            "malformed-proxy" => SensitiveMalformedProxy,
            "unsafe-proxy" => "0.0.0.0/0",
            "broad-public-proxy" => "8.0.0.0/8",
            _ => null
        };
        if (configuredProxy is not null)
        {
            Assert.DoesNotContain(
                failures,
                failure => failure.Contains(configuredProxy, StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Defaults_and_safe_proxy_override_start_a_healthy_host(
        bool configureTrustedProxy)
    {
        var settings = new Dictionary<string, string?>();
        if (configureTrustedProxy)
        {
            settings["AuthenticationAbuse:ApiReplicaCount"] = "1";
            settings["AuthenticationAbuse:TrustedProxyNetworks:0"] = "192.0.2.0/24";
        }

        using var factory = new AuthenticationAbuseConfigurationFactory(settings);
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
            foreach (var innerException in aggregate.InnerExceptions.SelectMany(Flatten))
            {
                yield return innerException;
            }
        }
        else if (exception.InnerException is not null)
        {
            foreach (var innerException in Flatten(exception.InnerException))
            {
                yield return innerException;
            }
        }
    }

    private sealed class AuthenticationAbuseConfigurationFactory(
        IReadOnlyDictionary<string, string?> settings) : WebApplicationFactory<Program>
    {
        private readonly string _temporaryRoot = Path.Join(
            Path.GetTempPath(),
            "promptly-authentication-abuse-configuration-tests",
            Guid.NewGuid().ToString("N"));

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder
                .UseEnvironment("IntegrationTest")
                .UseSetting("ConnectionStrings:Default", "Host=unused;Database=unused")
                .UseSetting("JWT:Issuer", "Promptly.AuthenticationAbuseConfiguration.Tests")
                .UseSetting("JWT:Audience", "Promptly.AuthenticationAbuseConfiguration.Tests")
                .UseSetting(
                    "JWT:Key",
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)))
                .UseSetting("JWT:ExpiryMinutes", "10")
                .UseSetting("DATA_PROTECTION_PATH", Path.Join(_temporaryRoot, "keys"))
                .UseSetting("PROMPTLY_EVAL_BASE_URL", "http://127.0.0.1:1")
                .UseSetting("Startup:ApplyDatabaseMigrations", "false")
                .UseSetting("TestRunner:Enabled", "false");
            foreach (var setting in settings)
            {
                builder.UseSetting(setting.Key, setting.Value);
            }
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
