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

public sealed class TenantResourceControllerCoverageTests
{
    [Fact]
    public async Task Project_routes_map_owned_resources_and_mutations()
    {
        var project = Project();
        var service = new ConfigurableProjectService { Project = project, DeleteResult = true };
        var controller = new ProjectsController(
            service,
            Scope(),
            NullLogger<ProjectsController>.Instance);

        var list = Assert.IsType<OkObjectResult>(await controller.GetProjects());
        Assert.Equal(project.Id, Assert.Single(
            Assert.IsAssignableFrom<IEnumerable<ProjectResponse>>(list.Value)).Id);
        Assert.Equal(project.Id, Assert.IsType<ProjectResponse>(
            Assert.IsType<OkObjectResult>(await controller.GetProject(project.Id)).Value).Id);
        Assert.Equal("updated", Assert.IsType<ProjectResponse>(
            Assert.IsType<OkObjectResult>(await controller.UpdateProject(
                project.Id,
                new UpdateProjectRequest { Name = "updated", Description = "changed" })).Value).Name);
        Assert.IsType<NoContentResult>(await controller.DeleteProject(project.Id));
    }

    [Fact]
    public async Task Project_routes_return_validation_authentication_and_absence_results()
    {
        var unavailable = new ProjectsController(
            new ConfigurableProjectService(),
            NoScope(),
            NullLogger<ProjectsController>.Instance);
        var createRequest = new CreateProjectRequest { Name = "project" };
        Assert.IsType<UnauthorizedResult>(await unavailable.GetProject(Guid.NewGuid()));
        Assert.IsType<UnauthorizedResult>(await unavailable.CreateProject(createRequest));
        Assert.IsType<UnauthorizedResult>(await unavailable.DeleteProject(Guid.NewGuid()));

        var service = new ConfigurableProjectService();
        var controller = new ProjectsController(
            service,
            Scope(),
            NullLogger<ProjectsController>.Instance);
        Assert.IsType<NotFoundObjectResult>(await controller.GetProject(Guid.NewGuid()));
        Assert.IsType<NotFoundObjectResult>(await controller.DeleteProject(Guid.NewGuid()));

        controller.ModelState.AddModelError("Name", "required");
        Assert.IsType<BadRequestObjectResult>(await controller.CreateProject(createRequest));
        Assert.IsType<BadRequestObjectResult>(await controller.UpdateProject(
            Guid.NewGuid(),
            new UpdateProjectRequest { Name = "project" }));

        var unauthorizedUpdate = new ProjectsController(
            service,
            NoScope(),
            NullLogger<ProjectsController>.Instance);
        Assert.IsType<UnauthorizedResult>(await unauthorizedUpdate.UpdateProject(
            Guid.NewGuid(),
            new UpdateProjectRequest { Name = "project" }));

        var missingUpdate = new ProjectsController(
            service,
            Scope(),
            NullLogger<ProjectsController>.Instance);
        Assert.IsType<NotFoundObjectResult>(await missingUpdate.UpdateProject(
            Guid.NewGuid(),
            new UpdateProjectRequest { Name = "project" }));
    }

    [Fact]
    public async Task Project_create_forbids_a_service_level_denial()
    {
        var controller = new ProjectsController(
            new ConfigurableProjectService(),
            Scope(),
            NullLogger<ProjectsController>.Instance);

        Assert.IsType<ForbidResult>(await controller.CreateProject(
            new CreateProjectRequest { Name = "denied" }));
    }

