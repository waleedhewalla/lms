using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R19BoardIntegrity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RecordedBy",
                table: "MeetingVotes",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "QuorumPercent",
                table: "Committees",
                type: "double precision",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AgendaRecusals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    MeetingId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgendaItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DeclaredBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeclaredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgendaRecusals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CircularResolutions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    CommitteeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Text = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: false),
                    RequiresUnanimous = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosesAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CircularResolutions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InterestDeclarations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Period = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    NoInterests = table.Column<bool>(type: "boolean", nullable: false),
                    Interests = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    DeclaredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InterestDeclarations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResolutionVotes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ResolutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    Choice = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Recused = table.Column<bool>(type: "boolean", nullable: false),
                    Remarks = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CastAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResolutionVotes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AgendaRecusals_TenantId_AgendaItemId_PersonId",
                table: "AgendaRecusals",
                columns: new[] { "TenantId", "AgendaItemId", "PersonId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgendaRecusals_TenantId_MeetingId",
                table: "AgendaRecusals",
                columns: new[] { "TenantId", "MeetingId" });

            migrationBuilder.CreateIndex(
                name: "IX_CircularResolutions_TenantId_CommitteeId_CreatedAt",
                table: "CircularResolutions",
                columns: new[] { "TenantId", "CommitteeId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_InterestDeclarations_TenantId_PersonId_Period",
                table: "InterestDeclarations",
                columns: new[] { "TenantId", "PersonId", "Period" });

            migrationBuilder.CreateIndex(
                name: "IX_ResolutionVotes_TenantId_ResolutionId_PersonId",
                table: "ResolutionVotes",
                columns: new[] { "TenantId", "ResolutionId", "PersonId" },
                unique: true);

            migrationBuilder.Sql(@"ALTER TABLE ""AgendaRecusals"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE ""AgendaRecusals"" FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"CREATE POLICY tenant_isolation ON ""AgendaRecusals""
                USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");

            migrationBuilder.Sql(@"ALTER TABLE ""InterestDeclarations"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE ""InterestDeclarations"" FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"CREATE POLICY tenant_isolation ON ""InterestDeclarations""
                USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");

            migrationBuilder.Sql(@"ALTER TABLE ""CircularResolutions"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE ""CircularResolutions"" FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"CREATE POLICY tenant_isolation ON ""CircularResolutions""
                USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");

            migrationBuilder.Sql(@"ALTER TABLE ""ResolutionVotes"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE ""ResolutionVotes"" FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"CREATE POLICY tenant_isolation ON ""ResolutionVotes""
                USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgendaRecusals");

            migrationBuilder.DropTable(
                name: "CircularResolutions");

            migrationBuilder.DropTable(
                name: "InterestDeclarations");

            migrationBuilder.DropTable(
                name: "ResolutionVotes");

            migrationBuilder.DropColumn(
                name: "RecordedBy",
                table: "MeetingVotes");

            migrationBuilder.DropColumn(
                name: "QuorumPercent",
                table: "Committees");
        }
    }
}
