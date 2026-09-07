using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using Promptly.Server.Controllers;
using Promptly.Server.Models;
using Promptly.Server.Security;
using Environment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.UnitTests;

public sealed class EndpointTargetPolicyTests
{
    [Theory]
    [InlineData("/v1/chat")]
    [InlineData("v1/chat")]
    [InlineData("../chat")]
    [InlineData("?model=safe")]
    [InlineData("#response")]
    [InlineData("v1/chat?model=safe#response")]
    [InlineData("caf%C3%A9")]
    [InlineData("/v1/😀")]
    [InlineData("/v1/𠀀")]
    [InlineData("/v1:chat")]
    public void Relative_targets_are_accepted(string target)
    {
        Assert.True(EndpointTargetPolicy.TryValidateRelativeTarget(target, out var error));
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("/v1/chat prompt")]
    [InlineData("/v1\tchat")]
    [InlineData("/v1\r\nInjected:true")]
    [InlineData("\\\\attacker.test/path")]
    [InlineData("/v1\\chat")]
    [InlineData("//attacker.test/path")]
    [InlineData("///attacker.test/path")]
    [InlineData("https://attacker.test/path")]
    [InlineData("HTTPS://attacker.test/path")]
    [InlineData("http:attacker.test/path")]
    [InlineData("javascript:alert(1)")]
    [InlineData("custom+scheme:value")]
    [InlineData("1malformed:value")]
    [InlineData("/bad%")]
    [InlineData("/bad%2")]
    [InlineData("/bad%XZ")]
    public void Unsafe_or_malformed_targets_are_rejected(string? target)
    {
        Assert.False(EndpointTargetPolicy.TryValidateRelativeTarget(target, out var error));
        Assert.NotEmpty(error);
        Assert.Throws<EndpointTargetValidationException>(
            () => EndpointTargetPolicy.EnsureRelativeTarget(target));
    }

    [Fact]
    public void Unpaired_utf16_surrogates_are_rejected()
    {
        var highSurrogate = new string('\uD800', 1);
        var lowSurrogate = new string('\uDC00', 1);
        var targets = new[]
        {
            $"/v1/{highSurrogate}",
            $"/v1/{lowSurrogate}",
            $"/v1/{highSurrogate}text",
            $"/v1/text{lowSurrogate}"
        };

        Assert.All(targets, target =>
        {
            Assert.False(EndpointTargetPolicy.TryValidateRelativeTarget(target, out var error));
            Assert.NotEmpty(error);
            Assert.Throws<EndpointTargetValidationException>(
                () => EndpointTargetPolicy.EnsureRelativeTarget(target));
        });
    }

