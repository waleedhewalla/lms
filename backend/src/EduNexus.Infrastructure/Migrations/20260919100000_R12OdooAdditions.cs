using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R12OdooAdditions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "LeaderPersonId",
                table: "OrganizationalUnits",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeputyPersonId",
                table: "OrganizationalUnits",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DepartmentId",
                table: "People",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AcademicRank",
                table: "People",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ScheduledActivities",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AssigneeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    DueDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledActivities", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecordComments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    IsInternalOnly = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordComments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EntityFollowers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "text", nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntityFollowers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentWorkspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentWorkspaces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentTags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagCategory = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TagValue = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentTags", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledActivities_TenantId_EntityType_EntityId",
                table: "ScheduledActivities",
                columns: new[] { "TenantId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledActivities_TenantId_AssigneeId_IsCompleted",
                table: "ScheduledActivities",
                columns: new[] { "TenantId", "AssigneeId", "IsCompleted" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordComments_TenantId_EntityType_EntityId",
                table: "RecordComments",
                columns: new[] { "TenantId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_EntityFollowers_TenantId_EntityType_EntityId_PersonId",
                table: "EntityFollowers",
                columns: new[] { "TenantId", "EntityType", "EntityId", "PersonId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentWorkspaces_TenantId_Code",
                table: "DocumentWorkspaces",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentTags_TenantId_DocumentId",
                table: "DocumentTags",
                columns: new[] { "TenantId", "DocumentId" });

            // RLS on new tenant-scoped tables
            foreach (var t in new[] { "ScheduledActivities", "RecordComments", "EntityFollowers", "DocumentWorkspaces", "DocumentTags" })
            {
                migrationBuilder.Sql($@"ALTER TABLE ""{t}"" ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"ALTER TABLE ""{t}"" FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"CREATE POLICY tenant_isolation ON ""{t}""
                    USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                    WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ScheduledActivities");
            migrationBuilder.DropTable(name: "RecordComments");
            migrationBuilder.DropTable(name: "EntityFollowers");
            migrationBuilder.DropTable(name: "DocumentWorkspaces");
            migrationBuilder.DropTable(name: "DocumentTags");

            migrationBuilder.DropColumn(name: "LeaderPersonId", table: "OrganizationalUnits");
            migrationBuilder.DropColumn(name: "DeputyPersonId", table: "OrganizationalUnits");
            migrationBuilder.DropColumn(name: "DepartmentId", table: "People");
            migrationBuilder.DropColumn(name: "AcademicRank", table: "People");
        }
    }
}
