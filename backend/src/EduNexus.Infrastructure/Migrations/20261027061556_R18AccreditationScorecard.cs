using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R18AccreditationScorecard : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Framework",
                table: "Standards",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "Custom");

            migrationBuilder.AddColumn<double>(
                name: "Baseline",
                table: "Kpis",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "LowerIsBetter",
                table: "Kpis",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "KpiReadings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    KpiId = table.Column<Guid>(type: "uuid", nullable: false),
                    Value = table.Column<double>(type: "double precision", nullable: false),
                    Period = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RecordedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_KpiReadings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_KpiReadings_TenantId_KpiId_RecordedAt",
                table: "KpiReadings",
                columns: new[] { "TenantId", "KpiId", "RecordedAt" });

            migrationBuilder.Sql(@"ALTER TABLE ""KpiReadings"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE ""KpiReadings"" FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"CREATE POLICY tenant_isolation ON ""KpiReadings""
                USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "KpiReadings");

            migrationBuilder.DropColumn(
                name: "Framework",
                table: "Standards");

            migrationBuilder.DropColumn(
                name: "Baseline",
                table: "Kpis");

            migrationBuilder.DropColumn(
                name: "LowerIsBetter",
                table: "Kpis");
        }
    }
}
