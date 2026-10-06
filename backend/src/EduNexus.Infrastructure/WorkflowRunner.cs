using EduNexus.Foundation;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace EduNexus.Infrastructure;

/// <summary>
/// Linear-chain workflow runner (v1). Executes definition nodes in order, waiting at
/// approval nodes until OnApprovalDecidedAsync advances or rejects the instance.
/// Reuses Approval/WorkTask/Notification primitives; every transition is audited + evented.
/// </summary>
public static class WorkflowRunner
{
    public sealed record StartResult(Guid InstanceId, Guid? PendingApprovalId);

    public static async Task<StartResult> StartAsync(
        AppDbContext db, Guid tenantId, string definitionCode,
        string entityType, Guid entityId, Guid submitterId, CancellationToken ct = default)
    {
        var def = await db.WorkflowDefinitions.FirstOrDefaultAsync(
            d => d.TenantId == tenantId && d.Code == definitionCode && d.IsActive, ct)
            ?? throw new KeyNotFoundException($"Workflow '{definitionCode}' not found.");
        using var nodes = JsonDocument.Parse(def.NodesJson);
        var list = nodes.RootElement.EnumerateArray().ToList();
        if (list.Count == 0 || list[0].GetProperty("type").GetString() != "start")
            throw new InvalidOperationException("Workflow must start with a 'start' node.");
        var instance = new WorkflowInstance(Guid.NewGuid(), tenantId, def.Id, entityType, entityId,
            list[0].GetProperty("id").GetString() ?? "start",
            WorkflowInstanceStatus.Running,
            JsonSerializer.Serialize(new { submitterId }),
            DateTimeOffset.UtcNow);
        db.WorkflowInstances.Add(instance);
        DomainEvents.Record(db, tenantId, "WorkflowStarted", "WorkflowStarted",
            nameof(WorkflowInstance), instance.Id.ToString(),
            payload: new { tenantId, instanceId = instance.Id, definition = def.Code, entityType, entityId },
            details: def.Code);
        var pending = await AdvanceAsync(db, tenantId, instance, submitterId, ct);
        return new StartResult(instance.Id, pending);
    }

