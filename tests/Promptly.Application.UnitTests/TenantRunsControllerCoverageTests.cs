using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;
using Promptly.Server.Controllers;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class TenantRunsControllerCoverageTests
{
    private static readonly TenantAccessScope Scope = new("owner", Guid.NewGuid());

    [Fact]
    public async Task Successful_actions_project_runs_results_and_valid_status_filter()
    {
        var run = CreateRun();
        var resultWithTest = CreateResult(run.Id, includeTestCase: true);
        var resultWithoutTest = CreateResult(run.Id, includeTestCase: false);
        var service = new StubTestRunService
        {
            Queued = run,
            Loaded = run,
            Listed = [run],
            Results = [resultWithTest, resultWithoutTest],
            Result = resultWithTest
        };
        var controller = CreateController(service);

        var queued = Assert.IsType<CreatedAtActionResult>(
            await controller.QueueRun(CreateQueueRequest(run)));
        Assert.Equal(nameof(RunsController.GetRun), queued.ActionName);
        AssertRunResponse(run, Assert.IsType<TestRunResponse>(queued.Value));

        var loaded = Assert.IsType<OkObjectResult>(await controller.GetRun(run.Id));
        AssertRunResponse(run, Assert.IsType<TestRunResponse>(loaded.Value));

        var listed = Assert.IsType<OkObjectResult>(
            await controller.GetRunsBySuite(run.SuiteId, "completed", 7));
        AssertRunResponse(
            run,
            Assert.Single(Assert.IsType<List<TestRunResponse>>(listed.Value)));
        Assert.Equal(TestRunStatus.Completed, service.LastStatus);
        Assert.Equal(7, service.LastLimit);

        var results = Assert.IsType<OkObjectResult>(await controller.GetRunResults(run.Id));
        var resultResponses = Assert.IsType<List<TestRunResultResponse>>(results.Value);
        Assert.Equal(2, resultResponses.Count);
        Assert.Equal(resultWithTest.TestCase!.Name, resultResponses[0].TestCaseName);
        Assert.Null(resultResponses[1].TestCaseName);
        Assert.Null(resultResponses[1].TestCaseExternalId);

        var result = Assert.IsType<OkObjectResult>(
            await controller.GetRunResult(run.Id, resultWithTest.Id));
        AssertResultResponse(
            resultWithTest,
            Assert.IsType<TestRunResultResponse>(result.Value));
        Assert.Equal(Scope, service.LastScope);
    }

    [Fact]
    public async Task Missing_scope_rejects_every_action_before_service_access()
    {
        var service = new StubTestRunService();
        var controller = CreateController(service, hasScope: false);
        var run = CreateRun();

        var results = new IActionResult[]
        {
            await controller.QueueRun(CreateQueueRequest(run)),
            await controller.GetRun(run.Id),
            await controller.GetRunsBySuite(run.SuiteId),
            await controller.GetRunResults(run.Id),
            await controller.GetRunResult(run.Id, Guid.NewGuid())
        };

        Assert.All(results, result => Assert.IsType<UnauthorizedResult>(result));
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task Inaccessible_resources_return_not_found_for_every_action()
    {
        var service = new StubTestRunService();
        var controller = CreateController(service);
        var run = CreateRun();

        var results = new IActionResult[]
        {
            await controller.QueueRun(CreateQueueRequest(run)),
            await controller.GetRun(run.Id),
            await controller.GetRunsBySuite(run.SuiteId),
            await controller.GetRunResults(run.Id),
            await controller.GetRunResult(run.Id, Guid.NewGuid())
        };

        Assert.All(results, result => Assert.IsType<NotFoundObjectResult>(result));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-status")]
    public async Task Missing_blank_or_invalid_status_does_not_apply_a_status_filter(string? status)
    {
        var service = new StubTestRunService { Listed = [] };
        var controller = CreateController(service);

        Assert.IsType<OkObjectResult>(
            await controller.GetRunsBySuite(Guid.NewGuid(), status, limit: null));

        Assert.Null(service.LastStatus);
        Assert.Null(service.LastLimit);
    }

    [Theory]
    [InlineData(RunAction.Queue)]
    [InlineData(RunAction.Get)]
    [InlineData(RunAction.List)]
    [InlineData(RunAction.Results)]
    [InlineData(RunAction.Result)]
    public async Task Unexpected_service_failures_return_internal_server_error(RunAction action)
    {
        var controller = CreateController(new StubTestRunService
        {
            Failure = new InvalidOperationException("storage unavailable")
        });

        var result = await InvokeAsync(controller, action);

        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    private static Task<IActionResult> InvokeAsync(RunsController controller, RunAction action)
    {
        var run = CreateRun();
        return action switch
        {
            RunAction.Queue => controller.QueueRun(CreateQueueRequest(run)),
            RunAction.Get => controller.GetRun(run.Id),
            RunAction.List => controller.GetRunsBySuite(run.SuiteId, "queued", 1),
            RunAction.Results => controller.GetRunResults(run.Id),
            RunAction.Result => controller.GetRunResult(run.Id, Guid.NewGuid()),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    private static RunsController CreateController(
        StubTestRunService service,
        bool hasScope = true) =>
        new(
            service,
            new StubScopeAccessor(hasScope),
            NullLogger<RunsController>.Instance);

    private static QueueRunRequest CreateQueueRequest(TestRun run) => new()
    {
        SuiteId = run.SuiteId,
        EnvironmentId = run.EnvironmentId,
        EndpointId = run.EndpointId,
        MappingSpecId = run.MappingSpecId,
        GitCommitHash = run.GitCommitHash,
        ConfigSnapshotJson = run.ConfigSnapshotJson
    };

    private static TestRun CreateRun() => new()
    {
        Id = Guid.NewGuid(),
        SuiteId = Guid.NewGuid(),
        EnvironmentId = Guid.NewGuid(),
        EndpointId = Guid.NewGuid(),
        MappingSpecId = Guid.NewGuid(),
        Status = TestRunStatus.Completed,
        SummaryJson = "{\"passed\":1}",
        GitCommitHash = "abc123",
        ConfigSnapshotJson = "{\"temperature\":0}",
        CreatedByUserId = Scope.OwnerUserId,
        ErrorMessage = "historical message",
        CreatedAt = new DateTime(2026, 8, 12, 1, 2, 3, DateTimeKind.Utc),
        StartedAt = new DateTime(2026, 8, 12, 1, 3, 3, DateTimeKind.Utc),
        CompletedAt = new DateTime(2026, 8, 12, 1, 4, 3, DateTimeKind.Utc)
    };

    private static TestRunResult CreateResult(Guid runId, bool includeTestCase)
    {
        var testCase = includeTestCase
            ? new TestCase
            {
                Id = Guid.NewGuid(),
                SuiteId = Guid.NewGuid(),
                ExternalId = "external",
                Name = "test name",
                InputSpecJson = "{}",
                ExpectationsJson = "[]"
            }
            : null;

        return new TestRunResult
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            TestCaseId = testCase?.Id ?? Guid.NewGuid(),
            Status = TestResultStatus.Fail,
            TraceJson = "{\"trace\":true}",
            MetricsJson = "{\"latencyMs\":12}",
            FailureReasonsJson = "[\"failed\"]",
            CreatedAt = new DateTime(2026, 8, 12, 1, 5, 3, DateTimeKind.Utc),
            TestCase = testCase
        };
    }

    private static void AssertRunResponse(TestRun expected, TestRunResponse actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.SuiteId, actual.SuiteId);
        Assert.Equal(expected.EnvironmentId, actual.EnvironmentId);
        Assert.Equal(expected.EndpointId, actual.EndpointId);
        Assert.Equal(expected.MappingSpecId, actual.MappingSpecId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.SummaryJson, actual.SummaryJson);
        Assert.Equal(expected.GitCommitHash, actual.GitCommitHash);
        Assert.Equal(expected.ConfigSnapshotJson, actual.ConfigSnapshotJson);
        Assert.Equal(expected.CreatedByUserId, actual.CreatedByUserId);
        Assert.Equal(expected.ErrorMessage, actual.ErrorMessage);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.StartedAt, actual.StartedAt);
        Assert.Equal(expected.CompletedAt, actual.CompletedAt);
    }

    private static void AssertResultResponse(
        TestRunResult expected,
        TestRunResultResponse actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.TestCaseId, actual.TestCaseId);
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(expected.TraceJson, actual.TraceJson);
        Assert.Equal(expected.MetricsJson, actual.MetricsJson);
        Assert.Equal(expected.FailureReasonsJson, actual.FailureReasonsJson);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.TestCase?.Name, actual.TestCaseName);
        Assert.Equal(expected.TestCase?.ExternalId, actual.TestCaseExternalId);
    }

    public enum RunAction
    {
        Queue,
        Get,
        List,
        Results,
        Result
    }

    private sealed class StubScopeAccessor(bool hasScope) : ITenantAccessScopeAccessor
    {
        public bool TryGetScope([NotNullWhen(true)] out TenantAccessScope? scope)
        {
            scope = hasScope ? Scope : null;
            return hasScope;
        }
    }

    private sealed class StubTestRunService : ITestRunService
    {
        public TestRun? Queued { get; init; }
        public TestRun? Loaded { get; init; }
        public List<TestRun>? Listed { get; init; }
        public List<TestRunResult>? Results { get; init; }
        public TestRunResult? Result { get; init; }
        public Exception? Failure { get; init; }
        public int CallCount { get; private set; }
        public TenantAccessScope? LastScope { get; private set; }
        public TestRunStatus? LastStatus { get; private set; }
        public int? LastLimit { get; private set; }

        public Task<TestRun?> QueueRunAsync(
            Guid suiteId,
            Guid environmentId,
            Guid endpointId,
            Guid mappingSpecId,
            string? gitCommitHash,
            string? configSnapshotJson,
            TenantAccessScope scope) => Return(scope, Queued);

        public Task<TestRun?> GetRunByIdAsync(Guid runId, TenantAccessScope scope) =>
            Return(scope, Loaded);

        public Task<List<TestRun>?> GetRunsBySuiteAsync(
            Guid suiteId,
            TestRunStatus? status,
            int? limit,
            TenantAccessScope scope)
        {
            LastStatus = status;
            LastLimit = limit;
            return Return(scope, Listed);
        }

        public Task<List<TestRunResult>?> GetRunResultsAsync(
            Guid runId,
            TenantAccessScope scope) => Return(scope, Results);

        public Task<TestRunResult?> GetRunResultAsync(
            Guid runId,
            Guid resultId,
            TenantAccessScope scope) => Return(scope, Result);

        private Task<T> Return<T>(TenantAccessScope scope, T value)
        {
            CallCount++;
            LastScope = scope;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(value);
        }
    }
}