    [Theory]
    [InlineData("https://example.test/api/", "/v1/chat", "https://example.test/v1/chat")]
    [InlineData("https://example.test/api/", "v1/chat", "https://example.test/api/v1/chat")]
    [InlineData("https://example.test/api/", "?model=safe", "https://example.test/api/?model=safe")]
    [InlineData("https://example.test/api/", "#response", "https://example.test/api/#response")]
    [InlineData("https://EXAMPLE.test:443/api/", "v1", "https://example.test/api/v1")]
    [InlineData("http://example.test:80/api/", "../v1", "http://example.test/v1")]
    [InlineData("https://bücher.example/api/", "v1", "https://bücher.example/api/v1")]
    public void Resolve_returns_the_normalized_same_origin_uri(
        string baseUrl,
        string target,
        string expected)
    {
        Assert.True(EndpointTargetPolicy.TryResolve(
            baseUrl,
            target,
            out var resolved,
            out var error));
        Assert.Equal(expected, resolved.AbsoluteUri);
        Assert.Equal(new Uri(expected).IdnHost, resolved.IdnHost);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData("ftp://example.test/api/")]
    [InlineData("https://user@example.test/api/")]
    [InlineData("https://example.test\\api/")]
    [InlineData("https://example.test/api path/")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void Invalid_environment_bases_fail_closed(string baseUrl)
    {
        Assert.False(EndpointTargetPolicy.TryResolve(
            baseUrl,
            "/v1",
            out var resolved,
            out var error));
        Assert.Null(resolved);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("https://example.test/api/", "https://example.test/v1", true)]
    [InlineData("https://bücher.example/api/", "https://xn--bcher-kva.example/v1", true)]
    [InlineData("relative", "https://example.test/v1", false)]
    [InlineData("https://example.test/api/", "relative", false)]
    [InlineData("ftp://example.test/api/", "https://example.test/v1", false)]
    [InlineData("https://user@example.test/api/", "https://example.test/v1", false)]
    [InlineData("https://example.test/api/", "http://example.test/v1", false)]
    [InlineData("https://example.test/api/", "ftp://example.test/v1", false)]
    [InlineData("https://example.test/api/", "https://user@example.test/v1", false)]
    [InlineData("https://example.test/api/", "https://other.test/v1", false)]
    [InlineData("https://example.test/api/", "https://example.test:444/v1", false)]
    public void Resolved_target_origin_comparison_is_explicit(
        string baseUrl,
        string candidate,
        bool expected)
    {
        Assert.Equal(expected, EndpointTargetPolicy.IsAllowedResolvedTarget(
            new Uri(baseUrl, UriKind.RelativeOrAbsolute),
            new Uri(candidate, UriKind.RelativeOrAbsolute)));
    }

    [Fact]
    public void Malformed_unicode_is_not_a_relative_uri()
    {
        Assert.False(EndpointTargetPolicy.TryValidateRelativeTarget(
            new string('\uD800', 1),
            out var error));
        Assert.Contains("malformed Unicode", error);
    }

    [Fact]
    public void Request_models_validate_endpoint_targets()
    {
        var valid = new CreateEndpointRequest { Name = "chat", Path = "v1/chat?model=safe" };
        var invalid = new UpdateEndpointRequest { Name = "chat", Path = "https://attacker.test" };

        Assert.Empty(Validate(valid));
        Assert.Contains(Validate(invalid), result =>
            result.ErrorMessage?.Contains("Invalid endpoint target", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Endpoint_target_attribute_handles_non_string_values_defensively()
    {
        var attribute = new EndpointTargetAttribute();
        var context = new ValidationContext(new object());

        Assert.Null(attribute.GetValidationResult(null, context));
        Assert.NotNull(attribute.GetValidationResult(42, context));
    }

    private static IReadOnlyList<ValidationResult> Validate(object request)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(request, new ValidationContext(request), results, true);
        return results;
    }
}

public sealed class EndpointTargetServiceAndControllerTests
{
    [Fact]
    public async Task Service_rejects_unsafe_create_and_update_without_persisting_changes()
    {
        await using var dbContext = CreateDbContext();
        var (environment, endpoint, scope) = await SeedGraphAsync(dbContext);
        var service = new EndpointService(dbContext, NullLogger<EndpointService>.Instance);

        await Assert.ThrowsAsync<EndpointTargetValidationException>(() =>
            service.CreateEndpointAsync(
                environment.Id,
                "unsafe",
                "https://attacker.test/collect",
                "POST",
                30,
                scope));
        await Assert.ThrowsAsync<EndpointTargetValidationException>(() =>
            service.UpdateEndpointAsync(
                endpoint.Id,
                "changed",
                "//attacker.test/collect",
                "POST",
                30,
                scope));

        Assert.Equal(1, await dbContext.Endpoints.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal("safe", endpoint.Name);
        Assert.Equal("/v1/chat", endpoint.Path);
    }

    [Fact]
    public async Task Service_persists_root_and_path_relative_targets()
    {
        await using var dbContext = CreateDbContext();
        var (environment, endpoint, scope) = await SeedGraphAsync(dbContext);
        var service = new EndpointService(dbContext, NullLogger<EndpointService>.Instance);

        var created = await service.CreateEndpointAsync(
            environment.Id,
            "created",
            "v2/chat?model=safe#response",
            "POST",
            30,
            scope);
        var updated = await service.UpdateEndpointAsync(
            endpoint.Id,
            "updated",
            "/v3/chat",
            "POST",
            30,
            scope);

        Assert.Equal("v2/chat?model=safe#response", Assert.IsType<Endpoint>(created).Path);
        Assert.Equal("/v3/chat", Assert.IsType<Endpoint>(updated).Path);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Controller_converts_service_target_validation_to_bad_request(bool create)
    {
        var controller = new EndpointsController(
            new RejectingEndpointService(),
            new FixedScopeAccessor(),
            NullLogger<EndpointsController>.Instance);

        var result = create
            ? await controller.CreateEndpoint(
                Guid.NewGuid(),
                new CreateEndpointRequest { Name = "unsafe", Path = "https://attacker.test" })
            : await controller.UpdateEndpoint(
                Guid.NewGuid(),
                new UpdateEndpointRequest { Name = "unsafe", Path = "//attacker.test" });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid endpoint target", badRequest.Value?.ToString());
    }

    private static PromptlyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase($"endpoint-targets-{Guid.NewGuid():N}")
            .Options;
        return new PromptlyDbContext(options);
    }

    private static async Task<(Environment Environment, Endpoint Endpoint, TenantAccessScope Scope)>
        SeedGraphAsync(PromptlyDbContext dbContext)
    {
        const string ownerId = "endpoint-owner";
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "project",
            OwnerUserId = ownerId
        };
        var environment = new Environment
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Project = project,
            Name = "environment",
            BaseUrl = "https://example.test/api/"
        };
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = environment.Id,
            Environment = environment,
            Name = "safe",
            Path = "/v1/chat"
        };

        dbContext.Users.Add(new User { Id = ownerId, UserName = ownerId });
        dbContext.Projects.Add(project);
        dbContext.Environments.Add(environment);
        dbContext.Endpoints.Add(endpoint);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (environment, endpoint, new TenantAccessScope(ownerId, ProjectId: null));
    }

    private sealed class FixedScopeAccessor : ITenantAccessScopeAccessor
    {
        public bool TryGetScope([NotNullWhen(true)] out TenantAccessScope? scope)
        {
            scope = new TenantAccessScope("owner", ProjectId: null);
            return true;
        }
    }

    private sealed class RejectingEndpointService : IEndpointService
    {
        public Task<IReadOnlyList<Endpoint>?> GetEndpointsByEnvironmentAsync(
            Guid environmentId,
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<Endpoint?> GetEndpointByIdAsync(
            Guid endpointId,
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<Endpoint?> CreateEndpointAsync(
            Guid environmentId,
            string name,
            string path,
            string httpMethod,
            int timeoutSeconds,
            TenantAccessScope scope) => throw new EndpointTargetValidationException("unsafe");

        public Task<Endpoint?> UpdateEndpointAsync(
            Guid endpointId,
            string name,
            string path,
            string httpMethod,
            int timeoutSeconds,
            TenantAccessScope scope) => throw new EndpointTargetValidationException("unsafe");

        public Task<bool> DeleteEndpointAsync(
            Guid endpointId,
            TenantAccessScope scope) => throw new NotSupportedException();
    }
}

public sealed class EndpointExecutorTargetSecurityTests
{
    [Theory]
    [InlineData("/v1/chat", "https://example.test/v1/chat")]
    [InlineData("v1/chat", "https://example.test/api/v1/chat")]
    [InlineData("?model=safe", "https://example.test/api/?model=safe")]
    [InlineData("#response", "https://example.test/api/#response")]
    public async Task Legitimate_targets_use_the_exact_resolved_same_origin_uri(
        string target,
        string expected)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var factory = new RecordingHttpClientFactory(client);
        var encryption = new RecordingEncryptionService();
        var executor = new EndpointExecutor(
            factory,
            encryption,
            NullLogger<EndpointExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            Endpoint(target),
            Environment("https://example.test/api/", encryptedHeaders: "ciphertext"),
            TestCase(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(expected, handler.RequestUri?.AbsoluteUri);
        Assert.Equal(EndpointExecutor.HttpClientName, factory.ClientName);
        Assert.Equal(1, handler.SendCount);
        Assert.Equal(1, encryption.DecryptCount);
    }

    [Theory]
    [InlineData("https://attacker.test/collect")]
    [InlineData("//attacker.test/collect")]
    [InlineData("/v1\\collect")]
    [InlineData("/v1\r\nInjected:true")]
    [InlineData("/bad%")]
    public async Task Unsafe_legacy_targets_fail_before_decryption_or_send(string target)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var factory = new RecordingHttpClientFactory(client);
        var encryption = new RecordingEncryptionService();
        var executor = new EndpointExecutor(
            factory,
            encryption,
            NullLogger<EndpointExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            Endpoint(target),
            Environment("https://example.test/api/", encryptedHeaders: "ciphertext"),
            TestCase(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Unsafe endpoint target", result.ErrorMessage);
        Assert.Equal(0, encryption.DecryptCount);
        Assert.Equal(0, handler.SendCount);
        Assert.Null(factory.ClientName);
    }

    [Theory]
    [InlineData("ftp://example.test/api/")]
    [InlineData("https://user@example.test/api/")]
    [InlineData("https://example.test/api path/")]
    public async Task Unsafe_legacy_environment_origins_fail_before_decryption_or_send(
        string baseUrl)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var factory = new RecordingHttpClientFactory(client);
        var encryption = new RecordingEncryptionService();
        var executor = new EndpointExecutor(
            factory,
            encryption,
            NullLogger<EndpointExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            Endpoint("v1/chat"),
            Environment(baseUrl, encryptedHeaders: "ciphertext"),
            TestCase(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(0, encryption.DecryptCount);
        Assert.Equal(0, handler.SendCount);
        Assert.Null(factory.ClientName);
    }

    [Fact]
    public async Task Redirect_response_is_returned_without_a_second_outbound_request()
    {
        var handler = new RecordingHandler(HttpStatusCode.Found);
        using var client = new HttpClient(handler);
        var executor = new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            new RecordingEncryptionService(),
            NullLogger<EndpointExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            Endpoint("/redirect"),
            Environment("https://example.test/api/", encryptedHeaders: null),
            TestCase(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal((int)HttpStatusCode.Found, result.StatusCode);
        Assert.Equal(1, handler.SendCount);
    }

    [Theory]
    [InlineData("POST", "POST")]
    [InlineData("get", "GET")]
    [InlineData("PUT", "PUT")]
    [InlineData("PATCH", "PATCH")]
    [InlineData("DELETE", "DELETE")]
    [InlineData("unsupported", "POST")]
    public async Task Executor_maps_methods_and_preserves_the_messages_payload(
        string configuredMethod,
        string expectedMethod)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var executor = new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            new RecordingEncryptionService(),
            NullLogger<EndpointExecutor>.Instance);
        var endpoint = Endpoint("/v1/chat");
        endpoint.HttpMethod = configuredMethod;
        var testCase = TestCase();
        testCase.InputSpecJson = "{\"messages\":[{\"role\":\"user\"}],\"ignored\":true}";

        var result = await executor.ExecuteAsync(
            endpoint,
            Environment("https://example.test/api/", encryptedHeaders: null),
            testCase,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(expectedMethod, handler.Method?.Method);
        Assert.Equal("{\"messages\":[{\"role\":\"user\"}]}", handler.ContentJson);
    }

    [Fact]
    public async Task Null_input_returns_failure_without_creating_a_client()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var factory = new RecordingHttpClientFactory(client);
        var executor = new EndpointExecutor(
            factory,
            new RecordingEncryptionService(),
            NullLogger<EndpointExecutor>.Instance);
        var testCase = TestCase();
        testCase.InputSpecJson = "null";

        var result = await executor.ExecuteAsync(
            Endpoint("/v1/chat"),
            Environment("https://example.test/api/", encryptedHeaders: null),
            testCase,
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("Failed to parse test input", result.ErrorMessage);
        Assert.Null(factory.ClientName);
        Assert.Equal(0, handler.SendCount);
    }

    [Theory]
    [InlineData("null", false)]
    [InlineData("invalid-json", true)]
    public async Task Invalid_or_null_decrypted_headers_do_not_prevent_safe_send(
        string decryptedHeaders,
        bool throws)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        var encryption = new RecordingEncryptionService
        {
            DecryptedValue = decryptedHeaders,
            Failure = throws ? new InvalidOperationException("invalid ciphertext") : null
        };
        using var client = new HttpClient(handler);
        var executor = new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            encryption,
            NullLogger<EndpointExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            Endpoint("/v1/chat"),
            Environment("https://example.test/api/", encryptedHeaders: "ciphertext"),
            TestCase(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(1, encryption.DecryptCount);
        Assert.Equal(1, handler.SendCount);
        Assert.Empty(handler.Headers);
    }

    [Fact]
    public async Task Timeout_is_reported_without_becoming_external_cancellation()
    {
        using var client = new HttpClient(
            new ThrowingHandler(new TaskCanceledException("timeout")));
        var executor = new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            new RecordingEncryptionService(),
            NullLogger<EndpointExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            Endpoint("/v1/chat"),
            Environment("https://example.test/api/", encryptedHeaders: null),
            TestCase(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("timed out after 30s", result.ErrorMessage);
    }

    [Fact]
    public async Task Unexpected_send_failure_is_returned_as_an_execution_failure()
    {
        using var client = new HttpClient(
            new ThrowingHandler(new HttpRequestException("network down")));
        var executor = new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            new RecordingEncryptionService(),
            NullLogger<EndpointExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            Endpoint("/v1/chat"),
            Environment("https://example.test/api/", encryptedHeaders: null),
            TestCase(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Execution failed: network down", result.ErrorMessage);
    }

    private static Endpoint Endpoint(string path) => new()
    {
        Id = Guid.NewGuid(),
        EnvironmentId = Guid.NewGuid(),
        Name = "endpoint",
        Path = path,
        HttpMethod = "POST",
        TimeoutSeconds = 30
    };

    private static Environment Environment(string baseUrl, string? encryptedHeaders) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        Name = "environment",
        BaseUrl = baseUrl,
        DefaultHeadersEncryptedJson = encryptedHeaders
    };

    private static TestCase TestCase() => new()
    {
        Id = Guid.NewGuid(),
        SuiteId = Guid.NewGuid(),
        ExternalId = "case-1",
        Name = "case",
        InputSpecJson = "{}",
        ExpectationsJson = "[]"
    };

    private sealed class RecordingHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public string? ClientName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            ClientName = name;
            return client;
        }
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? ContentJson { get; private set; }
        public IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> Headers { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            RequestUri = request.RequestUri;
            Method = request.Method;
            ContentJson = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Headers = request.Headers.ToList();
            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(failure);
    }

    private sealed class RecordingEncryptionService : IEncryptionService
    {
        public int DecryptCount { get; private set; }
        public string DecryptedValue { get; init; } = "{\"Authorization\":\"secret\"}";
        public Exception? Failure { get; init; }

        public string Encrypt(string plainText) => plainText;

        public string Decrypt(string cipherText)
        {
            DecryptCount++;
            if (Failure != null)
            {
                throw Failure;
            }

            return DecryptedValue;
        }
    }
}
