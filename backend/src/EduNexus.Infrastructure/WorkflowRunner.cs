using EduNexus.Foundation;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EduNexus.Infrastructure;

/// <summary>
/// Linear-chain workflow runner. Executes definition nodes in order, waiting at approval nodes until a decision advances,
/// rejects or returns the instance. Reuses Approval/WorkTask/Notification primitives; every transition is audited + evented.
///
/// Node types: start, approval, task, notification, close, end. Optional node properties (a definition that uses none of them
/// runs exactly as it always has):
///   label / labelAr  what the step is called in the task and the notification
///   when             {"field":"amount","op":">","value":50000}: the step is skipped unless the condition holds (fields come from the instance context)
///   skipDuplicate    skip an approval whose approver is the submitter or already approved earlier in this run (nobody approves their own letter or twice)
///   workingDays      with slaDays: count the deadline in working days (weekend and Holiday calendar events excluded)
///   notify           also send the approver an in-app notification when the step reaches them
/// assigneeFrom: submitter, submitter_head, submitter_deputy, submitter_dean, target_head, target_dean, recipient; or personId / roleCode.
/// </summary>
public static class WorkflowRunner
{
    public sealed record StartResult(Guid InstanceId, Guid? PendingApprovalId, bool Completed = false, bool Closed = false);

    /// <summary>What a decision did to the run it belongs to. <see cref="Owned"/> is false when the approval is not part of any workflow run.</summary>
    public sealed record DecisionOutcome(bool Owned, WorkflowInstanceStatus Status, Guid? PendingApprovalId, bool Closed, Guid InstanceId);

    /// <summary>What the caller knows about the thing the workflow is for. All parts are optional.</summary>
    public sealed record Options(IReadOnlyDictionary<string, object?>? Context = null, string? TitleHint = null, WorkingCalendar? Calendar = null);

    public static async Task<StartResult> StartAsync(
        AppDbContext db, Guid tenantId, string definitionCode,
        string entityType, Guid entityId, Guid submitterId, CancellationToken ct = default, Options? options = null)
    {
        var def = await db.WorkflowDefinitions.FirstOrDefaultAsync(
            d => d.TenantId == tenantId && d.Code == definitionCode && d.IsActive, ct)
            ?? throw new KeyNotFoundException($"Workflow '{definitionCode}' not found.");
        using var nodes = JsonDocument.Parse(def.NodesJson);
        var list = nodes.RootElement.EnumerateArray().ToList();
        if (list.Count == 0 || list[0].GetProperty("type").GetString() != "start")
            throw new InvalidOperationException("Workflow must start with a 'start' node.");
        var context = new JsonObject { ["submitterId"] = submitterId.ToString() };
        foreach (var (k, v) in options?.Context ?? new Dictionary<string, object?>())
            context[k] = v is null ? null : JsonSerializer.SerializeToNode(v);
        if (options?.TitleHint is { } hint) context["titleHint"] = hint;
        context["approvedBy"] = new JsonArray();
        var instance = new WorkflowInstance(Guid.NewGuid(), tenantId, def.Id, entityType, entityId,
            list[0].GetProperty("id").GetString() ?? "start",
            WorkflowInstanceStatus.Running,
            context.ToJsonString(),
            DateTimeOffset.UtcNow);
        db.WorkflowInstances.Add(instance);
        DomainEvents.Record(db, tenantId, "WorkflowStarted", "WorkflowStarted",
            nameof(WorkflowInstance), instance.Id.ToString(),
            payload: new { tenantId, instanceId = instance.Id, definition = def.Code, entityType, entityId },
            details: def.Code);
        var pending = await AdvanceAsync(db, tenantId, instance, submitterId, options?.Calendar ?? WorkingCalendar.Default, ct);
        var status = StatusOf(db, instance);
        return new StartResult(instance.Id, pending, status == WorkflowInstanceStatus.Completed, ClosedFlag(db, instance));
    }

