using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R5AiEcosystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AiInteractions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Capability = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    InputExcerpt = table.Column<string>(type: "text", nullable: false),
                    OutputExcerpt = table.Column<string>(type: "text", nullable: false),
                    Model = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Confidence = table.Column<double>(type: "double precision", nullable: true),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiInteractions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationDeliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationDeliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "IntegrationEndpoints",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    TargetUrl = table.Column<string>(type: "text", nullable: false),
                    Secret = table.Column<string>(type: "text", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IntegrationEndpoints", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiInteractions_TenantId_At",
                table: "AiInteractions",
                columns: new[] { "TenantId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationDeliveries_TenantId_EndpointId",
                table: "IntegrationDeliveries",
                columns: new[] { "TenantId", "EndpointId" });

            migrationBuilder.CreateIndex(
                name: "IX_IntegrationEndpoints_TenantId_EventType",
                table: "IntegrationEndpoints",
                columns: new[] { "TenantId", "EventType" });

            // RLS on all tenant-scoped R5 tables.
            foreach (var t in new[] { "AiInteractions", "IntegrationEndpoints", "IntegrationDeliveries" })
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
                name: "AiInteractions");

            migrationBuilder.DropTable(
                name: "IntegrationDeliveries");

            migrationBuilder.DropTable(
                name: "IntegrationEndpoints");
        }
    }
}
