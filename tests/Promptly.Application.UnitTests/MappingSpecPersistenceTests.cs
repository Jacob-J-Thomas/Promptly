using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Data;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using Promptly.Infrastructure.Services;
using PromptlyEnvironment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.UnitTests;

public sealed class MappingSpecPersistenceTests
{
    private const string ValidSpec =
        """{"version":1,"fallback":{"singleAssistantContentPath":"$.answer"}}""";

    [Fact]
    public async Task Save_and_get_preserve_the_public_mapping_spec_contract()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var graph = await SeedOwnedGraphAsync(dbContext);
        var scope = JwtScope(graph);
        var before = DateTime.UtcNow;

        var saved = await SaveAsync(
            service,
            graph.Endpoint.Id,
            "default",
            scope);
        var loaded = await service.GetMappingSpecByIdAsync(saved.Id, scope);

        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(graph.Endpoint.Id, saved.EndpointId);
        Assert.Equal("default", saved.Name);
        Assert.False(saved.IsDefault);
        Assert.InRange(saved.CreatedAt, before, DateTime.UtcNow);
        Assert.InRange(saved.UpdatedAt, before, DateTime.UtcNow);
        Assert.Same(saved, loaded);
    }

    [Fact]
    public async Task GetMappingSpecsByEndpointAsync_orders_the_default_first_then_newest()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var graph = await SeedOwnedGraphAsync(dbContext);
        var scope = JwtScope(graph);
        var otherEndpoint = await AddEndpointAsync(dbContext, graph.Environment, "other endpoint");
        var oldest = await SaveAsync(service, graph.Endpoint.Id, "oldest", scope);
        var newest = await SaveAsync(service, graph.Endpoint.Id, "newest", scope);
        var defaultSpec = await SaveAsync(service, graph.Endpoint.Id, "default", scope);
        await SaveAsync(service, otherEndpoint.Id, "other endpoint", scope);
        oldest.CreatedAt = DateTime.UtcNow.AddMinutes(-2);
        defaultSpec.CreatedAt = DateTime.UtcNow.AddMinutes(-1);
        newest.CreatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.True(await service.SetDefaultMappingAsync(defaultSpec.Id, scope));

        var specs = await service.GetMappingSpecsByEndpointAsync(graph.Endpoint.Id, scope);

        Assert.NotNull(specs);
        Assert.Collection(
            specs,
            spec => Assert.Equal("default", spec.Name),
            spec => Assert.Equal("newest", spec.Name),
            spec => Assert.Equal("oldest", spec.Name));
    }

    [Fact]
    public async Task SetDefaultMappingAsync_clears_only_defaults_for_the_same_endpoint()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var graph = await SeedOwnedGraphAsync(dbContext);
        var scope = JwtScope(graph);
        var otherEndpoint = await AddEndpointAsync(dbContext, graph.Environment, "other endpoint");
        var first = await SaveAsync(service, graph.Endpoint.Id, "first", scope);
        var replacement = await SaveAsync(service, graph.Endpoint.Id, "replacement", scope);
        var other = await SaveAsync(service, otherEndpoint.Id, "other", scope);
        Assert.True(await service.SetDefaultMappingAsync(first.Id, scope));
        Assert.True(await service.SetDefaultMappingAsync(other.Id, scope));

        Assert.True(await service.SetDefaultMappingAsync(replacement.Id, scope));

        Assert.False(first.IsDefault);
        Assert.True(replacement.IsDefault);
        Assert.True(other.IsDefault);
        Assert.Same(
            replacement,
            await service.GetDefaultMappingAsync(graph.Endpoint.Id, scope));
    }

    [Fact]
    public async Task UpdateMappingSpecAsync_updates_content_and_timestamp()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var graph = await SeedOwnedGraphAsync(dbContext);
        var scope = JwtScope(graph);
        var saved = await SaveAsync(service, graph.Endpoint.Id, "before", scope);
        saved.UpdatedAt = DateTime.UtcNow.AddDays(-1);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var priorUpdatedAt = saved.UpdatedAt;

        var updated = await service.UpdateMappingSpecAsync(
            saved.Id,
            "after",
            """{"version":1,"fallback":{"singleAssistantContentPath":"$.result"}}""",
            scope);

        Assert.NotNull(updated);
        Assert.Same(saved, updated);
        Assert.Equal("after", updated.Name);
        Assert.Equal(
            """{"version":1,"fallback":{"singleAssistantContentPath":"$.result"}}""",
            updated.SpecJson);
        Assert.True(updated.UpdatedAt > priorUpdatedAt);
    }

    [Fact]
    public async Task DeleteMappingSpecAsync_removes_the_spec()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var graph = await SeedOwnedGraphAsync(dbContext);
        var scope = JwtScope(graph);
        var saved = await SaveAsync(service, graph.Endpoint.Id, "delete", scope);

        Assert.True(await service.DeleteMappingSpecAsync(saved.Id, scope));

        Assert.Null(await service.GetMappingSpecByIdAsync(saved.Id, scope));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("set-default")]
    [InlineData("delete")]
    public async Task Mutation_methods_reject_unknown_mapping_specs(string operation)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var graph = await SeedOwnedGraphAsync(dbContext);
        var scope = JwtScope(graph);
        var missingId = Guid.NewGuid();

        var found = operation switch
        {
            "update" => await service.UpdateMappingSpecAsync(
                missingId,
                "name",
                ValidSpec,
                scope) != null,
            "set-default" => await service.SetDefaultMappingAsync(missingId, scope),
            "delete" => await service.DeleteMappingSpecAsync(missingId, scope),
            _ => throw new InvalidOperationException($"Unknown test operation {operation}")
        };

        Assert.False(found);
    }

    [Theory]
    [InlineData("{\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "version")]
    [InlineData("{\"version\":\"one\",\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "version")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":5}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":1,\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "mappingSpec")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$.\\ud800\"}}", "mappingSpec")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$[\\\"a\\tb\\\"]\"}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$['a\\u001fb']\"}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":1,\"messages\":{\"itemsPath\":\"$.items[9007199254740992]\"},\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "messages.itemsPath")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$[999999999999999999999999999999999999999]\"}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":null,\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "version")]
    [InlineData("{\"version\":2,\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}", "version")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$[\"}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$..answer\"}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$[-1]\"}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$[0:2]\"}}", "fallback.singleAssistantContentPath")]
    [InlineData("{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$[?(@.enabled)]\"}}", "fallback.singleAssistantContentPath")]
    public async Task SaveMappingSpecAsync_rejects_invalid_specs_without_persisting(
        string invalidSpec,
        string expectedPath)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var graph = await SeedOwnedGraphAsync(dbContext);
        var scope = JwtScope(graph);

        var exception = await Assert.ThrowsAsync<MappingSpecValidationException>(() =>
            service.SaveMappingSpecAsync(graph.Endpoint.Id, "invalid", invalidSpec, scope));

        Assert.Equal(expectedPath, exception.Path);
        Assert.Empty(dbContext.MappingSpecs);
    }

    [Fact]
    public async Task UpdateMappingSpecAsync_rejects_invalid_specs_without_mutating_the_spec()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var graph = await SeedOwnedGraphAsync(dbContext);
        var scope = JwtScope(graph);
        var saved = await SaveAsync(service, graph.Endpoint.Id, "original", scope);

        var exception = await Assert.ThrowsAsync<MappingSpecValidationException>(() =>
            service.UpdateMappingSpecAsync(
                saved.Id,
                "invalid update",
                """{"version":1,"messages":{"itemsPath":" "}}""",
                scope));

        Assert.Equal("messages.itemsPath", exception.Path);
        Assert.Equal("original", saved.Name);
        Assert.Equal(ValidSpec, saved.SpecJson);
        Assert.Equal(
            1,
            await dbContext.MappingSpecs.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Api_key_scope_denies_same_owner_sibling_mapping_access_without_side_effects()
    {
        await using var dbContext = CreateDbContext();
        var owner = new User
        {
            Id = "owner",
            UserName = "owner@example.test",
            Email = "owner@example.test"
        };
        dbContext.Users.Add(owner);
        var allowed = AddGraph(dbContext, owner, "allowed");
        var sibling = AddGraph(dbContext, owner, "sibling");
        var siblingSpec = new MappingSpec
        {
            Id = Guid.NewGuid(),
            Endpoint = sibling.Endpoint,
            EndpointId = sibling.Endpoint.Id,
            Name = "sibling mapping",
            SpecJson = ValidSpec,
            IsDefault = true
        };
        dbContext.MappingSpecs.Add(siblingSpec);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = CreateService(dbContext);
        var scope = new TenantAccessScope(owner.Id, allowed.Project.Id);

        var ownedEmpty = await service.GetMappingSpecsByEndpointAsync(
            allowed.Endpoint.Id,
            scope);
        var inaccessibleList = await service.GetMappingSpecsByEndpointAsync(
            sibling.Endpoint.Id,
            scope);
        var inaccessibleSpec = await service.GetMappingSpecByIdAsync(siblingSpec.Id, scope);
        var inaccessibleDefault = await service.GetDefaultMappingAsync(
            sibling.Endpoint.Id,
            scope);
        var created = await service.SaveMappingSpecAsync(
            sibling.Endpoint.Id,
            "intruder",
            "{}",
            scope);
        var updated = await service.UpdateMappingSpecAsync(
            siblingSpec.Id,
            "tampered",
            "{}",
            scope);
        var setDefault = await service.SetDefaultMappingAsync(siblingSpec.Id, scope);
        var deleted = await service.DeleteMappingSpecAsync(siblingSpec.Id, scope);

        Assert.NotNull(ownedEmpty);
        Assert.Empty(ownedEmpty);
        Assert.Null(inaccessibleList);
        Assert.Null(inaccessibleSpec);
        Assert.Null(inaccessibleDefault);
        Assert.Null(created);
        Assert.Null(updated);
        Assert.False(setDefault);
        Assert.False(deleted);
        var persisted = await dbContext.MappingSpecs.FindAsync(
            [siblingSpec.Id],
            TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal("sibling mapping", persisted.Name);
        Assert.Equal(ValidSpec, persisted.SpecJson);
        Assert.True(persisted.IsDefault);
        Assert.Single(dbContext.MappingSpecs);
    }

    private static async Task<MappingSpec> SaveAsync(
        MappingService service,
        Guid endpointId,
        string name,
        TenantAccessScope scope)
    {
        var saved = await service.SaveMappingSpecAsync(endpointId, name, ValidSpec, scope);
        return Assert.IsType<MappingSpec>(saved);
    }

    private static TenantAccessScope JwtScope(TenantMappingGraph graph) =>
        new(graph.Owner.Id, ProjectId: null);

    private static async Task<TenantMappingGraph> SeedOwnedGraphAsync(
        PromptlyDbContext dbContext)
    {
        var owner = new User
        {
            Id = "owner",
            UserName = "owner@example.test",
            Email = "owner@example.test"
        };
        dbContext.Users.Add(owner);
        var graph = AddGraph(dbContext, owner, "owned");
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return graph;
    }

    private static TenantMappingGraph AddGraph(
        PromptlyDbContext dbContext,
        User owner,
        string name)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Owner = owner,
            OwnerUserId = owner.Id,
            Name = $"{name} project"
        };
        var environment = new PromptlyEnvironment
        {
            Id = Guid.NewGuid(),
            Project = project,
            ProjectId = project.Id,
            Name = $"{name} environment",
            BaseUrl = "https://provider.example.test"
        };
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            Environment = environment,
            EnvironmentId = environment.Id,
            Name = $"{name} endpoint",
            Path = "/v1/chat"
        };
        dbContext.AddRange(project, environment, endpoint);
        return new TenantMappingGraph(owner, project, environment, endpoint);
    }

    private static async Task<Endpoint> AddEndpointAsync(
        PromptlyDbContext dbContext,
        PromptlyEnvironment environment,
        string name)
    {
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            Environment = environment,
            EnvironmentId = environment.Id,
            Name = name,
            Path = "/v1/other"
        };
        dbContext.Endpoints.Add(endpoint);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return endpoint;
    }

    private static PromptlyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase($"promptly-mapping-persistence-{Guid.NewGuid():N}")
            .Options;
        return new PromptlyDbContext(options);
    }

    private static MappingService CreateService(PromptlyDbContext dbContext) =>
        new(
            new JsonPathService(NullLogger<JsonPathService>.Instance),
            NullLogger<MappingService>.Instance,
            dbContext);

    private sealed record TenantMappingGraph(
        User Owner,
        Project Project,
        PromptlyEnvironment Environment,
        Endpoint Endpoint);
}
