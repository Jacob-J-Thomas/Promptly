using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Promptly.Application.Data;
using Promptly.Domain.Entities;
using Promptly.Domain.Enums;

namespace Promptly.IntegrationTests;

public sealed class TenantIsolationTests(IntegrationFixture fixture)
{
    private const string ValidMappingSpec =
        "{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$.answer\"}}";

    [Fact]
    public async Task JwtAttacker_WithLeakedVictimIds_CannotAccessAnyTenantRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var victim = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        using var attacker = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var graph = await CreateGraphAsync(victim, "jwt-victim", cancellationToken);
        var stateBefore = await CaptureStateAsync(graph, cancellationToken);
        var providerEvidenceBefore = await fixture.ReadProviderEvidenceAsync(cancellationToken);

        var observations = await ExerciseDeniedTenantMatrixAsync(
            graph,
            request => attacker.SendAsync(request),
            cancellationToken);

        using (var projectList = await attacker.GetAsync("/api/projects"))
        {
            Assert.Equal(HttpStatusCode.OK, projectList.StatusCode);
            var body = await projectList.Content.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain(
                graph.ForbiddenValues,
                value => body.Contains(value, StringComparison.OrdinalIgnoreCase));
            using var document = JsonDocument.Parse(body);
            Assert.DoesNotContain(
                document.RootElement.EnumerateArray(),
                project => project.GetProperty("id").GetGuid() == graph.ProjectId);
        }

        observations.Add(await SendAndObserveAsync(
            "projects.get",
            new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{graph.ProjectId}"),
            attacker.SendAsync,
            graph,
            cancellationToken));
        observations.Add(await SendAndObserveAsync(
            "projects.update",
            JsonRequest(
                HttpMethod.Put,
                $"/api/projects/{graph.ProjectId}",
                new
                {
                    name = "attacker-project-update",
                    description = "attacker-project-update"
                }),
            attacker.SendAsync,
            graph,
            cancellationToken));
        observations.Add(await SendAndObserveAsync(
            "projects.delete",
            new HttpRequestMessage(
                HttpMethod.Delete,
                $"/api/projects/{graph.ProjectId}"),
            attacker.SendAsync,
            graph,
            cancellationToken));

        var stateAfter = await CaptureStateAsync(graph, cancellationToken);
        var providerEvidenceAfter = await fixture.ReadProviderEvidenceAsync(cancellationToken);

