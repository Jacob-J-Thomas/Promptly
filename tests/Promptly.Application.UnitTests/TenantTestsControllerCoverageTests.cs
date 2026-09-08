using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Promptly.Application.Interfaces;
using Promptly.Application.Models;
using Promptly.Domain.Entities;
using Promptly.Server.Controllers;
using Promptly.Server.Security;

namespace Promptly.Application.UnitTests;

public sealed class TenantTestsControllerCoverageTests
{
    private static readonly TenantAccessScope Scope = new("owner", Guid.NewGuid());

    [Fact]
    public async Task Successful_actions_project_the_public_test_case_contract()
    {
        var testCase = CreateTestCase();
        var service = new StubTestCaseService
        {
            Created = testCase,
            Listed = [testCase],
            Loaded = testCase,
            Updated = testCase,
            Deleted = true
        };
        var controller = CreateController(service);

        var created = Assert.IsType<CreatedAtActionResult>(await controller.CreateTestCase(
            testCase.SuiteId,
            CreateRequest()));
        Assert.Equal(nameof(TestsController.GetTestCase), created.ActionName);
        AssertTestCaseResponse(testCase, Assert.IsType<TestCaseResponse>(created.Value));

        var listed = Assert.IsType<OkObjectResult>(await controller.GetTestCases(testCase.SuiteId));
        var listedResponse = Assert.Single(Assert.IsType<List<TestCaseResponse>>(listed.Value));
        AssertTestCaseResponse(testCase, listedResponse);

        var loaded = Assert.IsType<OkObjectResult>(await controller.GetTestCase(testCase.Id));
        AssertTestCaseResponse(testCase, Assert.IsType<TestCaseResponse>(loaded.Value));

        var updated = Assert.IsType<OkObjectResult>(await controller.UpdateTestCase(
            testCase.Id,
            UpdateRequest()));
        AssertTestCaseResponse(testCase, Assert.IsType<TestCaseResponse>(updated.Value));

        Assert.IsType<NoContentResult>(await controller.DeleteTestCase(testCase.Id));
        Assert.Equal(Scope, service.LastScope);
        Assert.Equal(testCase.Id, service.LastId);
    }

    [Fact]
    public async Task Missing_scope_rejects_every_action_before_service_access()
    {
        var service = new StubTestCaseService();
        var controller = CreateController(service, hasScope: false);
        var id = Guid.NewGuid();

        var results = new IActionResult[]
        {
            await controller.CreateTestCase(id, CreateRequest()),
            await controller.GetTestCases(id),
            await controller.GetTestCase(id),
            await controller.UpdateTestCase(id, UpdateRequest()),
            await controller.DeleteTestCase(id)
        };

        Assert.All(results, result => Assert.IsType<UnauthorizedResult>(result));
        Assert.Equal(0, service.CallCount);
    }

    [Fact]
    public async Task Inaccessible_resources_return_not_found_for_every_action()
    {
        var controller = CreateController(new StubTestCaseService());
        var id = Guid.NewGuid();

        var results = new IActionResult[]
        {
            await controller.CreateTestCase(id, CreateRequest()),
            await controller.GetTestCases(id),
            await controller.GetTestCase(id),
            await controller.UpdateTestCase(id, UpdateRequest()),
            await controller.DeleteTestCase(id)
        };

        Assert.All(results, result => Assert.IsType<NotFoundObjectResult>(result));
    }

