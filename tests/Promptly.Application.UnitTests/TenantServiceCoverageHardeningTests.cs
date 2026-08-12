using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;
using PromptlyEnvironment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.UnitTests;

public sealed class TenantServiceCoverageHardeningTests
{
    [Fact]
    public async Task Project_service_persists_owned_create_update_and_delete()
    {
        await using var dbContext = CreateDbContext();
        var owner = CreateUser();
        dbContext.Users.Add(owner);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var scope = new TenantAccessScope(owner.Id, ProjectId: null);
        var service = new ProjectService(dbContext, NullLogger<ProjectService>.Instance);

        var created = await service.CreateProjectAsync(
            "created project",
            "initial description",
            scope);
        Assert.NotNull(created);
        var updated = await service.UpdateProjectAsync(
            created.Id,
            "updated project",
            "updated description",
            scope);
        var deleted = await service.DeleteProjectAsync(created.Id, scope);

        Assert.Equal(owner.Id, created.OwnerUserId);
        Assert.NotEqual(default, created.CreatedAt);
        Assert.Same(created, updated);
        var updatedProject = Assert.IsType<Project>(updated);
        Assert.Equal("updated project", updatedProject.Name);
        Assert.Equal("updated description", updatedProject.Description);
        Assert.True(deleted);
        Assert.Null(await service.GetProjectByIdAsync(created.Id, scope));
    }

    [Fact]
    public async Task Environment_service_handles_each_header_shape_during_owned_crud()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedProjectGraphAsync(dbContext);
        var encryption = new RecordingEncryptionService();
        var service = new EnvironmentService(
            dbContext,
            encryption,
            NullLogger<EnvironmentService>.Instance);
        var scope = new TenantAccessScope(graph.Owner.Id, ProjectId: null);

        var withoutHeaders = await service.CreateEnvironmentAsync(
            graph.Project.Id,
            "without headers",
            "https://none.example.test",
            headers: null,
            scope);
        var withEmptyHeaders = await service.CreateEnvironmentAsync(
            graph.Project.Id,
            "empty headers",
            "https://empty.example.test",
            new Dictionary<string, string>(),
            scope);
        var withHeaders = await service.CreateEnvironmentAsync(
            graph.Project.Id,
            "with headers",
            "https://headers.example.test",
            new Dictionary<string, string> { ["Authorization"] = "Bearer secret" },
            scope);

        Assert.NotNull(withoutHeaders);
        Assert.NotNull(withEmptyHeaders);
        Assert.NotNull(withHeaders);
        Assert.Null(withoutHeaders.DefaultHeadersEncryptedJson);
        Assert.Null(withEmptyHeaders.DefaultHeadersEncryptedJson);
        Assert.Contains("Authorization", withHeaders.DefaultHeadersEncryptedJson);
        Assert.Equal(1, encryption.EncryptCallCount);

        var populatedUpdate = await service.UpdateEnvironmentAsync(
            withoutHeaders.Id,
            "now populated",
            "https://populated.example.test",
            new Dictionary<string, string> { ["X-Correlation"] = "trace" },
            scope);
        var emptyUpdate = await service.UpdateEnvironmentAsync(
            withEmptyHeaders.Id,
            "still empty",
            "https://still-empty.example.test",
            new Dictionary<string, string>(),
            scope);
        var nullUpdate = await service.UpdateEnvironmentAsync(
            withHeaders.Id,
            "cleared",
            "https://cleared.example.test",
            headers: null,
            scope);