        AssertDeniedMatrix(
            observations,
            stateBefore,
            stateAfter,
            providerEvidenceBefore,
            providerEvidenceAfter);
    }

    [Fact]
    public async Task ProjectApiKey_IsRestrictedToItsExactProjectAcrossTenantRoutes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var projectA = await CreateGraphAsync(owner, "api-key-project-a", cancellationToken);
        var projectB = await CreateGraphAsync(owner, "api-key-project-b", cancellationToken);
        var rawApiKey = $"promptly-integration-{Guid.NewGuid():N}";
        await SeedProjectApiKeyAsync(projectA.ProjectId, rawApiKey, cancellationToken);
        using var apiKeyClient = fixture.PrimaryHost.Factory.CreateClient(new()
        {
            AllowAutoRedirect = false
        });

        await AssertApiKeyOwnsOnlyProjectAAsync(
            apiKeyClient,
            rawApiKey,
            owner,
            projectA,
            projectB,
            cancellationToken);

        var projectBStateBefore = await CaptureStateAsync(projectB, cancellationToken);
        var providerEvidenceBefore = await fixture.ReadProviderEvidenceAsync(cancellationToken);
        var observations = await ExerciseDeniedTenantMatrixAsync(
            projectB,
            request => SendWithApiKeyAsync(
                apiKeyClient,
                request,
                rawApiKey,
                bearerToken: null,
                cancellationToken),
            cancellationToken);

        observations.Add(await SendAndObserveAsync(
            "projects.get",
            new HttpRequestMessage(HttpMethod.Get, $"/api/projects/{projectB.ProjectId}"),
            request => SendWithApiKeyAsync(
                apiKeyClient,
                request,
                rawApiKey,
                bearerToken: null,
                cancellationToken),
            projectB,
            cancellationToken));
        observations.Add(await SendAndObserveAsync(
            "projects.update",
            JsonRequest(
                HttpMethod.Put,
                $"/api/projects/{projectB.ProjectId}",
                new { name = "api-key-sibling-update", description = "denied" }),
            request => SendWithApiKeyAsync(
                apiKeyClient,
                request,
                rawApiKey,
                bearerToken: null,
                cancellationToken),
            projectB,
            cancellationToken));
        observations.Add(await SendAndObserveAsync(
            "projects.delete",
            new HttpRequestMessage(HttpMethod.Delete, $"/api/projects/{projectB.ProjectId}"),
            request => SendWithApiKeyAsync(
                apiKeyClient,
                request,
                rawApiKey,
                bearerToken: null,
                cancellationToken),
            projectB,
            cancellationToken));

        using (var createProjectRequest = JsonRequest(
                   HttpMethod.Post,
                   "/api/projects",
                   new { name = "forbidden-api-key-project", description = "denied" }))
        using (var createProject = await SendWithApiKeyAsync(
                   apiKeyClient,
                   createProjectRequest,
                   rawApiKey,
                   bearerToken: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, createProject.StatusCode);
        }

        using (var dualCredentialRequest = new HttpRequestMessage(
                   HttpMethod.Get,
                   $"/api/projects/{projectB.ProjectId}"))
        using (var dualCredentialResponse = await SendWithApiKeyAsync(
                   apiKeyClient,
                   dualCredentialRequest,
                   rawApiKey,
                   owner.User.Token,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, dualCredentialResponse.StatusCode);
        }

        var projectBStateAfter = await CaptureStateAsync(projectB, cancellationToken);
        var providerEvidenceAfter = await fixture.ReadProviderEvidenceAsync(cancellationToken);
        AssertDeniedMatrix(
            observations,
            projectBStateBefore,
            projectBStateAfter,
            providerEvidenceBefore,
            providerEvidenceAfter);
    }

    [Fact]
    public async Task Database_RejectsNewIncoherentRunGraph()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var projectA = await CreateGraphAsync(owner, "guarded-run-project-a", cancellationToken);
        var projectB = await CreateGraphAsync(owner, "guarded-run-project-b", cancellationToken);
        var runId = Guid.NewGuid();

        await using (var scope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
            dbContext.TestRuns.Add(new TestRun
            {
                Id = runId,
                ProjectId = projectA.ProjectId,
                SuiteId = projectA.SuiteId,
                EnvironmentId = projectB.EnvironmentId,
                EndpointId = projectB.EndpointId,
                MappingSpecId = projectB.MappingId,
                Status = TestRunStatus.Queued,
                CreatedByUserId = owner.User.Id,
                GitCommitHash = "must-be-rejected",
                ConfigSnapshotJson = "{\"mustNotPersist\":true}"
            });

            await Assert.ThrowsAsync<DbUpdateException>(
                () => dbContext.SaveChangesAsync(cancellationToken));
        }

        await using (var verificationScope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope())
        {
            var dbContext = verificationScope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
            Assert.False(await dbContext.TestRuns.AsNoTracking().AnyAsync(
                run => run.Id == runId,
                cancellationToken));
        }
    }

    [Fact]
    public async Task ProtectedRoutes_RejectMissingInvalidAndMixedCredentials()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var user = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var graph = await CreateGraphAsync(user, "invalid-credentials", cancellationToken);
        using var client = fixture.PrimaryHost.Factory.CreateClient(new()
        {
            AllowAutoRedirect = false
        });

        using var missing = await client.GetAsync("/api/projects", cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        using var invalidBearerRequest = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        invalidBearerRequest.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "not-a-valid-token");
        using var invalidBearer = await client.SendAsync(invalidBearerRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidBearer.StatusCode);

        using var invalidApiKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/api/projects");
        invalidApiKeyRequest.Headers.Add("X-API-Key", "not-a-valid-key");
        using var invalidApiKey = await client.SendAsync(invalidApiKeyRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidApiKey.StatusCode);

        using var mixedRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/projects/{graph.ProjectId}");
        mixedRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", user.User.Token);
        mixedRequest.Headers.Add("X-API-Key", "not-a-valid-key");
        using var mixed = await client.SendAsync(mixedRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, mixed.StatusCode);
    }

    [Fact]
    public async Task JwtOwner_CannotQueueRunFromIndividuallyOwnedButIncoherentGraph()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var owner = await PromptlyApiClient.RegisterAsync(fixture.PrimaryHost.Factory);
        var projectA = await CreateGraphAsync(owner, "mixed-run-project-a", cancellationToken);
        var projectB = await CreateGraphAsync(owner, "mixed-run-project-b", cancellationToken);
        var runCountBefore = await CountRunsAsync(cancellationToken);
        var providerEvidenceBefore = await fixture.ReadProviderEvidenceAsync(cancellationToken);
        var mismatches = new[]
        {
            new
            {
                Name = "suite-project/environment-project",
                EnvironmentId = projectB.EnvironmentId,
                EndpointId = projectB.EndpointId,
                MappingId = projectB.MappingId
            },
            new
            {
                Name = "environment/endpoint",
                EnvironmentId = projectA.EnvironmentId,
                EndpointId = projectB.EndpointId,
                MappingId = projectB.MappingId
            },
            new
            {
                Name = "endpoint/mapping",
                EnvironmentId = projectA.EnvironmentId,
                EndpointId = projectA.EndpointId,
                MappingId = projectB.MappingId
            }
        };

        foreach (var mismatch in mismatches)
        {
            using var response = await owner.PostJsonAsync(
                "/api/runs",
                new
                {
                    suiteId = projectA.SuiteId,
                    environmentId = mismatch.EnvironmentId,
                    endpointId = mismatch.EndpointId,
                    mappingSpecId = mismatch.MappingId,
                    gitCommitHash = $"denied-{mismatch.Name}",
                    configSnapshotJson = "{\"mustNotPersist\":true}"
                });

            Assert.True(
                response.StatusCode == HttpStatusCode.NotFound,
                $"{mismatch.Name}: expected 404, received " +
                $"{(int)response.StatusCode} ({response.StatusCode})");
        }

        Assert.Equal(runCountBefore, await CountRunsAsync(cancellationToken));
        var providerEvidenceAfter = await fixture.ReadProviderEvidenceAsync(cancellationToken);
        Assert.Equal(
            providerEvidenceBefore?.Select(record => record.Sequence),
            providerEvidenceAfter?.Select(record => record.Sequence));
    }

    private async Task<TenantGraph> CreateGraphAsync(
        PromptlyApiClient user,
        string label,
        CancellationToken cancellationToken)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var secret = $"tenant-secret-{label}-{nonce}";
        var headerSecret = $"header-{secret}";
        var mappingName = $"mapping-{secret}";
        var suiteName = $"suite-{secret}";
        var testExternalId = $"test-{nonce}";
        var testName = $"test-name-{secret}";
        var testInput = $"{{\"prompt\":\"input-{secret}\"}}";
        var testExpectations = $"[{{\"type\":\"contains\",\"value\":\"expect-{secret}\"}}]";
        var runConfig = $"{{\"runSecret\":\"config-{secret}\"}}";
        var runSummary = $"{{\"summarySecret\":\"summary-{secret}\"}}";
        var resultTrace = $"{{\"traceSecret\":\"trace-{secret}\"}}";
        var resultMetrics = $"{{\"metricsSecret\":\"metrics-{secret}\"}}";
        var failureReasons = $"[\"failure-{secret}\"]";

        var projectId = await PostAndReadIdAsync(
            user,
            "/api/projects",
            new { name = $"project-{secret}", description = $"description-{secret}" },
            cancellationToken);
        var environmentId = await PostAndReadIdAsync(
            user,
            $"/api/projects/{projectId}/environments",
            new
            {
                name = $"environment-{secret}",
                baseUrl = $"https://{nonce}.example.test",
                headers = new Dictionary<string, string>
                {
                    ["X-Tenant-Secret"] = headerSecret
                }
            },
            cancellationToken);
        var endpointId = await PostAndReadIdAsync(
            user,
            $"/api/environments/{environmentId}/endpoints",
            new
            {
                name = $"endpoint-{secret}",
                path = $"/tenant/{nonce}",
                httpMethod = "POST",
                timeoutSeconds = 10
            },
            cancellationToken);
        var mappingId = await PostAndReadIdAsync(
            user,
            $"/api/endpoints/{endpointId}/mapping",
            new { name = mappingName, specJson = ValidMappingSpec },
            cancellationToken);
        var suiteId = await PostAndReadIdAsync(
            user,
            $"/api/suites?projectId={projectId}",
            new { name = suiteName, description = $"suite-description-{secret}" },
            cancellationToken);
        var testId = await PostAndReadIdAsync(
            user,
            $"/api/suites/{suiteId}/tests",
            new
            {
                externalId = testExternalId,
                name = testName,
                description = $"test-description-{secret}",
                inputSpecJson = testInput,
                expectationsJson = testExpectations
            },
            cancellationToken);
        var runId = await PostAndReadIdAsync(
            user,
            "/api/runs",
            new
            {
                suiteId,
                environmentId,
                endpointId,
                mappingSpecId = mappingId,
                gitCommitHash = $"commit-{nonce}",
                configSnapshotJson = runConfig
            },
            cancellationToken);
        var resultId = Guid.NewGuid();

        await using (var scope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
            var run = await dbContext.TestRuns.SingleAsync(
                candidate => candidate.Id == runId,
                cancellationToken);
            run.SummaryJson = runSummary;
            dbContext.TestRunResults.Add(new TestRunResult
            {
                Id = resultId,
                RunId = runId,
                TestCaseId = testId,
                Status = TestResultStatus.Fail,
                TraceJson = resultTrace,
                MetricsJson = resultMetrics,
                FailureReasonsJson = failureReasons
            });
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new TenantGraph(
            projectId,
            environmentId,
            endpointId,
            mappingId,
            suiteId,
            testId,
            runId,
            resultId,
            secret,
            headerSecret,
            mappingName,
            suiteName,
            testExternalId,
            testName,
            testInput,
            testExpectations,
            runConfig,
            runSummary,
            resultTrace,
            resultMetrics,
            failureReasons);
    }

    private static async Task<Guid> PostAndReadIdAsync(
        PromptlyApiClient user,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        using var response = await user.PostJsonAsync(path, body);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
        return document.RootElement.GetProperty("id").GetGuid();
    }

    private async Task<List<DeniedObservation>> ExerciseDeniedTenantMatrixAsync(
        TenantGraph graph,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync,
        CancellationToken cancellationToken)
    {
        var requests = CreateDeniedTenantRequests(graph);
        var observations = new List<DeniedObservation>(requests.Count);

        foreach (var operation in requests)
        {
            using var request = operation.CreateRequest();
            var response = await sendAsync(request);
            observations.Add(await ObserveAsync(
                operation.Name,
                response,
                graph,
                cancellationToken));
        }

        return observations;
    }

    private static List<DeniedOperation> CreateDeniedTenantRequests(TenantGraph graph) =>
    [
        new("environments.list", () => new(
            HttpMethod.Get,
            $"/api/projects/{graph.ProjectId}/environments")),
        new("environments.get", () => new(
            HttpMethod.Get,
            $"/api/environments/{graph.EnvironmentId}")),
        new("environments.create", () => JsonRequest(
            HttpMethod.Post,
            $"/api/projects/{graph.ProjectId}/environments",
            new
            {
                name = "attacker-environment",
                baseUrl = "https://attacker.example.test",
                headers = new Dictionary<string, string> { ["X-Attacker"] = "denied" }
            })),
        new("environments.update", () => JsonRequest(
            HttpMethod.Put,
            $"/api/environments/{graph.EnvironmentId}",
            new
            {
                name = "attacker-environment-update",
                baseUrl = "https://attacker.example.test",
                headers = new Dictionary<string, string> { ["X-Attacker"] = "denied" }
            })),

        new("endpoints.list", () => new(
            HttpMethod.Get,
            $"/api/environments/{graph.EnvironmentId}/endpoints")),
        new("endpoints.get", () => new(HttpMethod.Get, $"/api/endpoints/{graph.EndpointId}")),
        new("endpoints.create", () => JsonRequest(
            HttpMethod.Post,
            $"/api/environments/{graph.EnvironmentId}/endpoints",
            new
            {
                name = "attacker-endpoint",
                path = "/attacker",
                httpMethod = "POST",
                timeoutSeconds = 10
            })),
        new("endpoints.update", () => JsonRequest(
            HttpMethod.Put,
            $"/api/endpoints/{graph.EndpointId}",
            new
            {
                name = "attacker-endpoint-update",
                path = "/attacker-update",
                httpMethod = "GET",
                timeoutSeconds = 11
            })),

        new("mapping.propose", () => JsonRequest(
            HttpMethod.Post,
            $"/api/endpoints/{graph.EndpointId}/mapping/propose",
            new
            {
                sampleResponseJson = "{\"answer\":\"attacker\"}",
                sampleRequestJson = "{\"prompt\":\"attacker\"}",
                hints = new { purpose = "must-not-reach-provider" }
            })),
        new("mapping.validate", () => JsonRequest(
            HttpMethod.Post,
            $"/api/endpoints/{graph.EndpointId}/mapping/validate",
            new
            {
                mappingSpecJson = ValidMappingSpec,
                sampleResponseJson = "{\"answer\":\"attacker\"}"
            })),
        new("mapping.create", () => JsonRequest(
            HttpMethod.Post,
            $"/api/endpoints/{graph.EndpointId}/mapping",
            new { name = "attacker-mapping", specJson = ValidMappingSpec })),
        new("mapping.list", () => new(
            HttpMethod.Get,
            $"/api/endpoints/{graph.EndpointId}/mapping")),
        new("mapping.get", () => new(HttpMethod.Get, $"/api/mapping/{graph.MappingId}")),
        new("mapping.update", () => JsonRequest(
            HttpMethod.Put,
            $"/api/mapping/{graph.MappingId}",
            new { name = "attacker-mapping-update", specJson = ValidMappingSpec })),
        new("mapping.set-default", () => new(
            HttpMethod.Post,
            $"/api/mapping/{graph.MappingId}/set-default")),

        new("suites.create", () => JsonRequest(
            HttpMethod.Post,
            $"/api/suites?projectId={graph.ProjectId}",
            new { name = "attacker-suite", description = "denied" })),
        new("suites.list", () => new(
            HttpMethod.Get,
            $"/api/projects/{graph.ProjectId}/suites")),
        new("suites.get", () => new(HttpMethod.Get, $"/api/suites/{graph.SuiteId}")),
        new("suites.update", () => JsonRequest(
            HttpMethod.Put,
            $"/api/suites/{graph.SuiteId}",
            new { name = "attacker-suite-update", description = "denied" })),
        new("suites.import", () => ImportRequest(graph.SuiteId)),
        new("suites.export", () => new(
            HttpMethod.Get,
            $"/api/suites/{graph.SuiteId}/tests/export")),

        new("tests.create", () => JsonRequest(
            HttpMethod.Post,
            $"/api/suites/{graph.SuiteId}/tests",
            TestRequest("attacker-created"))),
        new("tests.list", () => new(
            HttpMethod.Get,
            $"/api/suites/{graph.SuiteId}/tests")),
        new("tests.get", () => new(HttpMethod.Get, $"/api/tests/{graph.TestId}")),
        new("tests.update", () => JsonRequest(
            HttpMethod.Put,
            $"/api/tests/{graph.TestId}",
            TestRequest("attacker-updated"))),

        new("runs.queue", () => JsonRequest(
            HttpMethod.Post,
            "/api/runs",
            new
            {
                suiteId = graph.SuiteId,
                environmentId = graph.EnvironmentId,
                endpointId = graph.EndpointId,
                mappingSpecId = graph.MappingId,
                gitCommitHash = "attacker-run",
                configSnapshotJson = "{\"attacker\":true}"
            })),
        new("runs.get", () => new(HttpMethod.Get, $"/api/runs/{graph.RunId}")),
        new("runs.list", () => new(
            HttpMethod.Get,
            $"/api/suites/{graph.SuiteId}/runs?status=Queued&limit=10")),
        new("results.list", () => new(
            HttpMethod.Get,
            $"/api/runs/{graph.RunId}/results")),
        new("results.get", () => new(
            HttpMethod.Get,
            $"/api/runs/{graph.RunId}/results/{graph.ResultId}")),

        // Destructive operations run last so one defect cannot hide the rest of
        // the read/create/update authorization matrix behind a cascade delete.
        new("mapping.delete", () => new(HttpMethod.Delete, $"/api/mapping/{graph.MappingId}")),
        new("tests.delete", () => new(HttpMethod.Delete, $"/api/tests/{graph.TestId}")),
        new("suites.delete", () => new(HttpMethod.Delete, $"/api/suites/{graph.SuiteId}")),
        new("endpoints.delete", () => new(HttpMethod.Delete, $"/api/endpoints/{graph.EndpointId}")),
        new("environments.delete", () => new(
            HttpMethod.Delete,
            $"/api/environments/{graph.EnvironmentId}"))
    ];

    private async Task AssertApiKeyOwnsOnlyProjectAAsync(
        HttpClient client,
        string rawApiKey,
        PromptlyApiClient owner,
        TenantGraph projectA,
        TenantGraph projectB,
        CancellationToken cancellationToken)
    {
        await ExerciseApiKeyProjectActionsAsync(
            client,
            rawApiKey,
            projectA,
            projectB,
            cancellationToken);
        await ExerciseApiKeyEnvironmentActionsAsync(
            client,
            rawApiKey,
            projectA,
            cancellationToken);
        await ExerciseApiKeyEndpointActionsAsync(
            client,
            rawApiKey,
            projectA,
            cancellationToken);
        await ExerciseApiKeyMappingActionsAsync(
            client,
            rawApiKey,
            projectA,
            projectB,
            cancellationToken);
        await ExerciseApiKeySuiteAndTestActionsAsync(
            client,
            rawApiKey,
            projectA,
            projectB,
            cancellationToken);
        await ExerciseApiKeyRunActionsAsync(
            client,
            rawApiKey,
            projectA,
            projectB,
            cancellationToken);
        await ExerciseApiKeyProjectDeleteAsync(
            client,
            owner,
            cancellationToken);
    }

    private static async Task ExerciseApiKeyProjectActionsAsync(
        HttpClient client,
        string rawApiKey,
        TenantGraph projectA,
        TenantGraph projectB,
        CancellationToken cancellationToken)
    {
        using (var projectsRequest = new HttpRequestMessage(HttpMethod.Get, "/api/projects"))
        using (var projects = await SendWithApiKeyAsync(
                   client,
                   projectsRequest,
                   rawApiKey,
                   bearerToken: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, projects.StatusCode);
            var body = await projects.Content.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain(
                projectB.ForbiddenValues,
                value => body.Contains(value, StringComparison.OrdinalIgnoreCase));
            using var document = JsonDocument.Parse(body);
            var visibleIds = document.RootElement
                .EnumerateArray()
                .Select(project => project.GetProperty("id").GetGuid())
                .ToArray();
            Assert.Equal([projectA.ProjectId], visibleIds);
            Assert.DoesNotContain(projectB.ProjectId, visibleIds);
        }

        using (var project = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/projects/{projectA.ProjectId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, project.StatusCode);
            using var document = await ReadJsonAsync(project, cancellationToken);
            Assert.Equal(projectA.ProjectId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Contains(
                projectA.Secret,
                document.RootElement.GetProperty("name").GetString(),
                StringComparison.Ordinal);
        }

        using (var updated = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Put,
                   $"/api/projects/{projectA.ProjectId}",
                   new
                   {
                       name = "api-key-updated-project",
                       description = "updated through exact-project API key"
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var document = await ReadJsonAsync(updated, cancellationToken);
            Assert.Equal(projectA.ProjectId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(
                "api-key-updated-project",
                document.RootElement.GetProperty("name").GetString());
            Assert.Equal(
                "updated through exact-project API key",
                document.RootElement.GetProperty("description").GetString());
        }
    }

    private static async Task ExerciseApiKeyEnvironmentActionsAsync(
        HttpClient client,
        string rawApiKey,
        TenantGraph projectA,
        CancellationToken cancellationToken)
    {
        using (var environments = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/projects/{projectA.ProjectId}/environments",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, environments.StatusCode);
            using var document = await ReadJsonAsync(environments, cancellationToken);
            var environment = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal(projectA.EnvironmentId, environment.GetProperty("id").GetGuid());
            Assert.Equal(projectA.ProjectId, environment.GetProperty("projectId").GetGuid());
            Assert.True(environment.GetProperty("hasHeaders").GetBoolean());
        }

        using (var environment = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/environments/{projectA.EnvironmentId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, environment.StatusCode);
            using var document = await ReadJsonAsync(environment, cancellationToken);
            Assert.Equal(projectA.EnvironmentId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(
                projectA.HeaderSecret,
                document.RootElement.GetProperty("headers")
                    .GetProperty("X-Tenant-Secret")
                    .GetString());
        }

        var environmentName = $"api-key-environment-{Guid.NewGuid():N}";
        Guid environmentId;
        using (var created = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Post,
                   $"/api/projects/{projectA.ProjectId}/environments",
                   new
                   {
                       name = environmentName,
                       baseUrl = "https://api-key-created.example.test",
                       headers = new Dictionary<string, string>
                       {
                           ["X-Api-Key-Created"] = "created-secret"
                       }
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var document = await ReadJsonAsync(created, cancellationToken);
            environmentId = document.RootElement.GetProperty("id").GetGuid();
            Assert.NotEqual(Guid.Empty, environmentId);
            Assert.Equal(projectA.ProjectId, document.RootElement.GetProperty("projectId").GetGuid());
            Assert.Equal(environmentName, document.RootElement.GetProperty("name").GetString());
            Assert.True(document.RootElement.GetProperty("hasHeaders").GetBoolean());
        }

        using (var updated = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Put,
                   $"/api/environments/{environmentId}",
                   new
                   {
                       name = "api-key-updated-environment",
                       baseUrl = "https://api-key-updated.example.test",
                       headers = new Dictionary<string, string>
                       {
                           ["X-Api-Key-Updated"] = "updated-secret"
                       }
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var document = await ReadJsonAsync(updated, cancellationToken);
            Assert.Equal(environmentId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(
                "api-key-updated-environment",
                document.RootElement.GetProperty("name").GetString());
            Assert.Equal(
                "https://api-key-updated.example.test",
                document.RootElement.GetProperty("baseUrl").GetString());
        }

        using (var updatedDetail = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/environments/{environmentId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, updatedDetail.StatusCode);
            using var document = await ReadJsonAsync(updatedDetail, cancellationToken);
            Assert.Equal(
                "updated-secret",
                document.RootElement.GetProperty("headers")
                    .GetProperty("X-Api-Key-Updated")
                    .GetString());
            Assert.False(
                document.RootElement.GetProperty("headers")
                    .TryGetProperty("X-Api-Key-Created", out _));
        }

        using (var deleted = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Delete,
                   $"/api/environments/{environmentId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var missing = await SendApiKeyAsync(
            client,
            rawApiKey,
            HttpMethod.Get,
            $"/api/environments/{environmentId}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static async Task ExerciseApiKeyEndpointActionsAsync(
        HttpClient client,
        string rawApiKey,
        TenantGraph projectA,
        CancellationToken cancellationToken)
    {
        using (var endpoints = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/environments/{projectA.EnvironmentId}/endpoints",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, endpoints.StatusCode);
            using var document = await ReadJsonAsync(endpoints, cancellationToken);
            var endpoint = Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal(projectA.EndpointId, endpoint.GetProperty("id").GetGuid());
            Assert.Equal(projectA.EnvironmentId, endpoint.GetProperty("environmentId").GetGuid());
        }

        using (var endpoint = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/endpoints/{projectA.EndpointId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, endpoint.StatusCode);
            using var document = await ReadJsonAsync(endpoint, cancellationToken);
            Assert.Equal(projectA.EndpointId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Contains(
                projectA.Secret,
                document.RootElement.GetProperty("name").GetString(),
                StringComparison.Ordinal);
        }

        Guid endpointId;
        using (var created = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Post,
                   $"/api/environments/{projectA.EnvironmentId}/endpoints",
                   new
                   {
                       name = "api-key-created-endpoint",
                       path = "/api-key-created",
                       httpMethod = "POST",
                       timeoutSeconds = 21
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var document = await ReadJsonAsync(created, cancellationToken);
            endpointId = document.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(projectA.EnvironmentId, document.RootElement.GetProperty("environmentId").GetGuid());
            Assert.Equal("/api-key-created", document.RootElement.GetProperty("path").GetString());
            Assert.Equal(21, document.RootElement.GetProperty("timeoutSeconds").GetInt32());
        }

        using (var updated = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Put,
                   $"/api/endpoints/{endpointId}",
                   new
                   {
                       name = "api-key-updated-endpoint",
                       path = "/api-key-updated",
                       httpMethod = "PATCH",
                       timeoutSeconds = 22
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var document = await ReadJsonAsync(updated, cancellationToken);
            Assert.Equal(endpointId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("api-key-updated-endpoint", document.RootElement.GetProperty("name").GetString());
            Assert.Equal("PATCH", document.RootElement.GetProperty("httpMethod").GetString());
            Assert.Equal(22, document.RootElement.GetProperty("timeoutSeconds").GetInt32());
        }

        using (var deleted = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Delete,
                   $"/api/endpoints/{endpointId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var missing = await SendApiKeyAsync(
            client,
            rawApiKey,
            HttpMethod.Get,
            $"/api/endpoints/{endpointId}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task ExerciseApiKeyMappingActionsAsync(
        HttpClient client,
        string rawApiKey,
        TenantGraph projectA,
        TenantGraph projectB,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using (var proposed = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Post,
                   $"/api/endpoints/{projectA.EndpointId}/mapping/propose",
                   new
                   {
                       sampleResponseJson = "{\"answer\":\"api-key-proposal\"}",
                       sampleRequestJson = "{\"prompt\":\"api-key-request\"}",
                       hints = new Dictionary<string, object>
                       {
                           ["integrationCorrelation"] = correlationId,
                           ["purpose"] = "tenant-api-key-positive-matrix"
                       }
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, proposed.StatusCode);
            using var document = await ReadJsonAsync(proposed, cancellationToken);
            Assert.Equal(
                "Mapping spec generated successfully based on sample structure",
                document.RootElement.GetProperty("reason").GetString());
            var proposedSpec = document.RootElement.GetProperty("mappingSpecJson").GetString();
            Assert.False(string.IsNullOrWhiteSpace(proposedSpec));
            using var mapping = JsonDocument.Parse(proposedSpec);
            Assert.Equal(1, mapping.RootElement.GetProperty("version").GetInt32());
            Assert.Equal(
                "$.answer",
                mapping.RootElement.GetProperty("fallback")
                    .GetProperty("singleAssistantContentPath")
                    .GetString());
        }

        var providerAfter = await fixture.ReadProviderEvidenceAsync(cancellationToken);
        var providerRequest = Assert.Single(
            providerAfter,
            record => record.CorrelationId == correlationId);
        Assert.Equal("POST", providerRequest.Method);
        Assert.Equal("/v1/chat/completions", providerRequest.Path);
        Assert.True(providerRequest.Authorized);
        Assert.True(providerRequest.ValidJson);
        Assert.Equal(correlationId, providerRequest.CorrelationId);

        using (var validated = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Post,
                   $"/api/endpoints/{projectA.EndpointId}/mapping/validate",
                   new
                   {
                       mappingSpecJson = ValidMappingSpec,
                       sampleResponseJson = "{\"answer\":\"api-key-preview\"}"
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
            using var document = await ReadJsonAsync(validated, cancellationToken);
            Assert.True(document.RootElement.GetProperty("success").GetBoolean());
            var message = Assert.Single(
                document.RootElement.GetProperty("previewTrace")
                    .GetProperty("messages")
                    .EnumerateArray());
            Assert.Equal("assistant", message.GetProperty("role").GetString());
            Assert.Equal("api-key-preview", message.GetProperty("content").GetString());
        }

        var mappingName = $"api-key-mapping-{Guid.NewGuid():N}";
        Guid mappingId;
        using (var created = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Post,
                   $"/api/endpoints/{projectA.EndpointId}/mapping",
                   new { name = mappingName, specJson = ValidMappingSpec },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var document = await ReadJsonAsync(created, cancellationToken);
            mappingId = document.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(projectA.EndpointId, document.RootElement.GetProperty("endpointId").GetGuid());
            Assert.Equal(mappingName, document.RootElement.GetProperty("name").GetString());
            Assert.Equal(ValidMappingSpec, document.RootElement.GetProperty("specJson").GetString());
        }

        using (var mappings = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/endpoints/{projectA.EndpointId}/mapping",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, mappings.StatusCode);
            var body = await mappings.Content.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain(projectB.MappingId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(body);
            var values = document.RootElement.EnumerateArray().ToArray();
            Assert.Contains(values, value => value.GetProperty("id").GetGuid() == projectA.MappingId);
            Assert.Contains(values, value => value.GetProperty("id").GetGuid() == mappingId);
            Assert.All(
                values,
                value => Assert.Equal(
                    projectA.EndpointId,
                    value.GetProperty("endpointId").GetGuid()));
        }

        const string updatedSpec =
            "{\"version\":1,\"fallback\":{\"singleAssistantContentPath\":\"$.result\"}}";
        using (var updated = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Put,
                   $"/api/mapping/{mappingId}",
                   new { name = "api-key-updated-mapping", specJson = updatedSpec },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var document = await ReadJsonAsync(updated, cancellationToken);
            Assert.Equal(mappingId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("api-key-updated-mapping", document.RootElement.GetProperty("name").GetString());
            Assert.Equal(updatedSpec, document.RootElement.GetProperty("specJson").GetString());
        }

        using (var setDefault = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Post,
                   $"/api/mapping/{mappingId}/set-default",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, setDefault.StatusCode);
        }

        using (var mapping = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/mapping/{mappingId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, mapping.StatusCode);
            using var document = await ReadJsonAsync(mapping, cancellationToken);
            Assert.Equal(mappingId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(projectA.EndpointId, document.RootElement.GetProperty("endpointId").GetGuid());
            Assert.True(document.RootElement.GetProperty("isDefault").GetBoolean());
            Assert.Equal(updatedSpec, document.RootElement.GetProperty("specJson").GetString());
        }

        using (var deleted = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Delete,
                   $"/api/mapping/{mappingId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var missing = await SendApiKeyAsync(
            client,
            rawApiKey,
            HttpMethod.Get,
            $"/api/mapping/{mappingId}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static async Task ExerciseApiKeySuiteAndTestActionsAsync(
        HttpClient client,
        string rawApiKey,
        TenantGraph projectA,
        TenantGraph projectB,
        CancellationToken cancellationToken)
    {
        var suiteName = $"api-key-suite-{Guid.NewGuid():N}";
        Guid suiteId;
        using (var created = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Post,
                   $"/api/suites?projectId={projectA.ProjectId}",
                   new { name = suiteName, description = "created by exact-project API key" },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var document = await ReadJsonAsync(created, cancellationToken);
            suiteId = document.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(projectA.ProjectId, document.RootElement.GetProperty("projectId").GetGuid());
            Assert.Equal(suiteName, document.RootElement.GetProperty("name").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("testCaseCount").GetInt32());
        }

        using (var suites = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/projects/{projectA.ProjectId}/suites",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, suites.StatusCode);
            var body = await suites.Content.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain(projectB.SuiteId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(body);
            var values = document.RootElement.EnumerateArray().ToArray();
            Assert.Contains(values, value => value.GetProperty("id").GetGuid() == projectA.SuiteId);
            Assert.Contains(values, value => value.GetProperty("id").GetGuid() == suiteId);
            Assert.All(
                values,
                value => Assert.Equal(
                    projectA.ProjectId,
                    value.GetProperty("projectId").GetGuid()));
        }

        using (var updated = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Put,
                   $"/api/suites/{suiteId}",
                   new
                   {
                       name = "api-key-updated-suite",
                       description = "updated by exact-project API key"
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var document = await ReadJsonAsync(updated, cancellationToken);
            Assert.Equal(suiteId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("api-key-updated-suite", document.RootElement.GetProperty("name").GetString());
            Assert.Equal(
                "updated by exact-project API key",
                document.RootElement.GetProperty("description").GetString());
        }

        var testExternalId = $"api-key-test-{Guid.NewGuid():N}";
        Guid testId;
        using (var created = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Post,
                   $"/api/suites/{suiteId}/tests",
                   new
                   {
                       externalId = testExternalId,
                       name = "API key created test",
                       description = "created by exact-project API key",
                       inputSpecJson = "{\"prompt\":\"api-key-created\"}",
                       expectationsJson = "[{\"type\":\"contains\",\"value\":\"created\"}]"
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var document = await ReadJsonAsync(created, cancellationToken);
            testId = document.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(suiteId, document.RootElement.GetProperty("suiteId").GetGuid());
            Assert.Equal(testExternalId, document.RootElement.GetProperty("externalId").GetString());
            Assert.Equal(
                "{\"prompt\":\"api-key-created\"}",
                document.RootElement.GetProperty("inputSpecJson").GetString());
        }

        var importedExternalId = $"api-key-import-{Guid.NewGuid():N}";
        using (var import = CreateImportRequest(suiteId, importedExternalId))
        using (var imported = await SendWithApiKeyAsync(
                   client,
                   import,
                   rawApiKey,
                   bearerToken: null,
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
            using var document = await ReadJsonAsync(imported, cancellationToken);
            Assert.Equal(1, document.RootElement.GetProperty("importedCount").GetInt32());
            Assert.Equal(
                importedExternalId,
                Assert.Single(document.RootElement.GetProperty("importedTestIds").EnumerateArray())
                    .GetString());
        }

        using (var tests = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/suites/{suiteId}/tests",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, tests.StatusCode);
            using var document = await ReadJsonAsync(tests, cancellationToken);
            var values = document.RootElement.EnumerateArray().ToArray();
            Assert.Equal(2, values.Length);
            Assert.Contains(values, value => value.GetProperty("id").GetGuid() == testId);
            Assert.Contains(
                values,
                value => value.GetProperty("externalId").GetString() == importedExternalId);
            Assert.All(
                values,
                value => Assert.Equal(suiteId, value.GetProperty("suiteId").GetGuid()));
        }

        var updatedExternalId = $"api-key-updated-test-{Guid.NewGuid():N}";
        using (var updated = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Put,
                   $"/api/tests/{testId}",
                   new
                   {
                       externalId = updatedExternalId,
                       name = "API key updated test",
                       description = "updated by exact-project API key",
                       inputSpecJson = "{\"prompt\":\"api-key-updated\"}",
                       expectationsJson = "[{\"type\":\"contains\",\"value\":\"updated\"}]"
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var document = await ReadJsonAsync(updated, cancellationToken);
            Assert.Equal(testId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(updatedExternalId, document.RootElement.GetProperty("externalId").GetString());
            Assert.Equal("API key updated test", document.RootElement.GetProperty("name").GetString());
            Assert.Equal(
                "{\"prompt\":\"api-key-updated\"}",
                document.RootElement.GetProperty("inputSpecJson").GetString());
        }

        using (var test = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/tests/{testId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, test.StatusCode);
            using var document = await ReadJsonAsync(test, cancellationToken);
            Assert.Equal(testId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(suiteId, document.RootElement.GetProperty("suiteId").GetGuid());
            Assert.Equal(updatedExternalId, document.RootElement.GetProperty("externalId").GetString());
            Assert.Equal(
                "[{\"type\":\"contains\",\"value\":\"updated\"}]",
                document.RootElement.GetProperty("expectationsJson").GetString());
        }

        using (var suite = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/suites/{suiteId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, suite.StatusCode);
            using var document = await ReadJsonAsync(suite, cancellationToken);
            Assert.Equal(suiteId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(projectA.ProjectId, document.RootElement.GetProperty("projectId").GetGuid());
            Assert.Equal(2, document.RootElement.GetProperty("testCaseCount").GetInt32());
        }

        using (var exported = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/suites/{suiteId}/tests/export",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
            Assert.Equal("application/x-yaml", exported.Content.Headers.ContentType?.MediaType);
            var yaml = await exported.Content.ReadAsStringAsync(cancellationToken);
            Assert.Contains(updatedExternalId, yaml, StringComparison.Ordinal);
            Assert.Contains(importedExternalId, yaml, StringComparison.Ordinal);
            Assert.Contains("API key updated test", yaml, StringComparison.Ordinal);
            Assert.Contains("API key imported test", yaml, StringComparison.Ordinal);
        }

        using (var deletedTest = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Delete,
                   $"/api/tests/{testId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deletedTest.StatusCode);
        }

        using (var missingTest = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/tests/{testId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, missingTest.StatusCode);
        }

        using (var deletedSuite = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Delete,
                   $"/api/suites/{suiteId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deletedSuite.StatusCode);
        }

        using var missingSuite = await SendApiKeyAsync(
            client,
            rawApiKey,
            HttpMethod.Get,
            $"/api/suites/{suiteId}",
            cancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, missingSuite.StatusCode);
    }

    private static async Task ExerciseApiKeyRunActionsAsync(
        HttpClient client,
        string rawApiKey,
        TenantGraph projectA,
        TenantGraph projectB,
        CancellationToken cancellationToken)
    {
        var gitCommit = $"api-key-run-{Guid.NewGuid():N}";
        const string configSnapshot = "{\"apiKeyPositiveMatrix\":true}";
        Guid runId;
        using (var queued = await SendApiKeyJsonAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Post,
                   "/api/runs",
                   new
                   {
                       suiteId = projectA.SuiteId,
                       environmentId = projectA.EnvironmentId,
                       endpointId = projectA.EndpointId,
                       mappingSpecId = projectA.MappingId,
                       gitCommitHash = gitCommit,
                       configSnapshotJson = configSnapshot
                   },
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.Created, queued.StatusCode);
            using var document = await ReadJsonAsync(queued, cancellationToken);
            runId = document.RootElement.GetProperty("id").GetGuid();
            Assert.Equal(projectA.SuiteId, document.RootElement.GetProperty("suiteId").GetGuid());
            Assert.Equal(
                projectA.EnvironmentId,
                document.RootElement.GetProperty("environmentId").GetGuid());
            Assert.Equal(projectA.EndpointId, document.RootElement.GetProperty("endpointId").GetGuid());
            Assert.Equal(
                projectA.MappingId,
                document.RootElement.GetProperty("mappingSpecId").GetGuid());
            Assert.Equal((int)TestRunStatus.Queued, document.RootElement.GetProperty("status").GetInt32());
            Assert.Equal(gitCommit, document.RootElement.GetProperty("gitCommitHash").GetString());
            Assert.Equal(
                configSnapshot,
                document.RootElement.GetProperty("configSnapshotJson").GetString());
        }

        using (var run = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/runs/{runId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            using var document = await ReadJsonAsync(run, cancellationToken);
            Assert.Equal(runId, document.RootElement.GetProperty("id").GetGuid());
            Assert.Equal(projectA.SuiteId, document.RootElement.GetProperty("suiteId").GetGuid());
            Assert.Equal(gitCommit, document.RootElement.GetProperty("gitCommitHash").GetString());
            Assert.Equal(
                configSnapshot,
                document.RootElement.GetProperty("configSnapshotJson").GetString());
        }

        using (var runs = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/suites/{projectA.SuiteId}/runs?status=Queued&limit=20",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, runs.StatusCode);
            var body = await runs.Content.ReadAsStringAsync(cancellationToken);
            Assert.DoesNotContain(projectB.RunId.ToString(), body, StringComparison.OrdinalIgnoreCase);
            using var document = JsonDocument.Parse(body);
            var values = document.RootElement.EnumerateArray().ToArray();
            Assert.Contains(values, value => value.GetProperty("id").GetGuid() == projectA.RunId);
            Assert.Contains(values, value => value.GetProperty("id").GetGuid() == runId);
            Assert.All(
                values,
                value => Assert.Equal(
                    projectA.SuiteId,
                    value.GetProperty("suiteId").GetGuid()));
        }

        using (var results = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/runs/{projectA.RunId}/results",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, results.StatusCode);
            using var document = await ReadJsonAsync(results, cancellationToken);
            var result = Assert.Single(document.RootElement.EnumerateArray());
            AssertRunResult(projectA, result);
        }

        using (var result = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/runs/{projectA.RunId}/results/{projectA.ResultId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            using var document = await ReadJsonAsync(result, cancellationToken);
            AssertRunResult(projectA, document.RootElement);
        }
    }

    private async Task ExerciseApiKeyProjectDeleteAsync(
        HttpClient client,
        PromptlyApiClient owner,
        CancellationToken cancellationToken)
    {
        var projectId = await owner.CreateProjectAsync(
            $"api-key-empty-delete-{Guid.NewGuid():N}");
        var rawApiKey = $"promptly-integration-delete-{Guid.NewGuid():N}";
        await SeedProjectApiKeyAsync(projectId, rawApiKey, cancellationToken);

        using (var project = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Get,
                   $"/api/projects/{projectId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, project.StatusCode);
            using var document = await ReadJsonAsync(project, cancellationToken);
            Assert.Equal(projectId, document.RootElement.GetProperty("id").GetGuid());
        }

        using (var deleted = await SendApiKeyAsync(
                   client,
                   rawApiKey,
                   HttpMethod.Delete,
                   $"/api/projects/{projectId}",
                   cancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        await using var scope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
        Assert.False(await dbContext.Projects.AnyAsync(
            project => project.Id == projectId,
            cancellationToken));
        Assert.False(await dbContext.ProjectApiKeys.AnyAsync(
            apiKey => apiKey.ProjectId == projectId,
            cancellationToken));
    }

    private static void AssertRunResult(TenantGraph graph, JsonElement result)
    {
        Assert.Equal(graph.ResultId, result.GetProperty("id").GetGuid());
        Assert.Equal(graph.RunId, result.GetProperty("runId").GetGuid());
        Assert.Equal(graph.TestId, result.GetProperty("testCaseId").GetGuid());
        Assert.Equal((int)TestResultStatus.Fail, result.GetProperty("status").GetInt32());
        Assert.Equal(graph.ResultTrace, result.GetProperty("traceJson").GetString());
        Assert.Equal(graph.ResultMetrics, result.GetProperty("metricsJson").GetString());
        Assert.Equal(graph.FailureReasons, result.GetProperty("failureReasonsJson").GetString());
        Assert.Equal(graph.TestName, result.GetProperty("testCaseName").GetString());
        Assert.Equal(graph.TestExternalId, result.GetProperty("testCaseExternalId").GetString());
    }

    private static async Task<HttpResponseMessage> SendApiKeyAsync(
        HttpClient client,
        string rawApiKey,
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        return await SendWithApiKeyAsync(
            client,
            request,
            rawApiKey,
            bearerToken: null,
            cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendApiKeyJsonAsync(
        HttpClient client,
        string rawApiKey,
        HttpMethod method,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = JsonRequest(method, path, body);
        return await SendWithApiKeyAsync(
            client,
            request,
            rawApiKey,
            bearerToken: null,
            cancellationToken);
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        return JsonDocument.Parse(
            await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static HttpRequestMessage CreateImportRequest(Guid suiteId, string externalId)
    {
        var yaml = $$"""
            - id: {{externalId}}
              name: API key imported test
              description: Imported through exact-project API key
              input:
                prompt: imported-prompt
              expectations:
                - type: contains
                  value: imported
            """;
        var multipart = new MultipartFormDataContent();
        var file = new StringContent(yaml, Encoding.UTF8);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-yaml");
        multipart.Add(file, "file", "api-key-import.yaml");
        return new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/suites/{suiteId}/tests/import")
        {
            Content = multipart
        };
    }

    private async Task SeedProjectApiKeyAsync(
        Guid projectId,
        string rawApiKey,
        CancellationToken cancellationToken)
    {
        await using var scope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
        dbContext.ProjectApiKeys.Add(new ProjectApiKey
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Name = "Tenant isolation integration key",
            KeyHashSha256 = Convert.ToBase64String(
                SHA256.HashData(Encoding.UTF8.GetBytes(rawApiKey))),
            KeyLastFourChars = rawApiKey[^4..]
        });
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> CountRunsAsync(CancellationToken cancellationToken)
    {
        await using var scope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
        return await dbContext.TestRuns.CountAsync(cancellationToken);
    }

    private async Task<TenantGraphState> CaptureStateAsync(
        TenantGraph graph,
        CancellationToken cancellationToken)
    {
        await using var scope = fixture.PrimaryHost.Factory.Services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<PromptlyDbContext>();
        var project = await dbContext.Projects.AsNoTracking().SingleOrDefaultAsync(
            entity => entity.Id == graph.ProjectId,
            cancellationToken);
        var environment = await dbContext.Environments.AsNoTracking().SingleOrDefaultAsync(
            entity => entity.Id == graph.EnvironmentId,
            cancellationToken);
        var endpoint = await dbContext.Endpoints.AsNoTracking().SingleOrDefaultAsync(
            entity => entity.Id == graph.EndpointId,
            cancellationToken);
        var mapping = await dbContext.MappingSpecs.AsNoTracking().SingleOrDefaultAsync(
            entity => entity.Id == graph.MappingId,
            cancellationToken);
        var suite = await dbContext.TestSuites.AsNoTracking().SingleOrDefaultAsync(
            entity => entity.Id == graph.SuiteId,
            cancellationToken);
        var test = await dbContext.TestCases.AsNoTracking().SingleOrDefaultAsync(
            entity => entity.Id == graph.TestId,
            cancellationToken);
        var run = await dbContext.TestRuns.AsNoTracking().SingleOrDefaultAsync(
            entity => entity.Id == graph.RunId,
            cancellationToken);
        var result = await dbContext.TestRunResults.AsNoTracking().SingleOrDefaultAsync(
            entity => entity.Id == graph.ResultId,
            cancellationToken);

        return new TenantGraphState(
            await dbContext.Projects.CountAsync(cancellationToken),
            await dbContext.Environments.CountAsync(cancellationToken),
            await dbContext.Endpoints.CountAsync(cancellationToken),
            await dbContext.MappingSpecs.CountAsync(cancellationToken),
            await dbContext.TestSuites.CountAsync(cancellationToken),
            await dbContext.TestCases.CountAsync(cancellationToken),
            await dbContext.TestRuns.CountAsync(cancellationToken),
            await dbContext.TestRunResults.CountAsync(cancellationToken),
            project?.Name,
            project?.Description,
            environment?.Name,
            environment?.BaseUrl,
            environment?.DefaultHeadersEncryptedJson,
            endpoint?.Name,
            endpoint?.Path,
            endpoint?.HttpMethod,
            endpoint?.TimeoutSeconds,
            mapping?.Name,
            mapping?.SpecJson,
            mapping?.IsDefault,
            mapping?.UpdatedAt,
            suite?.Name,
            suite?.Description,
            test?.ExternalId,
            test?.Name,
            test?.Description,
            test?.InputSpecJson,
            test?.ExpectationsJson,
            test?.UpdatedAt,
            run?.GitCommitHash,
            run?.ConfigSnapshotJson,
            run?.SummaryJson,
            result?.TraceJson,
            result?.MetricsJson,
            result?.FailureReasonsJson);
    }

    private static async Task<DeniedObservation> ObserveAsync(
        string name,
        HttpResponseMessage response,
        TenantGraph graph,
        CancellationToken cancellationToken)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var leakedValues = graph.ForbiddenValues
                .Where(value => body.Contains(value, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return new DeniedObservation(name, response.StatusCode, body, leakedValues);
        }
    }

    private static async Task<DeniedObservation> SendAndObserveAsync(
        string name,
        HttpRequestMessage request,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> sendAsync,
        TenantGraph graph,
        CancellationToken cancellationToken)
    {
        using (request)
        {
            return await ObserveAsync(
                name,
                await sendAsync(request),
                graph,
                cancellationToken);
        }
    }

    private static void AssertDeniedMatrix(
        IReadOnlyCollection<DeniedObservation> observations,
        TenantGraphState stateBefore,
        TenantGraphState stateAfter,
        IReadOnlyList<ProviderRequestEvidence>? providerEvidenceBefore,
        IReadOnlyList<ProviderRequestEvidence>? providerEvidenceAfter)
    {
        var failures = new List<string>();
        foreach (var observation in observations)
        {
            if (observation.StatusCode != HttpStatusCode.NotFound)
            {
                failures.Add(
                    $"{observation.Name}: expected 404, received " +
                    $"{(int)observation.StatusCode} ({observation.StatusCode}); " +
                    $"body={observation.Body}");
            }

            if (observation.LeakedValues.Count > 0)
            {
                failures.Add(
                    $"{observation.Name}: response leaked victim data: " +
                    string.Join(", ", observation.LeakedValues));
            }
        }

        if (stateBefore != stateAfter)
        {
            failures.Add(
                "Denied requests changed tenant data or inserted rows. " +
                $"Before={stateBefore}; After={stateAfter}");
        }

        if (providerEvidenceBefore is not null
            && providerEvidenceAfter is not null
            && !providerEvidenceBefore.Select(record => record.Sequence).SequenceEqual(
                providerEvidenceAfter.Select(record => record.Sequence)))
        {
            failures.Add(
                "Denied mapping proposal reached the provider. " +
                $"Before sequences=[{string.Join(',', providerEvidenceBefore.Select(r => r.Sequence))}]; " +
                $"after=[{string.Join(',', providerEvidenceAfter.Select(r => r.Sequence))}]");
        }

        Assert.True(failures.Count == 0, string.Join(System.Environment.NewLine, failures));
    }

    private static async Task<HttpResponseMessage> SendWithApiKeyAsync(
        HttpClient client,
        HttpRequestMessage request,
        string rawApiKey,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        request.Headers.Add("X-API-Key", rawApiKey);
        if (bearerToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }

        return await client.SendAsync(request, cancellationToken);
    }

    private static HttpRequestMessage JsonRequest(HttpMethod method, string path, object body) =>
        new(method, path)
        {
            Content = JsonContent.Create(body)
        };

    private static HttpRequestMessage ImportRequest(Guid suiteId)
    {
        var yaml = """
            - id: attacker-import
              name: Attacker import
              input:
                prompt: denied
              expectations:
                - type: contains
                  value: denied
            """;
        var multipart = new MultipartFormDataContent();
        var file = new StringContent(yaml, Encoding.UTF8);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/x-yaml");
        multipart.Add(file, "file", "attacker.yaml");
        return new HttpRequestMessage(HttpMethod.Post, $"/api/suites/{suiteId}/tests/import")
        {
            Content = multipart
        };
    }

    private static object TestRequest(string externalId) => new
    {
        externalId,
        name = $"name-{externalId}",
        description = "denied",
        inputSpecJson = "{\"prompt\":\"denied\"}",
        expectationsJson = "[]"
    };

    private sealed record DeniedOperation(
        string Name,
        Func<HttpRequestMessage> CreateRequest);

    private sealed record DeniedObservation(
        string Name,
        HttpStatusCode StatusCode,
        string Body,
        IReadOnlyCollection<string> LeakedValues);

    private sealed record TenantGraph(
        Guid ProjectId,
        Guid EnvironmentId,
        Guid EndpointId,
        Guid MappingId,
        Guid SuiteId,
        Guid TestId,
        Guid RunId,
        Guid ResultId,
        string Secret,
        string HeaderSecret,
        string MappingName,
        string SuiteName,
        string TestExternalId,
        string TestName,
        string TestInput,
        string TestExpectations,
        string RunConfig,
        string RunSummary,
        string ResultTrace,
        string ResultMetrics,
        string FailureReasons)
    {
        public IReadOnlyCollection<string> ForbiddenValues =>
        [
            Secret,
            HeaderSecret,
            MappingName,
            SuiteName,
            TestExternalId,
            TestName,
            TestInput,
            TestExpectations,
            RunConfig,
            RunSummary,
            ResultTrace,
            ResultMetrics,
            FailureReasons,
            ProjectId.ToString(),
            EnvironmentId.ToString(),
            EndpointId.ToString(),
            MappingId.ToString(),
            SuiteId.ToString(),
            TestId.ToString(),
            RunId.ToString(),
            ResultId.ToString()
        ];
    }

    private sealed record TenantGraphState(
        int ProjectCount,
        int EnvironmentCount,
        int EndpointCount,
        int MappingCount,
        int SuiteCount,
        int TestCount,
        int RunCount,
        int ResultCount,
        string? ProjectName,
        string? ProjectDescription,
        string? EnvironmentName,
        string? EnvironmentBaseUrl,
        string? EncryptedHeaders,
        string? EndpointName,
        string? EndpointPath,
        string? EndpointMethod,
        int? EndpointTimeoutSeconds,
        string? MappingName,
        string? MappingSpec,
        bool? MappingIsDefault,
        DateTime? MappingUpdatedAt,
        string? SuiteName,
        string? SuiteDescription,
        string? TestExternalId,
        string? TestName,
        string? TestDescription,
        string? TestInput,
        string? TestExpectations,
        DateTime? TestUpdatedAt,
        string? RunGitCommit,
        string? RunConfig,
        string? RunSummary,
        string? ResultTrace,
        string? ResultMetrics,
        string? FailureReasons);
}
