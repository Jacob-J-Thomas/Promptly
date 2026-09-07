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

    private sealed class StubEndpointService(Endpoint endpoint) : IEndpointService
    {
        public Task<Endpoint?> GetEndpointByIdAsync(Guid endpointId) =>
            Task.FromResult<Endpoint?>(endpointId == endpoint.Id ? endpoint : null);

        public Task<IEnumerable<Endpoint>> GetEndpointsByEnvironmentAsync(Guid environmentId) =>
            throw new NotSupportedException();

        public Task<Endpoint> CreateEndpointAsync(
            Guid environmentId,
            string name,
            string path,
            string httpMethod,
            int timeoutSeconds) => throw new NotSupportedException();

        public Task<Endpoint?> UpdateEndpointAsync(
            Guid endpointId,
            string name,
            string path,
            string httpMethod,
            int timeoutSeconds) => throw new NotSupportedException();

        public Task<bool> DeleteEndpointAsync(Guid endpointId) => throw new NotSupportedException();
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

        public Task<MappingSpec> SaveMappingSpecAsync(
            Guid endpointId,
            string name,
            string specJson) => throw new NotSupportedException();

        public Task<List<MappingSpec>> GetMappingSpecsByEndpointAsync(Guid endpointId) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> GetMappingSpecByIdAsync(Guid id) => throw new NotSupportedException();

        public Task<MappingSpec?> GetDefaultMappingAsync(Guid endpointId) =>
            throw new NotSupportedException();

        public Task<MappingSpec> UpdateMappingSpecAsync(
            Guid id,
            string name,
            string specJson) => throw new NotSupportedException();

        public Task SetDefaultMappingAsync(Guid id) => throw new NotSupportedException();

        public Task DeleteMappingSpecAsync(Guid id) => throw new NotSupportedException();
    }
}
