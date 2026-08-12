using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using Promptly.Domain.ValueObjects;
using Promptly.Server.Controllers;

namespace Promptly.Application.UnitTests;

public sealed class MappingControllerValidationTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    public async Task Persistence_routes_return_structured_bad_requests_for_invalid_specs(
        string operation)
    {
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = Guid.NewGuid(),
            Name = "chat",
            Path = "/chat"
        };
        var controller = CreateController(
            endpoint,
            new ValidationMappingService());

        var action = operation == "create"
            ? await controller.CreateMappingSpec(
                endpoint.Id,
                new CreateMappingSpecRequest { Name = "invalid", SpecJson = "{}" })
            : await controller.UpdateMappingSpec(
                Guid.NewGuid(),
                new UpdateMappingSpecRequest { Name = "invalid", SpecJson = "{}" });

        var result = Assert.IsType<BadRequestObjectResult>(action);
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(
            "fallback.singleAssistantContentPath",
            body.RootElement.GetProperty("errorPath").GetString());
        Assert.Contains(
            "Invalid mapping spec",
            body.RootElement.GetProperty("message").GetString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_route_preserves_the_structured_mapping_error_path()
    {
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = Guid.NewGuid(),
            Name = "chat",
            Path = "/chat"
        };
        var controller = CreateController(
            endpoint,
            new ValidationMappingService(new MappingResult
            {
                Success = false,
                ErrorMessage = "Mapping failed at 'messages.itemsPath': no values",
                ErrorPath = "messages.itemsPath"
            }));

        var action = await controller.ValidateMapping(
            endpoint.Id,
            new ValidateMappingRequest
            {
                MappingSpecJson = "{}",
                SampleResponseJson = "{}"
            });

        var result = Assert.IsType<OkObjectResult>(action);
        var response = Assert.IsType<ValidateMappingResponse>(result.Value);
        Assert.False(response.Success);
        Assert.Equal("messages.itemsPath", response.ErrorPath);
    }

    private static MappingController CreateController(
        Endpoint endpoint,
        IMappingService mappingService) =>
        new(
            mappingService,
            new StubEndpointService(endpoint),
            new UnusedPythonEvalClient(),
            NullLogger<MappingController>.Instance);

    private sealed class ValidationMappingService(MappingResult? validationResult = null)
        : IMappingService
    {
        public Task<MappingResult> ValidateMappingAsync(
            string mappingSpecJson,
            string sampleResponseJson) =>
            Task.FromResult(validationResult ?? new MappingResult { Success = true });

        public Task<MappingSpec> SaveMappingSpecAsync(
            Guid endpointId,
            string name,
            string specJson) =>
            throw InvalidSpec();

        public Task<MappingSpec> UpdateMappingSpecAsync(
            Guid id,
            string name,
            string specJson) =>
            throw InvalidSpec();

        public Task<MappingResult> ApplyMappingAsync(string mappingSpecJson, string responseJson) =>
            throw new NotSupportedException();

        public Task<List<MappingSpec>> GetMappingSpecsByEndpointAsync(Guid endpointId) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> GetMappingSpecByIdAsync(Guid id) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> GetDefaultMappingAsync(Guid endpointId) =>
            throw new NotSupportedException();

        public Task SetDefaultMappingAsync(Guid id) =>
            throw new NotSupportedException();

        public Task DeleteMappingSpecAsync(Guid id) =>
            throw new NotSupportedException();

        private static MappingSpecValidationException InvalidSpec() =>
            new(
                "fallback.singleAssistantContentPath",
                "a non-empty JSONPath is required");
    }

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
            int timeoutSeconds) =>
            throw new NotSupportedException();

        public Task<Endpoint?> UpdateEndpointAsync(
            Guid endpointId,
            string name,
            string path,
            string httpMethod,
            int timeoutSeconds) =>
            throw new NotSupportedException();

        public Task<bool> DeleteEndpointAsync(Guid endpointId) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedPythonEvalClient : IPythonEvalClient
    {
        public Task<MappingProposalResult> ProposeMappingAsync(
            string sampleResponse,
            string? sampleRequest = null,
            Dictionary<string, object>? hints = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EvaluationResult> EvaluateLlmJudgeAsync(
            string rubric,
            double minScore,
            CanonicalTrace trace,
            string? model = null,
            string? provider = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<EvaluationResult> EvaluateGroundednessAsync(
            double minScore,
            CanonicalTrace trace,
            List<RetrievedDoc> docs,
            string? model = null,
            string? provider = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
