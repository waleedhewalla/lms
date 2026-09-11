using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EduNexus.Infrastructure;

/// <summary>
/// Transaction-scoped tenant isolation: begins a transaction and sets
/// app.current_tenant as a LOCAL GUC (reset automatically on commit/rollback),
/// so pooled connections can never leak one tenant's RLS context into another's.
/// </summary>
public sealed class TenantScope : IAsyncDisposable
{
    private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction _tx;

    private TenantScope(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx) => _tx = tx;

    public static async Task<TenantScope> BeginAsync(AppDbContext db, Guid tenantId, CancellationToken ct = default)
    {
        var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant', {0}, true)", new[] { tenantId.ToString() }, ct);
        return new TenantScope(tx);
    }

    public Task CommitAsync(CancellationToken ct = default) => _tx.CommitAsync(ct);

    public async ValueTask DisposeAsync()
    {
        try { await _tx.RollbackAsync(); } catch { /* already committed */ }
        await _tx.DisposeAsync();
    }
}
