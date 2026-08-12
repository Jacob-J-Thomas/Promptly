using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;
using Promptly.Server.Controllers;
using Environment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.UnitTests;

public sealed class TenantRunServiceTests
{
    [Theory]
    [InlineData("suite")]
    [InlineData("environment")]
    [InlineData("endpoint")]
    [InlineData("mapping")]
    public async Task Queue_rejects_each_individually_foreign_resource_without_inserting(
        string foreignResource)
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var scope = new TenantAccessScope(graph.OwnerId, ProjectId: null);
        var runCountBefore = await dbContext.TestRuns.CountAsync(
            TestContext.Current.CancellationToken);

        var queued = await service.QueueRunAsync(
            foreignResource == "suite" ? graph.ForeignSuite.Id : graph.AllowedSuite.Id,
            foreignResource == "environment"
                ? graph.ForeignEnvironment.Id
                : graph.AllowedEnvironment.Id,
            foreignResource == "endpoint" ? graph.ForeignEndpoint.Id : graph.AllowedEndpoint.Id,
            foreignResource == "mapping" ? graph.ForeignMapping.Id : graph.AllowedMapping.Id,
            gitCommitHash: null,
            configSnapshotJson: null,
            scope);

        Assert.Null(queued);
        Assert.Equal(
            runCountBefore,
            await dbContext.TestRuns.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("suite/environment", false)]
    [InlineData("suite/environment", true)]
    [InlineData("environment/endpoint", false)]
    [InlineData("environment/endpoint", true)]
    [InlineData("endpoint/mapping", false)]
    [InlineData("endpoint/mapping", true)]
    public async Task Queue_rejects_each_incoherent_graph_relationship_without_inserting(
        string mismatch,
        bool crossOwner)
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var scope = new TenantAccessScope(graph.OwnerId, ProjectId: null);
        var runCountBefore = await dbContext.TestRuns.CountAsync(
            TestContext.Current.CancellationToken);

        var mismatchedEnvironment = crossOwner
            ? graph.ForeignEnvironment
            : graph.SiblingEnvironment;
        var mismatchedEndpoint = crossOwner
            ? graph.ForeignEndpoint
            : graph.SiblingEndpoint;
        var mismatchedMapping = crossOwner
            ? graph.ForeignMapping
            : graph.SiblingMapping;

        var environment = mismatch == "suite/environment"
            ? mismatchedEnvironment
            : graph.AllowedEnvironment;
        var endpoint = mismatch is "suite/environment" or "environment/endpoint"
            ? mismatchedEndpoint
            : graph.AllowedEndpoint;
        var mapping = mismatch is "suite/environment" or "environment/endpoint" or "endpoint/mapping"
            ? mismatchedMapping
            : graph.AllowedMapping;

        var queued = await service.QueueRunAsync(
            graph.AllowedSuite.Id,
            environment.Id,
            endpoint.Id,
            mapping.Id,
            gitCommitHash: null,
            configSnapshotJson: null,
            scope);

        Assert.Null(queued);
        Assert.Equal(
            runCountBefore,
            await dbContext.TestRuns.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Queue_allows_a_coherent_same_owner_sibling_graph_for_a_jwt_scope()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var scope = new TenantAccessScope(graph.OwnerId, ProjectId: null);

        var queued = await service.QueueRunAsync(
            graph.SiblingSuite.Id,
            graph.SiblingEnvironment.Id,
            graph.SiblingEndpoint.Id,
            graph.SiblingMapping.Id,
            gitCommitHash: null,
            configSnapshotJson: null,
            scope);

        Assert.NotNull(queued);
        Assert.Equal(graph.SiblingSuite.Id, queued.SuiteId);
        Assert.Equal(graph.SiblingEnvironment.Id, queued.EnvironmentId);
        Assert.Equal(graph.SiblingEndpoint.Id, queued.EndpointId);
        Assert.Equal(graph.SiblingMapping.Id, queued.MappingSpecId);
    }

    [Fact]
    public async Task Queue_derives_the_creator_from_the_validated_scope()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var scope = new TenantAccessScope(graph.OwnerId, ProjectId: null);

        var queued = await service.QueueRunAsync(
            graph.AllowedSuite.Id,
            graph.AllowedEnvironment.Id,
            graph.AllowedEndpoint.Id,
            graph.AllowedMapping.Id,
            "abc123",
            "{}",
            scope);

