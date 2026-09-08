using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Data;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Application.Services;
using Promptly.Domain.Entities;

namespace Promptly.Application.UnitTests;

public sealed class TenantSuiteAndTestServiceTests
{
    [Fact]
    public async Task Api_key_scope_cannot_list_or_create_in_a_same_owner_sibling_project()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = new TestSuiteService(
            dbContext,
            NullLogger<TestSuiteService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);

        var inaccessible = await service.GetTestSuitesByProjectAsync(
            graph.SiblingProjectId,
            scope);
        var created = await service.CreateTestSuiteAsync(
            graph.SiblingProjectId,
            "intruder suite",
            null,
            scope);

        Assert.Null(inaccessible);
        Assert.Null(created);
        Assert.DoesNotContain(
            dbContext.TestSuites,
            suite => suite.Name == "intruder suite");
    }

    [Fact]
    public async Task Owned_empty_suite_collection_is_distinct_from_an_inaccessible_project()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var emptyProject = new Project
        {
            Id = Guid.NewGuid(),
            OwnerUserId = graph.OwnerId,
            Name = "empty project"
        };
        dbContext.Projects.Add(emptyProject);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        var service = new TestSuiteService(
            dbContext,
            NullLogger<TestSuiteService>.Instance);
        var jwtScope = new TenantAccessScope(graph.OwnerId, ProjectId: null);

        var ownedEmpty = await service.GetTestSuitesByProjectAsync(emptyProject.Id, jwtScope);
        var inaccessible = await service.GetTestSuitesByProjectAsync(
            graph.OtherProjectId,
            jwtScope);

        Assert.NotNull(ownedEmpty);
        Assert.Empty(ownedEmpty);
        Assert.Null(inaccessible);
    }

    [Fact]
    public async Task Foreign_suite_mutations_are_absent_and_leave_the_row_unchanged()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = new TestSuiteService(
            dbContext,
            NullLogger<TestSuiteService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);

        var loaded = await service.GetTestSuiteByIdAsync(graph.SiblingSuiteId, scope);
        var updated = await service.UpdateTestSuiteAsync(
            graph.SiblingSuiteId,
            "tampered",
            "tampered",
            scope);
        var deleted = await service.DeleteTestSuiteAsync(graph.SiblingSuiteId, scope);

        Assert.Null(loaded);
        Assert.Null(updated);
        Assert.False(deleted);
        var persisted = await dbContext.TestSuites.FindAsync(
            [graph.SiblingSuiteId],
            TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal("sibling suite", persisted.Name);
    }

    [Fact]
    public async Task Owned_suite_crud_succeeds_and_includes_test_count_navigation()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = new TestSuiteService(
            dbContext,
            NullLogger<TestSuiteService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);

        var created = await service.CreateTestSuiteAsync(
            graph.AllowedProjectId,
            "created",
            "description",
            scope);
        Assert.NotNull(created);

        var updated = await service.UpdateTestSuiteAsync(
            created.Id,
            "updated",
            null,
            scope);
        var listed = await service.GetTestSuitesByProjectAsync(
            graph.AllowedProjectId,
            scope);
        var deleted = await service.DeleteTestSuiteAsync(created.Id, scope);

        Assert.NotNull(updated);
        Assert.Equal("updated", updated.Name);
        Assert.NotNull(listed);
        Assert.Contains(listed, suite => suite.Id == graph.AllowedSuiteId && suite.TestCases.Count == 1);
        Assert.True(deleted);
        Assert.Null(await dbContext.TestSuites.FindAsync(
            [created.Id],
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Api_key_scope_cannot_read_or_mutate_sibling_project_test_cases()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = new TestCaseService(
            dbContext,
            NullLogger<TestCaseService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);

        var listed = await service.GetTestCasesBySuiteAsync(graph.SiblingSuiteId, scope);
        var loaded = await service.GetTestCaseByIdAsync(graph.SiblingTestCaseId, scope);
        var created = await service.CreateTestCaseAsync(
            graph.SiblingSuiteId,
            "foreign-create",
            "foreign-create",
            null,
            "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
            "[{\"type\":\"contains_text\",\"text\":\"ok\"}]",
            scope);
        var updated = await service.UpdateTestCaseAsync(
            graph.SiblingTestCaseId,
            "tampered",
            "tampered",
            null,
            "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
            "[{\"type\":\"contains_text\",\"text\":\"ok\"}]",
            scope);
        var deleted = await service.DeleteTestCaseAsync(graph.SiblingTestCaseId, scope);

        Assert.Null(listed);
        Assert.Null(loaded);
        Assert.Null(created);
        Assert.Null(updated);
        Assert.False(deleted);
        Assert.DoesNotContain(dbContext.TestCases, testCase => testCase.ExternalId == "foreign-create");
        var persisted = await dbContext.TestCases.FindAsync(
            [graph.SiblingTestCaseId],
            TestContext.Current.CancellationToken);
        Assert.NotNull(persisted);
        Assert.Equal("sibling case", persisted.Name);
    }

    [Fact]
    public async Task Denied_bulk_import_neither_mutates_input_nor_persists_tests()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = new TestCaseService(
            dbContext,
            NullLogger<TestCaseService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);
        var suppliedId = Guid.NewGuid();
        var suppliedSuiteId = Guid.NewGuid();
        var supplied = new TestCase
        {
            Id = suppliedId,
            SuiteId = suppliedSuiteId,
            ExternalId = "imported",
            Name = "imported",
            InputSpecJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
            ExpectationsJson = "[{\"type\":\"contains_text\",\"text\":\"ok\"}]"
        };

        var result = await service.BulkCreateTestsAsync(
            graph.SiblingSuiteId,
            [supplied],
            scope);

        Assert.Null(result);
        Assert.Equal(suppliedId, supplied.Id);
        Assert.Equal(suppliedSuiteId, supplied.SuiteId);
        Assert.DoesNotContain(dbContext.TestCases, testCase => testCase.ExternalId == "imported");
    }

    [Fact]
    public async Task Invalid_later_bulk_row_is_atomic_and_does_not_mutate_callers()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = new TestCaseService(
            dbContext,
            NullLogger<TestCaseService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);
        var first = new TestCase
        {
            Id = Guid.NewGuid(),
            SuiteId = Guid.NewGuid(),
            ExternalId = "bulk-first",
            Name = "first",
            InputSpecJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
            ExpectationsJson = "[{\"type\":\"contains_text\",\"text\":\"hello\"}]"
        };
        var invalidLater = new TestCase
        {
            Id = Guid.NewGuid(),
            SuiteId = Guid.NewGuid(),
            ExternalId = "bulk-invalid",
            Name = "invalid later",
            InputSpecJson = "{\"messages\":[]}",
            ExpectationsJson = "[]"
        };
        var firstId = first.Id;
        var firstSuiteId = first.SuiteId;
        var invalidId = invalidLater.Id;
        var invalidSuiteId = invalidLater.SuiteId;

        var exception = await Assert.ThrowsAsync<TestSpecificationValidationException>(() =>
            service.BulkCreateTestsAsync(graph.AllowedSuiteId, [first, invalidLater], scope));

        Assert.Contains(exception.Issues, issue => issue.Path.StartsWith("rows[1]", StringComparison.Ordinal));
        Assert.Equal(firstId, first.Id);
        Assert.Equal(firstSuiteId, first.SuiteId);
        Assert.Equal(invalidId, invalidLater.Id);
        Assert.Equal(invalidSuiteId, invalidLater.SuiteId);
        Assert.DoesNotContain(dbContext.TestCases, testCase =>
            testCase.ExternalId is "bulk-first" or "bulk-invalid");
    }

    [Fact]
    public async Task Test_case_identity_validation_rejects_missing_and_duplicate_ids_without_mutation()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = new TestCaseService(
            dbContext,
            NullLogger<TestCaseService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);
        const string input = "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}";
        const string expectations = "[{\"type\":\"contains_text\",\"text\":\"hello\"}]";

        var missing = await Assert.ThrowsAsync<TestSpecificationValidationException>(() =>
            service.CreateTestCaseAsync(
                graph.AllowedSuiteId,
                "",
                "",
                null,
                input,
                expectations,
                scope));
        Assert.Contains(missing.Issues, issue => issue.Code == "required" && issue.Path == "externalId");
        Assert.Contains(missing.Issues, issue => issue.Code == "required" && issue.Path == "name");

        var duplicateCreate = await Assert.ThrowsAsync<TestSpecificationValidationException>(() =>
            service.CreateTestCaseAsync(
                graph.AllowedSuiteId,
                "allowed case",
                "replacement",
                null,
                input,
                expectations,
                scope));
        Assert.Contains(duplicateCreate.Issues, issue =>
            issue.Code == "duplicate_external_id" && issue.Path == "externalId");

        var existingOther = await service.CreateTestCaseAsync(
            graph.AllowedSuiteId,
            "other case",
            "other case",
            null,
            input,
            expectations,
            scope);
        Assert.NotNull(existingOther);

        var duplicateUpdate = await Assert.ThrowsAsync<TestSpecificationValidationException>(() =>
            service.UpdateTestCaseAsync(
                graph.AllowedTestCaseId,
                "other case",
                "replacement",
                null,
                input,
                expectations,
                scope));
        Assert.Contains(duplicateUpdate.Issues, issue =>
            issue.Code == "duplicate_external_id" && issue.Path == "externalId");

        var persisted = await service.GetTestCaseByIdAsync(graph.AllowedTestCaseId, scope);
        Assert.NotNull(persisted);
        Assert.Equal("allowed case", persisted.ExternalId);
        Assert.Equal("allowed case", persisted.Name);
        Assert.DoesNotContain(dbContext.TestCases, testCase => testCase.Name == "replacement");
    }

    [Fact]
    public async Task Bulk_import_reports_row_identity_and_existing_id_issues_before_persisting()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = new TestCaseService(
            dbContext,
            NullLogger<TestCaseService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);
        const string input = "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}";
        const string expectations = "[{\"type\":\"contains_text\",\"text\":\"hello\"}]";
        var rows = new List<TestCase>
        {
            new() { ExternalId = "", Name = "", InputSpecJson = input, ExpectationsJson = expectations },
            new() { ExternalId = "allowed case", Name = "existing", InputSpecJson = input, ExpectationsJson = expectations },
            new() { ExternalId = "allowed case", Name = "in-file duplicate", InputSpecJson = input, ExpectationsJson = expectations }
        };

        var exception = await Assert.ThrowsAsync<TestSpecificationValidationException>(() =>
            service.BulkCreateTestsAsync(graph.AllowedSuiteId, rows, scope));

        Assert.Contains(exception.Issues, issue => issue.Code == "required" && issue.Path == "rows[0].id");
        Assert.Contains(exception.Issues, issue => issue.Code == "required" && issue.Path == "rows[0].name");
        Assert.Contains(exception.Issues, issue =>
            issue.Code == "duplicate_external_id" && issue.Path == "rows[1].id");
        Assert.Contains(exception.Issues, issue =>
            issue.Code == "duplicate_external_id" && issue.Path == "rows[2].id");
        Assert.Equal(3, await dbContext.TestCases.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Owned_test_case_crud_and_bulk_import_succeed()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = new TestCaseService(
            dbContext,
            NullLogger<TestCaseService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);

        var created = await service.CreateTestCaseAsync(
            graph.AllowedSuiteId,
            "created",
            "created",
            null,
            "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
            "[{\"type\":\"contains_text\",\"text\":\"ok\"}]",
            scope);
        Assert.NotNull(created);
        var updated = await service.UpdateTestCaseAsync(
            created.Id,
            "updated",
            "updated",
            "updated",
            "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
            "[{\"type\":\"contains_text\",\"text\":\"ok\"}]",
            scope);
        var imported = await service.BulkCreateTestsAsync(
            graph.AllowedSuiteId,
            [new TestCase
            {
                ExternalId = "imported",
                Name = "imported",
                InputSpecJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                ExpectationsJson = "[{\"type\":\"contains_text\",\"text\":\"ok\"}]"
            }],
            scope);
        var listed = await service.GetTestCasesBySuiteAsync(graph.AllowedSuiteId, scope);
        var deleted = await service.DeleteTestCaseAsync(created.Id, scope);

        Assert.NotNull(updated);
        Assert.Equal("updated", updated.Name);
        Assert.NotNull(imported);
        Assert.Single(imported);
        Assert.NotEqual(Guid.Empty, imported[0].Id);
        Assert.NotNull(listed);
        Assert.Contains(listed, testCase => testCase.ExternalId == "imported");
        Assert.True(deleted);
    }

    [Fact]
    public async Task Owned_test_case_create_rejects_empty_expectations_before_persisting()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = new TestCaseService(
            dbContext,
            NullLogger<TestCaseService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);

        var exception = await Assert.ThrowsAsync<ExpectationValidationException>(() =>
            service.CreateTestCaseAsync(
                graph.AllowedSuiteId,
                "invalid",
                "invalid",
                null,
                "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                "[]",
                scope));

        Assert.NotEmpty(exception.Issues);
        Assert.Equal("no_expectations", exception.Issues[0].Code);
        Assert.DoesNotContain(
            dbContext.TestCases,
            testCase => testCase.ExternalId == "invalid");
    }

    [Fact]
    public async Task Owned_test_case_update_rejects_invalid_expectations_without_mutating_the_row()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var service = new TestCaseService(
            dbContext,
            NullLogger<TestCaseService>.Instance);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);

        var existing = await service.GetTestCaseByIdAsync(graph.AllowedTestCaseId, scope);
        Assert.NotNull(existing);
        var originalName = existing.Name;
        var originalExpectations = existing.ExpectationsJson;
        var exception = await Assert.ThrowsAsync<ExpectationValidationException>(() =>
            service.UpdateTestCaseAsync(
                graph.AllowedTestCaseId,
                "updated",
                "updated",
                null,
                "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                "[]",
                scope));

        Assert.NotEmpty(exception.Issues);
        var persisted = await service.GetTestCaseByIdAsync(graph.AllowedTestCaseId, scope);
        Assert.NotNull(persisted);
        Assert.Equal(originalName, persisted.Name);
        Assert.Equal(originalExpectations, persisted.ExpectationsJson);
    }

    [Fact]
    public async Task Owned_test_case_create_and_update_use_the_supplied_validator()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var validator = new RecordingRejectingValidator();
        var service = new TestCaseService(
            dbContext,
            NullLogger<TestCaseService>.Instance,
            validator);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);
        var existing = await service.GetTestCaseByIdAsync(graph.AllowedTestCaseId, scope);
        Assert.NotNull(existing);
        var originalName = existing.Name;
        var originalExpectations = existing.ExpectationsJson;

        var createException = await Assert.ThrowsAsync<ExpectationValidationException>(() =>
            service.CreateTestCaseAsync(
                graph.AllowedSuiteId,
                "supplied-validator-create",
                "invalid",
                null,
                "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                "[]",
                scope));
        var updateException = await Assert.ThrowsAsync<ExpectationValidationException>(() =>
            service.UpdateTestCaseAsync(
                graph.AllowedTestCaseId,
                "supplied-validator-update",
                "updated",
                null,
                "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
                "[]",
                scope));

        Assert.Equal(2, validator.Documents.Count);
        Assert.All(validator.Documents, document => Assert.Equal("[]", document));
        Assert.Equal("supplied_validator_rejected", createException.Issues[0].Code);
        Assert.Equal("supplied_validator_rejected", updateException.Issues[0].Code);
        Assert.DoesNotContain(
            dbContext.TestCases,
            testCase => testCase.ExternalId == "supplied-validator-create");
        var persisted = await service.GetTestCaseByIdAsync(graph.AllowedTestCaseId, scope);
        Assert.NotNull(persisted);
        Assert.Equal(originalName, persisted.Name);
        Assert.Equal(originalExpectations, persisted.ExpectationsJson);
    }

    [Fact]
    public async Task Bulk_import_uses_the_supplied_specification_validator_and_maps_public_issue_paths()
    {
        await using var dbContext = CreateDbContext();
        var graph = await SeedAsync(dbContext);
        var validator = new RecordingSpecificationValidator();
        var service = new TestCaseService(
            dbContext,
            NullLogger<TestCaseService>.Instance,
            specificationValidator: validator);
        var scope = new TenantAccessScope(graph.OwnerId, graph.AllowedProjectId);
        var rows = new List<TestCase>
        {
            new()
            {
                ExternalId = "spec-validator-one",
                Name = "first",
                InputSpecJson = "input-one",
                ExpectationsJson = "expectations-one"
            },
            new()
            {
                ExternalId = "spec-validator-two",
                Name = "second",
                InputSpecJson = "input-two",
                ExpectationsJson = "expectations-two"
            }
        };

        var exception = await Assert.ThrowsAsync<TestSpecificationValidationException>(() =>
            service.BulkCreateTestsAsync(graph.AllowedSuiteId, rows, scope));

        Assert.Equal(["input-one", "input-two"], validator.InputDocuments);
        Assert.Equal(["expectations-one", "expectations-two"], validator.ExpectationDocuments);
        Assert.Contains(exception.Issues, issue =>
            issue.Code == "input_issue" && issue.Path == "rows[0].input.messages");
        Assert.Contains(exception.Issues, issue =>
            issue.Code == "expectation_issue" && issue.Path == "rows[0].expectations[0].type");
        Assert.Contains(exception.Issues, issue =>
            issue.Code == "input_issue" && issue.Path == "rows[1].input.messages");
        Assert.Contains(exception.Issues, issue =>
            issue.Code == "expectation_issue" && issue.Path == "rows[1].expectations[0].type");
        Assert.DoesNotContain(dbContext.TestCases, testCase =>
            testCase.ExternalId.StartsWith("spec-validator-", StringComparison.Ordinal));
    }

    private static PromptlyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseInMemoryDatabase($"promptly-tenant-suite-tests-{Guid.NewGuid():N}")
            .Options;
        return new PromptlyDbContext(options);
    }

    private static async Task<TenantTestGraph> SeedAsync(PromptlyDbContext dbContext)
    {
        const string ownerId = "owner";
        const string otherOwnerId = "other-owner";
        var owner = new User { Id = ownerId, UserName = "owner", Email = "owner@example.test" };
        var otherOwner = new User
        {
            Id = otherOwnerId,
            UserName = "other-owner",
            Email = "other@example.test"
        };
        var allowedProject = Project(owner, "allowed project");
        var siblingProject = Project(owner, "sibling project");
        var otherProject = Project(otherOwner, "other project");
        var allowedSuite = Suite(allowedProject, "allowed suite");
        var siblingSuite = Suite(siblingProject, "sibling suite");
        var otherSuite = Suite(otherProject, "other suite");
        var allowedCase = Test(allowedSuite, "allowed case");
        var siblingCase = Test(siblingSuite, "sibling case");
        var otherCase = Test(otherSuite, "other case");
        dbContext.AddRange(
            owner,
            otherOwner,
            allowedProject,
            siblingProject,
            otherProject,
            allowedSuite,
            siblingSuite,
            otherSuite,
            allowedCase,
            siblingCase,
            otherCase);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        return new TenantTestGraph(
            ownerId,
            allowedProject.Id,
            siblingProject.Id,
            otherProject.Id,
            allowedSuite.Id,
            siblingSuite.Id,
            siblingCase.Id,
            allowedCase.Id);
    }

    private static Project Project(User owner, string name) => new()
    {
        Id = Guid.NewGuid(),
        Owner = owner,
        OwnerUserId = owner.Id,
        Name = name
    };

    private static TestSuite Suite(Project project, string name) => new()
    {
        Id = Guid.NewGuid(),
        Project = project,
        ProjectId = project.Id,
        Name = name
    };

    private static TestCase Test(TestSuite suite, string name) => new()
    {
        Id = Guid.NewGuid(),
        Suite = suite,
        SuiteId = suite.Id,
        ExternalId = name,
        Name = name,
        InputSpecJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
        ExpectationsJson = "[{\"type\":\"contains_text\",\"text\":\"hello\"}]"
    };

    private sealed class RecordingRejectingValidator : IExpectationValidator
    {
        public List<string> Documents { get; } = [];

        public ExpectationValidationResult ValidateExpectationsJson(string expectationsJson)
        {
            Documents.Add(expectationsJson);
            return new ExpectationValidationResult(
            [new ExpectationValidationIssue(
                "supplied_validator_rejected",
                "$",
                "The supplied validator rejected this document")]);
        }

        public ExpectationValidationResult ValidateExpectation(
            IReadOnlyDictionary<string, object> expectation) =>
            ExpectationValidationResult.Valid;
    }

    private sealed class RecordingSpecificationValidator : ITestSpecificationValidator
    {
        public List<string> InputDocuments { get; } = [];
        public List<string> ExpectationDocuments { get; } = [];

        public ExpectationValidationResult Validate(string inputSpecJson, string expectationsJson)
        {
            InputDocuments.Add(inputSpecJson);
            ExpectationDocuments.Add(expectationsJson);
            return new ExpectationValidationResult(
            [
                new("input_issue", "inputSpecJson.messages", "Input was rejected"),
                new("expectation_issue", "expectationsJson[0].type", "Expectation was rejected")
            ]);
        }

        public ExpectationValidationResult ValidateInput(string inputSpecJson) =>
            ExpectationValidationResult.Valid;
    }

    private sealed record TenantTestGraph(
        string OwnerId,
        Guid AllowedProjectId,
        Guid SiblingProjectId,
        Guid OtherProjectId,
        Guid AllowedSuiteId,
        Guid SiblingSuiteId,
        Guid SiblingTestCaseId,
        Guid AllowedTestCaseId);
}
