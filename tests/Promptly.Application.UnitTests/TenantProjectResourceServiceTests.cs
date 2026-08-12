using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using PromptlyEnvironment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.UnitTests;

public sealed class TenantProjectResourceServiceTests
{
    [Fact]
    public async Task Api_key_scope_cannot_cross_into_same_owner_sibling_project()
    {
        await using var context = CreateContext();
        var graph = await SeedGraphAsync(context);
        var scope = new TenantAccessScope(graph.OwnerUserId, graph.FirstProjectId);
        var projectService = new ProjectService(context, NullLogger<ProjectService>.Instance);
        var encryption = new SpyEncryptionService();
        var environmentService = new EnvironmentService(
            context,
            encryption,
            NullLogger<EnvironmentService>.Instance);
        var endpointService = new EndpointService(context, NullLogger<EndpointService>.Instance);
        var projectCountBefore = await context.Projects.CountAsync(
            TestContext.Current.CancellationToken);

        var visibleProjects = await projectService.GetProjectsAsync(scope);

        Assert.Equal(graph.FirstProjectId, Assert.Single(visibleProjects).Id);
        Assert.Null(await projectService.CreateProjectAsync(
            "unauthorized project",
            "API keys cannot create projects",
            scope));
        Assert.Null(await projectService.GetProjectByIdAsync(graph.SecondProjectId, scope));
        Assert.Null(await projectService.UpdateProjectAsync(
            graph.SecondProjectId,
            "unauthorized update",
            null,
            scope));
        Assert.False(await projectService.DeleteProjectAsync(graph.SecondProjectId, scope));

        Assert.Null(await environmentService.GetEnvironmentsByProjectAsync(
            graph.SecondProjectId,
            scope));
        Assert.Null(await environmentService.GetEnvironmentByIdAsync(
            graph.SecondEnvironmentId,
            scope));
        Assert.Null(await environmentService.CreateEnvironmentAsync(
            graph.SecondProjectId,
            "unauthorized environment",
            "https://denied.example",
            new Dictionary<string, string> { ["Secret"] = "value" },
            scope));
        Assert.Null(await environmentService.UpdateEnvironmentAsync(
            graph.SecondEnvironmentId,
            "unauthorized update",
            "https://denied.example",
            new Dictionary<string, string> { ["Secret"] = "value" },
            scope));
        Assert.False(await environmentService.DeleteEnvironmentAsync(
            graph.SecondEnvironmentId,
            scope));

        Assert.Null(await endpointService.GetEndpointsByEnvironmentAsync(
            graph.SecondEnvironmentId,
            scope));
        Assert.Null(await endpointService.GetEndpointByIdAsync(graph.SecondEndpointId, scope));
        Assert.Null(await endpointService.CreateEndpointAsync(
            graph.SecondEnvironmentId,
            "unauthorized endpoint",
            "/denied",
            "POST",
            30,
            scope));
        Assert.Null(await endpointService.UpdateEndpointAsync(
            graph.SecondEndpointId,
            "unauthorized update",
            "/denied",
            "PUT",
            60,
            scope));
        Assert.False(await endpointService.DeleteEndpointAsync(graph.SecondEndpointId, scope));

        Assert.Equal(0, encryption.EncryptCallCount);
        Assert.Equal(
            projectCountBefore,
            await context.Projects.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal("second project", graph.SecondProject.Name);
        Assert.Equal("second environment", graph.SecondEnvironment.Name);
        Assert.Equal("second endpoint", graph.SecondEndpoint.Name);
    }

    [Fact]
    public async Task Parent_collections_distinguish_owned_empty_from_absent_or_inaccessible_parent()
    {
        await using var context = CreateContext();
        var graph = await SeedGraphAsync(context);
        var scope = new TenantAccessScope(graph.OwnerUserId, ProjectId: null);
        var environmentService = new EnvironmentService(
            context,
            new SpyEncryptionService(),
            NullLogger<EnvironmentService>.Instance);
        var endpointService = new EndpointService(context, NullLogger<EndpointService>.Instance);

        var ownedEmptyEnvironments = await environmentService.GetEnvironmentsByProjectAsync(
            graph.EmptyProjectId,
            scope);
        var ownedEmptyEndpoints = await endpointService.GetEndpointsByEnvironmentAsync(
            graph.EmptyEnvironmentId,
            scope);

        Assert.NotNull(ownedEmptyEnvironments);
        Assert.Empty(ownedEmptyEnvironments);
        Assert.NotNull(ownedEmptyEndpoints);
        Assert.Empty(ownedEmptyEndpoints);
        Assert.Null(await environmentService.GetEnvironmentsByProjectAsync(Guid.NewGuid(), scope));
        Assert.Null(await environmentService.GetEnvironmentsByProjectAsync(
            graph.OtherOwnerProjectId,
            scope));
        Assert.Null(await endpointService.GetEndpointsByEnvironmentAsync(Guid.NewGuid(), scope));
        Assert.Null(await endpointService.GetEndpointsByEnvironmentAsync(
            graph.OtherOwnerEnvironmentId,
            scope));
    }

    [Fact]
    public async Task Inaccessible_environment_never_decrypts_headers()
    {
        await using var context = CreateContext();
        var graph = await SeedGraphAsync(context);
        var encryption = new SpyEncryptionService
        {
            DecryptedValue = "{\"Authorization\":\"secret\"}"
        };
        var service = new EnvironmentService(
            context,
            encryption,
            NullLogger<EnvironmentService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerUserId, graph.FirstProjectId);

        var headers = await service.GetDecryptedHeadersAsync(graph.SecondEnvironmentId, scope);

        Assert.Null(headers);
        Assert.Equal(0, encryption.DecryptCallCount);
    }

    [Fact]
    public async Task Owned_environment_decrypts_headers_only_after_scoped_lookup_succeeds()
    {
        await using var context = CreateContext();
        var graph = await SeedGraphAsync(context);
        var encryption = new SpyEncryptionService
        {
            DecryptedValue = "{\"Authorization\":\"secret\"}"
        };
        var service = new EnvironmentService(
            context,
            encryption,
            NullLogger<EnvironmentService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerUserId, graph.FirstProjectId);

        var headers = await service.GetDecryptedHeadersAsync(graph.FirstEnvironmentId, scope);

        Assert.NotNull(headers);
        Assert.Equal("secret", headers["Authorization"]);
        Assert.Equal(1, encryption.DecryptCallCount);
    }

    private static PromptlyDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase($"tenant-project-resources-{Guid.NewGuid():N}")
            .Options;
        return new PromptlyDbContext(options);
    }