    [Fact]
    public async Task Environment_routes_map_owned_resources_and_mutations()
    {
        var environment = Environment();
        var service = new ConfigurableEnvironmentService
        {
            Environment = environment,
            Environments = [environment],
            Headers = new Dictionary<string, string> { ["Authorization"] = "secret" },
            DeleteResult = true
        };
        var controller = new EnvironmentsController(
            service,
            Scope(),
            NullLogger<EnvironmentsController>.Instance);

        var list = Assert.IsType<OkObjectResult>(
            await controller.GetEnvironments(environment.ProjectId));
        Assert.True(Assert.Single(
            Assert.IsAssignableFrom<IEnumerable<EnvironmentResponse>>(list.Value)).HasHeaders);
        var detail = Assert.IsType<EnvironmentDetailResponse>(
            Assert.IsType<OkObjectResult>(await controller.GetEnvironment(environment.Id)).Value);
        Assert.Equal("secret", detail.Headers?["Authorization"]);
        Assert.IsType<CreatedAtActionResult>(await controller.CreateEnvironment(
            environment.ProjectId,
            new CreateEnvironmentRequest
            {
                Name = environment.Name,
                BaseUrl = environment.BaseUrl,
                Headers = service.Headers
            }));
        Assert.IsType<OkObjectResult>(await controller.UpdateEnvironment(
            environment.Id,
            new UpdateEnvironmentRequest
            {
                Name = "updated",
                BaseUrl = "https://updated.example.test"
            }));
        Assert.IsType<NoContentResult>(await controller.DeleteEnvironment(environment.Id));
    }

    [Fact]
    public async Task Environment_routes_return_validation_authentication_and_absence_results()
    {
        var unavailable = new EnvironmentsController(
            new ConfigurableEnvironmentService(),
            NoScope(),
            NullLogger<EnvironmentsController>.Instance);
        Assert.IsType<UnauthorizedResult>(await unavailable.GetEnvironment(Guid.NewGuid()));
        Assert.IsType<UnauthorizedResult>(await unavailable.DeleteEnvironment(Guid.NewGuid()));

        var service = new ConfigurableEnvironmentService();
        var controller = new EnvironmentsController(
            service,
            Scope(),
            NullLogger<EnvironmentsController>.Instance);
        Assert.IsType<NotFoundObjectResult>(await controller.GetEnvironment(Guid.NewGuid()));
        Assert.IsType<NotFoundObjectResult>(await controller.CreateEnvironment(
            Guid.NewGuid(),
            new CreateEnvironmentRequest { Name = "missing", BaseUrl = "https://example.test" }));
        Assert.IsType<NotFoundObjectResult>(await controller.UpdateEnvironment(
            Guid.NewGuid(),
            new UpdateEnvironmentRequest { Name = "missing", BaseUrl = "https://example.test" }));
        Assert.IsType<NotFoundObjectResult>(await controller.DeleteEnvironment(Guid.NewGuid()));

        var unauthorizedCreate = new EnvironmentsController(
            service,
            NoScope(),
            NullLogger<EnvironmentsController>.Instance);
        Assert.IsType<UnauthorizedResult>(await unauthorizedCreate.CreateEnvironment(
            Guid.NewGuid(),
            new CreateEnvironmentRequest { Name = "missing", BaseUrl = "https://example.test" }));
        Assert.IsType<UnauthorizedResult>(await unauthorizedCreate.UpdateEnvironment(
            Guid.NewGuid(),
            new UpdateEnvironmentRequest { Name = "missing", BaseUrl = "https://example.test" }));

        var invalid = new EnvironmentsController(
            service,
            Scope(),
            NullLogger<EnvironmentsController>.Instance);
        invalid.ModelState.AddModelError("BaseUrl", "invalid");
        Assert.IsType<BadRequestObjectResult>(await invalid.CreateEnvironment(
            Guid.NewGuid(),
            new CreateEnvironmentRequest { Name = "invalid", BaseUrl = "bad" }));
        Assert.IsType<BadRequestObjectResult>(await invalid.UpdateEnvironment(
            Guid.NewGuid(),
            new UpdateEnvironmentRequest { Name = "invalid", BaseUrl = "bad" }));
    }

