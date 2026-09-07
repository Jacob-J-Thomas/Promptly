using Microsoft.EntityFrameworkCore;
using Promptly.Application.Data;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;
using PromptlyEnvironment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.UnitTests;

public sealed class TenantOwnedQueriesTests
{
    [Fact]
    public async Task Jwt_owner_scope_includes_sibling_projects_and_excludes_other_owners()
    {
        await using var dbContext = CreateDbContext();
        var graphs = await SeedTenantGraphsAsync(dbContext);
        var scope = new TenantAccessScope(graphs.OwnerPrimary.OwnerUserId, ProjectId: null);

        await AssertVisibleGraphsAsync(
            dbContext,
            scope,
            graphs.OwnerPrimary,
            graphs.OwnerSibling);
    }

    [Fact]
    public async Task Api_key_scope_includes_only_its_exact_project()
    {
        await using var dbContext = CreateDbContext();
        var graphs = await SeedTenantGraphsAsync(dbContext);
        var scope = new TenantAccessScope(
            graphs.OwnerPrimary.OwnerUserId,
            graphs.OwnerPrimary.ProjectId);

        await AssertVisibleGraphsAsync(dbContext, scope, graphs.OwnerPrimary);
    }

    [Fact]
    public async Task Run_queries_fail_closed_for_legacy_incoherent_rows()
    {
        await using var dbContext = CreateDbContext();
        var graphs = await SeedTenantGraphsAsync(dbContext);
        var mixedRun = new TestRun
        {
            Id = Guid.NewGuid(),
            SuiteId = graphs.OwnerPrimary.TestSuiteId,
            EnvironmentId = graphs.OwnerSibling.EnvironmentId,
            EndpointId = graphs.OwnerSibling.EndpointId,
            MappingSpecId = graphs.OwnerSibling.MappingSpecId,
            CreatedByUserId = graphs.OwnerPrimary.OwnerUserId,
            Status = TestRunStatus.Queued
        };
        var foreignCreatorRun = new TestRun
        {
            Id = Guid.NewGuid(),
            SuiteId = graphs.OwnerPrimary.TestSuiteId,
            EnvironmentId = graphs.OwnerPrimary.EnvironmentId,
            EndpointId = graphs.OwnerPrimary.EndpointId,
            MappingSpecId = graphs.OwnerPrimary.MappingSpecId,
            CreatedByUserId = graphs.OtherOwner.OwnerUserId,
            Status = TestRunStatus.Queued
        };
        var mixedRunResult = new TestRunResult
        {
            Id = Guid.NewGuid(),
            RunId = mixedRun.Id,
            TestCaseId = graphs.OwnerPrimary.TestCaseId,
            Status = TestResultStatus.Pass,
            TraceJson = "{\"legacy\":\"mixed-run\"}"
        };
        var foreignCreatorResult = new TestRunResult
        {
            Id = Guid.NewGuid(),
            RunId = foreignCreatorRun.Id,
            TestCaseId = graphs.OwnerPrimary.TestCaseId,
            Status = TestResultStatus.Pass,
            TraceJson = "{\"legacy\":\"foreign-creator\"}"
        };
        var wrongSuiteResult = new TestRunResult
        {
            Id = Guid.NewGuid(),
            RunId = graphs.OwnerPrimary.TestRunId,
            TestCaseId = graphs.OwnerSibling.TestCaseId,
            Status = TestResultStatus.Pass,
            TraceJson = "{\"legacy\":\"wrong-suite\"}"
        };
        dbContext.AddRange(
            mixedRun,
            foreignCreatorRun,
            mixedRunResult,
            foreignCreatorResult,
            wrongSuiteResult);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        dbContext.ChangeTracker.Clear();
        var jwtScope = new TenantAccessScope(
            graphs.OwnerPrimary.OwnerUserId,
            ProjectId: null);
        var apiKeyScope = new TenantAccessScope(
            graphs.OwnerPrimary.OwnerUserId,
            graphs.OwnerPrimary.ProjectId);

        foreach (var scope in new[] { jwtScope, apiKeyScope })
        {
            var visibleRunIds = await dbContext.TestRuns
                .ForTenant(scope)
                .Select(run => run.Id)
                .ToArrayAsync(TestContext.Current.CancellationToken);
            var visibleResultIds = await dbContext.TestRunResults
                .ForTenant(scope)
                .Select(result => result.Id)
                .ToArrayAsync(TestContext.Current.CancellationToken);

            Assert.DoesNotContain(mixedRun.Id, visibleRunIds);
            Assert.DoesNotContain(foreignCreatorRun.Id, visibleRunIds);
            Assert.DoesNotContain(mixedRunResult.Id, visibleResultIds);
            Assert.DoesNotContain(foreignCreatorResult.Id, visibleResultIds);
            Assert.DoesNotContain(wrongSuiteResult.Id, visibleResultIds);
        }
    }

