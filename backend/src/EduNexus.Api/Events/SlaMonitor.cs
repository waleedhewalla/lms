using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EduNexus.Api.Events;

/// <summary>
/// SLA monitor: every minute, per tenant, marks overdue open tasks Breached
/// and emits TaskBreached (→ notification). Approval breaches are queryable via
/// GET /api/sla/breaches; decisions after due date stay allowed (flagged by DueAt).
/// </summary>
public sealed class SlaMonitor(IServiceScopeFactory scopes, ILogger<SlaMonitor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var tenants = await db.Tenants.Select(t => t.Id).ToListAsync(stoppingToken);
                foreach (var tenantId in tenants)
                {
                    await using var tx = await TenantScope.BeginAsync(db, tenantId, stoppingToken);
                    var now = DateTimeOffset.UtcNow;
                    var overdue = await db.WorkTasks
                        .Where(t => t.TenantId == tenantId
                            && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress)
                            && t.DueAt < now)
                        .ToListAsync(stoppingToken);
                    foreach (var t in overdue)
                    {
                        db.Entry(t).CurrentValues.SetValues(t with { Status = WorkTaskStatus.Breached });
                        DomainEvents.Record(db, tenantId, "TaskBreached", "TaskBreached",
                            nameof(WorkTask), t.Id.ToString(),
                            payload: new { tenantId, taskId = t.Id, title = t.Title, assigneeId = t.AssigneeId },
                            details: t.Title);
                    }
                    if (overdue.Count > 0)
                    {
                        await db.SaveChangesAsync(stoppingToken);
                        log.LogInformation("SLA: marked {Count} breached tasks in {Tenant}", overdue.Count, tenantId);
                    }
                    await tx.CommitAsync(stoppingToken);
                    db.ChangeTracker.Clear();
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "SLA monitor tick failed; retrying");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
