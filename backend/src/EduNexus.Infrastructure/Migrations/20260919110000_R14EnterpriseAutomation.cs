using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R14EnterpriseAutomation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DocumentActionRules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TriggerCategory = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TriggerValue = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ActionType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    TargetValue = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentActionRules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentActionRules_TenantId_TriggerCategory_TriggerValue_IsActive",
                table: "DocumentActionRules",
                columns: new[] { "TenantId", "TriggerCategory", "TriggerValue", "IsActive" });

            migrationBuilder.Sql(@"ALTER TABLE ""DocumentActionRules"" ENABLE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"ALTER TABLE ""DocumentActionRules"" FORCE ROW LEVEL SECURITY;");
            migrationBuilder.Sql(@"CREATE POLICY tenant_isolation ON ""DocumentActionRules""
                USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DocumentActionRules");
        }
    }
}
