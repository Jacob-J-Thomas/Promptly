using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Domain.ValueObjects;
using Promptly.Server.Controllers;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class TenantMappingControllerCoverageTests
{
    [Fact]
    public async Task Mapping_routes_return_mapped_success_responses()
    {
        var endpoint = Endpoint();
        var mapping = Mapping(endpoint.Id);
        var service = new ConfigurableMappingService
        {
            Mapping = mapping,
            Mappings = [mapping],
            MutationResult = true,
            ValidationResult = new MappingResult { Success = true }
        };
        var controller = Controller(
            service,
            new ConfigurableEndpointService { Endpoint = endpoint },
            new ConfigurablePythonClient
            {
                Result = new MappingProposalResult
                {
                    Success = true,
                    MappingSpecJson = "{\"messages\":{}}",
                    Reason = "observed response shape"
                }
            });

        var proposal = Assert.IsType<ProposeMappingResponse>(
            Assert.IsType<OkObjectResult>(await controller.ProposeMapping(
                endpoint.Id,
                new ProposeMappingRequest { SampleResponseJson = "{}" },
                TestContext.Current.CancellationToken)).Value);
        Assert.Equal("observed response shape", proposal.Reason);
        Assert.IsType<OkObjectResult>(await controller.ValidateMapping(
            endpoint.Id,
            new ValidateMappingRequest
            {
                MappingSpecJson = mapping.SpecJson,
                SampleResponseJson = "{}"
            }));
        Assert.IsType<CreatedAtActionResult>(await controller.CreateMappingSpec(
            endpoint.Id,
            new CreateMappingSpecRequest { Name = mapping.Name, SpecJson = mapping.SpecJson }));
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<MappingSpecResponse>>(
            Assert.IsType<OkObjectResult>(await controller.GetMappingSpecs(endpoint.Id)).Value));
        Assert.Equal(mapping.Id, Assert.IsType<MappingSpecResponse>(
            Assert.IsType<OkObjectResult>(await controller.GetMappingSpec(mapping.Id)).Value).Id);
        Assert.IsType<OkObjectResult>(await controller.UpdateMappingSpec(
            mapping.Id,
            new UpdateMappingSpecRequest { Name = "updated", SpecJson = mapping.SpecJson }));
        Assert.IsType<NoContentResult>(await controller.SetDefaultMapping(mapping.Id));
        Assert.IsType<NoContentResult>(await controller.DeleteMappingSpec(mapping.Id));
    }

    [Fact]
    public async Task Mapping_routes_reject_missing_scope_before_dependencies()
    {
        var controller = Controller(
            new ConfigurableMappingService(),
            new ConfigurableEndpointService(),
            new ConfigurablePythonClient(),
            scopeAccessor: new StubScopeAccessor(scope: null));
        var endpointId = Guid.NewGuid();
        var mappingId = Guid.NewGuid();

        Assert.IsType<UnauthorizedResult>(await controller.ProposeMapping(
            endpointId,
            new ProposeMappingRequest { SampleResponseJson = "{}" },
            TestContext.Current.CancellationToken));
        Assert.IsType<UnauthorizedResult>(await controller.ValidateMapping(
            endpointId,
            new ValidateMappingRequest { MappingSpecJson = "{}", SampleResponseJson = "{}" }));
        Assert.IsType<UnauthorizedResult>(await controller.CreateMappingSpec(
            endpointId,
            new CreateMappingSpecRequest { Name = "mapping", SpecJson = "{}" }));
        Assert.IsType<UnauthorizedResult>(await controller.GetMappingSpecs(endpointId));
        Assert.IsType<UnauthorizedResult>(await controller.GetMappingSpec(mappingId));
        Assert.IsType<UnauthorizedResult>(await controller.UpdateMappingSpec(
            mappingId,
            new UpdateMappingSpecRequest { Name = "mapping", SpecJson = "{}" }));
        Assert.IsType<UnauthorizedResult>(await controller.SetDefaultMapping(mappingId));
        Assert.IsType<UnauthorizedResult>(await controller.DeleteMappingSpec(mappingId));
    }

    [Fact]
    public async Task Mapping_persistence_routes_hide_missing_resources()
    {
        var controller = Controller(
            new ConfigurableMappingService(),
            new ConfigurableEndpointService(),
            new ConfigurablePythonClient());
        var endpointId = Guid.NewGuid();
        var mappingId = Guid.NewGuid();

        Assert.IsType<NotFoundObjectResult>(await controller.CreateMappingSpec(
            endpointId,
            new CreateMappingSpecRequest { Name = "mapping", SpecJson = "{}" }));
        Assert.IsType<NotFoundObjectResult>(await controller.GetMappingSpecs(endpointId));
        Assert.IsType<NotFoundObjectResult>(await controller.GetMappingSpec(mappingId));
        Assert.IsType<NotFoundObjectResult>(await controller.UpdateMappingSpec(
            mappingId,
            new UpdateMappingSpecRequest { Name = "mapping", SpecJson = "{}" }));
        Assert.IsType<NotFoundObjectResult>(await controller.SetDefaultMapping(mappingId));
        Assert.IsType<NotFoundObjectResult>(await controller.DeleteMappingSpec(mappingId));
    }

    [Fact]
    public async Task Mapping_routes_convert_dependency_failures_to_server_errors()
    {
        var failure = new InvalidOperationException("dependency failed");
        var endpointController = Controller(
            new ConfigurableMappingService(),
            new ConfigurableEndpointService { Failure = failure },
            new ConfigurablePythonClient());
        Assert.Equal(500, Assert.IsType<ObjectResult>(await endpointController.ProposeMapping(
            Guid.NewGuid(),
            new ProposeMappingRequest { SampleResponseJson = "{}" },
            TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(500, Assert.IsType<ObjectResult>(await endpointController.ValidateMapping(
            Guid.NewGuid(),
            new ValidateMappingRequest { MappingSpecJson = "{}", SampleResponseJson = "{}" }))
            .StatusCode);

        var mappingController = Controller(
            new ConfigurableMappingService { Failure = failure },
            new ConfigurableEndpointService { Endpoint = Endpoint() },
            new ConfigurablePythonClient());
        var id = Guid.NewGuid();
        Assert.Equal(500, Assert.IsType<ObjectResult>(await mappingController.CreateMappingSpec(
            id,
            new CreateMappingSpecRequest { Name = "mapping", SpecJson = "{}" })).StatusCode);
        Assert.Equal(500, Assert.IsType<ObjectResult>(
            await mappingController.GetMappingSpecs(id)).StatusCode);
        Assert.Equal(500, Assert.IsType<ObjectResult>(
            await mappingController.GetMappingSpec(id)).StatusCode);
        Assert.Equal(500, Assert.IsType<ObjectResult>(await mappingController.UpdateMappingSpec(
            id,
            new UpdateMappingSpecRequest { Name = "mapping", SpecJson = "{}" })).StatusCode);
        Assert.Equal(500, Assert.IsType<ObjectResult>(
            await mappingController.SetDefaultMapping(id)).StatusCode);
        Assert.Equal(500, Assert.IsType<ObjectResult>(
            await mappingController.DeleteMappingSpec(id)).StatusCode);
    }

    [Fact]
    public async Task Propose_mapping_rethrows_caller_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var endpoint = Endpoint();
        var controller = Controller(
            new ConfigurableMappingService(),
            new ConfigurableEndpointService { Endpoint = endpoint },
            new ConfigurablePythonClient
            {
                Failure = new OperationCanceledException(cancellation.Token)
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() => controller.ProposeMapping(
            endpoint.Id,
            new ProposeMappingRequest { SampleResponseJson = "{}" },
            cancellation.Token));
    }

    [Fact]
    public async Task Propose_mapping_treats_noncaller_cancellation_as_a_server_error()
    {
        var endpoint = Endpoint();
        var controller = Controller(
            new ConfigurableMappingService(),
            new ConfigurableEndpointService { Endpoint = endpoint },
            new ConfigurablePythonClient { Failure = new OperationCanceledException() });

        var result = Assert.IsType<ObjectResult>(await controller.ProposeMapping(
            endpoint.Id,
            new ProposeMappingRequest { SampleResponseJson = "{}" },
            CancellationToken.None));

        Assert.Equal(500, result.StatusCode);
    }

    private static MappingController Controller(
        IMappingService mappingService,
        IEndpointService endpointService,
        IPythonEvalClient pythonClient,
        ITenantAccessScopeAccessor? scopeAccessor = null) =>
        new(
            mappingService,
            endpointService,
            pythonClient,
            scopeAccessor ?? new StubScopeAccessor(
                new TenantAccessScope("owner", ProjectId: null)),
            NullLogger<MappingController>.Instance);

    private static Endpoint Endpoint() => new()
    {
        Id = Guid.NewGuid(),
        EnvironmentId = Guid.NewGuid(),
        Name = "endpoint",
        Path = "/chat"
    };

    private static MappingSpec Mapping(Guid endpointId) => new()
    {
        Id = Guid.NewGuid(),
        EndpointId = endpointId,
        Name = "mapping",
        SpecJson = "{}",
        IsDefault = true
    };

    private sealed class StubScopeAccessor(TenantAccessScope? scope)
        : ITenantAccessScopeAccessor
    {
        public bool TryGetScope([NotNullWhen(true)] out TenantAccessScope? value)
        {
            value = scope;
            return value != null;
        }
    }

    private sealed class ConfigurableEndpointService : IEndpointService
    {
        public Endpoint? Endpoint { get; init; }
        public Exception? Failure { get; init; }

        public Task<Endpoint?> GetEndpointByIdAsync(Guid endpointId, TenantAccessScope scope) =>
            Failure == null ? Task.FromResult(Endpoint) : Task.FromException<Endpoint?>(Failure);

        public Task<IReadOnlyList<Endpoint>?> GetEndpointsByEnvironmentAsync(
            Guid environmentId,
            TenantAccessScope scope) => throw new NotSupportedException();

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

    private sealed class ConfigurableMappingService : IMappingService
    {
        public MappingSpec? Mapping { get; init; }
        public List<MappingSpec>? Mappings { get; init; }
        public bool MutationResult { get; init; }
        public MappingResult ValidationResult { get; init; } = new();
        public Exception? Failure { get; init; }

        public Task<MappingResult> ValidateMappingAsync(
            string mappingSpecJson,
            string sampleResponseJson) => Result(ValidationResult);

        public Task<MappingSpec?> SaveMappingSpecAsync(
            Guid endpointId,
            string name,
            string specJson,
            TenantAccessScope scope) => Result(Mapping);

        public Task<List<MappingSpec>?> GetMappingSpecsByEndpointAsync(
            Guid endpointId,
            TenantAccessScope scope) => Result(Mappings);

        public Task<MappingSpec?> GetMappingSpecByIdAsync(Guid id, TenantAccessScope scope) =>
            Result(Mapping);

        public Task<MappingSpec?> UpdateMappingSpecAsync(
            Guid id,
            string name,
            string specJson,
            TenantAccessScope scope) => Result(Mapping);

        public Task<bool> SetDefaultMappingAsync(Guid id, TenantAccessScope scope) =>
            Result(MutationResult);

        public Task<bool> DeleteMappingSpecAsync(Guid id, TenantAccessScope scope) =>
            Result(MutationResult);

        public Task<MappingResult> ApplyMappingAsync(string mappingSpecJson, string responseJson) =>
            throw new NotSupportedException();

        public Task<MappingSpec?> GetDefaultMappingAsync(
            Guid endpointId,
            TenantAccessScope scope) => throw new NotSupportedException();

        private Task<T> Result<T>(T value) =>
            Failure == null ? Task.FromResult(value) : Task.FromException<T>(Failure);
    }

    private sealed class ConfigurablePythonClient : IPythonEvalClient
    {
        public MappingProposalResult Result { get; init; } = new();
        public Exception? Failure { get; init; }

        public Task<MappingProposalResult> ProposeMappingAsync(
            string sampleResponse,
            string? sampleRequest = null,
            Dictionary<string, object>? hints = null,
            CancellationToken cancellationToken = default) =>
            Failure == null
                ? Task.FromResult(Result)
                : Task.FromException<MappingProposalResult>(Failure);

        public Task<EvaluationResult> EvaluateLlmJudgeAsync(
            string rubric,
            double minScore,
            CanonicalTrace trace,
            string? model = null,
            string? provider = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EvaluationResult> EvaluateGroundednessAsync(
            double minScore,
            CanonicalTrace trace,
            List<RetrievedDoc> docs,
            string? model = null,
            string? provider = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
