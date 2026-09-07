using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Promptly.Application.Migrations
{
    /// <inheritdoc />
    public partial class EnforceTestRunGraph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TestRuns_EndpointId",
                table: "TestRuns");

            migrationBuilder.DropIndex(
                name: "IX_TestRuns_EnvironmentId",
                table: "TestRuns");

            migrationBuilder.DropIndex(
                name: "IX_TestRuns_MappingSpecId",
                table: "TestRuns");

            migrationBuilder.AddColumn<Guid>(
                name: "ProjectId",
                table: "TestRuns",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "TestRuns" AS run
                SET "ProjectId" = suite."ProjectId"
                FROM "TestSuites" AS suite
                WHERE suite."Id" = run."SuiteId";
                """);

            migrationBuilder.Sql(
                """
                UPDATE "TestRuns" AS run
                SET "Status" = 'Failed',
                    "CompletedAt" = COALESCE(run."CompletedAt", NOW()),
                    "ErrorMessage" = 'Run graph failed integrity validation during migration.'
                FROM "TestSuites" AS suite,
                     "Projects" AS project
                WHERE run."SuiteId" = suite."Id"
                  AND project."Id" = suite."ProjectId"
                  AND run."Status" IN ('Queued', 'Running')
                  AND (
                      run."CreatedByUserId" <> project."OwnerUserId"
                      OR NOT EXISTS (
                          SELECT 1
                          FROM "Environments" AS environment
                          WHERE environment."Id" = run."EnvironmentId"
                            AND environment."ProjectId" = suite."ProjectId")
                      OR NOT EXISTS (
                          SELECT 1
                          FROM "Endpoints" AS endpoint
                          WHERE endpoint."Id" = run."EndpointId"
                            AND endpoint."EnvironmentId" = run."EnvironmentId")
                      OR NOT EXISTS (
                          SELECT 1
                          FROM "MappingSpecs" AS mapping
                          WHERE mapping."Id" = run."MappingSpecId"
                            AND mapping."EndpointId" = run."EndpointId")
                  );
                """);

            migrationBuilder.AlterColumn<Guid>(
                name: "ProjectId",
                table: "TestRuns",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_TestSuites_Id_ProjectId",
                table: "TestSuites",
                columns: new[] { "Id", "ProjectId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Projects_Id_OwnerUserId",
                table: "Projects",
                columns: new[] { "Id", "OwnerUserId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_MappingSpecs_Id_EndpointId",
                table: "MappingSpecs",
                columns: new[] { "Id", "EndpointId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Environments_Id_ProjectId",
                table: "Environments",
                columns: new[] { "Id", "ProjectId" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Endpoints_Id_EnvironmentId",
                table: "Endpoints",
                columns: new[] { "Id", "EnvironmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_EndpointId_EnvironmentId",
                table: "TestRuns",
                columns: new[] { "EndpointId", "EnvironmentId" });

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_EnvironmentId_ProjectId",
                table: "TestRuns",
                columns: new[] { "EnvironmentId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_MappingSpecId_EndpointId",
                table: "TestRuns",
                columns: new[] { "MappingSpecId", "EndpointId" });

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_ProjectId_CreatedByUserId",
                table: "TestRuns",
                columns: new[] { "ProjectId", "CreatedByUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_SuiteId_ProjectId",
                table: "TestRuns",
                columns: new[] { "SuiteId", "ProjectId" });

            migrationBuilder.Sql(
                """
                ALTER TABLE "TestRuns"
                    ADD CONSTRAINT "FK_TestRuns_Endpoints_EndpointId_EnvironmentId"
                    FOREIGN KEY ("EndpointId", "EnvironmentId")
                    REFERENCES "Endpoints" ("Id", "EnvironmentId")
                    ON DELETE RESTRICT NOT VALID;

                ALTER TABLE "TestRuns"
                    ADD CONSTRAINT "FK_TestRuns_Environments_EnvironmentId_ProjectId"
                    FOREIGN KEY ("EnvironmentId", "ProjectId")
                    REFERENCES "Environments" ("Id", "ProjectId")
                    ON DELETE RESTRICT NOT VALID;

                ALTER TABLE "TestRuns"
                    ADD CONSTRAINT "FK_TestRuns_MappingSpecs_MappingSpecId_EndpointId"
                    FOREIGN KEY ("MappingSpecId", "EndpointId")
                    REFERENCES "MappingSpecs" ("Id", "EndpointId")
                    ON DELETE RESTRICT NOT VALID;

                ALTER TABLE "TestRuns"
                    ADD CONSTRAINT "FK_TestRuns_Projects_ProjectId_CreatedByUserId"
                    FOREIGN KEY ("ProjectId", "CreatedByUserId")
                    REFERENCES "Projects" ("Id", "OwnerUserId")
                    ON DELETE RESTRICT NOT VALID;

                ALTER TABLE "TestRuns"
                    ADD CONSTRAINT "FK_TestRuns_TestSuites_SuiteId_ProjectId"
                    FOREIGN KEY ("SuiteId", "ProjectId")
                    REFERENCES "TestSuites" ("Id", "ProjectId")
                    ON DELETE RESTRICT NOT VALID;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "TestRuns"
                    DROP CONSTRAINT "FK_TestRuns_Endpoints_EndpointId_EnvironmentId";
                ALTER TABLE "TestRuns"
                    DROP CONSTRAINT "FK_TestRuns_Environments_EnvironmentId_ProjectId";
                ALTER TABLE "TestRuns"
                    DROP CONSTRAINT "FK_TestRuns_MappingSpecs_MappingSpecId_EndpointId";
                ALTER TABLE "TestRuns"
                    DROP CONSTRAINT "FK_TestRuns_Projects_ProjectId_CreatedByUserId";
                ALTER TABLE "TestRuns"
                    DROP CONSTRAINT "FK_TestRuns_TestSuites_SuiteId_ProjectId";
                """);

            migrationBuilder.DropUniqueConstraint(
                name: "AK_TestSuites_Id_ProjectId",
                table: "TestSuites");

            migrationBuilder.DropIndex(
                name: "IX_TestRuns_EndpointId_EnvironmentId",
                table: "TestRuns");

            migrationBuilder.DropIndex(
                name: "IX_TestRuns_EnvironmentId_ProjectId",
                table: "TestRuns");

            migrationBuilder.DropIndex(
                name: "IX_TestRuns_MappingSpecId_EndpointId",
                table: "TestRuns");

            migrationBuilder.DropIndex(
                name: "IX_TestRuns_ProjectId_CreatedByUserId",
                table: "TestRuns");

            migrationBuilder.DropIndex(
                name: "IX_TestRuns_SuiteId_ProjectId",
                table: "TestRuns");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Projects_Id_OwnerUserId",
                table: "Projects");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_MappingSpecs_Id_EndpointId",
                table: "MappingSpecs");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Environments_Id_ProjectId",
                table: "Environments");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Endpoints_Id_EnvironmentId",
                table: "Endpoints");

            migrationBuilder.DropColumn(
                name: "ProjectId",
                table: "TestRuns");

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_EndpointId",
                table: "TestRuns",
                column: "EndpointId");

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_EnvironmentId",
                table: "TestRuns",
                column: "EnvironmentId");

            migrationBuilder.CreateIndex(
                name: "IX_TestRuns_MappingSpecId",
                table: "TestRuns",
                column: "MappingSpecId");
        }
    }
}
