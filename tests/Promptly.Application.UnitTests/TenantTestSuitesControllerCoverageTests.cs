using System.Diagnostics.CodeAnalysis;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Server.Controllers;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class TenantTestSuitesControllerCoverageTests
{
    private static readonly TenantAccessScope Scope = new("owner", Guid.NewGuid());

    [Fact]
    public async Task Successful_actions_project_the_public_suite_import_and_export_contracts()
    {
        var suite = CreateSuite();
        suite.TestCases.Add(CreateTestCase(suite.Id, "existing"));
        var imported = CreateTestCase(suite.Id, "imported");
        var suiteService = new StubTestSuiteService
        {
            Created = suite,
            Listed = [suite],
            Loaded = suite,
            Updated = suite,
            Deleted = true
        };
        var testCaseService = new StubTestCaseService
        {
            Listed = [imported],
            BulkCreated = [imported]
        };
        var yamlService = new StubYamlService
        {
            Deserialized = [imported],
            Serialized = "- id: imported"
        };
        var controller = CreateController(suiteService, testCaseService, yamlService);

        var created = Assert.IsType<CreatedAtActionResult>(await controller.CreateTestSuite(
            new CreateTestSuiteRequest { Name = "suite", Description = "description" },
            suite.ProjectId));
        Assert.Equal(nameof(TestSuitesController.GetTestSuite), created.ActionName);
        Assert.Equal(suite.Id, Assert.IsType<TestSuiteResponse>(created.Value).Id);

        var listed = Assert.IsType<OkObjectResult>(
            await controller.GetTestSuitesByProject(suite.ProjectId));
        var listedResponse = Assert.Single(Assert.IsType<List<TestSuiteResponse>>(listed.Value));
        Assert.Equal(1, listedResponse.TestCaseCount);

        var loaded = Assert.IsType<OkObjectResult>(await controller.GetTestSuite(suite.Id));
        Assert.Equal(suite.Name, Assert.IsType<TestSuiteResponse>(loaded.Value).Name);

        var updated = Assert.IsType<OkObjectResult>(await controller.UpdateTestSuite(
            suite.Id,
            new UpdateTestSuiteRequest { Name = "updated", Description = "updated description" }));
        Assert.Equal(suite.Id, Assert.IsType<TestSuiteResponse>(updated.Value).Id);
        Assert.IsType<NoContentResult>(await controller.DeleteTestSuite(suite.Id));

        var import = Assert.IsType<OkObjectResult>(
            await controller.ImportTests(suite.Id, CreateFile("- id: imported")));
        var importResponse = Assert.IsType<ImportTestsResponse>(import.Value);
        Assert.Equal(1, importResponse.ImportedCount);
        Assert.Equal(["imported"], importResponse.ImportedTestIds);
        Assert.Equal("- id: imported", yamlService.LastYaml);
        Assert.Equal(Scope, testCaseService.LastScope);

        var export = Assert.IsType<FileContentResult>(await controller.ExportTests(suite.Id));
        Assert.Equal("application/x-yaml", export.ContentType);
        Assert.Equal("Suite_Name_tests.yaml", export.FileDownloadName);
        Assert.Equal("- id: imported", Encoding.UTF8.GetString(export.FileContents));
        Assert.Equal([imported], yamlService.LastSerializedTests);
    }

    [Fact]
    public async Task Missing_scope_rejects_every_action_before_service_access()
    {
        var suiteService = new StubTestSuiteService();
        var controller = CreateController(
            suiteService,
            new StubTestCaseService(),
            new StubYamlService(),
            hasScope: false);

        var results = new IActionResult[]
        {
            await controller.CreateTestSuite(
                new CreateTestSuiteRequest { Name = "suite" },
                Guid.NewGuid()),
            await controller.GetTestSuitesByProject(Guid.NewGuid()),
            await controller.GetTestSuite(Guid.NewGuid()),
            await controller.UpdateTestSuite(
                Guid.NewGuid(),
                new UpdateTestSuiteRequest { Name = "suite" }),
            await controller.DeleteTestSuite(Guid.NewGuid()),
            await controller.ImportTests(Guid.NewGuid(), CreateFile("tests: []")),
            await controller.ExportTests(Guid.NewGuid())
        };

        Assert.All(results, result => Assert.IsType<UnauthorizedResult>(result));
        Assert.Equal(0, suiteService.CallCount);
    }

    [Fact]
    public async Task Inaccessible_resources_return_not_found_for_every_action()
    {
        var controller = CreateController(
            new StubTestSuiteService(),
            new StubTestCaseService(),
            new StubYamlService());
        var id = Guid.NewGuid();

        var results = new IActionResult[]
        {
            await controller.CreateTestSuite(
                new CreateTestSuiteRequest { Name = "suite" },
                id),
            await controller.GetTestSuitesByProject(id),
            await controller.GetTestSuite(id),
            await controller.UpdateTestSuite(id, new UpdateTestSuiteRequest { Name = "suite" }),
            await controller.DeleteTestSuite(id),
            await controller.ImportTests(id, CreateFile("tests: []")),
            await controller.ExportTests(id)
        };

        Assert.All(results, result => Assert.IsType<NotFoundObjectResult>(result));
    }

    [Fact]
    public async Task Import_rejects_null_empty_invalid_and_lost_parent_inputs()
    {
        var suite = CreateSuite();
        var suiteService = new StubTestSuiteService { Loaded = suite };
        var testCases = new StubTestCaseService();
        var yaml = new StubYamlService { Deserialized = [CreateTestCase(suite.Id, "imported")] };
        var controller = CreateController(suiteService, testCases, yaml);

        Assert.IsType<BadRequestObjectResult>(await controller.ImportTests(suite.Id, null!));
        Assert.IsType<BadRequestObjectResult>(
            await controller.ImportTests(suite.Id, CreateFile(string.Empty)));
        Assert.IsType<NotFoundObjectResult>(
            await controller.ImportTests(suite.Id, CreateFile("tests: []")));

        yaml.DeserializeFailure = new InvalidOperationException("invalid yaml");
        var invalid = Assert.IsType<BadRequestObjectResult>(
            await controller.ImportTests(suite.Id, CreateFile("invalid")));
        Assert.Contains("invalid yaml", invalid.Value!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_serializes_an_empty_collection_when_service_returns_no_tests()
    {
        var suite = CreateSuite();
        var yaml = new StubYamlService { Serialized = "[]" };
        var controller = CreateController(
            new StubTestSuiteService { Loaded = suite },
            new StubTestCaseService { Listed = null },
            yaml);

        var result = Assert.IsType<FileContentResult>(await controller.ExportTests(suite.Id));

        Assert.Equal("[]", Encoding.UTF8.GetString(result.FileContents));
        Assert.Empty(Assert.IsType<List<TestCase>>(yaml.LastSerializedTests));
    }

    [Theory]
    [InlineData(SuiteAction.Create)]
    [InlineData(SuiteAction.List)]
    [InlineData(SuiteAction.Get)]
    [InlineData(SuiteAction.Update)]
    [InlineData(SuiteAction.Delete)]
    [InlineData(SuiteAction.Import)]
    [InlineData(SuiteAction.Export)]
    public async Task Unexpected_service_failures_return_internal_server_error(SuiteAction action)
    {
        var suiteService = new StubTestSuiteService
        {
            Failure = new ApplicationException("storage unavailable")
        };
        var controller = CreateController(
            suiteService,
            new StubTestCaseService(),
            new StubYamlService());

        var result = await InvokeAsync(controller, action);

        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    private static Task<IActionResult> InvokeAsync(
        TestSuitesController controller,
        SuiteAction action)
    {
        var id = Guid.NewGuid();
        return action switch
        {
            SuiteAction.Create => controller.CreateTestSuite(
                new CreateTestSuiteRequest { Name = "suite" },
                id),
            SuiteAction.List => controller.GetTestSuitesByProject(id),
            SuiteAction.Get => controller.GetTestSuite(id),
            SuiteAction.Update => controller.UpdateTestSuite(
                id,
                new UpdateTestSuiteRequest { Name = "suite" }),
            SuiteAction.Delete => controller.DeleteTestSuite(id),
            SuiteAction.Import => controller.ImportTests(id, CreateFile("tests: []")),
            SuiteAction.Export => controller.ExportTests(id),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    private static TestSuitesController CreateController(
        StubTestSuiteService suiteService,
        StubTestCaseService testCaseService,
        StubYamlService yamlService,
        bool hasScope = true) =>
        new(
            suiteService,
            testCaseService,
            yamlService,
            new StubScopeAccessor(hasScope),
            NullLogger<TestSuitesController>.Instance);

    private static TestSuite CreateSuite() => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = Scope.ProjectId!.Value,
        Name = "Suite Name",
        Description = "description",
        CreatedAt = new DateTime(2026, 8, 12, 1, 2, 3, DateTimeKind.Utc)
    };

    private static TestCase CreateTestCase(Guid suiteId, string externalId) => new()
    {
        Id = Guid.NewGuid(),
        SuiteId = suiteId,
        ExternalId = externalId,
        Name = $"name-{externalId}",
        Description = "description",
        InputSpecJson = "{}",
        ExpectationsJson = "[]"
    };

    private static FormFile CreateFile(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "tests.yaml");
    }

    public enum SuiteAction
    {
        Create,
        List,
        Get,
        Update,
        Delete,
        Import,
        Export
    }

    private sealed class StubScopeAccessor(bool hasScope) : ITenantAccessScopeAccessor
    {
        public bool TryGetScope([NotNullWhen(true)] out TenantAccessScope? scope)
        {
            scope = hasScope ? Scope : null;
            return hasScope;
        }
    }

    private sealed class StubTestSuiteService : ITestSuiteService
    {
        public TestSuite? Created { get; init; }
        public IReadOnlyList<TestSuite>? Listed { get; init; }
        public TestSuite? Loaded { get; init; }
        public TestSuite? Updated { get; init; }
        public bool Deleted { get; init; }
        public Exception? Failure { get; init; }
        public int CallCount { get; private set; }

        public Task<TestSuite?> CreateTestSuiteAsync(
            Guid projectId,
            string name,
            string? description,
            TenantAccessScope scope) => Return(Created);

        public Task<IReadOnlyList<TestSuite>?> GetTestSuitesByProjectAsync(
            Guid projectId,
            TenantAccessScope scope) => Return(Listed);

        public Task<TestSuite?> GetTestSuiteByIdAsync(Guid id, TenantAccessScope scope) =>
            Return(Loaded);

        public Task<TestSuite?> UpdateTestSuiteAsync(
            Guid id,
            string name,
            string? description,
            TenantAccessScope scope) => Return(Updated);

        public Task<bool> DeleteTestSuiteAsync(Guid id, TenantAccessScope scope) => Return(Deleted);

        private Task<T> Return<T>(T value)
        {
            CallCount++;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(value);
        }
    }

    private sealed class StubTestCaseService : ITestCaseService
    {
        public IReadOnlyList<TestCase>? Listed { get; init; }
        public IReadOnlyList<TestCase>? BulkCreated { get; init; }
        public TenantAccessScope? LastScope { get; private set; }

        public Task<TestCase?> CreateTestCaseAsync(
            Guid suiteId,
            string externalId,
            string name,
            string? description,
            string inputSpecJson,
            string expectationsJson,
            TenantAccessScope scope) => Task.FromResult<TestCase?>(null);

        public Task<IReadOnlyList<TestCase>?> GetTestCasesBySuiteAsync(
            Guid suiteId,
            TenantAccessScope scope) => Task.FromResult(Listed);

        public Task<TestCase?> GetTestCaseByIdAsync(Guid id, TenantAccessScope scope) =>
            Task.FromResult<TestCase?>(null);

        public Task<TestCase?> UpdateTestCaseAsync(
            Guid id,
            string externalId,
            string name,
            string? description,
            string inputSpecJson,
            string expectationsJson,
            TenantAccessScope scope) => Task.FromResult<TestCase?>(null);

        public Task<bool> DeleteTestCaseAsync(Guid id, TenantAccessScope scope) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<TestCase>?> BulkCreateTestsAsync(
            Guid suiteId,
            List<TestCase> testCases,
            TenantAccessScope scope)
        {
            LastScope = scope;
            return Task.FromResult(BulkCreated);
        }
    }

    private sealed class StubYamlService : IYamlService
    {
        public List<TestCase> Deserialized { get; init; } = [];
        public string Serialized { get; init; } = string.Empty;
        public Exception? DeserializeFailure { get; set; }
        public string? LastYaml { get; private set; }
        public List<TestCase>? LastSerializedTests { get; private set; }

        public List<TestCase> DeserializeTests(string yamlContent, Guid suiteId)
        {
            LastYaml = yamlContent;
            if (DeserializeFailure is not null)
            {
                throw DeserializeFailure;
            }

            return Deserialized;
        }

        public string SerializeTests(List<TestCase> testCases)
        {
            LastSerializedTests = testCases;
            return Serialized;
        }
    }
}
