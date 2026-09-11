using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EduNexus.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RlsPolicies : Migration
    {
        private static readonly string[] TenantTables =
        [
            "Organizations", "Campuses", "OrganizationalUnits", "People",
            "Roles", "RoleAssignments", "AuthorityDelegations", "AuditEvents",
        ];

        /// <summary>
        /// Row-Level Security: tenant-scoped rows visible only when the
        /// app.current_tenant GUC (set per-request via AppDbContext.SetTenantAsync)
        /// matches TenantId. FORCE restricts even the table owner.
        /// Tenants table is platform-level and excluded.
        /// </summary>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            foreach (var t in TenantTables)
            {
                migrationBuilder.Sql($@"ALTER TABLE ""{t}"" ENABLE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"ALTER TABLE ""{t}"" FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"DROP POLICY IF EXISTS tenant_isolation ON ""{t}"";");
                migrationBuilder.Sql($@"CREATE POLICY tenant_isolation ON ""{t}""
                    USING (""TenantId"" = current_setting('app.current_tenant', true)::uuid)
                    WITH CHECK (""TenantId"" = current_setting('app.current_tenant', true)::uuid);");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var t in TenantTables)
            {
                migrationBuilder.Sql($@"DROP POLICY IF EXISTS tenant_isolation ON ""{t}"";");
                migrationBuilder.Sql($@"ALTER TABLE ""{t}"" NO FORCE ROW LEVEL SECURITY;");
                migrationBuilder.Sql($@"ALTER TABLE ""{t}"" DISABLE ROW LEVEL SECURITY;");
            }
        }
    }
}