    [Fact]
    public async Task Endpoint_routes_map_owned_resources_and_mutations()
    {
        var endpoint = Endpoint();
        var service = new ConfigurableEndpointService
        {
            Endpoint = endpoint,
            Endpoints = [endpoint],
            DeleteResult = true
        };
        var controller = new EndpointsController(
            service,
            Scope(),
            NullLogger<EndpointsController>.Instance);

        var list = Assert.IsType<OkObjectResult>(
            await controller.GetEndpoints(endpoint.EnvironmentId));
        Assert.Equal(endpoint.Id, Assert.Single(
            Assert.IsAssignableFrom<IEnumerable<EndpointResponse>>(list.Value)).Id);
        Assert.Equal(endpoint.Id, Assert.IsType<EndpointResponse>(
            Assert.IsType<OkObjectResult>(await controller.GetEndpoint(endpoint.Id)).Value).Id);
        Assert.IsType<CreatedAtActionResult>(await controller.CreateEndpoint(
            endpoint.EnvironmentId,
            new CreateEndpointRequest { Name = endpoint.Name, Path = endpoint.Path }));
        Assert.IsType<OkObjectResult>(await controller.UpdateEndpoint(
            endpoint.Id,
            new UpdateEndpointRequest
            {
                Name = "updated",
                Path = "/updated",
                HttpMethod = "PUT",
                TimeoutSeconds = 45
            }));
        Assert.IsType<NoContentResult>(await controller.DeleteEndpoint(endpoint.Id));
    }

    [Fact]
    public async Task Endpoint_routes_return_validation_authentication_and_absence_results()
    {
        var unavailable = new EndpointsController(
            new ConfigurableEndpointService(),
            NoScope(),
            NullLogger<EndpointsController>.Instance);
        Assert.IsType<UnauthorizedResult>(await unavailable.GetEndpoint(Guid.NewGuid()));
        Assert.IsType<UnauthorizedResult>(await unavailable.DeleteEndpoint(Guid.NewGuid()));

        var service = new ConfigurableEndpointService();
        var controller = new EndpointsController(
            service,
            Scope(),
            NullLogger<EndpointsController>.Instance);
        Assert.IsType<NotFoundObjectResult>(await controller.GetEndpoint(Guid.NewGuid()));
        Assert.IsType<NotFoundObjectResult>(await controller.CreateEndpoint(
            Guid.NewGuid(),
            new CreateEndpointRequest { Name = "missing", Path = "/missing" }));
        Assert.IsType<NotFoundObjectResult>(await controller.UpdateEndpoint(
            Guid.NewGuid(),
            new UpdateEndpointRequest { Name = "missing", Path = "/missing" }));
        Assert.IsType<NotFoundObjectResult>(await controller.DeleteEndpoint(Guid.NewGuid()));

        var unauthorizedCreate = new EndpointsController(
            service,
            NoScope(),
            NullLogger<EndpointsController>.Instance);
        Assert.IsType<UnauthorizedResult>(await unauthorizedCreate.CreateEndpoint(
            Guid.NewGuid(),
            new CreateEndpointRequest { Name = "missing", Path = "/missing" }));
        Assert.IsType<UnauthorizedResult>(await unauthorizedCreate.UpdateEndpoint(
            Guid.NewGuid(),
            new UpdateEndpointRequest { Name = "missing", Path = "/missing" }));

        var invalid = new EndpointsController(
            service,
            Scope(),
            NullLogger<EndpointsController>.Instance);
        invalid.ModelState.AddModelError("TimeoutSeconds", "invalid");
        Assert.IsType<BadRequestObjectResult>(await invalid.CreateEndpoint(
            Guid.NewGuid(),
            new CreateEndpointRequest { Name = "invalid", Path = "/invalid" }));
        Assert.IsType<BadRequestObjectResult>(await invalid.UpdateEndpoint(
            Guid.NewGuid(),
            new UpdateEndpointRequest { Name = "invalid", Path = "/invalid" }));
    }

    private static Project Project() => new()
    {
        Id = Guid.NewGuid(),
        Name = "project",
        Description = "description",
        OwnerUserId = "owner"
    };

