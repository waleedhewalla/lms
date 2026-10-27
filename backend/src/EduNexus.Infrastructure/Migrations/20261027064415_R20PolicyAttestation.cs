using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R20PolicyAttestation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PolicyAcknowledgements_TenantId_PolicyId_PersonId",
                table: "PolicyAcknowledgements");

            migrationBuilder.AddColumn<int>(
                name: "PolicyVersion",
                table: "PolicyAcknowledgements",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "QuizScore",
                table: "PolicyAcknowledgements",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttestEveryMonths",
                table: "Policies",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuizPassPercent",
                table: "Policies",
                type: "integer",
                nullable: false,
                defaultValue: 80);

            migrationBuilder.CreateTable(
                name: "PolicyQuizQuestions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<Guid>(type: "uuid", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Question = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    OptionsText = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CorrectIndex = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyQuizQuestions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAcknowledgements_TenantId_PolicyId_PersonId_PolicyVer~",
                table: "PolicyAcknowledgements",
                columns: new[] { "TenantId", "PolicyId", "PersonId", "PolicyVersion" });

            migrationBuilder.CreateIndex(
                name: "IX_PolicyQuizQuestions_TenantId_PolicyId_Order",
                table: "PolicyQuizQuestions",
                columns: new[] { "TenantId", "PolicyId", "Order" });

            migrationBuilder.Sql(@"ALTER TABLE ""PolicyQuizQuestions"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE ""PolicyQuizQuestions"" FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"CREATE POLICY tenant_isolation ON ""PolicyQuizQuestions""
                USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");

            // Acknowledgements given before versioning count for the policy's current version, so nobody
            // is asked to re-acknowledge on upgrade. Row-level security applies even here, so set each tenant in turn.
            migrationBuilder.Sql(@"DO $$
DECLARE t uuid;
BEGIN
  FOR t IN SELECT ""Id"" FROM ""Tenants"" LOOP
    PERFORM set_config('app.current_tenant', t::text, true);
    UPDATE ""PolicyAcknowledgements"" a SET ""PolicyVersion"" = p.""Version""
      FROM ""Policies"" p WHERE p.""Id"" = a.""PolicyId"" AND p.""TenantId"" = t AND a.""TenantId"" = t;
  END LOOP;
  PERFORM set_config('app.current_tenant', '', true);
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PolicyQuizQuestions");

            migrationBuilder.DropIndex(
                name: "IX_PolicyAcknowledgements_TenantId_PolicyId_PersonId_PolicyVer~",
                table: "PolicyAcknowledgements");

            migrationBuilder.DropColumn(
                name: "PolicyVersion",
                table: "PolicyAcknowledgements");

            migrationBuilder.DropColumn(
                name: "QuizScore",
                table: "PolicyAcknowledgements");

            migrationBuilder.DropColumn(
                name: "AttestEveryMonths",
                table: "Policies");

            migrationBuilder.DropColumn(
                name: "QuizPassPercent",
                table: "Policies");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyAcknowledgements_TenantId_PolicyId_PersonId",
                table: "PolicyAcknowledgements",
                columns: new[] { "TenantId", "PolicyId", "PersonId" },
                unique: true);
        }
    }
}