        Assert.NotNull(queued);
        Assert.Equal(graph.OwnerId, queued.CreatedByUserId);
        Assert.Equal(TestRunStatus.Queued, queued.Status);
        Assert.Contains(dbContext.TestRuns, run => run.Id == queued.Id);
    }

    [Fact]
    public async Task Api_key_scope_denies_same_owner_sibling_runs_and_results()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProject.Id);

        var queued = await service.QueueRunAsync(
            graph.SiblingSuite.Id,
            graph.SiblingEnvironment.Id,
            graph.SiblingEndpoint.Id,
            graph.SiblingMapping.Id,
            null,
            null,
            scope);
        var run = await service.GetRunByIdAsync(graph.SiblingRun.Id, scope);
        var runs = await service.GetRunsBySuiteAsync(
            graph.SiblingSuite.Id,
            status: null,
            limit: null,
            scope);
        var results = await service.GetRunResultsAsync(graph.SiblingRun.Id, scope);
        var result = await service.GetRunResultAsync(
            graph.SiblingRun.Id,
            graph.SiblingResult.Id,
            scope);

        Assert.Null(queued);
        Assert.Null(run);
        Assert.Null(runs);
        Assert.Null(results);
        Assert.Null(result);
    }

    [Fact]
    public async Task Run_list_distinguishes_an_owned_empty_suite_from_an_inaccessible_suite()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var scope = new TenantAccessScope(graph.OwnerId, ProjectId: null);

        var ownedEmpty = await service.GetRunsBySuiteAsync(
            graph.EmptySuite.Id,
            status: null,
            limit: null,
            scope);
        var inaccessible = await service.GetRunsBySuiteAsync(
            graph.ForeignSuite.Id,
            status: null,
            limit: null,
            scope);

        Assert.NotNull(ownedEmpty);
        Assert.Empty(ownedEmpty);
        Assert.Null(inaccessible);
    }

    [Fact]
    public async Task Results_are_scoped_ordered_and_constrained_to_the_parent_run()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = CreateService(dbContext);
        var scope = new TenantAccessScope(graph.OwnerId, ProjectId: null);

        var results = await service.GetRunResultsAsync(graph.AllowedRun.Id, scope);
        var detail = await service.GetRunResultAsync(
            graph.AllowedRun.Id,
            graph.AllowedErrorResult.Id,
            scope);
        var wrongParent = await service.GetRunResultAsync(
            graph.EmptyRun.Id,
            graph.AllowedErrorResult.Id,
            scope);
        var foreign = await service.GetRunResultsAsync(graph.ForeignRun.Id, scope);

        Assert.NotNull(results);
        Assert.Equal(
            [graph.AllowedErrorResult.Id, graph.AllowedPassResult.Id],
            results.Select(result => result.Id));
        Assert.NotNull(detail);
        Assert.Equal(graph.AllowedErrorCase.Name, detail.TestCase?.Name);
        Assert.Null(wrongParent);
        Assert.Null(foreign);
    }

    [Fact]
    public void Run_HTTP_contracts_are_scope_last_and_controllers_cannot_depend_on_worker_storage()
    {
        Assert.All(
            typeof(ITestRunService).GetMethods(),
            method => Assert.Equal(
                typeof(TenantAccessScope),
                method.GetParameters()[^1].ParameterType));

        var controllerParameters = typeof(RunsController).Assembly
            .GetTypes()
            .Where(type => !type.IsAbstract && typeof(ControllerBase).IsAssignableFrom(type))
            .SelectMany(type => type.GetConstructors())
            .SelectMany(constructor => constructor.GetParameters())
            .ToList();

        Assert.DoesNotContain(
            controllerParameters,
            parameter => parameter.ParameterType == typeof(PromptlyDbContext));
        Assert.DoesNotContain(
            controllerParameters,
            parameter => parameter.ParameterType == typeof(ITestRunWorkerStore));
    }

    [Fact]
    public async Task Worker_store_preserves_oldest_queue_claim_and_status_updates()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var older = Run(
            graph.AllowedSuite,
            graph.AllowedEnvironment,
            graph.AllowedEndpoint,
            graph.AllowedMapping,
            graph.OwnerId);
        older.Status = TestRunStatus.Queued;
        older.CreatedAt = DateTime.UtcNow.AddMinutes(-2);
        var newer = Run(
            graph.AllowedSuite,
            graph.AllowedEnvironment,
            graph.AllowedEndpoint,
            graph.AllowedMapping,
            graph.OwnerId);
        newer.Status = TestRunStatus.Queued;
        newer.CreatedAt = DateTime.UtcNow.AddMinutes(-1);
        dbContext.AddRange(older, newer);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var store = new TestRunWorkerStore(
            dbContext,
            NullLogger<TestRunWorkerStore>.Instance);

        var claimed = await store.ClaimNextQueuedRunAsync();
        await store.UpdateRunStatusAsync(
            older.Id,
            TestRunStatus.Completed,
            summaryJson: "{\"total\":0}");
        var loaded = await store.GetRunByIdAsync(older.Id);

        Assert.NotNull(claimed);
        Assert.Equal(older.Id, claimed.Id);
        Assert.NotNull(claimed.StartedAt);
        Assert.NotNull(loaded);
        Assert.Equal(TestRunStatus.Completed, loaded.Status);
        Assert.Equal("{\"total\":0}", loaded.SummaryJson);
        Assert.NotNull(loaded.CompletedAt);
        Assert.NotNull(loaded.Suite);
        Assert.NotNull(loaded.Environment);
        Assert.NotNull(loaded.Endpoint);
        Assert.NotNull(loaded.MappingSpec);
    }

    private static PromptlyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase($"promptly-tenant-run-tests-{Guid.NewGuid():N}")
            .ConfigureWarnings(warnings =>
                warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new PromptlyDbContext(options);
    }

    private static TestRunService CreateService(PromptlyDbContext dbContext) =>
        new(dbContext, NullLogger<TestRunService>.Instance);

    private static async Task<TenantRunGraph> SeedAsync(PromptlyDbContext dbContext)
    {
        const string ownerId = "owner";
        const string otherOwnerId = "other-owner";
        var owner = User(ownerId, "owner@example.test");
        var otherOwner = User(otherOwnerId, "other@example.test");

        var allowedProject = Project(owner, "allowed");
        var siblingProject = Project(owner, "sibling");
        var foreignProject = Project(otherOwner, "foreign");

        var allowedSuite = Suite(allowedProject, "allowed suite");
        var emptySuite = Suite(allowedProject, "empty suite");
        var siblingSuite = Suite(siblingProject, "sibling suite");
        var foreignSuite = Suite(foreignProject, "foreign suite");

        var allowedEnvironment = Environment(allowedProject, "allowed environment");
        var siblingEnvironment = Environment(siblingProject, "sibling environment");
        var foreignEnvironment = Environment(foreignProject, "foreign environment");

        var allowedEndpoint = Endpoint(allowedEnvironment, "allowed endpoint");
        var siblingEndpoint = Endpoint(siblingEnvironment, "sibling endpoint");
        var foreignEndpoint = Endpoint(foreignEnvironment, "foreign endpoint");

        var allowedMapping = Mapping(allowedEndpoint, "allowed mapping");
        var siblingMapping = Mapping(siblingEndpoint, "sibling mapping");
        var foreignMapping = Mapping(foreignEndpoint, "foreign mapping");

        var allowedRun = Run(
            allowedSuite,
            allowedEnvironment,
            allowedEndpoint,
            allowedMapping,
            ownerId);
        var emptyRun = Run(
            allowedSuite,
            allowedEnvironment,
            allowedEndpoint,
            allowedMapping,
            ownerId);
        var siblingRun = Run(
            siblingSuite,
            siblingEnvironment,
            siblingEndpoint,
            siblingMapping,
            ownerId);
        var foreignRun = Run(
            foreignSuite,
            foreignEnvironment,
            foreignEndpoint,
            foreignMapping,
            otherOwnerId);

        var allowedPassCase = TestCase(allowedSuite, "allowed pass");
        var allowedErrorCase = TestCase(allowedSuite, "allowed error");
        var siblingCase = TestCase(siblingSuite, "sibling result");
        var foreignCase = TestCase(foreignSuite, "foreign result");

        var allowedPassResult = Result(
            allowedRun,
            allowedPassCase,
            TestResultStatus.Pass);
        var allowedErrorResult = Result(
            allowedRun,
            allowedErrorCase,
            TestResultStatus.Error);
        var siblingResult = Result(siblingRun, siblingCase, TestResultStatus.Pass);
        var foreignResult = Result(foreignRun, foreignCase, TestResultStatus.Pass);

        dbContext.AddRange(
            owner,
            otherOwner,
            allowedProject,
            siblingProject,
            foreignProject,
            allowedSuite,
            emptySuite,
            siblingSuite,
            foreignSuite,
            allowedEnvironment,
            siblingEnvironment,
            foreignEnvironment,
            allowedEndpoint,
            siblingEndpoint,
            foreignEndpoint,
            allowedMapping,
            siblingMapping,
            foreignMapping,
            allowedRun,
            emptyRun,
            siblingRun,
            foreignRun,
            allowedPassCase,
            allowedErrorCase,
            siblingCase,
            foreignCase,
            allowedPassResult,
            allowedErrorResult,
            siblingResult,
            foreignResult);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new TenantRunGraph(
            ownerId,
            allowedProject,
            allowedSuite,
            emptySuite,
            siblingSuite,
            foreignSuite,
            allowedEnvironment,
            siblingEnvironment,
            foreignEnvironment,
            allowedEndpoint,
            siblingEndpoint,
            foreignEndpoint,
            allowedMapping,
            siblingMapping,
            foreignMapping,
            allowedRun,
            emptyRun,
            siblingRun,
            foreignRun,
            allowedPassResult,
            allowedErrorResult,
            siblingResult,
            allowedErrorCase);
    }

    private static User User(string id, string email) => new()
    {
        Id = id,
        UserName = id,
        Email = email
    };

    private static Project Project(User owner, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        OwnerUserId = owner.Id,
        Owner = owner
    };

    private static TestSuite Suite(Project project, string name) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = project.Id,
        Project = project,
        Name = name
    };

    private static Environment Environment(Project project, string name) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = project.Id,
        Project = project,
        Name = name,
        BaseUrl = "https://example.test"
    };

    private static Endpoint Endpoint(Environment environment, string name) => new()
    {
        Id = Guid.NewGuid(),
        EnvironmentId = environment.Id,
        Environment = environment,
        Name = name,
        Path = "/chat"
    };

    private static MappingSpec Mapping(Endpoint endpoint, string name) => new()
    {
        Id = Guid.NewGuid(),
        EndpointId = endpoint.Id,
        Endpoint = endpoint,
        Name = name,
        SpecJson = "{}"
    };

    private static TestRun Run(
        TestSuite suite,
        Environment environment,
        Endpoint endpoint,
        MappingSpec mapping,
        string creatorId) => new()
        {
            Id = Guid.NewGuid(),
            SuiteId = suite.Id,
            Suite = suite,
            EnvironmentId = environment.Id,
            Environment = environment,
            EndpointId = endpoint.Id,
            Endpoint = endpoint,
            MappingSpecId = mapping.Id,
            MappingSpec = mapping,
            CreatedByUserId = creatorId,
            Status = TestRunStatus.Completed
        };

    private static TestCase TestCase(TestSuite suite, string name) => new()
    {
        Id = Guid.NewGuid(),
        SuiteId = suite.Id,
        Suite = suite,
        ExternalId = name,
        Name = name,
        InputSpecJson = "{}",
        ExpectationsJson = "[]"
    };

    private static TestRunResult Result(
        TestRun run,
        TestCase testCase,
        TestResultStatus status) => new()
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            TestRun = run,
            TestCaseId = testCase.Id,
            TestCase = testCase,
            Status = status,
            TraceJson = "{}"
        };

    private sealed record TenantRunGraph(
        string OwnerId,
        Project AllowedProject,
        TestSuite AllowedSuite,
        TestSuite EmptySuite,
        TestSuite SiblingSuite,
        TestSuite ForeignSuite,
        Environment AllowedEnvironment,
        Environment SiblingEnvironment,
        Environment ForeignEnvironment,
        Endpoint AllowedEndpoint,
        Endpoint SiblingEndpoint,
        Endpoint ForeignEndpoint,
        MappingSpec AllowedMapping,
        MappingSpec SiblingMapping,
        MappingSpec ForeignMapping,
        TestRun AllowedRun,
        TestRun EmptyRun,
        TestRun SiblingRun,
        TestRun ForeignRun,
        TestRunResult AllowedPassResult,
        TestRunResult AllowedErrorResult,
        TestRunResult SiblingResult,
        TestCase AllowedErrorCase);
}
