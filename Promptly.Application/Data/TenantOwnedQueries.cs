using Promptly.Application.Models;
using Promptly.Domain.Entities;
using PromptlyEnvironment = Promptly.Domain.Entities.Environment;

namespace Promptly.Application.Data;

public static class TenantOwnedQueries
{
    public static IQueryable<Project> ForTenant(
        this IQueryable<Project> projects,
        TenantAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(scope);

        var ownerUserId = scope.OwnerUserId;
        var projectId = scope.ProjectId;
        return projects.Where(project =>
            project.OwnerUserId == ownerUserId
            && (!projectId.HasValue || project.Id == projectId.Value));
    }

    public static IQueryable<PromptlyEnvironment> ForTenant(
        this IQueryable<PromptlyEnvironment> environments,
        TenantAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(environments);
        ArgumentNullException.ThrowIfNull(scope);

        var ownerUserId = scope.OwnerUserId;
        var projectId = scope.ProjectId;
        return environments.Where(environment =>
            environment.Project!.OwnerUserId == ownerUserId
            && (!projectId.HasValue || environment.Project.Id == projectId.Value));
    }

    public static IQueryable<Endpoint> ForTenant(
        this IQueryable<Endpoint> endpoints,
        TenantAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(scope);

        var ownerUserId = scope.OwnerUserId;
        var projectId = scope.ProjectId;
        return endpoints.Where(endpoint =>
            endpoint.Environment!.Project!.OwnerUserId == ownerUserId
            && (!projectId.HasValue || endpoint.Environment.Project.Id == projectId.Value));
    }

    public static IQueryable<TestSuite> ForTenant(
        this IQueryable<TestSuite> testSuites,
        TenantAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(testSuites);
        ArgumentNullException.ThrowIfNull(scope);

        var ownerUserId = scope.OwnerUserId;
        var projectId = scope.ProjectId;
        return testSuites.Where(testSuite =>
            testSuite.Project!.OwnerUserId == ownerUserId
            && (!projectId.HasValue || testSuite.Project.Id == projectId.Value));
    }

    public static IQueryable<TestCase> ForTenant(
        this IQueryable<TestCase> testCases,
        TenantAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(testCases);
        ArgumentNullException.ThrowIfNull(scope);

        var ownerUserId = scope.OwnerUserId;
        var projectId = scope.ProjectId;
        return testCases.Where(testCase =>
            testCase.Suite!.Project!.OwnerUserId == ownerUserId
            && (!projectId.HasValue || testCase.Suite.Project.Id == projectId.Value));
    }

    public static IQueryable<MappingSpec> ForTenant(
        this IQueryable<MappingSpec> mappingSpecs,
        TenantAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(mappingSpecs);
        ArgumentNullException.ThrowIfNull(scope);

        var ownerUserId = scope.OwnerUserId;
        var projectId = scope.ProjectId;
        return mappingSpecs.Where(mappingSpec =>
            mappingSpec.Endpoint!.Environment!.Project!.OwnerUserId == ownerUserId
            && (!projectId.HasValue
                || mappingSpec.Endpoint.Environment.Project.Id == projectId.Value));
    }

    public static IQueryable<TestRun> ForTenant(
        this IQueryable<TestRun> testRuns,
        TenantAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(testRuns);
        ArgumentNullException.ThrowIfNull(scope);

        var ownerUserId = scope.OwnerUserId;
        var projectId = scope.ProjectId;
        return testRuns.Where(testRun =>
            testRun.Suite!.Project!.OwnerUserId == ownerUserId
            && testRun.CreatedByUserId == ownerUserId
            && testRun.Environment!.ProjectId == testRun.Suite.ProjectId
            && testRun.Endpoint!.EnvironmentId == testRun.EnvironmentId
            && testRun.MappingSpec!.EndpointId == testRun.EndpointId
            && (!projectId.HasValue || testRun.Suite.Project.Id == projectId.Value));
    }

    public static IQueryable<TestRunResult> ForTenant(
        this IQueryable<TestRunResult> testRunResults,
        TenantAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(testRunResults);
        ArgumentNullException.ThrowIfNull(scope);

        var ownerUserId = scope.OwnerUserId;
        var projectId = scope.ProjectId;
        return testRunResults.Where(testRunResult =>
            testRunResult.TestRun!.Suite!.Project!.OwnerUserId == ownerUserId
            && testRunResult.TestRun.CreatedByUserId == ownerUserId
            && testRunResult.TestRun.Environment!.ProjectId
                == testRunResult.TestRun.Suite.ProjectId
            && testRunResult.TestRun.Endpoint!.EnvironmentId
                == testRunResult.TestRun.EnvironmentId
            && testRunResult.TestRun.MappingSpec!.EndpointId
                == testRunResult.TestRun.EndpointId
            && testRunResult.TestCase!.SuiteId == testRunResult.TestRun.SuiteId
            && (!projectId.HasValue
                || testRunResult.TestRun.Suite.Project.Id == projectId.Value));
    }
}