    private static async Task AssertVisibleGraphsAsync(
        PromptlyDbContext dbContext,
        TenantAccessScope scope,
        params TenantGraphIds[] expectedGraphs)
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        Assert.Equal(
            Ordered(expectedGraphs.Select(graph => graph.ProjectId)),
            await dbContext.Projects.ForTenant(scope)
                .Select(project => project.Id)
                .OrderBy(id => id)
                .ToArrayAsync(cancellationToken));
        Assert.Equal(
            Ordered(expectedGraphs.Select(graph => graph.EnvironmentId)),
            await dbContext.Environments.ForTenant(scope)
                .Select(environment => environment.Id)
                .OrderBy(id => id)
                .ToArrayAsync(cancellationToken));
        Assert.Equal(
            Ordered(expectedGraphs.Select(graph => graph.EndpointId)),
            await dbContext.Endpoints.ForTenant(scope)
                .Select(endpoint => endpoint.Id)
                .OrderBy(id => id)
                .ToArrayAsync(cancellationToken));
        Assert.Equal(
            Ordered(expectedGraphs.Select(graph => graph.TestSuiteId)),
            await dbContext.TestSuites.ForTenant(scope)
                .Select(testSuite => testSuite.Id)
                .OrderBy(id => id)
                .ToArrayAsync(cancellationToken));
        Assert.Equal(
            Ordered(expectedGraphs.Select(graph => graph.TestCaseId)),
            await dbContext.TestCases.ForTenant(scope)
                .Select(testCase => testCase.Id)
                .OrderBy(id => id)
                .ToArrayAsync(cancellationToken));
        Assert.Equal(
            Ordered(expectedGraphs.Select(graph => graph.MappingSpecId)),
            await dbContext.MappingSpecs.ForTenant(scope)
                .Select(mappingSpec => mappingSpec.Id)
                .OrderBy(id => id)
                .ToArrayAsync(cancellationToken));
        Assert.Equal(
            Ordered(expectedGraphs.Select(graph => graph.TestRunId)),
            await dbContext.TestRuns.ForTenant(scope)
                .Select(testRun => testRun.Id)
                .OrderBy(id => id)
                .ToArrayAsync(cancellationToken));
        Assert.Equal(
            Ordered(expectedGraphs.Select(graph => graph.TestRunResultId)),
            await dbContext.TestRunResults.ForTenant(scope)
                .Select(testRunResult => testRunResult.Id)
                .OrderBy(id => id)
                .ToArrayAsync(cancellationToken));
    }

    private static Guid[] Ordered(IEnumerable<Guid> ids) => [.. ids.OrderBy(id => id)];

    private static PromptlyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase($"promptly-tenant-queries-{Guid.NewGuid():N}")
            .Options;
        return new PromptlyDbContext(options);
    }

    private static async Task<TenantGraphs> SeedTenantGraphsAsync(PromptlyDbContext dbContext)
    {
        var owner = new User
        {
            Id = "owner-user",
            UserName = "owner@example.test",
            Email = "owner@example.test"
        };
        var otherOwner = new User
        {
            Id = "other-owner-user",
            UserName = "other@example.test",
            Email = "other@example.test"
        };

        var ownerPrimary = AddTenantGraph(dbContext, owner, "owner-primary");
        var ownerSibling = AddTenantGraph(dbContext, owner, "owner-sibling");
        var otherOwnerGraph = AddTenantGraph(dbContext, otherOwner, "other-owner");
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        dbContext.ChangeTracker.Clear();

        return new TenantGraphs(ownerPrimary, ownerSibling, otherOwnerGraph);
    }

    private static TenantGraphIds AddTenantGraph(
        PromptlyDbContext dbContext,
        User owner,
        string name)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = name,
            OwnerUserId = owner.Id,
            Owner = owner
        };
        var environment = new PromptlyEnvironment
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Project = project,
            Name = $"{name}-environment",
            BaseUrl = "https://provider.example.test"
        };
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = environment.Id,
            Environment = environment,
            Name = $"{name}-endpoint",
            Path = "/v1/chat"
        };
        var mappingSpec = new MappingSpec
        {
            Id = Guid.NewGuid(),
            EndpointId = endpoint.Id,
            Endpoint = endpoint,
            Name = $"{name}-mapping",
            SpecJson = "{}"
        };
        var testSuite = new TestSuite
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Project = project,
            Name = $"{name}-suite"
        };
        var testCase = new TestCase
        {
            Id = Guid.NewGuid(),
            SuiteId = testSuite.Id,
            Suite = testSuite,
            ExternalId = $"{name}-case",
            Name = $"{name}-case",
            InputSpecJson = "{}",
            ExpectationsJson = "[]"
        };
        var testRun = new TestRun
        {
            Id = Guid.NewGuid(),
            SuiteId = testSuite.Id,
            Suite = testSuite,
            EnvironmentId = environment.Id,
            Environment = environment,
            EndpointId = endpoint.Id,
            Endpoint = endpoint,
            MappingSpecId = mappingSpec.Id,
            MappingSpec = mappingSpec,
            CreatedByUserId = owner.Id,
            CreatedBy = owner,
            Status = TestRunStatus.Queued
        };
        var testRunResult = new TestRunResult
        {
            Id = Guid.NewGuid(),
            RunId = testRun.Id,
            TestRun = testRun,
            TestCaseId = testCase.Id,
            TestCase = testCase,
            Status = TestResultStatus.Pass,
            TraceJson = "{}"
        };

        dbContext.AddRange(
            project,
            environment,
            endpoint,
            mappingSpec,
            testSuite,
            testCase,
            testRun,
            testRunResult);

        return new TenantGraphIds(
            owner.Id,
            project.Id,
            environment.Id,
            endpoint.Id,
            testSuite.Id,
            testCase.Id,
            mappingSpec.Id,
            testRun.Id,
            testRunResult.Id);
    }

    private sealed record TenantGraphs(
        TenantGraphIds OwnerPrimary,
        TenantGraphIds OwnerSibling,
        TenantGraphIds OtherOwner);

    private sealed record TenantGraphIds(
        string OwnerUserId,
        Guid ProjectId,
        Guid EnvironmentId,
        Guid EndpointId,
        Guid TestSuiteId,
        Guid TestCaseId,
        Guid MappingSpecId,
        Guid TestRunId,
        Guid TestRunResultId);
}
