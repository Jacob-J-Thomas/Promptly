using Promptly.Domain.Entities;

namespace Promptly.Application.Data;

public static class ExecutableRunQueries
{
    public static IQueryable<TestRun> WhereExecutionGraphIsValid(
        this IQueryable<TestRun> testRuns)
    {
        ArgumentNullException.ThrowIfNull(testRuns);

        return testRuns.Where(run =>
            run.Suite!.Project!.OwnerUserId == run.CreatedByUserId
            && run.ProjectId == run.Suite.ProjectId
            && run.Environment!.ProjectId == run.Suite.ProjectId
            && run.Endpoint!.EnvironmentId == run.EnvironmentId
            && run.MappingSpec!.EndpointId == run.EndpointId);
    }
}