    private static async Task<SeededGraph> SeedGraphAsync(PromptlyDbContext context)
    {
        const string ownerUserId = "owner-user";
        const string otherOwnerUserId = "other-owner";
        var firstProject = Project(ownerUserId, "first project");
        var secondProject = Project(ownerUserId, "second project");
        var emptyProject = Project(ownerUserId, "empty project");
        var otherOwnerProject = Project(otherOwnerUserId, "other owner's project");
        var firstEnvironment = Environment(firstProject, "first environment", encrypted: true);
        var secondEnvironment = Environment(secondProject, "second environment", encrypted: true);
        // Keep one truly empty project and one environment with no endpoints so
        // both parent-collection contracts are exercised independently.
        var emptyEnvironment = Environment(firstProject, "empty environment", encrypted: false);
        var otherOwnerEnvironment = Environment(
            otherOwnerProject,
            "other owner's environment",
            encrypted: false);
        var firstEndpoint = Endpoint(firstEnvironment, "first endpoint");
        var secondEndpoint = Endpoint(secondEnvironment, "second endpoint");

        context.Users.AddRange(
            new User { Id = ownerUserId, UserName = "owner" },
            new User { Id = otherOwnerUserId, UserName = "other-owner" });
        context.Projects.AddRange(firstProject, secondProject, emptyProject, otherOwnerProject);
        context.Environments.AddRange(
            firstEnvironment,
            secondEnvironment,
            emptyEnvironment,
            otherOwnerEnvironment);
        context.Endpoints.AddRange(firstEndpoint, secondEndpoint);
        await context.SaveChangesAsync();

        return new SeededGraph(
            ownerUserId,
            firstProject.Id,
            secondProject.Id,
            emptyProject.Id,
            otherOwnerProject.Id,
            firstEnvironment.Id,
            secondEnvironment.Id,
            emptyEnvironment.Id,
            otherOwnerEnvironment.Id,
            secondEndpoint.Id,
            secondProject,
            secondEnvironment,
            secondEndpoint);
    }

    private static Project Project(string ownerUserId, string name) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerUserId = ownerUserId
        };

    private static PromptlyEnvironment Environment(
        Project project,
        string name,
        bool encrypted) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Project = project,
            Name = name,
            BaseUrl = "https://example.test",
            DefaultHeadersEncryptedJson = encrypted ? "encrypted" : null
        };

    private static Endpoint Endpoint(PromptlyEnvironment environment, string name) =>
        new()
        {
            Id = Guid.NewGuid(),
            EnvironmentId = environment.Id,
            Environment = environment,
            Name = name,
            Path = "/test"
        };

    private sealed record SeededGraph(
        string OwnerUserId,
        Guid FirstProjectId,
        Guid SecondProjectId,
        Guid EmptyProjectId,
        Guid OtherOwnerProjectId,
        Guid FirstEnvironmentId,
        Guid SecondEnvironmentId,
        Guid EmptyEnvironmentId,
        Guid OtherOwnerEnvironmentId,
        Guid SecondEndpointId,
        Project SecondProject,
        PromptlyEnvironment SecondEnvironment,
        Endpoint SecondEndpoint);

    private sealed class SpyEncryptionService : IEncryptionService
    {
        public int EncryptCallCount { get; private set; }
        public int DecryptCallCount { get; private set; }
        public string DecryptedValue { get; init; } = "{}";

        public string Encrypt(string plainText)
        {
            EncryptCallCount++;
            return $"encrypted:{plainText}";
        }

        public string Decrypt(string cipherText)
        {
            DecryptCallCount++;
            return DecryptedValue;
        }
    }
}