        Assert.NotNull(populatedUpdate);
        Assert.Contains("X-Correlation", populatedUpdate.DefaultHeadersEncryptedJson);
        Assert.NotNull(emptyUpdate);
        Assert.Null(emptyUpdate.DefaultHeadersEncryptedJson);
        Assert.NotNull(nullUpdate);
        Assert.Null(nullUpdate.DefaultHeadersEncryptedJson);
        Assert.Equal(2, encryption.EncryptCallCount);
        Assert.True(await service.DeleteEnvironmentAsync(withHeaders.Id, scope));
        Assert.Null(await service.GetEnvironmentByIdAsync(withHeaders.Id, scope));
    }

    [Fact]
    public async Task Environment_service_does_not_decrypt_blank_ciphertext()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedProjectGraphAsync(dbContext, encryptedHeaders: "   ");
        var encryption = new RecordingEncryptionService();
        var service = new EnvironmentService(
            dbContext,
            encryption,
            NullLogger<EnvironmentService>.Instance);
        var scope = new TenantAccessScope(graph.Owner.Id, ProjectId: null);

        var headers = await service.GetDecryptedHeadersAsync(graph.Environment.Id, scope);

        Assert.Null(headers);
        Assert.Equal(0, encryption.DecryptCallCount);
    }

    [Fact]
    public async Task Environment_service_propagates_decryption_failures_for_owned_data()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedProjectGraphAsync(dbContext, encryptedHeaders: "ciphertext");
        var encryption = new RecordingEncryptionService
        {
            DecryptionFailure = new InvalidOperationException("invalid ciphertext")
        };
        var service = new EnvironmentService(
            dbContext,
            encryption,
            NullLogger<EnvironmentService>.Instance);
        var scope = new TenantAccessScope(graph.Owner.Id, ProjectId: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.GetDecryptedHeadersAsync(graph.Environment.Id, scope));

        Assert.Equal("invalid ciphertext", exception.Message);
        Assert.Equal(1, encryption.DecryptCallCount);
    }

    [Fact]
    public async Task Endpoint_service_persists_owned_create_update_and_delete()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedProjectGraphAsync(dbContext);
        var service = new EndpointService(dbContext, NullLogger<EndpointService>.Instance);
        var scope = new TenantAccessScope(graph.Owner.Id, ProjectId: null);

        var created = await service.CreateEndpointAsync(
            graph.Environment.Id,
            "created endpoint",
            "/created",
            "POST",
            15,
            scope);
        Assert.NotNull(created);

        var updated = await service.UpdateEndpointAsync(
            created.Id,
            "updated endpoint",
            "/updated",
            "PUT",
            45,
            scope);
        var deleted = await service.DeleteEndpointAsync(created.Id, scope);

        Assert.Same(created, updated);
        var updatedEndpoint = Assert.IsType<Endpoint>(updated);
        Assert.Equal("updated endpoint", updatedEndpoint.Name);
        Assert.Equal("/updated", updatedEndpoint.Path);
        Assert.Equal("PUT", updatedEndpoint.HttpMethod);
        Assert.Equal(45, updatedEndpoint.TimeoutSeconds);
        Assert.True(deleted);
        Assert.Null(await service.GetEndpointByIdAsync(created.Id, scope));
    }

    [Fact]
    public async Task Run_list_applies_status_and_limit_together()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedRunGraphAsync(dbContext);
        var service = new TestRunService(dbContext, NullLogger<TestRunService>.Instance);
        var scope = new TenantAccessScope(graph.Owner.Id, ProjectId: null);

        var runs = await service.GetRunsBySuiteAsync(
            graph.Suite.Id,
            TestRunStatus.Completed,
            limit: 1,
            scope);

        var run = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<TestRun>>(runs));
        Assert.Equal(graph.NewerCompletedRun.Id, run.Id);
        Assert.Equal(TestRunStatus.Completed, run.Status);
    }

    [Fact]
    public async Task Worker_store_returns_null_when_no_run_is_queued()
    {
        await using var dbContext = CreateDbContext();
        await SeedRunGraphAsync(dbContext);
        var store = new TestRunWorkerStore(
            dbContext,
            NullLogger<TestRunWorkerStore>.Instance);

        var claimed = await store.ClaimNextQueuedRunAsync(
            TestContext.Current.CancellationToken);

        Assert.Null(claimed);
    }

    [Fact]
    public async Task Worker_store_updates_nonterminal_state_and_rejects_missing_runs()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedRunGraphAsync(dbContext);
        var store = new TestRunWorkerStore(
            dbContext,
            NullLogger<TestRunWorkerStore>.Instance);

        await store.UpdateRunStatusAsync(
            graph.NewerCompletedRun.Id,
            TestRunStatus.Running,
            summaryJson: "{\"progress\":1}",
            errorMessage: "retrying");

        Assert.Equal(TestRunStatus.Running, graph.NewerCompletedRun.Status);
        Assert.Equal("{\"progress\":1}", graph.NewerCompletedRun.SummaryJson);
        Assert.Equal("retrying", graph.NewerCompletedRun.ErrorMessage);
        Assert.Null(graph.NewerCompletedRun.CompletedAt);

        var missingId = Guid.NewGuid();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpdateRunStatusAsync(missingId, TestRunStatus.Failed));
        Assert.Contains(missingId.ToString(), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worker_store_rolls_back_and_propagates_claim_failures()
    {
        var interceptor = new ToggleSaveFailureInterceptor();
        await using var dbContext = CreateDbContext(interceptor);
        var graph = await SeedRunGraphAsync(dbContext);
        graph.OlderCompletedRun.Status = TestRunStatus.Queued;
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        interceptor.ShouldFail = true;
        var store = new TestRunWorkerStore(
            dbContext,
            NullLogger<TestRunWorkerStore>.Instance);

        var exception = await Assert.ThrowsAsync<SaveFailureException>(
            () => store.ClaimNextQueuedRunAsync(TestContext.Current.CancellationToken));

        Assert.Equal("forced save failure", exception.Message);
    }

    private static PromptlyDbContext CreateDbContext(
        SaveChangesInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase($"tenant-service-coverage-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings =>
                warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        if (interceptor != null)
        {
            options.AddInterceptors(interceptor);
        }

        return new PromptlyDbContext(options.Options);
    }

    private static User CreateUser() => new()
    {
        Id = $"owner-{Guid.NewGuid():N}",
        UserName = "coverage-owner",
        Email = "coverage-owner@example.test"
    };

    private static async Task<ProjectGraph> SeedProjectGraphAsync(
        PromptlyDbContext dbContext,
        string? encryptedHeaders = null)
    {
        var owner = CreateUser();
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "coverage project",
            OwnerUserId = owner.Id,
            Owner = owner
        };
        var environment = new PromptlyEnvironment
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Project = project,
            Name = "coverage environment",
            BaseUrl = "https://coverage.example.test",
            DefaultHeadersEncryptedJson = encryptedHeaders
        };
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = environment.Id,
            Environment = environment,
            Name = "coverage endpoint",
            Path = "/coverage"
        };

        dbContext.AddRange(owner, project, environment, endpoint);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new ProjectGraph(owner, project, environment, endpoint);
    }

    private static async Task<RunGraph> SeedRunGraphAsync(PromptlyDbContext dbContext)
    {
        var projectGraph = await SeedProjectGraphAsync(dbContext);
        var suite = new TestSuite
        {
            Id = Guid.NewGuid(),
            ProjectId = projectGraph.Project.Id,
            Project = projectGraph.Project,
            Name = "coverage suite"
        };
        var mapping = new MappingSpec
        {
            Id = Guid.NewGuid(),
            EndpointId = projectGraph.Endpoint.Id,
            Endpoint = projectGraph.Endpoint,
            Name = "coverage mapping",
            SpecJson = "{}"
        };
        var olderCompletedRun = CreateRun(
            projectGraph,
            suite,
            mapping,
            DateTime.UtcNow.AddMinutes(-2));
        var newerCompletedRun = CreateRun(
            projectGraph,
            suite,
            mapping,
            DateTime.UtcNow.AddMinutes(-1));

        dbContext.AddRange(suite, mapping, olderCompletedRun, newerCompletedRun);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new RunGraph(
            projectGraph.Owner,
            suite,
            olderCompletedRun,
            newerCompletedRun);
    }

    private static TestRun CreateRun(
        ProjectGraph graph,
        TestSuite suite,
        MappingSpec mapping,
        DateTime createdAt) => new()
        {
            Id = Guid.NewGuid(),
            ProjectId = suite.ProjectId,
            SuiteId = suite.Id,
            Suite = suite,
            EnvironmentId = graph.Environment.Id,
            Environment = graph.Environment,
            EndpointId = graph.Endpoint.Id,
            Endpoint = graph.Endpoint,
            MappingSpecId = mapping.Id,
            MappingSpec = mapping,
            Status = TestRunStatus.Completed,
            CreatedByUserId = graph.Owner.Id,
            CreatedBy = graph.Owner,
            CreatedAt = createdAt
        };

    private sealed record ProjectGraph(
        User Owner,
        Project Project,
        PromptlyEnvironment Environment,
        Endpoint Endpoint);

    private sealed record RunGraph(
        User Owner,
        TestSuite Suite,
        TestRun OlderCompletedRun,
        TestRun NewerCompletedRun);

    private sealed class RecordingEncryptionService : IEncryptionService
    {
        public int EncryptCallCount { get; private set; }
        public int DecryptCallCount { get; private set; }
        public Exception? DecryptionFailure { get; init; }

        public string Encrypt(string plainText)
        {
            EncryptCallCount++;
            return $"encrypted:{plainText}";
        }

        public string Decrypt(string cipherText)
        {
            DecryptCallCount++;
            if (DecryptionFailure != null)
            {
                throw DecryptionFailure;
            }

            return cipherText;
        }
    }

    private sealed class ToggleSaveFailureInterceptor : SaveChangesInterceptor
    {
        public bool ShouldFail { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (ShouldFail)
            {
                throw new SaveFailureException("forced save failure");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class SaveFailureException(string message) : Exception(message);
}