    /// <summary>Called after an approval decision is saved. Returns true when it owned the approval.</summary>
    public static async Task<bool> OnApprovalDecidedAsync(
        AppDbContext db, Guid tenantId, Approval approval, bool approved, CancellationToken ct = default)
    {
        var instance = await db.WorkflowInstances.FirstOrDefaultAsync(i =>
            i.TenantId == tenantId && i.EntityType == approval.EntityType && i.EntityId == approval.EntityId
            && i.Status == WorkflowInstanceStatus.Running, ct);
        if (instance is null) return false;
        if (!approved)
        {
            db.Entry(instance).CurrentValues.SetValues(instance with
            {
                Status = WorkflowInstanceStatus.Rejected,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            DomainEvents.Record(db, tenantId, "WorkflowRejected", "WorkflowRejected",
                nameof(WorkflowInstance), instance.Id.ToString(),
                payload: new { tenantId, instanceId = instance.Id }, details: "rejected");
        }
        else
        {
            await AdvanceAsync(db, tenantId, instance, approval.AssigneeId, ct);
        }
        return true;
    }

    /// <returns>Pending approval id, or null when the instance completed.</returns>
    private static async Task<Guid?> AdvanceAsync(
        AppDbContext db, Guid tenantId, WorkflowInstance instance, Guid actorId, CancellationToken ct)
    {
        var def = await db.WorkflowDefinitions.FirstAsync(d => d.Id == instance.DefinitionId, ct);
        using var nodes = JsonDocument.Parse(def.NodesJson);
        var list = nodes.RootElement.EnumerateArray().ToList();
        var idx = list.FindIndex(n => (n.TryGetProperty("id", out var id) ? id.GetString() : null) == instance.CurrentNodeId);
        for (var i = idx + 1; i < list.Count; i++)
        {
            var node = list[i];
            var nodeId = node.TryGetProperty("id", out var nid) ? nid.GetString() ?? $"n{i}" : $"n{i}";
            var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "approval":
                {
                    var assignee = await ApplyDelegationFallbackAsync(db, tenantId, await ResolveAssigneeAsync(db, tenantId, node, actorId, instance, ct), ct);
                    var slaDays = node.TryGetProperty("slaDays", out var s) && s.ValueKind == JsonValueKind.Number
                        ? s.GetInt32() : 5;
                    var approval = new Approval(Guid.NewGuid(), tenantId, instance.EntityType, instance.EntityId,
                        assignee, ApprovalStatus.Pending, ApprovalPriority.Normal,
                        DateTimeOffset.UtcNow.AddDays(slaDays), null, null, null);
                    db.Approvals.Add(approval);
                    db.WorkTasks.Add(new WorkTask(Guid.NewGuid(), tenantId,
                        $"Workflow step: {instance.EntityType} {instance.EntityId}",
                        assignee, approval.Id, WorkTaskStatus.Open, approval.DueAt, DateTimeOffset.UtcNow));
                    db.Entry(instance).CurrentValues.SetValues(instance with
                    {
                        CurrentNodeId = nodeId,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                    DomainEvents.Record(db, tenantId, "WorkflowAdvanced", "WorkflowAdvanced",
                        nameof(WorkflowInstance), instance.Id.ToString(),
                        payload: new { tenantId, instanceId = instance.Id, node = nodeId, approvalId = approval.Id },
                        details: nodeId);
                    return approval.Id;
                }
                case "task":
                {
                    var assignee = await ApplyDelegationFallbackAsync(db, tenantId, await ResolveAssigneeAsync(db, tenantId, node, actorId, instance, ct), ct);
                    var title = node.TryGetProperty("title", out var tt) ? tt.GetString() ?? "Workflow task" : "Workflow task";
                    var task = new WorkTask(Guid.NewGuid(), tenantId, title, assignee,
                        null, WorkTaskStatus.Open, DateTimeOffset.UtcNow.AddDays(7), DateTimeOffset.UtcNow);
                    db.WorkTasks.Add(task);
                    db.Entry(instance).CurrentValues.SetValues(instance with
                    {
                        CurrentNodeId = nodeId,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                    DomainEvents.Record(db, tenantId, "WorkflowAdvanced", "WorkflowAdvanced",
                        nameof(WorkflowInstance), instance.Id.ToString(),
                        payload: new { tenantId, instanceId = instance.Id, node = nodeId, taskId = task.Id },
                        details: nodeId);
                    break;
                }
                case "notification":
                {
                    var personId = await ApplyDelegationFallbackAsync(db, tenantId, await ResolveAssigneeAsync(db, tenantId, node, actorId, instance, ct), ct);
                    var text = node.TryGetProperty("text", out var tx) ? tx.GetString() ?? "Workflow update" : "Workflow update";
                    db.Notifications.Add(new Notification(Guid.NewGuid(), tenantId, personId,
                        "Workflow update", text, NotificationChannel.InApp, NotificationStatus.Sent, DateTimeOffset.UtcNow));
                    break;
                }
                case "end":
                    db.Entry(instance).CurrentValues.SetValues(instance with
                    {
                        CurrentNodeId = nodeId,
                        Status = WorkflowInstanceStatus.Completed,
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                    DomainEvents.Record(db, tenantId, "WorkflowCompleted", "WorkflowCompleted",
                        nameof(WorkflowInstance), instance.Id.ToString(),
                        payload: new { tenantId, instanceId = instance.Id }, details: "completed");
                    return null;
                default:
                    throw new InvalidOperationException($"Unsupported workflow node type '{type}' (v1 supports approval/task/notification/end).");
            }
        }
        // No explicit end node: complete.
        db.Entry(instance).CurrentValues.SetValues(instance with
        {
            Status = WorkflowInstanceStatus.Completed,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return null;
    }

    private static async Task<Guid> ResolveAssigneeAsync(
        AppDbContext db, Guid tenantId, JsonElement node, Guid fallback, WorkflowInstance instance, CancellationToken ct)
    {
        if (node.TryGetProperty("personId", out var p) && Guid.TryParse(p.GetString(), out var pid))
        {
            if (await db.People.AnyAsync(x => x.TenantId == tenantId && x.Id == pid, ct)) return pid;
            throw new KeyNotFoundException($"Workflow assignee {pid} not found in tenant.");
        }
        if (node.TryGetProperty("assigneeFrom", out var af))
        {
            var key = af.GetString()?.ToLowerInvariant();
            var sid = Guid.Empty;
            try
            {
                using var ctx = JsonDocument.Parse(instance.ContextJson);
                if (ctx.RootElement.TryGetProperty("submitterId", out var s) && Guid.TryParse(s.GetString(), out var parsedSid))
                    sid = parsedSid;
            }
            catch { }

            if (sid == Guid.Empty) sid = fallback;

            if (key == "submitter")
            {
                return sid;
            }

            if (key is "submitter_head" or "department_head" or "head")
            {
                var person = await db.People.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == sid, ct);
                if (person?.DepartmentId is Guid deptId)
                {
                    var unit = await db.OrganizationalUnits.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == deptId, ct);
                    if (unit?.LeaderPersonId is Guid leaderId && leaderId != Guid.Empty)
                        return leaderId;
                }
                throw new KeyNotFoundException($"Could not resolve department head for submitter {sid}.");
            }

            if (key is "submitter_deputy" or "deputy")
            {
                var person = await db.People.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == sid, ct);
                if (person?.DepartmentId is Guid deptId)
                {
                    var unit = await db.OrganizationalUnits.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == deptId, ct);
                    if (unit?.DeputyPersonId is Guid depId && depId != Guid.Empty)
                        return depId;
                }
                throw new KeyNotFoundException($"Could not resolve deputy head for submitter {sid}.");
            }

            if (key is "submitter_dean" or "dean")
            {
                var person = await db.People.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == sid, ct);
                if (person?.DepartmentId is Guid deptId)
                {
                    var unit = await db.OrganizationalUnits.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == deptId, ct);
                    if (unit?.ParentId is Guid parentId)
                    {
                        var parentUnit = await db.OrganizationalUnits.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == parentId, ct);
                        if (parentUnit?.LeaderPersonId is Guid deanId && deanId != Guid.Empty)
                            return deanId;
                    }
                    if (unit?.LeaderPersonId is Guid leaderId && leaderId != Guid.Empty)
                        return leaderId;
                }
                throw new KeyNotFoundException($"Could not resolve dean for submitter {sid}.");
            }

            return fallback;
        }
        if (node.TryGetProperty("roleCode", out var rc))
        {
            var roleId = await db.Roles.Where(r => r.TenantId == tenantId && r.Code == rc.GetString())
                .Select(r => r.Id).FirstOrDefaultAsync(ct);
            if (roleId != Guid.Empty)
            {
                var personId = await db.RoleAssignments
                    .Where(a => a.TenantId == tenantId && a.RoleId == roleId)
                    .Select(a => a.PersonId).FirstOrDefaultAsync(ct);
                if (personId != Guid.Empty) return personId;
            }
            throw new KeyNotFoundException($"No assignee found for role '{rc}'.");
        }
        return fallback;
    }

    private static async Task<Guid> ApplyDelegationFallbackAsync(AppDbContext db, Guid tenantId, Guid targetPersonId, CancellationToken ct)
    {
        if (targetPersonId == Guid.Empty) return targetPersonId;
        var now = DateTimeOffset.UtcNow;
        var delegation = await db.AuthorityDelegations
            .Where(d => d.TenantId == tenantId && d.FromPersonId == targetPersonId && d.ExpiresAt > now)
            .OrderByDescending(d => d.ExpiresAt)
            .FirstOrDefaultAsync(ct);
        return delegation is not null && delegation.ToPersonId != Guid.Empty ? delegation.ToPersonId : targetPersonId;
    }
}
