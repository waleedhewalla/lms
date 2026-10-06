using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R13EnterpriseBenchmarks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentCorrespondenceId",
                table: "Correspondences",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Correspondences_TenantId_ParentCorrespondenceId",
                table: "Correspondences",
                columns: new[] { "TenantId", "ParentCorrespondenceId" });

            migrationBuilder.CreateTable(
                name: "MeetingVotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgendaItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Choice = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CastAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MeetingVotes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DocumentRetentionPolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Standard = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    RetentionPeriodMonths = table.Column<int>(type: "integer", nullable: false),
                    DispositionAction = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ReviewIntervalMonths = table.Column<int>(type: "integer", nullable: false),
                    LastReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextReviewDueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReviewedByPersonId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentRetentionPolicies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CorrespondenceRoutingSlips",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CorrespondenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromPersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUnitId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToPersonId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActionRequired = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Instructions = table.Column<string>(type: "text", nullable: false),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsCompleted = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorrespondenceRoutingSlips", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MeetingVotes_TenantId_MeetingId_AgendaItemId_PersonId",
                table: "MeetingVotes",
                columns: new[] { "TenantId", "MeetingId", "AgendaItemId", "PersonId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentRetentionPolicies_TenantId_DocumentId",
                table: "DocumentRetentionPolicies",
                columns: new[] { "TenantId", "DocumentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DocumentRetentionPolicies_TenantId_NextReviewDueAt",
                table: "DocumentRetentionPolicies",
                columns: new[] { "TenantId", "NextReviewDueAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrespondenceRoutingSlips_TenantId_CorrespondenceId",
                table: "CorrespondenceRoutingSlips",
                columns: new[] { "TenantId", "CorrespondenceId" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrespondenceRoutingSlips_TenantId_ToPersonId_IsCompleted",
                table: "CorrespondenceRoutingSlips",
                columns: new[] { "TenantId", "ToPersonId", "IsCompleted" });

            // RLS on new tenant-scoped tables
            foreach (var t in new[] { "MeetingVotes", "DocumentRetentionPolicies", "CorrespondenceRoutingSlips" })
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
            migrationBuilder.DropTable(name: "MeetingVotes");
            migrationBuilder.DropTable(name: "DocumentRetentionPolicies");
            migrationBuilder.DropTable(name: "CorrespondenceRoutingSlips");

            migrationBuilder.DropIndex(
                name: "IX_Correspondences_TenantId_ParentCorrespondenceId",
                table: "Correspondences");

            migrationBuilder.DropColumn(
                name: "ParentCorrespondenceId",
                table: "Correspondences");
        }
    }
}