    [Fact]
    public async Task Validation_failures_are_returned_as_safe_structured_bad_requests()
    {
        var service = new StubTestCaseService
        {
            Failure = new TestSpecificationValidationException(
                [new ExpectationValidationIssue(
                    "invalid_type",
                    "inputSpecJson.messages",
                    "Messages must be a non-empty array")])
        };
        var controller = CreateController(service);

        var result = Assert.IsType<BadRequestObjectResult>(await controller.CreateTestCase(
            Guid.NewGuid(),
            CreateRequest()));

        var body = result.Value!.ToString()!;
        Assert.Contains("invalid_type", body, StringComparison.Ordinal);
        Assert.Contains("inputSpecJson.messages", body, StringComparison.Ordinal);
        Assert.DoesNotContain("TestSpecificationValidationException", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TestAction.Create)]
    [InlineData(TestAction.List)]
    [InlineData(TestAction.Get)]
    [InlineData(TestAction.Update)]
    [InlineData(TestAction.Delete)]
    public async Task Unexpected_service_failures_return_internal_server_error(TestAction action)
    {
        var controller = CreateController(new StubTestCaseService
        {
            Failure = new InvalidOperationException("storage unavailable")
        });

        var result = await InvokeAsync(controller, action);

        Assert.Equal(500, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    private static Task<IActionResult> InvokeAsync(TestsController controller, TestAction action)
    {
        var id = Guid.NewGuid();
        return action switch
        {
            TestAction.Create => controller.CreateTestCase(id, CreateRequest()),
            TestAction.List => controller.GetTestCases(id),
            TestAction.Get => controller.GetTestCase(id),
            TestAction.Update => controller.UpdateTestCase(id, UpdateRequest()),
            TestAction.Delete => controller.DeleteTestCase(id),
            _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
        };
    }

    private static TestsController CreateController(
        StubTestCaseService service,
        bool hasScope = true) =>
        new(
            service,
            new StubScopeAccessor(hasScope),
            NullLogger<TestsController>.Instance);

    private static CreateTestCaseRequest CreateRequest() => new()
    {
        ExternalId = "external",
        Name = "test name",
        Description = "description",
        InputSpecJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
        ExpectationsJson = "[{\"type\":\"contains_text\",\"text\":\"hello\"}]"
    };

    private static UpdateTestCaseRequest UpdateRequest() => new()
    {
        ExternalId = "external-updated",
        Name = "updated test name",
        Description = "updated description",
        InputSpecJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"updated\"}]}",
        ExpectationsJson = "[{\"type\":\"contains_text\",\"text\":\"updated\"}]"
    };

    private static TestCase CreateTestCase() => new()
    {
        Id = Guid.NewGuid(),
        SuiteId = Scope.ProjectId!.Value,
        ExternalId = "external",
        Name = "test name",
        Description = "description",
        InputSpecJson = "{\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]}",
        ExpectationsJson = "[{\"type\":\"contains_text\",\"text\":\"hello\"}]",
        CreatedAt = new DateTime(2026, 8, 12, 1, 2, 3, DateTimeKind.Utc)
    };

    private static void AssertTestCaseResponse(TestCase expected, TestCaseResponse actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.SuiteId, actual.SuiteId);
        Assert.Equal(expected.ExternalId, actual.ExternalId);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.Description, actual.Description);
        Assert.Equal(expected.InputSpecJson, actual.InputSpecJson);
        Assert.Equal(expected.ExpectationsJson, actual.ExpectationsJson);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
    }

    public enum TestAction
    {
        Create,
        List,
        Get,
        Update,
        Delete
    }

    private sealed class StubScopeAccessor(bool hasScope) : ITenantAccessScopeAccessor
    {
        public bool TryGetScope([NotNullWhen(true)] out TenantAccessScope? scope)
        {
            scope = hasScope ? Scope : null;
            return hasScope;
        }
    }

    private sealed class StubTestCaseService : ITestCaseService
    {
        public TestCase? Created { get; init; }
        public IReadOnlyList<TestCase>? Listed { get; init; }
        public TestCase? Loaded { get; init; }
        public TestCase? Updated { get; init; }
        public bool Deleted { get; init; }
        public Exception? Failure { get; init; }
        public int CallCount { get; private set; }
        public Guid LastId { get; private set; }
        public TenantAccessScope? LastScope { get; private set; }

        public Task<TestCase?> CreateTestCaseAsync(
            Guid suiteId,
            string externalId,
            string name,
            string? description,
            string inputSpecJson,
            string expectationsJson,
            TenantAccessScope scope) => Return(suiteId, scope, Created);

        public Task<IReadOnlyList<TestCase>?> GetTestCasesBySuiteAsync(
            Guid suiteId,
            TenantAccessScope scope) => Return(suiteId, scope, Listed);

        public Task<TestCase?> GetTestCaseByIdAsync(Guid id, TenantAccessScope scope) =>
            Return(id, scope, Loaded);

        public Task<TestCase?> UpdateTestCaseAsync(
            Guid id,
            string externalId,
            string name,
            string? description,
            string inputSpecJson,
            string expectationsJson,
            TenantAccessScope scope) => Return(id, scope, Updated);

        public Task<bool> DeleteTestCaseAsync(Guid id, TenantAccessScope scope) =>
            Return(id, scope, Deleted);

        public Task<IReadOnlyList<TestCase>?> BulkCreateTestsAsync(
            Guid suiteId,
            List<TestCase> testCases,
            TenantAccessScope scope) => Return<IReadOnlyList<TestCase>?>(suiteId, scope, null);

        private Task<T> Return<T>(Guid id, TenantAccessScope scope, T value)
        {
            CallCount++;
            LastId = id;
            LastScope = scope;
            if (Failure is not null)
            {
                throw Failure;
            }

            return Task.FromResult(value);
        }
    }
}
