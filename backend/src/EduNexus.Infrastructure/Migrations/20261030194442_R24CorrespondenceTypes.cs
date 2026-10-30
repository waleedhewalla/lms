using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R24CorrespondenceTypes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "Correspondences",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TargetUnitId",
                table: "Correspondences",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TypeCode",
                table: "Correspondences",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CorrespondenceTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    NameEn = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    NameAr = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    SenderDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ReceiverDescription = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatePermission = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    RequiresTargetUnit = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresAmount = table.Column<bool>(type: "boolean", nullable: false),
                    WorkflowCode = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CorrespondenceTypes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Correspondences_TenantId_TypeCode",
                table: "Correspondences",
                columns: new[] { "TenantId", "TypeCode" });

            migrationBuilder.CreateIndex(
                name: "IX_CorrespondenceTypes_TenantId_Code",
                table: "CorrespondenceTypes",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.Sql(@"ALTER TABLE ""CorrespondenceTypes"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE ""CorrespondenceTypes"" FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"CREATE POLICY tenant_isolation ON ""CorrespondenceTypes""
                USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CorrespondenceTypes");

            migrationBuilder.DropIndex(
                name: "IX_Correspondences_TenantId_TypeCode",
                table: "Correspondences");

            migrationBuilder.DropColumn(
                name: "Amount",
                table: "Correspondences");

            migrationBuilder.DropColumn(
                name: "TargetUnitId",
                table: "Correspondences");

            migrationBuilder.DropColumn(
                name: "TypeCode",
                table: "Correspondences");
        }
    }
}
