using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R6Requests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Forms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Category = table.Column<int>(type: "integer", nullable: false),
                    SchemaJson = table.Column<string>(type: "jsonb", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Forms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FormSubmissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    FormId = table.Column<Guid>(type: "uuid", nullable: false),
                    FormVersion = table.Column<int>(type: "integer", nullable: false),
                    DataJson = table.Column<string>(type: "jsonb", nullable: false),
                    SubmittedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormSubmissions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FormId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmitterId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Requests", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Forms_TenantId_Code",
                table: "Forms",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FormSubmissions_TenantId_RequestId",
                table: "FormSubmissions",
                columns: new[] { "TenantId", "RequestId" });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_TenantId_Category",
                table: "Requests",
                columns: new[] { "TenantId", "Category" });

            migrationBuilder.CreateIndex(
                name: "IX_Requests_TenantId_Number",
                table: "Requests",
                columns: new[] { "TenantId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Requests_TenantId_Status",
                table: "Requests",
                columns: new[] { "TenantId", "Status" });

            // RLS on all tenant-scoped Track A tables.
            foreach (var t in new[] { "Requests", "Forms", "FormSubmissions" })
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
            migrationBuilder.DropTable(
                name: "Forms");

            migrationBuilder.DropTable(
                name: "FormSubmissions");

            migrationBuilder.DropTable(
                name: "Requests");
        }
    }
}
