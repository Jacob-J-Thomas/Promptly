using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Domain.ValueObjects;
using Promptly.Infrastructure.Clients;
using Promptly.Server.Controllers;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class MappingControllerWorkerFailureTests
{
    [Theory]
    [InlineData(400, PythonWorkerErrorCodes.BadRequest)]
    [InlineData(422, PythonWorkerErrorCodes.BadRequest)]
    [InlineData(502, PythonWorkerErrorCodes.UpstreamFailure)]
    [InlineData(503, PythonWorkerErrorCodes.Unavailable)]
    public async Task ProposeMapping_preserves_worker_HTTP_failure_semantics(
        int workerStatusCode,
        string errorCode)
    {
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = Guid.NewGuid(),
            Name = "chat",
            Path = "/chat"
        };
        var controller = new MappingController(
            new UnusedMappingService(),
            new StubEndpointService(endpoint),
            CreatePythonClient(new FixedResponseHandler(() => JsonResponse(
                (HttpStatusCode)workerStatusCode,
                """{"detail":{"error":{"message":"safe worker failure"}}}"""))),
            new StubTenantAccessScopeAccessor(),
            NullLogger<MappingController>.Instance);

        var action = await controller.ProposeMapping(
            endpoint.Id,
            new ProposeMappingRequest { SampleResponseJson = "{}" },
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<ObjectResult>(action);
        Assert.Equal(workerStatusCode, result.StatusCode);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Contains(
            "safe worker failure",
            body.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);
        Assert.Equal(errorCode, body.RootElement.GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(500)]
    [InlineData(504)]
    public async Task ProposeMapping_maps_unexpected_downstream_statuses_to_bad_gateway(
        int workerStatusCode)
    {
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = Guid.NewGuid(),
            Name = "chat",
            Path = "/chat"
        };
        var controller = new MappingController(
            new UnusedMappingService(),
            new StubEndpointService(endpoint),
            CreatePythonClient(new FixedResponseHandler(() => JsonResponse(
                (HttpStatusCode)workerStatusCode,
                """{"detail":{"error":{"message":"downstream failure"}}}"""))),
            new StubTenantAccessScopeAccessor(),
            NullLogger<MappingController>.Instance);

        var action = await controller.ProposeMapping(
            endpoint.Id,
            new ProposeMappingRequest { SampleResponseJson = "{}" },
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<ObjectResult>(action);
        Assert.Equal(502, result.StatusCode);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(
            PythonWorkerErrorCodes.HttpError,
            body.RootElement.GetProperty("errorCode").GetString());
    }

    [Fact]
    public async Task ProposeMapping_maps_transport_failure_to_bad_gateway()
    {
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = Guid.NewGuid(),
            Name = "chat",
            Path = "/chat"
        };
        var controller = new MappingController(
            new UnusedMappingService(),
            new StubEndpointService(endpoint),
            CreatePythonClient(new FixedResponseHandler(
                () => throw new HttpRequestException("secret transport detail"))),
            new StubTenantAccessScopeAccessor(),
            NullLogger<MappingController>.Instance);

        var action = await controller.ProposeMapping(
            endpoint.Id,
            new ProposeMappingRequest { SampleResponseJson = "{}" },
            TestContext.Current.CancellationToken);

        var result = Assert.IsType<ObjectResult>(action);
        Assert.Equal(502, result.StatusCode);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(
            PythonWorkerErrorCodes.TransportError,
            body.RootElement.GetProperty("errorCode").GetString());
        Assert.DoesNotContain(
            "secret transport detail",
            body.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProposeMapping_does_not_call_the_worker_for_an_inaccessible_endpoint()
    {
        var workerCallCount = 0;
        var controller = new MappingController(
            new UnusedMappingService(),
            new StubEndpointService(endpoint: null),
            CreatePythonClient(new FixedResponseHandler(() =>
            {
                workerCallCount++;
                return JsonResponse(HttpStatusCode.OK, """{"mappingSpecJson":"{}"}""");
            })),
            new StubTenantAccessScopeAccessor(),
            NullLogger<MappingController>.Instance);

        var action = await controller.ProposeMapping(
            Guid.NewGuid(),
            new ProposeMappingRequest { SampleResponseJson = "{}" },
            TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundObjectResult>(action);
        Assert.Equal(0, workerCallCount);
    }

    private static PythonEvalClient CreatePythonClient(HttpMessageHandler handler)
    {
        var configuration = new ConfigurationManager
        {
            ["PROMPTLY_EVAL_BASE_URL"] = "https://worker.example.test"
        };
        return new PythonEvalClient(
            new HttpClient(handler),
            configuration,
            NullLogger<PythonEvalClient>.Instance);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) =>
        new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubEndpointService(Endpoint? endpoint) : IEndpointService
    {
        public Task<Endpoint?> GetEndpointByIdAsync(
            Guid endpointId,
            TenantAccessScope scope) =>
            Task.FromResult(endpoint != null && endpointId == endpoint.Id ? endpoint : null);

        public Task<IReadOnlyList<Endpoint>?> GetEndpointsByEnvironmentAsync(
            Guid environmentId,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<Endpoint?> CreateEndpointAsync(
            Guid environmentId,
            string name,
            string path,
            string httpMethod,
            int timeoutSeconds,
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<Endpoint?> UpdateEndpointAsync(
            Guid endpointId,
            string name,
            string path,
            string httpMethod,
            int timeoutSeconds,
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<bool> DeleteEndpointAsync(Guid endpointId, TenantAccessScope scope) =>
            throw new NotSupportedException();
    }

    private sealed class StubTenantAccessScopeAccessor : ITenantAccessScopeAccessor
    {
        public bool TryGetScope([NotNullWhen(true)] out TenantAccessScope? scope)
        {
            scope = new TenantAccessScope("owner", ProjectId: null);
            return true;
        }
    }

    private sealed class FixedResponseHandler(Func<HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory());
    }

    private sealed class UnusedMappingService : IMappingService
    {
        public Task<MappingResult> ApplyMappingAsync(string mappingSpecJson, string responseJson) =>
            throw new NotSupportedException();

        public Task<MappingResult> ValidateMappingAsync(
            string mappingSpecJson,
            string sampleResponseJson) => throw new NotSupportedException();

        public Task<MappingSpec?> SaveMappingSpecAsync(
            Guid endpointId,
            string name,
            string specJson,
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<List<MappingSpec>?> GetMappingSpecsByEndpointAsync(
            Guid endpointId,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> GetMappingSpecByIdAsync(Guid id, TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> GetDefaultMappingAsync(
            Guid endpointId,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> UpdateMappingSpecAsync(
            Guid id,
            string name,
            string specJson,
            TenantAccessScope scope) => throw new NotSupportedException();

        public Task<bool> SetDefaultMappingAsync(Guid id, TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<bool> DeleteMappingSpecAsync(Guid id, TenantAccessScope scope) =>
            throw new NotSupportedException();
    }
}
