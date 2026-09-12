using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R11DocDepth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Classification",
                table: "Documents",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetainUntil",
                table: "Documents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DocumentShares",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: false),
                    PersonId = table.Column<Guid>(type: "uuid", nullable: false),
                    SharedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DocumentShares", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DocumentShares_TenantId_DocumentId_PersonId",
                table: "DocumentShares",
                columns: new[] { "TenantId", "DocumentId", "PersonId" },
                unique: true);

            // RLS on new tenant-scoped tables.
            foreach (var t in new[] { "DocumentShares" })
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
                name: "DocumentShares");

            migrationBuilder.DropColumn(
                name: "Classification",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "RetainUntil",
                table: "Documents");
        }
    }
}
