using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Server.Controllers;
using Promptly.Server.Models;
using Promptly.Server.Security;
using PromptlyEnvironment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.UnitTests;

public sealed class TenantProjectResourceControllerTests
{
    [Fact]
    public async Task Api_key_scope_is_forbidden_from_creating_a_project()
    {
        var projectId = Guid.NewGuid();
        var service = new StubProjectService();
        var controller = new ProjectsController(
            service,
            new StubScopeAccessor(new TenantAccessScope("owner", projectId)),
            NullLogger<ProjectsController>.Instance);

        var action = await controller.CreateProject(
            new CreateProjectRequest { Name = "forbidden" });

        Assert.IsType<ForbidResult>(action);
        Assert.Equal(0, service.CreateCallCount);
    }

    [Fact]
    public async Task Jwt_scope_can_create_a_project_for_its_owner()
    {
        var service = new StubProjectService();
        var controller = new ProjectsController(
            service,
            new StubScopeAccessor(new TenantAccessScope("owner", ProjectId: null)),
            NullLogger<ProjectsController>.Instance);

        var action = await controller.CreateProject(
            new CreateProjectRequest { Name = "allowed" });

        var created = Assert.IsType<CreatedAtActionResult>(action);
        var response = Assert.IsType<ProjectResponse>(created.Value);
        Assert.Equal("owner", response.OwnerUserId);
        Assert.Equal(1, service.CreateCallCount);
    }

    [Fact]
    public async Task Nested_collections_return_not_found_for_an_inaccessible_parent()
    {
        var scopeAccessor = new StubScopeAccessor(
            new TenantAccessScope("owner", ProjectId: null));
        var environmentsController = new EnvironmentsController(
            new StubEnvironmentService(Environments: null),
            scopeAccessor,
            NullLogger<EnvironmentsController>.Instance);
        var endpointsController = new EndpointsController(
            new StubEndpointService(Endpoints: null),
            scopeAccessor,
            NullLogger<EndpointsController>.Instance);

        var environments = await environmentsController.GetEnvironments(Guid.NewGuid());
        var endpoints = await endpointsController.GetEndpoints(Guid.NewGuid());

        Assert.IsType<NotFoundObjectResult>(environments);
        Assert.IsType<NotFoundObjectResult>(endpoints);
    }

    [Fact]
    public async Task Nested_collections_return_ok_for_an_owned_empty_parent()
    {
        var scopeAccessor = new StubScopeAccessor(
            new TenantAccessScope("owner", ProjectId: null));
        var environmentsController = new EnvironmentsController(
            new StubEnvironmentService([]),
            scopeAccessor,
            NullLogger<EnvironmentsController>.Instance);
        var endpointsController = new EndpointsController(
            new StubEndpointService([]),
            scopeAccessor,
            NullLogger<EndpointsController>.Instance);

        var environments = Assert.IsType<OkObjectResult>(
            await environmentsController.GetEnvironments(Guid.NewGuid()));
        var endpoints = Assert.IsType<OkObjectResult>(
            await endpointsController.GetEndpoints(Guid.NewGuid()));

        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<EnvironmentResponse>>(environments.Value));
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<EndpointResponse>>(endpoints.Value));
    }

    [Fact]
    public async Task Missing_tenant_scope_is_rejected_before_service_access()
    {
        var service = new StubProjectService();
        var controller = new ProjectsController(
            service,
            new StubScopeAccessor(scope: null),
            NullLogger<ProjectsController>.Instance);

        var action = await controller.GetProjects();

        Assert.IsType<UnauthorizedResult>(action);
        Assert.Equal(0, service.GetProjectsCallCount);
    }

    private sealed class StubScopeAccessor(TenantAccessScope? scope)
        : ITenantAccessScopeAccessor
    {
        public bool TryGetScope([NotNullWhen(true)] out TenantAccessScope? value)
        {
            value = scope;
            return value != null;
        }
    }

    private sealed class StubProjectService : IProjectService
    {
        public int CreateCallCount { get; private set; }
        public int GetProjectsCallCount { get; private set; }

        public Task<IReadOnlyList<Project>> GetProjectsAsync(TenantAccessScope scope)
        {
            GetProjectsCallCount++;
            return Task.FromResult<IReadOnlyList<Project>>([]);
        }

        public Task<Project?> GetProjectByIdAsync(Guid projectId, TenantAccessScope scope) =>
            Task.FromResult<Project?>(null);

        public Task<Project?> CreateProjectAsync(
            string name,
            string? description,
            TenantAccessScope scope)
        {
            CreateCallCount++;
            return Task.FromResult<Project?>(new Project
            {
                Id = Guid.NewGuid(),
                Name = name,
                Description = description,
                OwnerUserId = scope.OwnerUserId
            });
        }

        public Task<Project?> UpdateProjectAsync(
            Guid projectId,
            string name,
            string? description,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<bool> DeleteProjectAsync(Guid projectId, TenantAccessScope scope) =>
            throw new NotSupportedException();
    }

    private sealed class StubEnvironmentService(
        IReadOnlyList<PromptlyEnvironment>? Environments) : IEnvironmentService
    {
        public Task<IReadOnlyList<PromptlyEnvironment>?> GetEnvironmentsByProjectAsync(
            Guid projectId,
            TenantAccessScope scope) =>
            Task.FromResult(Environments);

        public Task<PromptlyEnvironment?> GetEnvironmentByIdAsync(
            Guid environmentId,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<PromptlyEnvironment?> CreateEnvironmentAsync(
            Guid projectId,
            string name,
            string baseUrl,
            Dictionary<string, string>? headers,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<PromptlyEnvironment?> UpdateEnvironmentAsync(
            Guid environmentId,
            string name,
            string baseUrl,
            Dictionary<string, string>? headers,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<bool> DeleteEnvironmentAsync(
            Guid environmentId,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<Dictionary<string, string>?> GetDecryptedHeadersAsync(
            Guid environmentId,
            TenantAccessScope scope) =>
            throw new NotSupportedException();
    }

    private sealed class StubEndpointService(
        IReadOnlyList<Endpoint>? Endpoints) : IEndpointService
    {
        public Task<IReadOnlyList<Endpoint>?> GetEndpointsByEnvironmentAsync(
            Guid environmentId,
            TenantAccessScope scope) =>
            Task.FromResult(Endpoints);

        public Task<Endpoint?> GetEndpointByIdAsync(
            Guid endpointId,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<Endpoint?> CreateEndpointAsync(
            Guid environmentId,
            string name,
            string path,
            string httpMethod,
            int timeoutSeconds,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<Endpoint?> UpdateEndpointAsync(
            Guid endpointId,
            string name,
            string path,
            string httpMethod,
            int timeoutSeconds,
            TenantAccessScope scope) =>
            throw new NotSupportedException();

        public Task<bool> DeleteEndpointAsync(Guid endpointId, TenantAccessScope scope) =>
            throw new NotSupportedException();
    }
}
