using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class R16RecordsRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DisposalBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScheduleId = table.Column<Guid>(type: "uuid", nullable: false),
                    Disposition = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProposedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProposedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DecisionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ExecutedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DisposalBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DisposalItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RecordDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EligibleAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DisposalItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecordHolds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    PlacedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PlacedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordHolds", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RetentionSchedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RecordClass = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RetentionYears = table.Column<int>(type: "integer", nullable: true),
                    Disposition = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    LegalReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RetentionSchedules", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DisposalBatches_TenantId_Status",
                table: "DisposalBatches",
                columns: new[] { "TenantId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DisposalItems_TenantId_BatchId",
                table: "DisposalItems",
                columns: new[] { "TenantId", "BatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_DisposalItems_TenantId_EntityType_EntityId",
                table: "DisposalItems",
                columns: new[] { "TenantId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_RecordHolds_TenantId_EntityType_EntityId",
                table: "RecordHolds",
                columns: new[] { "TenantId", "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_RetentionSchedules_TenantId_Code",
                table: "RetentionSchedules",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            // RLS on new tenant-scoped tables
            foreach (var t in new[] { "RetentionSchedules", "RecordHolds", "DisposalBatches", "DisposalItems" })
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
                name: "DisposalBatches");

            migrationBuilder.DropTable(
                name: "DisposalItems");

            migrationBuilder.DropTable(
                name: "RecordHolds");

            migrationBuilder.DropTable(
                name: "RetentionSchedules");
        }
    }
}
