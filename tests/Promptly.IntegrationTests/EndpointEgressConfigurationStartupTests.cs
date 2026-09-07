using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Promptly.IntegrationTests;

public sealed class EndpointEgressConfigurationStartupTests
{
    [Theory]
    [InlineData("EndpointEgress:RequireProxy", "true", "ProxyUrl is required")]
    [InlineData(
        "EndpointEgress:DnsTimeoutSeconds",
        "0",
        "DnsTimeoutSeconds must be between 1 and 120")]
    [InlineData(
        "EndpointEgress:ConnectTimeoutSeconds",
        "0",
        "ConnectTimeoutSeconds must be between 1 and 120")]
    [InlineData(
        "EndpointEgress:ProxyUrl",
        "ftp://proxy.example:3128",
        "ProxyUrl must be an absolute HTTP(S) URL")]
    public void Invalid_egress_configuration_fails_during_host_startup(
        string key,
        string value,
        string expectedFailure)
    {
        using var factory = new EgressConfigurationFactory(key, value);

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
    [InlineData(true)]
    [InlineData(false)]
    public void Proxy_transport_with_non_public_rules_fails_during_host_startup(
        bool requireProxy)
    {
        using var factory = new EgressConfigurationFactory(
            new Dictionary<string, string>
            {
                ["EndpointEgress:RequireProxy"] = requireProxy.ToString(),
                ["EndpointEgress:ProxyUrl"] = "http://proxy.example:3128",
                ["EndpointEgress:AllowedNonPublicDestinations:0:Host"] = "internal.example",
                ["EndpointEgress:AllowedNonPublicDestinations:0:Port"] = "443",
                ["EndpointEgress:AllowedNonPublicDestinations:0:Cidrs:0"] = "10.0.0.0/8"
            });

        var exception = Record.Exception(() =>
        {
            using var client = factory.CreateClient();
        });

        Assert.NotNull(exception);
        Assert.Contains(
            Flatten(exception),
            failure => failure.Message.Contains(
                "AllowedNonPublicDestinations cannot be configured when a proxy transport is selected",
                StringComparison.Ordinal));
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

    private sealed class EgressConfigurationFactory : WebApplicationFactory<Program>
    {
        private readonly IReadOnlyDictionary<string, string> _configuration;
        private readonly string _temporaryRoot = Path.Join(
            Path.GetTempPath(),
            "promptly-egress-configuration-tests",
            Guid.NewGuid().ToString("N"));

        public EgressConfigurationFactory(string configurationKey, string configurationValue)
            : this(new Dictionary<string, string>
            {
                [configurationKey] = configurationValue
            })
        {
        }

        public EgressConfigurationFactory(IReadOnlyDictionary<string, string> configuration)
        {
            _configuration = configuration;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder
                .UseEnvironment("IntegrationTest")
                .UseSetting("ConnectionStrings:Default", "Host=unused;Database=unused")
                .UseSetting("JWT:Issuer", "Promptly.EgressConfiguration.Tests")
                .UseSetting("JWT:Audience", "Promptly.EgressConfiguration.Tests")
                .UseSetting(
                    "JWT:Key",
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)))
                .UseSetting("JWT:ExpiryMinutes", "10")
                .UseSetting("DATA_PROTECTION_PATH", Path.Join(_temporaryRoot, "keys"))
                .UseSetting("PROMPTLY_EVAL_BASE_URL", "http://127.0.0.1:1")
                .UseSetting("Startup:ApplyDatabaseMigrations", "false")
                .UseSetting("TestRunner:Enabled", "false");
            foreach (var setting in _configuration)
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