    /// <summary>True when this approval belongs to a running workflow (so the caller can ask for a reason before it decides).</summary>
    public static Task<bool> OwnsAsync(AppDbContext db, Guid tenantId, Approval approval, CancellationToken ct = default) =>
        db.WorkflowInstances.AnyAsync(i => i.TenantId == tenantId && i.EntityType == approval.EntityType && i.EntityId == approval.EntityId
            && i.Status == WorkflowInstanceStatus.Running, ct);

    /// <summary>Called after an approval decision is saved.</summary>
    public static async Task<DecisionOutcome> OnApprovalDecidedAsync(
        AppDbContext db, Guid tenantId, Approval approval, bool approved, CancellationToken ct = default, WorkingCalendar? calendar = null)
    {
        var instance = await RunningAsync(db, tenantId, approval, ct);
        if (instance is null) return new DecisionOutcome(false, WorkflowInstanceStatus.Completed, null, false, Guid.Empty);
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
            return new DecisionOutcome(true, WorkflowInstanceStatus.Rejected, null, false, instance.Id);
        }
        RememberApprover(db, instance, approval.AssigneeId);
        var pending = await AdvanceAsync(db, tenantId, instance, approval.AssigneeId, calendar ?? WorkingCalendar.Default, ct);
        return new DecisionOutcome(true, StatusOf(db, instance), pending, ClosedFlag(db, instance), instance.Id);
    }

    /// <summary>A reviewer asked for changes: the run stops and goes back to the author. A resubmission starts a new run (a new round).</summary>
    public static async Task<DecisionOutcome> OnApprovalReturnedAsync(AppDbContext db, Guid tenantId, Approval approval, CancellationToken ct = default)
    {
        var instance = await RunningAsync(db, tenantId, approval, ct);
        if (instance is null) return new DecisionOutcome(false, WorkflowInstanceStatus.Completed, null, false, Guid.Empty);
        db.Entry(instance).CurrentValues.SetValues(instance with { Status = WorkflowInstanceStatus.Returned, UpdatedAt = DateTimeOffset.UtcNow });
        DomainEvents.Record(db, tenantId, "WorkflowReturned", "WorkflowReturned", nameof(WorkflowInstance), instance.Id.ToString(),
            payload: new { tenantId, instanceId = instance.Id, node = instance.CurrentNodeId }, details: "returned for changes");
        return new DecisionOutcome(true, WorkflowInstanceStatus.Returned, null, false, instance.Id);
    }

    /// <summary>The author (or records) ends a run that is still going: the pending approval is cancelled and its task taken off the approver's list.</summary>
    public static async Task<bool> WithdrawAsync(AppDbContext db, Guid tenantId, string entityType, Guid entityId, Guid? actorId, string reason, CancellationToken ct = default)
    {
        var instance = await db.WorkflowInstances.FirstOrDefaultAsync(i => i.TenantId == tenantId && i.EntityType == entityType && i.EntityId == entityId
            && i.Status == WorkflowInstanceStatus.Running, ct);
        if (instance is null) return false;
        var pending = await db.Approvals.Where(a => a.TenantId == tenantId && a.EntityType == entityType && a.EntityId == entityId && a.Status == ApprovalStatus.Pending).ToListAsync(ct);
        foreach (var a in pending)
        {
            db.Entry(a).CurrentValues.SetValues(a with { Status = ApprovalStatus.Cancelled, DecidedAt = DateTimeOffset.UtcNow, DecidedBy = actorId, Comment = reason });
            foreach (var t in await db.WorkTasks.Where(t => t.TenantId == tenantId && t.ApprovalId == a.Id && (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Breached)).ToListAsync(ct))
                db.Entry(t).CurrentValues.SetValues(t with { Status = WorkTaskStatus.Done, Description = "Withdrawn: " + reason });
        }
        db.Entry(instance).CurrentValues.SetValues(instance with { Status = WorkflowInstanceStatus.Closed, UpdatedAt = DateTimeOffset.UtcNow });
        DomainEvents.Record(db, tenantId, "WorkflowClosed", "WorkflowClosed", nameof(WorkflowInstance), instance.Id.ToString(),
            actorId: actorId, payload: new { tenantId, instanceId = instance.Id }, details: reason);
        return true;
    }

    private static Task<WorkflowInstance?> RunningAsync(AppDbContext db, Guid tenantId, Approval approval, CancellationToken ct) =>
        db.WorkflowInstances.FirstOrDefaultAsync(i =>
            i.TenantId == tenantId && i.EntityType == approval.EntityType && i.EntityId == approval.EntityId
            && i.Status == WorkflowInstanceStatus.Running, ct);

    // The instance is tracked and updated through its entry, so what is current is read back from the entry.
    private static WorkflowInstanceStatus StatusOf(AppDbContext db, WorkflowInstance i) => db.Entry(i).CurrentValues.GetValue<WorkflowInstanceStatus>(nameof(WorkflowInstance.Status));
    private static JsonObject Context(AppDbContext db, WorkflowInstance i)
    {
        try { return JsonNode.Parse(db.Entry(i).CurrentValues.GetValue<string>(nameof(WorkflowInstance.ContextJson))) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }   // malformed context: treated as empty, the starter is still known from the fallback below
    }
    private static bool ClosedFlag(AppDbContext db, WorkflowInstance i) => Context(db, i)["closed"]?.GetValue<bool>() == true;
    private static void SaveContext(AppDbContext db, WorkflowInstance i, JsonObject ctx) =>
        db.Entry(i).CurrentValues.SetValues(i with { ContextJson = ctx.ToJsonString(), UpdatedAt = DateTimeOffset.UtcNow, Status = StatusOf(db, i), CurrentNodeId = db.Entry(i).CurrentValues.GetValue<string>(nameof(WorkflowInstance.CurrentNodeId)) });
    private static void RememberApprover(AppDbContext db, WorkflowInstance i, Guid person)
    {
        var ctx = Context(db, i);
        var list = ctx["approvedBy"] as JsonArray ?? new JsonArray();
        if (!list.Any(x => x?.GetValue<string>() == person.ToString())) list.Add(person.ToString());
        ctx["approvedBy"] = list;
        SaveContext(db, i, ctx);
    }

    /// <returns>Pending approval id, or null when the instance completed.</returns>
    private static async Task<Guid?> AdvanceAsync(
        AppDbContext db, Guid tenantId, WorkflowInstance instance, Guid actorId, WorkingCalendar calendar, CancellationToken ct)
    {
        var def = await db.WorkflowDefinitions.FirstAsync(d => d.Id == instance.DefinitionId, ct);
        using var nodes = JsonDocument.Parse(def.NodesJson);
        var list = nodes.RootElement.EnumerateArray().ToList();
        var current = db.Entry(instance).CurrentValues.GetValue<string>(nameof(WorkflowInstance.CurrentNodeId));
        var idx = list.FindIndex(n => (n.TryGetProperty("id", out var id) ? id.GetString() : null) == current);
        for (var i = idx + 1; i < list.Count; i++)
        {
            var node = list[i];
            var nodeId = node.TryGetProperty("id", out var nid) ? nid.GetString() ?? $"n{i}" : $"n{i}";
            var type = node.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!ConditionHolds(node, Context(db, instance)))
            {
                Skipped(db, tenantId, instance, nodeId, "condition not met");
                continue;
            }
            var hint = Context(db, instance)["titleHint"]?.GetValue<string>();
            var label = node.TryGetProperty("label", out var lb) ? lb.GetString() : null;
            switch (type)
            {
                case "approval":
                {
                    var assignee = await ApplyDelegationFallbackAsync(db, tenantId, await ResolveAssigneeAsync(db, tenantId, node, actorId, instance, ct), ct);
                    if (node.TryGetProperty("skipDuplicate", out var sd) && sd.ValueKind == JsonValueKind.True && IsSameAsBefore(db, instance, assignee))
                    {
                        Skipped(db, tenantId, instance, nodeId, "same person as the author or an earlier approver");
                        continue;
                    }
                    var slaDays = node.TryGetProperty("slaDays", out var s) && s.ValueKind == JsonValueKind.Number
                        ? s.GetInt32() : 5;
                    var working = node.TryGetProperty("workingDays", out var wd) && wd.ValueKind == JsonValueKind.True;
                    var now = DateTimeOffset.UtcNow;
                    var due = working ? await calendar.AddWorkingDaysAsync(db, tenantId, now, slaDays, ct) : now.AddDays(slaDays);
                    var approval = new Approval(Guid.NewGuid(), tenantId, instance.EntityType, instance.EntityId,
                        assignee, ApprovalStatus.Pending, ApprovalPriority.Normal,
                        due, null, null, null);
                    db.Approvals.Add(approval);
                    db.WorkTasks.Add(new WorkTask(Guid.NewGuid(), tenantId,
                        $"{label ?? "Workflow step"}: {hint ?? $"{instance.EntityType} {instance.EntityId}"}",
                        assignee, approval.Id, WorkTaskStatus.Open, approval.DueAt, DateTimeOffset.UtcNow));
                    if (node.TryGetProperty("notify", out var nf) && nf.ValueKind == JsonValueKind.True)
                        db.Notifications.Add(new Notification(Guid.NewGuid(), tenantId, assignee, label ?? "Action needed",
                            hint ?? $"{instance.EntityType} {instance.EntityId}", NotificationChannel.InApp, NotificationStatus.Sent, DateTimeOffset.UtcNow));
                    db.Entry(instance).CurrentValues.SetValues(instance with
                    {
                        CurrentNodeId = nodeId,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        ContextJson = db.Entry(instance).CurrentValues.GetValue<string>(nameof(WorkflowInstance.ContextJson)),
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
                        ContextJson = db.Entry(instance).CurrentValues.GetValue<string>(nameof(WorkflowInstance.ContextJson)),
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
                        label ?? "Workflow update", hint is null ? text : $"{text} ({hint})", NotificationChannel.InApp, NotificationStatus.Sent, DateTimeOffset.UtcNow));
                    break;
                }
                case "close":
                {
                    // The last step of a letter's life: it is finished, and the record says so.
                    var ctx = Context(db, instance);
                    ctx["closed"] = true;
                    db.Entry(instance).CurrentValues.SetValues(instance with
                    {
                        CurrentNodeId = nodeId,
                        Status = WorkflowInstanceStatus.Completed,
                        ContextJson = ctx.ToJsonString(),
                        UpdatedAt = DateTimeOffset.UtcNow,
                    });
                    DomainEvents.Record(db, tenantId, "WorkflowCompleted", "WorkflowCompleted",
                        nameof(WorkflowInstance), instance.Id.ToString(),
                        payload: new { tenantId, instanceId = instance.Id }, details: "completed and closed");
                    return null;
                }
                case "end":
                    db.Entry(instance).CurrentValues.SetValues(instance with
                    {
                        CurrentNodeId = nodeId,
                        Status = WorkflowInstanceStatus.Completed,
                        UpdatedAt = DateTimeOffset.UtcNow,
                        ContextJson = db.Entry(instance).CurrentValues.GetValue<string>(nameof(WorkflowInstance.ContextJson)),
                    });
                    DomainEvents.Record(db, tenantId, "WorkflowCompleted", "WorkflowCompleted",
                        nameof(WorkflowInstance), instance.Id.ToString(),
                        payload: new { tenantId, instanceId = instance.Id }, details: "completed");
                    return null;
                default:
                    throw new InvalidOperationException($"Unsupported workflow node type '{type}' (supported: approval, task, notification, close, end).");
            }
        }
        // No explicit end node: complete.
        db.Entry(instance).CurrentValues.SetValues(instance with
        {
            Status = WorkflowInstanceStatus.Completed,
            UpdatedAt = DateTimeOffset.UtcNow,
            ContextJson = db.Entry(instance).CurrentValues.GetValue<string>(nameof(WorkflowInstance.ContextJson)),
            CurrentNodeId = db.Entry(instance).CurrentValues.GetValue<string>(nameof(WorkflowInstance.CurrentNodeId)),
        });
        return null;
    }

    private static void Skipped(AppDbContext db, Guid tenantId, WorkflowInstance instance, string nodeId, string why) =>
        DomainEvents.Record(db, tenantId, "WorkflowStepSkipped", "WorkflowStepSkipped", nameof(WorkflowInstance), instance.Id.ToString(),
            payload: new { tenantId, instanceId = instance.Id, node = nodeId }, details: $"{nodeId}: {why}");

    private static bool IsSameAsBefore(AppDbContext db, WorkflowInstance instance, Guid assignee)
    {
        var ctx = Context(db, instance);
        if (Guid.TryParse(ctx["submitterId"]?.GetValue<string>(), out var submitter) && submitter == assignee) return true;
        return (ctx["approvedBy"] as JsonArray)?.Any(x => x?.GetValue<string>() == assignee.ToString()) == true;
    }

    /// <summary>A node with no "when" always runs. Fields: amount, priority (Normal &lt; High &lt; Urgent), isConfidential. An unknown field or an unset value makes the condition false, never an error.</summary>
    internal static bool ConditionHolds(JsonElement node, JsonObject ctx)
    {
        if (!node.TryGetProperty("when", out var w) || w.ValueKind != JsonValueKind.Object) return true;
        var field = w.TryGetProperty("field", out var f) ? f.GetString() : null;
        var op = w.TryGetProperty("op", out var o) ? o.GetString() : null;
        if (field is null || op is null || !w.TryGetProperty("value", out var want)) return false;
        var have = ctx[field];
        if (have is null) return false;
        int cmp;
        if (want.ValueKind == JsonValueKind.Number && have is JsonValue hv && hv.TryGetValue<decimal>(out var d))
            cmp = d.CompareTo(want.GetDecimal());
        else if (want.ValueKind is JsonValueKind.True or JsonValueKind.False && have is JsonValue bv && bv.TryGetValue<bool>(out var b))
            cmp = b.CompareTo(want.GetBoolean());
        else if (want.ValueKind == JsonValueKind.String && have is JsonValue sv && sv.TryGetValue<string>(out var str))
        {
            var rank = new[] { "Normal", "High", "Urgent" };
            var (ri, rw) = (Array.IndexOf(rank, str), Array.IndexOf(rank, want.GetString()));
            cmp = field == "priority" && ri >= 0 && rw >= 0 ? ri.CompareTo(rw) : string.Compare(str, want.GetString(), StringComparison.OrdinalIgnoreCase);
        }
        else return false;
        return op switch { ">" => cmp > 0, ">=" => cmp >= 0, "<" => cmp < 0, "<=" => cmp <= 0, "==" => cmp == 0, "!=" => cmp != 0, _ => false };
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
            var ctx = Context(db, instance);
            var sid = Guid.TryParse(ctx["submitterId"]?.GetValue<string>(), out var parsedSid) ? parsedSid : Guid.Empty;
            if (sid == Guid.Empty) sid = fallback;

            if (key == "submitter")
            {
                return sid;
            }

            // The head of the unit a letter is addressed to, and the head of the unit above it.
            if (key is "target_head" or "target_dean")
            {
                if (Guid.TryParse(ctx["targetUnitId"]?.GetValue<string>(), out var targetUnit))
                {
                    var unit = await db.OrganizationalUnits.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == targetUnit, ct);
                    if (key == "target_dean" && unit?.ParentId is Guid up)
                        unit = await db.OrganizationalUnits.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == up, ct) ?? unit;
                    if (unit?.LeaderPersonId is Guid head && head != Guid.Empty) return head;
                    throw new KeyNotFoundException($"The receiving unit '{unit?.Name ?? targetUnit.ToString()}' has no head to receive this letter.");
                }
                throw new KeyNotFoundException("This letter has no receiving unit, so the step addressed to its head cannot be assigned.");
            }

            // The first internal person the letter is addressed to (To, not Cc).
            if (key == "recipient")
            {
                if (Guid.TryParse(ctx["recipientId"]?.GetValue<string>(), out var rid)) return rid;
                if (Guid.TryParse(ctx["targetUnitId"]?.GetValue<string>(), out var tu)
                    && (await db.OrganizationalUnits.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == tu, ct))?.LeaderPersonId is Guid h && h != Guid.Empty) return h;
                throw new KeyNotFoundException("This letter has no internal recipient (To) and no receiving unit, so the step addressed to the recipient cannot be assigned.");
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
