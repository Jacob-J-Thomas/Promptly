using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Promptly.Application.Data;
using Promptly.Application.Migrations;
using Promptly.Domain.Entities;

namespace Promptly.Application.UnitTests;

public sealed class TestRunGraphModelTests
{
    [Fact]
    public void Test_run_model_preserves_scalar_relationships_and_enforces_full_graph()
    {
        using var dbContext = CreateDbContext();
        var testRun = dbContext.Model.FindEntityType(typeof(TestRun));
        Assert.NotNull(testRun);

        Assert.Equal(typeof(Guid), testRun.FindProperty(nameof(TestRun.ProjectId))?.ClrType);
        Assert.False(testRun.FindProperty(nameof(TestRun.ProjectId))?.IsNullable);

        AssertForeignKey(testRun, typeof(User), [nameof(TestRun.CreatedByUserId)], [nameof(User.Id)]);
        AssertForeignKey(testRun, typeof(TestSuite), [nameof(TestRun.SuiteId)], [nameof(TestSuite.Id)]);
        AssertForeignKey(
            testRun,
            typeof(Promptly.Domain.Entities.Environment),
            [nameof(TestRun.EnvironmentId)],
            [nameof(Promptly.Domain.Entities.Environment.Id)]);
        AssertForeignKey(testRun, typeof(Endpoint), [nameof(TestRun.EndpointId)], [nameof(Endpoint.Id)]);
        AssertForeignKey(
            testRun,
            typeof(MappingSpec),
            [nameof(TestRun.MappingSpecId)],
            [nameof(MappingSpec.Id)]);

        AssertForeignKey(
            testRun,
            typeof(Project),
            [nameof(TestRun.ProjectId), nameof(TestRun.CreatedByUserId)],
            [nameof(Project.Id), nameof(Project.OwnerUserId)]);
        AssertForeignKey(
            testRun,
            typeof(TestSuite),
            [nameof(TestRun.SuiteId), nameof(TestRun.ProjectId)],
            [nameof(TestSuite.Id), nameof(TestSuite.ProjectId)]);
        AssertForeignKey(
            testRun,
            typeof(Promptly.Domain.Entities.Environment),
            [nameof(TestRun.EnvironmentId), nameof(TestRun.ProjectId)],
            [nameof(Promptly.Domain.Entities.Environment.Id), nameof(Promptly.Domain.Entities.Environment.ProjectId)]);
        AssertForeignKey(
            testRun,
            typeof(Endpoint),
            [nameof(TestRun.EndpointId), nameof(TestRun.EnvironmentId)],
            [nameof(Endpoint.Id), nameof(Endpoint.EnvironmentId)]);
        AssertForeignKey(
            testRun,
            typeof(MappingSpec),
            [nameof(TestRun.MappingSpecId), nameof(TestRun.EndpointId)],
            [nameof(MappingSpec.Id), nameof(MappingSpec.EndpointId)]);

        AssertAlternateKey<Project>(
            dbContext,
            [nameof(Project.Id), nameof(Project.OwnerUserId)]);
        AssertAlternateKey<TestSuite>(
            dbContext,
            [nameof(TestSuite.Id), nameof(TestSuite.ProjectId)]);
        AssertAlternateKey<Promptly.Domain.Entities.Environment>(
            dbContext,
            [nameof(Promptly.Domain.Entities.Environment.Id), nameof(Promptly.Domain.Entities.Environment.ProjectId)]);
        AssertAlternateKey<Endpoint>(
            dbContext,
            [nameof(Endpoint.Id), nameof(Endpoint.EnvironmentId)]);
        AssertAlternateKey<MappingSpec>(
            dbContext,
            [nameof(MappingSpec.Id), nameof(MappingSpec.EndpointId)]);
    }

    [Fact]
    public void Migration_is_legacy_safe_and_enforces_new_writes()
    {
        var migration = new InspectableEnforceTestRunGraph();

        var up = migration.BuildUpOperations();
        var addProject = Assert.Single(
            up.OfType<AddColumnOperation>(),
            operation => operation.Table == "TestRuns" && operation.Name == "ProjectId");
        Assert.True(addProject.IsNullable);

        var alterProject = Assert.Single(
            up.OfType<AlterColumnOperation>(),
            operation => operation.Table == "TestRuns" && operation.Name == "ProjectId");
        Assert.False(alterProject.IsNullable);

        var sql = string.Join(
            "\n",
            up.OfType<SqlOperation>().Select(operation => operation.Sql));
        Assert.Contains(
            "SET \"ProjectId\" = suite.\"ProjectId\"",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "run.\"Status\" IN ('Queued', 'Running')",
            sql,
            StringComparison.Ordinal);
        Assert.Contains(
            "Run graph failed integrity validation during migration.",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SummaryJson", sql, StringComparison.Ordinal);
        Assert.Equal(5, Count(sql, "NOT VALID"));

        foreach (var constraint in CompositeConstraintNames)
        {
            Assert.Contains($"ADD CONSTRAINT \"{constraint}\"", sql, StringComparison.Ordinal);
        }

        var down = migration.BuildDownOperations();
        var downSql = string.Join(
            "\n",
            down.OfType<SqlOperation>().Select(operation => operation.Sql));
        foreach (var constraint in CompositeConstraintNames)
        {
            Assert.Contains($"DROP CONSTRAINT \"{constraint}\"", downSql, StringComparison.Ordinal);
        }

        Assert.Contains(
            down.OfType<DropColumnOperation>(),
            operation => operation.Table == "TestRuns" && operation.Name == "ProjectId");
        Assert.Equal(5, down.OfType<DropUniqueConstraintOperation>().Count());
    }

    private static readonly string[] CompositeConstraintNames =
    [
        "FK_TestRuns_Projects_ProjectId_CreatedByUserId",
        "FK_TestRuns_TestSuites_SuiteId_ProjectId",
        "FK_TestRuns_Environments_EnvironmentId_ProjectId",
        "FK_TestRuns_Endpoints_EndpointId_EnvironmentId",
        "FK_TestRuns_MappingSpecs_MappingSpecId_EndpointId"
    ];

    private static PromptlyDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PromptlyDbContext>()
            .UseNpgsql("Host=localhost;Database=promptly-model-tests")
            .Options;
        return new PromptlyDbContext(options);
    }

    private static void AssertForeignKey(
        IEntityType dependent,
        Type principalType,
        IReadOnlyList<string> dependentProperties,
        IReadOnlyList<string> principalProperties)
    {
        var foreignKey = Assert.Single(
            dependent.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType.ClrType == principalType
                && candidate.Properties.Select(property => property.Name)
                    .SequenceEqual(dependentProperties));

        Assert.Equal(
            principalProperties,
            foreignKey.PrincipalKey.Properties.Select(property => property.Name));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    private static void AssertAlternateKey<TEntity>(
        PromptlyDbContext dbContext,
        IReadOnlyList<string> properties)
    {
        var entity = dbContext.Model.FindEntityType(typeof(TEntity));
        Assert.NotNull(entity);
        var alternateKey = Assert.Single(
            entity.GetKeys(),
            key => !key.IsPrimaryKey()
                && key.Properties.Select(property => property.Name).SequenceEqual(properties));
        Assert.False(alternateKey.IsPrimaryKey());
    }

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private sealed class InspectableEnforceTestRunGraph : EnforceTestRunGraph
    {
        public IReadOnlyList<MigrationOperation> BuildUpOperations()
        {
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            Up(builder);
            return builder.Operations;
        }

        public IReadOnlyList<MigrationOperation> BuildDownOperations()
        {
            var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
            Down(builder);
            return builder.Operations;
        }
    }
}
