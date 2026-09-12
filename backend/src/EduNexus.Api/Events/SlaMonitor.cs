using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EduNexus.Api.Events;

/// <summary>
/// SLA escalation ladder (BBP M09): every minute, per tenant —
/// 1. Overdue open tasks → Breached + TaskBreached event.
/// 2. Tasks overdue &gt; 48h without prior reminder → reminder notification + TaskEscalated event.
/// 3. Tasks overdue &gt; 72h without prior escalation → reassign to tenant's escalation owner
///    (first person holding an `escalation:*` permission, else first active employee) +
///    TaskEscalated event. Idempotent via audit-action markers.
/// </summary>
public sealed class SlaMonitor(IServiceScopeFactory scopes, ILogger<SlaMonitor> log) : BackgroundService
{
    private static readonly TimeSpan ReminderAfter = TimeSpan.FromHours(48);
    private static readonly TimeSpan EscalateAfter = TimeSpan.FromHours(72);

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
                    await TickTenantAsync(db, tenantId, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "SLA monitor tick failed; retrying");
            }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    private async Task TickTenantAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        await using var tx = await TenantScope.BeginAsync(db, tenantId, ct);
        var now = DateTimeOffset.UtcNow;
        var overdue = await db.WorkTasks
            .Where(t => t.TenantId == tenantId
                && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Breached)
                && t.DueAt < now)
            .ToListAsync(ct);
        var changed = 0;
        foreach (var t in overdue)
        {
            if (t.Status != WorkTaskStatus.Breached)
            {
                db.Entry(t).CurrentValues.SetValues(t with { Status = WorkTaskStatus.Breached });
                DomainEvents.Record(db, tenantId, "TaskBreached", "TaskBreached",
                    nameof(WorkTask), t.Id.ToString(),
                    payload: new { tenantId, taskId = t.Id, title = t.Title, assigneeId = t.AssigneeId },
                    details: t.Title);
                changed++;
            }
            var overdueFor = now - t.DueAt;
            var reminded = await db.AuditEvents.AnyAsync(a => a.TenantId == tenantId
                && a.Action == "TaskEscalated" && a.EntityId == t.Id.ToString() && (a.Details ?? "").StartsWith("reminder"), ct);
            var escalated = await db.AuditEvents.AnyAsync(a => a.TenantId == tenantId
                && a.Action == "TaskEscalated" && a.EntityId == t.Id.ToString() && (a.Details ?? "").StartsWith("escalation"), ct);
            if (overdueFor >= ReminderAfter && !reminded)
            {
                db.Notifications.Add(new Notification(Guid.NewGuid(), tenantId, t.AssigneeId,
                    $"Reminder: task overdue — {t.Title}",
                    $"Task '{t.Title}' is {overdueFor.TotalHours:F0}h past due.",
                    NotificationChannel.InApp, NotificationStatus.Sent, now));
                DomainEvents.Record(db, tenantId, "TaskEscalated", "TaskEscalated",
                    nameof(WorkTask), t.Id.ToString(),
                    payload: new { tenantId, taskId = t.Id, level = "reminder", assigneeId = t.AssigneeId },
                    details: $"reminder:{t.AssigneeId}");
                changed++;
            }
            else if (overdueFor >= EscalateAfter && !escalated)
            {
                var owner = await FindEscalationOwnerAsync(db, tenantId, t.AssigneeId, ct);
                if (owner != Guid.Empty && owner != t.AssigneeId)
                {
                    db.Entry(t).CurrentValues.SetValues(t with { AssigneeId = owner });
                    db.Notifications.Add(new Notification(Guid.NewGuid(), tenantId, owner,
                        $"Escalated task — {t.Title}",
                        $"Task '{t.Title}' escalated to you after {overdueFor.TotalHours:F0}h overdue.",
                        NotificationChannel.InApp, NotificationStatus.Sent, now));
                    DomainEvents.Record(db, tenantId, "TaskEscalated", "TaskEscalated",
                        nameof(WorkTask), t.Id.ToString(),
                        payload: new { tenantId, taskId = t.Id, level = "escalation", from = t.AssigneeId, to = owner },
                        details: $"escalation:{owner}");
                    changed++;
                }
            }
        }
        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
            log.LogInformation("SLA: {Changed} breach/escalation updates in {Tenant}", changed, tenantId);
        }
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();
    }

    private static async Task<Guid> FindEscalationOwnerAsync(AppDbContext db, Guid tenantId, Guid exclude, CancellationToken ct)
    {
        var candidateRoleIds = await db.Roles
            .Where(r => r.TenantId == tenantId)
            .ToListAsync(ct)
            .ContinueWith(t => t.Result.Where(r => r.Permissions.Any(p => p.StartsWith("escalation:"))).Select(r => r.Id).ToList(), ct);
        if (candidateRoleIds.Count > 0)
        {
            var pid = await db.RoleAssignments
                .Where(a => a.TenantId == tenantId && candidateRoleIds.Contains(a.RoleId) && a.PersonId != exclude)
                .Select(a => a.PersonId).FirstOrDefaultAsync(ct);
            if (pid != Guid.Empty) return pid;
        }
        return await db.People
            .Where(p => p.TenantId == tenantId && p.IsActive && p.Type == PersonType.Employee && p.Id != exclude)
            .OrderBy(p => p.CreatedAt).Select(p => p.Id).FirstOrDefaultAsync(ct);
    }
}
