using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R23Connectors : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConnectorConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    System = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BaseUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CredentialSealed = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    OptionsJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSyncAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSyncStatus = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    LastSyncMessage = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectorConfigs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExternalCourses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConnectorId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Code = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    Term = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    StartsOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EndsOn = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    EnrolledCount = table.Column<int>(type: "integer", nullable: true),
                    InstructorNames = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SyncedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExternalCourses", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConnectorConfigs_TenantId_Name",
                table: "ConnectorConfigs",
                columns: new[] { "TenantId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExternalCourses_TenantId_ConnectorId_ExternalId",
                table: "ExternalCourses",
                columns: new[] { "TenantId", "ConnectorId", "ExternalId" },
                unique: true);

            migrationBuilder.Sql(@"ALTER TABLE ""ConnectorConfigs"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE ""ConnectorConfigs"" FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"CREATE POLICY tenant_isolation ON ""ConnectorConfigs""
                USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");

            migrationBuilder.Sql(@"ALTER TABLE ""ExternalCourses"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE ""ExternalCourses"" FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"CREATE POLICY tenant_isolation ON ""ExternalCourses""
                USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectorConfigs");

            migrationBuilder.DropTable(
                name: "ExternalCourses");
        }
    }
}
