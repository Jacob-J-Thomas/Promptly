using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;
using PromptlyEnvironment = Promptly.Domain.Entities.Environment;

namespace Promptly.IntegrationTests;

public sealed class WorkerRunSecurityTests(IntegrationFixture fixture)
{
    private const string EndpointTargetValidationError =
        "Run failed endpoint-target validation before execution";

    [Fact]
    public async Task Worker_QuarantinesUnsafeEndpointTargetsAtClaimAndProcessTimeWithoutEgress()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var graph = CreateGraph(owner.User.Id);
        var poisonRun = CreateRun(
            graph,
            graph.PoisonEndpoint.Id,
            graph.PoisonMapping.Id,
            DateTime.UtcNow.AddYears(-10));
        var claimableRun = CreateRun(
            graph,
            graph.ClaimableEndpoint.Id,
            graph.ClaimableMapping.Id,
            poisonRun.CreatedAt.AddSeconds(1));

        await using (var seedScope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope())
        {
            var dbContext = seedScope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
            dbContext.Projects.Add(graph.Project);
            dbContext.Environments.Add(graph.Environment);
            dbContext.Endpoints.AddRange(graph.PoisonEndpoint, graph.ClaimableEndpoint);
            dbContext.MappingSpecs.AddRange(graph.PoisonMapping, graph.ClaimableMapping);
            dbContext.TestSuites.Add(graph.Suite);
            dbContext.TestRuns.AddRange(poisonRun, claimableRun);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        using var captureListener = new TcpListener(IPAddress.Loopback, 0);
        captureListener.Start();
        using var captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        captureCancellation.CancelAfter(TimeSpan.FromSeconds(10));
        var captureTask = captureListener.AcceptTcpClientAsync(captureCancellation.Token).AsTask();

        try
        {
            var capturePort = ((IPEndPoint)captureListener.LocalEndpoint).Port;
            var absoluteTarget = $"http://127.0.0.1:{capturePort}/claim-capture";

            await using (var mutationScope =
                         fixture.PrimaryHost.Factory.Services.CreateAsyncScope())
            {
                var dbContext = mutationScope.ServiceProvider
                    .GetRequiredService<PromptlyDbContext>();
                var endpoint = await dbContext.Endpoints.SingleAsync(
                    candidate => candidate.Id == graph.PoisonEndpoint.Id,
                    cancellationToken);
                endpoint.Path = absoluteTarget;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await using (var queryScope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope())
            {
                var dbContext = queryScope.ServiceProvider
                    .GetRequiredService<PromptlyDbContext>();
                var executableRuns = dbContext.TestRuns
                    .AsNoTracking()
                    .WhereExecutionGraphIsValid()
                    .Where(run => run.Id == poisonRun.Id || run.Id == claimableRun.Id);

                var translatedSql = executableRuns.ToQueryString();
                Assert.Contains("JOIN", translatedSql, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(2, await executableRuns.CountAsync(cancellationToken));
            }

            var providerEvidenceBefore = await fixture.ReadProviderEvidenceAsync(
                cancellationToken);

            await using (var claimScope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope())
            {
                var workerStore = claimScope.ServiceProvider
                    .GetRequiredService<ITestRunWorkerStore>();

                Assert.Equal(
                    claimableRun.Id,
                    await workerStore.ClaimNextQueuedRunAsync(cancellationToken));
            }

            await AssertRunStateAsync(
                poisonRun.Id,
                TestRunStatus.Failed,
                expectStarted: false,
                expectCompleted: true,
                expectedError: EndpointTargetValidationError,
                cancellationToken);
            await AssertRunStateAsync(
                claimableRun.Id,
                TestRunStatus.Running,
                expectStarted: true,
                expectCompleted: false,
                expectedError: null,
                cancellationToken);

            await using (var raceMutationScope =
                         fixture.PrimaryHost.Factory.Services.CreateAsyncScope())
            {
                var dbContext = raceMutationScope.ServiceProvider
                    .GetRequiredService<PromptlyDbContext>();
                var endpoint = await dbContext.Endpoints.SingleAsync(
                    candidate => candidate.Id == graph.ClaimableEndpoint.Id,
                    cancellationToken);
                endpoint.Path = $"//127.0.0.1:{capturePort}/process-capture";
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            await using (var processScope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope())
            {
                var workerStore = processScope.ServiceProvider
                    .GetRequiredService<ITestRunWorkerStore>();
                var loadResult = await workerStore.LoadRunForProcessingAsync(
                    claimableRun.Id,
                    cancellationToken);

                Assert.Equal(WorkerRunLoadStatus.UnsafeEndpointTarget, loadResult.Status);
                Assert.Null(loadResult.Run);
            }

            await AssertRunStateAsync(
                claimableRun.Id,
                TestRunStatus.Failed,
                expectStarted: true,
                expectCompleted: true,
                expectedError: EndpointTargetValidationError,
                cancellationToken);

            await using (var resultScope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope())
            {
                var dbContext = resultScope.ServiceProvider
                    .GetRequiredService<PromptlyDbContext>();
                Assert.False(await dbContext.TestRunResults.AsNoTracking().AnyAsync(
                    result => result.RunId == poisonRun.Id || result.RunId == claimableRun.Id,
                    cancellationToken));
            }

            var providerEvidenceAfter = await fixture.ReadProviderEvidenceAsync(cancellationToken);
            Assert.Equal(
                providerEvidenceBefore.Select(record => record.Sequence),
                providerEvidenceAfter.Select(record => record.Sequence));

            var captureObservation = await Task.WhenAny(
                captureTask,
                Task.Delay(TimeSpan.FromSeconds(1), cancellationToken));
            Assert.NotSame(captureTask, captureObservation);
        }
        finally
        {
            await captureCancellation.CancelAsync();
            captureListener.Stop();
            var shutdownException = await Record.ExceptionAsync(async () =>
            {
                using var unexpectedConnection = await captureTask;
            });
            Assert.True(captureCancellation.IsCancellationRequested);
            Assert.True(
                shutdownException is OperationCanceledException or SocketException,
                shutdownException is null
                    ? "Unsafe worker target unexpectedly opened the capture listener"
                    : $"Unexpected capture-listener shutdown exception: {shutdownException}");
        }
    }

    private async Task AssertRunStateAsync(
        Guid runId,
        TestRunStatus expectedStatus,
        bool expectStarted,
        bool expectCompleted,
        string? expectedError,
        CancellationToken cancellationToken)
    {
        await using var scope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
        var run = await dbContext.TestRuns.AsNoTracking().SingleAsync(
            candidate => candidate.Id == runId,
            cancellationToken);

        Assert.Equal(expectedStatus, run.Status);
        Assert.Equal(expectStarted, run.StartedAt.HasValue);
        Assert.Equal(expectCompleted, run.CompletedAt.HasValue);
        Assert.Equal(expectedError, run.ErrorMessage);
        Assert.Null(run.SummaryJson);
    }

    private static WorkerGraph CreateGraph(string ownerUserId)
    {
        var project = new Project
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerUserId,
            Name = $"worker-security-{Guid.NewGuid():N}"
        };
        var environment = new PromptlyEnvironment
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = "worker security environment",
            BaseUrl = "https://worker-security.example.test"
        };
        var poisonEndpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = environment.Id,
            Name = "claim-time poison endpoint",
            Path = "/initially-safe",
            HttpMethod = "POST",
            TimeoutSeconds = 5
        };
        var claimableEndpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            EnvironmentId = environment.Id,
            Name = "process-time poison endpoint",
            Path = "/safe-at-claim",
            HttpMethod = "POST",
            TimeoutSeconds = 5
        };
        var poisonMapping = new MappingSpec
        {
            Id = Guid.NewGuid(),
            EndpointId = poisonEndpoint.Id,
            Name = "claim-time poison mapping",
            SpecJson = "{\"version\":1}"
        };
        var claimableMapping = new MappingSpec
        {
            Id = Guid.NewGuid(),
            EndpointId = claimableEndpoint.Id,
            Name = "process-time poison mapping",
            SpecJson = "{\"version\":1}"
        };
        var suite = new TestSuite
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = "worker security suite"
        };

        return new(
            project,
            environment,
            poisonEndpoint,
            claimableEndpoint,
            poisonMapping,
            claimableMapping,
            suite);
    }

    private static TestRun CreateRun(
        WorkerGraph graph,
        Guid endpointId,
        Guid mappingId,
        DateTime createdAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            ProjectId = graph.Project.Id,
            SuiteId = graph.Suite.Id,
            EnvironmentId = graph.Environment.Id,
            EndpointId = endpointId,
            MappingSpecId = mappingId,
            Status = TestRunStatus.Queued,
            CreatedAt = createdAt,
            CreatedByUserId = graph.Project.OwnerUserId,
            GitCommitHash = "worker-security-proof",
            ConfigSnapshotJson = "{}"
        };

    private sealed record WorkerGraph(
        Project Project,
        PromptlyEnvironment Environment,
        Endpoint PoisonEndpoint,
        Endpoint ClaimableEndpoint,
        MappingSpec PoisonMapping,
        MappingSpec ClaimableMapping,
        TestSuite Suite);
}
