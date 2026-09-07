using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;

namespace Promptly.Application.Services;

public class TestRunService : ITestRunService
{
    private readonly PromptlyDbContext _dbContext;
    private readonly ILogger<TestRunService> _logger;

    public TestRunService(PromptlyDbContext dbContext, ILogger<TestRunService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<TestRun?> QueueRunAsync(
        Guid suiteId,
        Guid environmentId,
        Guid endpointId,
        Guid mappingSpecId,
        string? gitCommitHash,
        string? configSnapshotJson,
        TenantAccessScope scope)
    {
        var graphIsAuthorized = await (
            from suite in _dbContext.TestSuites.ForTenant(scope)
            join environment in _dbContext.Environments
                on suite.ProjectId equals environment.ProjectId
            join endpoint in _dbContext.Endpoints
                on environment.Id equals endpoint.EnvironmentId
            join mappingSpec in _dbContext.MappingSpecs
                on endpoint.Id equals mappingSpec.EndpointId
            where suite.Id == suiteId
                && environment.Id == environmentId
                && endpoint.Id == endpointId
                && mappingSpec.Id == mappingSpecId
            select suite.Id)
            .AnyAsync();

        if (!graphIsAuthorized)
        {
            return null;
        }

        var testRun = new TestRun
        {
            Id = Guid.NewGuid(),
            SuiteId = suiteId,
            EnvironmentId = environmentId,
            EndpointId = endpointId,
            MappingSpecId = mappingSpecId,
            CreatedByUserId = scope.OwnerUserId,
            Status = TestRunStatus.Queued,
            GitCommitHash = gitCommitHash,
            ConfigSnapshotJson = configSnapshotJson,
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.TestRuns.Add(testRun);
        await _dbContext.SaveChangesAsync();

        _logger.LogInformation("Queued test run {RunId} for suite {SuiteId}", testRun.Id, suiteId);

        return testRun;
    }

    public async Task<TestRun?> GetRunByIdAsync(Guid runId, TenantAccessScope scope)
    {
        return await _dbContext.TestRuns
            .ForTenant(scope)
            .Include(r => r.Suite)
            .Include(r => r.Environment)
            .Include(r => r.Endpoint)
            .Include(r => r.MappingSpec)
            .FirstOrDefaultAsync(r => r.Id == runId);
    }

    public async Task<List<TestRun>?> GetRunsBySuiteAsync(
        Guid suiteId,
        TestRunStatus? status,
        int? limit,
        TenantAccessScope scope)
    {
        var ownsSuite = await _dbContext.TestSuites
            .ForTenant(scope)
            .AnyAsync(suite => suite.Id == suiteId);
        if (!ownsSuite)
        {
            return null;
        }

        var query = _dbContext.TestRuns
            .ForTenant(scope)
            .Where(r => r.SuiteId == suiteId);

        if (status.HasValue)
        {
            query = query.Where(r => r.Status == status.Value);
        }

        query = query.OrderByDescending(r => r.CreatedAt);

        if (limit.HasValue)
        {
            query = query.Take(limit.Value);
        }

        return await query.ToListAsync();
    }

    public async Task<List<TestRunResult>?> GetRunResultsAsync(
        Guid runId,
        TenantAccessScope scope)
    {
        var ownsRun = await _dbContext.TestRuns
            .ForTenant(scope)
            .AnyAsync(run => run.Id == runId);
        if (!ownsRun)
        {
            return null;
        }

        return await _dbContext.TestRunResults
            .ForTenant(scope)
            .Include(result => result.TestCase)
            .Where(result => result.RunId == runId)
            .OrderBy(result => result.Status == TestResultStatus.Error ? 0
                : result.Status == TestResultStatus.Fail ? 1 : 2)
            .ThenBy(result => result.TestCase!.ExternalId)
            .ToListAsync();
    }

    public async Task<TestRunResult?> GetRunResultAsync(
        Guid runId,
        Guid resultId,
        TenantAccessScope scope)
    {
        return await _dbContext.TestRunResults
            .ForTenant(scope)
            .Include(result => result.TestCase)
            .FirstOrDefaultAsync(result => result.Id == resultId && result.RunId == runId);
    }
}
