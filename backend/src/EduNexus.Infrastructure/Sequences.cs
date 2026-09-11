using Microsoft.EntityFrameworkCore;

namespace EduNexus.Infrastructure;

/// <summary>Atomic per-tenant sequences (single round-trip, race-safe).</summary>
public static class Sequences
{
    public static async Task<long> NextAsync(AppDbContext db, Guid tenantId, string scope, CancellationToken ct = default)
    {
        var rows = await db.Database.SqlQuery<long>($"""
            INSERT INTO "TenantSequences" ("TenantId", "Scope", "NextValue")
            VALUES ({tenantId}, {scope}, {1L})
            ON CONFLICT ("TenantId", "Scope")
            DO UPDATE SET "NextValue" = "TenantSequences"."NextValue" + 1
            RETURNING "NextValue"
            """).ToListAsync(ct);
        return rows[0];
    }
}