    private static PromptlyEnvironment Environment() => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Guid.NewGuid(),
        Name = "environment",
        BaseUrl = "https://example.test",
        DefaultHeadersEncryptedJson = "ciphertext"
    };

    private static Endpoint Endpoint() => new()
    {
        Id = Guid.NewGuid(),
        EnvironmentId = Guid.NewGuid(),
        Name = "endpoint",
        Path = "/chat",
        HttpMethod = "POST",
        TimeoutSeconds = 30
    };

    private static StubScopeAccessor Scope() =>
        new(new TenantAccessScope("owner", ProjectId: null));

    private static StubScopeAccessor NoScope() => new(scope: null);

    private sealed class StubScopeAccessor : ITenantAccessScopeAccessor
    {
        private readonly TenantAccessScope? _scope;

        public StubScopeAccessor(TenantAccessScope? scope) => _scope = scope;

        public bool TryGetScope([NotNullWhen(true)] out TenantAccessScope? scope)
        {
            scope = _scope;
            return scope != null;
        }
    }

    private sealed class ConfigurableProjectService : IProjectService
    {
        public Project? Project { get; init; }
        public bool DeleteResult { get; init; }

        public Task<IReadOnlyList<Project>> GetProjectsAsync(TenantAccessScope scope) =>
            Task.FromResult<IReadOnlyList<Project>>(Project == null ? [] : [Project]);

        public Task<Project?> GetProjectByIdAsync(Guid projectId, TenantAccessScope scope) =>
            Task.FromResult(Project);

        public Task<Project?> CreateProjectAsync(
            string name,
            string? description,
            TenantAccessScope scope) =>
            Task.FromResult(Project);

        public Task<Project?> UpdateProjectAsync(
            Guid projectId,
            string name,
            string? description,
            TenantAccessScope scope) =>
            Task.FromResult(Project == null
                ? null
                : new Project
                {
                    Id = Project.Id,
                    Name = name,
                    Description = description,
                    OwnerUserId = Project.OwnerUserId,
                    CreatedAt = Project.CreatedAt
                });

        public Task<bool> DeleteProjectAsync(Guid projectId, TenantAccessScope scope) =>
            Task.FromResult(DeleteResult);
    }

    private sealed class ConfigurableEnvironmentService : IEnvironmentService
    {
        public IReadOnlyList<PromptlyEnvironment>? Environments { get; init; }
        public PromptlyEnvironment? Environment { get; init; }
        public Dictionary<string, string>? Headers { get; init; }
        public bool DeleteResult { get; init; }

        public Task<IReadOnlyList<PromptlyEnvironment>?> GetEnvironmentsByProjectAsync(
            Guid projectId,
            TenantAccessScope scope) => Task.FromResult(Environments);

        public Task<PromptlyEnvironment?> GetEnvironmentByIdAsync(
            Guid environmentId,
            TenantAccessScope scope) => Task.FromResult(Environment);

        public Task<PromptlyEnvironment?> CreateEnvironmentAsync(
            Guid projectId,
            string name,
            string baseUrl,
            Dictionary<string, string>? headers,
            TenantAccessScope scope) => Task.FromResult(Environment);

        public Task<PromptlyEnvironment?> UpdateEnvironmentAsync(
            Guid environmentId,
            string name,
            string baseUrl,
            Dictionary<string, string>? headers,
            TenantAccessScope scope) => Task.FromResult(Environment);

        public Task<bool> DeleteEnvironmentAsync(
            Guid environmentId,
            TenantAccessScope scope) => Task.FromResult(DeleteResult);

        public Task<Dictionary<string, string>?> GetDecryptedHeadersAsync(
            Guid environmentId,
            TenantAccessScope scope) => Task.FromResult(Headers);
    }

    private sealed class ConfigurableEndpointService : IEndpointService
    {
        public IReadOnlyList<Endpoint>? Endpoints { get; init; }
        public Endpoint? Endpoint { get; init; }
        public bool DeleteResult { get; init; }

        public Task<IReadOnlyList<Endpoint>?> GetEndpointsByEnvironmentAsync(
            Guid environmentId,
            TenantAccessScope scope) => Task.FromResult(Endpoints);

        public Task<Endpoint?> GetEndpointByIdAsync(
            Guid endpointId,
            TenantAccessScope scope) => Task.FromResult(Endpoint);

        public Task<Endpoint?> CreateEndpointAsync(
            Guid environmentId,
            string name,
            string path,
            string httpMethod,
            int timeoutSeconds,
            TenantAccessScope scope) => Task.FromResult(Endpoint);

        public Task<Endpoint?> UpdateEndpointAsync(
            Guid endpointId,
            string name,
            string path,
            string httpMethod,
            int timeoutSeconds,
            TenantAccessScope scope) => Task.FromResult(Endpoint);

        public Task<bool> DeleteEndpointAsync(Guid endpointId, TenantAccessScope scope) =>
            Task.FromResult(DeleteResult);
    }
}
