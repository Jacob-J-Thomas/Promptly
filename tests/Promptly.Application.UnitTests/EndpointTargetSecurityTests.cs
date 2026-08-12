using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
    [InlineData("https://example.test/api/")]
    [InlineData("http://127.0.0.1:8080/")]
    [InlineData("https://bücher.example/v1")]
    public void Http_environment_base_urls_are_accepted(string baseUrl)
    {
        Assert.True(EndpointTargetPolicy.TryValidateBaseUri(
            baseUrl,
            out var parsed,
            out var error));
        Assert.Equal(new Uri(baseUrl), parsed);
        Assert.Equal(parsed, EndpointTargetPolicy.EnsureBaseUri(baseUrl));
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("ftp://example.test/")]
    [InlineData("https://user@example.test/")]
    [InlineData("https://example.test/path with space")]
    [InlineData("https://example.test\\path")]
    public void Unsafe_environment_base_urls_are_rejected(string? baseUrl)
    {
        Assert.False(EndpointTargetPolicy.TryValidateBaseUri(
            baseUrl,
            out var parsed,
            out var error));
        Assert.Null(parsed);
        Assert.NotEmpty(error);
        Assert.Throws<EnvironmentBaseUrlValidationException>(
            () => EndpointTargetPolicy.EnsureBaseUri(baseUrl));
    }

    [Fact]
    public void Environment_base_url_validation_rejects_unpaired_utf16()
    {
        var malformed = $"https://example.test/{new string('\uD800', 1)}";

        Assert.False(EndpointTargetPolicy.TryValidateBaseUri(
            malformed,
            out var parsed,
            out var error));
        Assert.Null(parsed);
        Assert.Contains("absolute HTTP(S)", error);
    }

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
    public void Environment_request_models_validate_base_urls()
    {
        var valid = new CreateEnvironmentRequest
        {
            Name = "public",
            BaseUrl = "https://example.test/api/"
        };
        var invalid = new UpdateEnvironmentRequest
        {
            Name = "unsafe",
            BaseUrl = "ftp://example.test/"
        };

        Assert.Empty(Validate(valid));
        Assert.Contains(Validate(invalid), result =>
            result.ErrorMessage?.Contains(
                "Invalid environment base URL",
                StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Environment_base_url_attribute_handles_non_string_values_defensively()
    {
        var attribute = new EnvironmentBaseUrlAttribute();
        var context = new ValidationContext(new object());

        Assert.Null(attribute.GetValidationResult(null, context));
        Assert.NotNull(attribute.GetValidationResult(42, context));
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

public sealed class EnvironmentBaseUrlServiceAndControllerTests
{
    [Fact]
    public async Task Service_rejects_unsafe_base_urls_without_mutating_state_or_headers()
    {
        await using var dbContext = CreateDbContext();
        var (project, environment, scope) = await SeedGraphAsync(dbContext);
        var encryption = new RecordingEnvironmentEncryptionService();
        var service = new EnvironmentService(
            dbContext,
            encryption,
            NullLogger<EnvironmentService>.Instance);

        await Assert.ThrowsAsync<EnvironmentBaseUrlValidationException>(() =>
            service.CreateEnvironmentAsync(
                project.Id,
                "unsafe create",
                "ftp://example.test/",
                new Dictionary<string, string> { ["Authorization"] = "secret" },
                scope));
        await Assert.ThrowsAsync<EnvironmentBaseUrlValidationException>(() =>
            service.UpdateEnvironmentAsync(
                environment.Id,
                "unsafe update",
                "https://user@example.test/",
                new Dictionary<string, string> { ["Authorization"] = "changed" },
                scope));

        Assert.Equal(1, await dbContext.Environments.CountAsync(
            TestContext.Current.CancellationToken));
        Assert.Equal("safe", environment.Name);
        Assert.Equal("https://example.test/api/", environment.BaseUrl);
        Assert.Null(environment.DefaultHeadersEncryptedJson);
        Assert.Equal(0, encryption.EncryptCount);
    }

    [Fact]
    public async Task Service_persists_valid_http_and_https_base_urls()
    {
        await using var dbContext = CreateDbContext();
        var (project, environment, scope) = await SeedGraphAsync(dbContext);
        var service = new EnvironmentService(
            dbContext,
            new RecordingEnvironmentEncryptionService(),
            NullLogger<EnvironmentService>.Instance);

        var created = await service.CreateEnvironmentAsync(
            project.Id,
            "created",
            "http://public.example.test:8080/api/",
            null,
            scope);
        var updated = await service.UpdateEnvironmentAsync(
            environment.Id,
            "updated",
            "https://bücher.example/v2/",
            null,
            scope);

        Assert.Equal(
            "http://public.example.test:8080/api/",
            Assert.IsType<Environment>(created).BaseUrl);
        Assert.Equal("https://bücher.example/v2/", Assert.IsType<Environment>(updated).BaseUrl);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Controller_converts_service_base_url_validation_to_bad_request(bool create)
    {
        var controller = new EnvironmentsController(
            new RejectingEnvironmentService(),
            new EnvironmentFixedScopeAccessor(),
            NullLogger<EnvironmentsController>.Instance);

        var result = create
            ? await controller.CreateEnvironment(
                Guid.NewGuid(),
                new CreateEnvironmentRequest { Name = "unsafe", BaseUrl = "ftp://example.test" })
            : await controller.UpdateEnvironment(
                Guid.NewGuid(),
                new UpdateEnvironmentRequest
                {
                    Name = "unsafe",
                    BaseUrl = "https://user@example.test"
                });

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid environment base URL", badRequest.Value?.ToString());
    }

    private static PromptlyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase($"environment-targets-{Guid.NewGuid():N}")
            .Options;
        return new PromptlyDbContext(options);
    }

    private static async Task<(Project Project, Environment Environment, TenantAccessScope Scope)>
        SeedGraphAsync(PromptlyDbContext dbContext)
    {
        const string ownerId = "environment-owner";
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
            Name = "safe",
            BaseUrl = "https://example.test/api/"
        };

        dbContext.Users.Add(new User { Id = ownerId, UserName = ownerId });
        dbContext.Projects.Add(project);
        dbContext.Environments.Add(environment);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (project, environment, new TenantAccessScope(ownerId, ProjectId: null));
    }

    private sealed class RecordingEnvironmentEncryptionService : IEncryptionService
    {
        public int EncryptCount { get; private set; }

        public string Encrypt(string plainText)
        {
            EncryptCount++;
            return plainText;
        }

        public string Decrypt(string cipherText) => cipherText;
    }

    private sealed class EnvironmentFixedScopeAccessor : ITenantAccessScopeAccessor
    {
        public bool TryGetScope([NotNullWhen(true)] out TenantAccessScope? scope)
        {
            scope = new TenantAccessScope("owner", ProjectId: null);
            return true;
        }
    }

    private sealed class RejectingEnvironmentService : IEnvironmentService
    {
        public Task<IReadOnlyList<Environment>?> GetEnvironmentsByProjectAsync(
            Guid projectId,
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<Environment?> GetEnvironmentByIdAsync(
            Guid environmentId,
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<Environment?> CreateEnvironmentAsync(
            Guid projectId,
            string name,
            string baseUrl,
            Dictionary<string, string>? headers,
            TenantAccessScope scope) => throw new EnvironmentBaseUrlValidationException("unsafe");

        public Task<Environment?> UpdateEnvironmentAsync(
            Guid environmentId,
            string name,
            string baseUrl,
            Dictionary<string, string>? headers,
            TenantAccessScope scope) => throw new EnvironmentBaseUrlValidationException("unsafe");

        public Task<bool> DeleteEnvironmentAsync(
            Guid environmentId,
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<Dictionary<string, string>?> GetDecryptedHeadersAsync(
            Guid environmentId,
            TenantAccessScope scope) => throw new NotSupportedException();
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
    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.OK));
        var factory = new RecordingHttpClientFactory(client);
        var encryption = new RecordingEncryptionService();
        var guard = new PassThroughEndpointDestinationGuard();
        var logger = NullLogger<EndpointExecutor>.Instance;

        Assert.Equal(
            "httpClientFactory",
            Assert.Throws<ArgumentNullException>(() =>
                new EndpointExecutor(null!, encryption, guard, logger)).ParamName);
        Assert.Equal(
            "encryptionService",
            Assert.Throws<ArgumentNullException>(() =>
                new EndpointExecutor(factory, null!, guard, logger)).ParamName);
        Assert.Equal(
            "destinationGuard",
            Assert.Throws<ArgumentNullException>(() =>
                new EndpointExecutor(factory, encryption, null!, logger)).ParamName);
        Assert.Equal(
            "logger",
            Assert.Throws<ArgumentNullException>(() =>
                new EndpointExecutor(factory, encryption, guard, null!)).ParamName);
    }

    [Fact]
    public async Task Destination_policy_denial_precedes_input_parsing_decryption_and_client_creation()
    {
        var rejection = new EndpointDestinationRejectedException(
            EndpointDestinationRejectionReason.PermanentlyDeniedDestination);
        var guard = new RecordingDestinationGuard(rejection);
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var factory = new RecordingHttpClientFactory(client);
        var encryption = new RecordingEncryptionService();
        var testCase = TestCase();
        testCase.InputSpecJson = "{";

        var result = await new EndpointExecutor(
            factory,
            encryption,
            guard,
            NullLogger<EndpointExecutor>.Instance).ExecuteAsync(
                Endpoint("/v1/chat"),
                Environment("https://example.test/api/", encryptedHeaders: "ciphertext"),
                testCase,
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, result.ErrorMessage);
        Assert.Null(result.StatusCode);
        var authorization = Assert.Single(guard.Calls);
        Assert.Equal("https://example.test/v1/chat", authorization.Destination.AbsoluteUri);
        Assert.Equal(TestContext.Current.CancellationToken, authorization.CancellationToken);
        Assert.Equal(0, encryption.DecryptCount);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task Nested_connector_policy_denial_is_sanitized()
    {
        var rejection = new EndpointDestinationRejectedException(
            EndpointDestinationRejectionReason.NonPublicDestination);
        var connectorFailure = new HttpRequestException(
            "connector failed",
            new AggregateException(
                new InvalidOperationException("unrelated address failure"),
                new InvalidOperationException("dial failed", rejection)));
        var handler = new ThrowingHandler(connectorFailure);
        using var client = new HttpClient(handler);

        var result = await new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            new RecordingEncryptionService(),
            new PassThroughEndpointDestinationGuard(),
            NullLogger<EndpointExecutor>.Instance,
            ProxyEgressOptions()).ExecuteAsync(
                Endpoint("/v1/chat"),
                Environment("https://example.test/api/", encryptedHeaders: null),
                TestCase(),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, result.ErrorMessage);
        Assert.Null(result.StatusCode);
        Assert.DoesNotContain("connector failed", result.ErrorMessage);
        Assert.Equal(1, handler.SendCount);
    }

    [Fact]
    public async Task Proxy_407_response_is_sanitized_and_preserves_status()
    {
        var handler = new RecordingHandler(HttpStatusCode.ProxyAuthenticationRequired);
        using var client = new HttpClient(handler);

        var result = await new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            new RecordingEncryptionService(),
            new PassThroughEndpointDestinationGuard(),
            NullLogger<EndpointExecutor>.Instance,
            ProxyEgressOptions()).ExecuteAsync(
                Endpoint("/v1/chat"),
                Environment("https://example.test/api/", encryptedHeaders: null),
                TestCase(),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, result.ErrorMessage);
        Assert.Equal((int)HttpStatusCode.ProxyAuthenticationRequired, result.StatusCode);
        Assert.Null(result.ResponseJson);
        Assert.Equal(1, handler.SendCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Smokescreen_error_response_is_sanitized_and_preserves_status(
        HttpStatusCode statusCode)
    {
        var handler = new RecordingHandler(statusCode, addSmokescreenError: true);
        using var client = new HttpClient(handler);

        var result = await new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            new RecordingEncryptionService(),
            new PassThroughEndpointDestinationGuard(),
            NullLogger<EndpointExecutor>.Instance,
            ProxyEgressOptions()).ExecuteAsync(
                Endpoint("/v1/chat"),
                Environment("https://example.test/api/", encryptedHeaders: null),
                TestCase(),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, result.ErrorMessage);
        Assert.Equal((int)statusCode, result.StatusCode);
        Assert.Null(result.ResponseJson);
        Assert.Equal(1, handler.SendCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Proxy_407_request_exception_is_sanitized_and_preserves_status(bool nested)
    {
        var proxyDenial = new HttpRequestException(
            "proxy response contained deployment details",
            inner: null,
            statusCode: HttpStatusCode.ProxyAuthenticationRequired);
        Exception failure = nested
            ? new HttpRequestException(
                "connector failed",
                new AggregateException(
                    new InvalidOperationException("unrelated failure"),
                    proxyDenial))
            : proxyDenial;
        var handler = new ThrowingHandler(failure);
        using var client = new HttpClient(handler);

        var result = await new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            new RecordingEncryptionService(),
            new PassThroughEndpointDestinationGuard(),
            NullLogger<EndpointExecutor>.Instance,
            ProxyEgressOptions()).ExecuteAsync(
                Endpoint("/v1/chat"),
                Environment("https://example.test/api/", encryptedHeaders: null),
                TestCase(),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, result.ErrorMessage);
        Assert.Equal((int)HttpStatusCode.ProxyAuthenticationRequired, result.StatusCode);
        Assert.DoesNotContain("deployment details", result.ErrorMessage);
        Assert.Equal(1, handler.SendCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Proxy_connect_failure_is_sanitized_and_preserves_status(
        HttpStatusCode statusCode)
    {
        using var client = new HttpClient(
            new ThrowingHandler(
                new HttpRequestException(
                    "proxy tunnel to internal-proxy-origin failed",
                    inner: null,
                    statusCode)));

        var result = await new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            new RecordingEncryptionService(),
            new PassThroughEndpointDestinationGuard(),
            NullLogger<EndpointExecutor>.Instance,
            ProxyEgressOptions()).ExecuteAsync(
                Endpoint("/v1/chat"),
                Environment("https://example.test/api/", encryptedHeaders: null),
                TestCase(),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(EndpointDestinationRejectedException.SafeMessage, result.ErrorMessage);
        Assert.Equal((int)statusCode, result.StatusCode);
        Assert.DoesNotContain("internal-proxy-origin", result.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.ProxyAuthenticationRequired, false)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    public async Task Direct_mode_preserves_legitimate_upstream_proxy_like_responses(
        HttpStatusCode statusCode,
        bool addSmokescreenError)
    {
        var handler = new RecordingHandler(statusCode, addSmokescreenError);
        using var client = new HttpClient(handler);

        var result = await new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            new RecordingEncryptionService(),
            new PassThroughEndpointDestinationGuard(),
            NullLogger<EndpointExecutor>.Instance).ExecuteAsync(
                Endpoint("/v1/chat"),
                Environment("https://example.test/api/", encryptedHeaders: null),
                TestCase(),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal((int)statusCode, result.StatusCode);
        Assert.Equal("{}", result.ResponseJson);
        Assert.Contains("HTTP ", result.ErrorMessage);
        Assert.NotEqual(EndpointDestinationRejectedException.SafeMessage, result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("hOsT")]
    [InlineData("Connection")]
    [InlineData("Keep-Alive")]
    [InlineData("Transfer-Encoding")]
    [InlineData("TE")]
    [InlineData("Trailer")]
    [InlineData("Upgrade")]
    [InlineData("Content-Length")]
    [InlineData("X-Upstream-Https-Proxy")]
    [InlineData("pRoXy-Authorization")]
    [InlineData("X-Smokescreen-Role")]
    public async Task Reserved_headers_fail_before_client_creation(string headerName)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var factory = new RecordingHttpClientFactory(client);
        var encryption = new RecordingEncryptionService
        {
            DecryptedValue = JsonSerializer.Serialize(
                new Dictionary<string, string> { [headerName] = "attacker-controlled" })
        };

        var result = await new EndpointExecutor(
            factory,
            encryption,
            new PassThroughEndpointDestinationGuard(),
            NullLogger<EndpointExecutor>.Instance).ExecuteAsync(
                Endpoint("/v1/chat"),
                Environment("https://example.test/api/", encryptedHeaders: "ciphertext"),
                TestCase(),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("Unsafe endpoint headers", result.ErrorMessage);
        Assert.Equal(1, encryption.DecryptCount);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, handler.SendCount);
    }

    [Theory]
    [InlineData("Bad Header", "value")]
    [InlineData("X-Test", "safe\r\nInjected: value")]
    [InlineData("Content-Type", "text/plain")]
    public async Task Malformed_or_misused_headers_fail_before_client_creation(
        string headerName,
        string headerValue)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var factory = new RecordingHttpClientFactory(client);
        var encryption = new RecordingEncryptionService
        {
            DecryptedValue = JsonSerializer.Serialize(
                new Dictionary<string, string> { [headerName] = headerValue })
        };

        var result = await new EndpointExecutor(
            factory,
            encryption,
            new PassThroughEndpointDestinationGuard(),
            NullLogger<EndpointExecutor>.Instance).ExecuteAsync(
                Endpoint("/v1/chat"),
                Environment("https://example.test/api/", encryptedHeaders: "ciphertext"),
                TestCase(),
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("Unsafe endpoint headers", result.ErrorMessage);
        Assert.Equal(1, encryption.DecryptCount);
        Assert.Equal(0, factory.CreateCount);
        Assert.Equal(0, handler.SendCount);
    }

    [Fact]
    public async Task Normal_headers_are_forwarded_with_bounded_http_version_policy()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK);
        using var client = new HttpClient(handler);
        var encryption = new RecordingEncryptionService
        {
            DecryptedValue = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer secret",
                ["X-Correlation-Id"] = "trace-123"
            })
        };

        var result = await new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            encryption,
            new PassThroughEndpointDestinationGuard(),
            NullLogger<EndpointExecutor>.Instance).ExecuteAsync(
                Endpoint("/v1/chat"),
                Environment("https://example.test/api/", encryptedHeaders: "ciphertext"),
                TestCase(),
                TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(HttpVersion.Version20, handler.RequestVersion);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, handler.RequestVersionPolicy);
        Assert.Contains(handler.Headers, header =>
            string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
            && header.Value.SequenceEqual(["Bearer secret"]));
        Assert.Contains(handler.Headers, header =>
            string.Equals(header.Key, "X-Correlation-Id", StringComparison.OrdinalIgnoreCase)
            && header.Value.SequenceEqual(["trace-123"]));
        Assert.Equal(1, encryption.DecryptCount);
        Assert.Equal(1, handler.SendCount);
    }

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
            new PassThroughEndpointDestinationGuard(),
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
            new PassThroughEndpointDestinationGuard(),
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
            new PassThroughEndpointDestinationGuard(),
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
            new PassThroughEndpointDestinationGuard(),
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
            new PassThroughEndpointDestinationGuard(),
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
            new PassThroughEndpointDestinationGuard(),
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
            new PassThroughEndpointDestinationGuard(),
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
            new PassThroughEndpointDestinationGuard(),
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
    public async Task Unexpected_http_failure_is_sanitized()
    {
        using var client = new HttpClient(
            new ThrowingHandler(new HttpRequestException("network down")));
        var executor = new EndpointExecutor(
            new RecordingHttpClientFactory(client),
            new RecordingEncryptionService(),
            new PassThroughEndpointDestinationGuard(),
            NullLogger<EndpointExecutor>.Instance);

        var result = await executor.ExecuteAsync(
            Endpoint("/v1/chat"),
            Environment("https://example.test/api/", encryptedHeaders: null),
            TestCase(),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("Endpoint transport failed", result.ErrorMessage);
        Assert.DoesNotContain("network down", result.ErrorMessage);
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

    private static IOptions<EndpointEgressOptions> ProxyEgressOptions() =>
        Options.Create(new EndpointEgressOptions
        {
            ProxyUrl = "http://proxy.example:3128"
        });

    private sealed class RecordingHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public string? ClientName { get; private set; }
        public int CreateCount { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreateCount++;
            ClientName = name;
            return client;
        }
    }

    private sealed class RecordingHandler(
        HttpStatusCode statusCode,
        bool addSmokescreenError = false) : HttpMessageHandler
    {
        public int SendCount { get; private set; }
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? ContentJson { get; private set; }
        public Version? RequestVersion { get; private set; }
        public HttpVersionPolicy? RequestVersionPolicy { get; private set; }
        public IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> Headers { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            RequestUri = request.RequestUri;
            Method = request.Method;
            RequestVersion = request.Version;
            RequestVersionPolicy = request.VersionPolicy;
            ContentJson = request.Content == null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Headers = request.Headers.ToList();
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
            if (addSmokescreenError)
            {
                response.Headers.Add("X-Smokescreen-Error", "internal resolver detail");
            }

            return response;
        }
    }

    private sealed class ThrowingHandler(Exception failure) : HttpMessageHandler
    {
        public int SendCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.FromException<HttpResponseMessage>(failure);
        }
    }

    private sealed class RecordingDestinationGuard(Exception failure) : IEndpointDestinationGuard
    {
        public List<(Uri Destination, CancellationToken CancellationToken)> Calls { get; } = [];

        public Task<AuthorizedEndpointDestination> AuthorizeAsync(
            Uri destination,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((destination, cancellationToken));
            return Task.FromException<AuthorizedEndpointDestination>(failure);
        }
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

internal sealed class PassThroughEndpointDestinationGuard : IEndpointDestinationGuard
{
    public Task<AuthorizedEndpointDestination> AuthorizeAsync(
        Uri destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new AuthorizedEndpointDestination(
            destination.IdnHost,
            destination.Port,
            [IPAddress.Parse("93.184.216.34")]));
    }
}
