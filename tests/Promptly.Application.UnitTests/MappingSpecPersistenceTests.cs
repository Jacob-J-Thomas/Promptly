using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Data;
using Promptly.Application.Services;
using Promptly.Infrastructure.Services;

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
        var endpointId = Guid.NewGuid();
        var before = DateTime.UtcNow;

        var saved = await service.SaveMappingSpecAsync(
            endpointId,
            "default",
            ValidSpec);
        var loaded = await service.GetMappingSpecByIdAsync(saved.Id);

        Assert.NotEqual(Guid.Empty, saved.Id);
        Assert.Equal(endpointId, saved.EndpointId);
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
        var endpointId = Guid.NewGuid();
        var oldest = await service.SaveMappingSpecAsync(endpointId, "oldest", ValidSpec);
        var newest = await service.SaveMappingSpecAsync(endpointId, "newest", ValidSpec);
        var defaultSpec = await service.SaveMappingSpecAsync(endpointId, "default", ValidSpec);
        await service.SaveMappingSpecAsync(Guid.NewGuid(), "other endpoint", ValidSpec);
        oldest.CreatedAt = DateTime.UtcNow.AddMinutes(-2);
        defaultSpec.CreatedAt = DateTime.UtcNow.AddMinutes(-1);
        newest.CreatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        await service.SetDefaultMappingAsync(defaultSpec.Id);

        var specs = await service.GetMappingSpecsByEndpointAsync(endpointId);

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
        var endpointId = Guid.NewGuid();
        var first = await service.SaveMappingSpecAsync(endpointId, "first", ValidSpec);
        var replacement = await service.SaveMappingSpecAsync(endpointId, "replacement", ValidSpec);
        var otherEndpoint = await service.SaveMappingSpecAsync(
            Guid.NewGuid(),
            "other",
            ValidSpec);
        await service.SetDefaultMappingAsync(first.Id);
        await service.SetDefaultMappingAsync(otherEndpoint.Id);

        await service.SetDefaultMappingAsync(replacement.Id);

        Assert.False(first.IsDefault);
        Assert.True(replacement.IsDefault);
        Assert.True(otherEndpoint.IsDefault);
        Assert.Same(replacement, await service.GetDefaultMappingAsync(endpointId));
    }

    [Fact]
    public async Task UpdateMappingSpecAsync_updates_content_and_timestamp()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var saved = await service.SaveMappingSpecAsync(Guid.NewGuid(), "before", ValidSpec);
        saved.UpdatedAt = DateTime.UtcNow.AddDays(-1);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var priorUpdatedAt = saved.UpdatedAt;

        var updated = await service.UpdateMappingSpecAsync(
            saved.Id,
            "after",
            """{"version":1,"fallback":{"singleAssistantContentPath":"$.result"}}""");

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
        var saved = await service.SaveMappingSpecAsync(Guid.NewGuid(), "delete", ValidSpec);

        await service.DeleteMappingSpecAsync(saved.Id);

        Assert.Null(await service.GetMappingSpecByIdAsync(saved.Id));
    }

    [Theory]
    [InlineData("update")]
    [InlineData("set-default")]
    [InlineData("delete")]
    public async Task Mutation_methods_reject_unknown_mapping_specs(string operation)
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var missingId = Guid.NewGuid();

        var exception = operation switch
        {
            "update" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.UpdateMappingSpecAsync(missingId, "name", ValidSpec)),
            "set-default" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.SetDefaultMappingAsync(missingId)),
            "delete" => await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.DeleteMappingSpecAsync(missingId)),
            _ => throw new InvalidOperationException($"Unknown test operation {operation}")
        };

        Assert.Contains(missingId.ToString(), exception.Message, StringComparison.Ordinal);
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

        var exception = await Assert.ThrowsAsync<MappingSpecValidationException>(() =>
            service.SaveMappingSpecAsync(Guid.NewGuid(), "invalid", invalidSpec));

        Assert.Equal(expectedPath, exception.Path);
        Assert.Empty(dbContext.MappingSpecs);
    }

    [Fact]
    public async Task UpdateMappingSpecAsync_rejects_invalid_specs_without_mutating_the_spec()
    {
        await using var dbContext = CreateDbContext();
        var service = CreateService(dbContext);
        var saved = await service.SaveMappingSpecAsync(Guid.NewGuid(), "original", ValidSpec);

        var exception = await Assert.ThrowsAsync<MappingSpecValidationException>(() =>
            service.UpdateMappingSpecAsync(
                saved.Id,
                "invalid update",
                """{"version":1,"messages":{"itemsPath":" "}}"""));

        Assert.Equal("messages.itemsPath", exception.Path);
        Assert.Equal("original", saved.Name);
        Assert.Equal(ValidSpec, saved.SpecJson);
        Assert.Equal(
            1,
            await dbContext.MappingSpecs.CountAsync(TestContext.Current.CancellationToken));
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
}
