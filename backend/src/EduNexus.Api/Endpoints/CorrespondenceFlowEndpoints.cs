using System.Text.Json;
using EduNexus.Api.Auth;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static EduNexus.Api.Endpoints.EndpointHelpers;

namespace EduNexus.Api.Endpoints;

/// <summary>
/// The life of a typed letter after it is written: send it through its type's workflow, take it back for changes, resubmit,
/// withdraw or close it, and read its history. A letter without a type keeps the original single-reviewer path.
/// </summary>
public static class CorrespondenceFlowEndpoints
{
    private static readonly string[] NodeTypes = ["start", "approval", "task", "notification", "close", "end"];
    private static readonly string[] Ops = [">", ">=", "<", "<=", "==", "!="];

    public static void MapCorrespondenceFlowEndpoints(this IEndpointRouteBuilder app)
    {
        var corr = app.MapGroup("/api/correspondence").WithTags("Correspondence").RequireAuthorization();

        // The caller's own letters in every status. A student sees only these; nothing here needs the general read permission.
        corr.MapGet("/mine", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (await ActingPersonAsync(db, ctx, tenantId, ct) is not { } me) return Results.Forbid();
            return Results.Ok(await db.Correspondences.Where(c => c.TenantId == tenantId && c.AuthorId == me).OrderByDescending(c => c.CreatedAt).ToListAsync(ct));
        });

        // The units a letter can be addressed to, for the screen's picker. Open to anyone who may start a letter, so it needs no directory permission.
        corr.MapGet("/units", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            if (!ctx.User.HasPermission("correspondence:create") && !ctx.User.HasPermission("correspondence:student-request") && !ctx.User.HasPermission("correspondence:read")) return Results.Forbid();
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.OrganizationalUnits.Where(u => u.TenantId == tenantId && u.IsActive).OrderBy(u => u.Code).Select(u => new { u.Id, u.Code, u.Name, u.ParentId }).ToListAsync(ct));
        });

        // Only the author changes a letter, and only while it is a draft or has been sent back.
        corr.MapPatch("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, EditCorrespondenceReq req, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = await db.Correspondences.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            var me = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            if (c is null || me is null || c.AuthorId != me) return Results.NotFound(new { error = "Correspondence not found." });
            if (c.Status is not (CorrespondenceStatus.Draft or CorrespondenceStatus.Returned))
                return Results.Conflict(new { error = $"Only a Draft or Returned letter can be edited (now {c.Status})." });
            var priority = c.Priority;
            if (req.Priority is not null && !Enum.TryParse(req.Priority, true, out priority)) return Results.BadRequest(new { error = "Priority must be Normal|High|Urgent." });
            if (req.Subject is not null && string.IsNullOrWhiteSpace(req.Subject)) return Results.BadRequest(new { error = "Subject required." });
            if (req.Content is not null && string.IsNullOrWhiteSpace(req.Content)) return Results.BadRequest(new { error = "Content required." });
            if (req.Amount is < 0 or > 1_000_000_000m) return Results.BadRequest(new { error = "Amount must be between 0 and 1,000,000,000." });
            if (req.TargetUnitId is { } unit && !await db.OrganizationalUnits.AnyAsync(u => u.TenantId == req.TenantId && u.Id == unit && u.IsActive, ct))
                return Results.BadRequest(new { error = "The receiving unit was not found in this tenant." });
            var next = c with
            {
                Subject = req.Subject?.Trim() ?? c.Subject, Content = req.Content ?? c.Content, Priority = priority,
                Amount = req.Amount ?? c.Amount, TargetUnitId = req.TargetUnitId ?? c.TargetUnitId,
            };
            db.Entry(c).CurrentValues.SetValues(next);
            DomainEvents.Record(db, req.TenantId, "CorrespondenceEdited", "CorrespondenceEdited", nameof(Correspondence), c.Id.ToString(), actorId: me, details: c.Number);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(next);
        });

        // After a return: the author has edited and sends it again. A new round starts from the first step (see docs).
        corr.MapPost("/{id:guid}/resubmit", async (AppDbContext db, HttpContext ctx, IConfiguration config, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = await db.Correspondences.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            var me = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            if (c is null || me is null || c.AuthorId != me) return Results.NotFound(new { error = "Correspondence not found." });
            if (c.Status != CorrespondenceStatus.Returned) return Results.Conflict(new { error = $"Only a Returned letter can be resubmitted (now {c.Status})." });
            var (error, started) = await BeginAsync(db, config, c, ct);
            if (error is not null) return error;
            DomainEvents.Record(db, req.TenantId, "CorrespondenceResubmitted", "CorrespondenceResubmitted", nameof(Correspondence), c.Id.ToString(), actorId: me, details: c.Number);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(started);
        });

        // The author withdraws a letter that is still going; records staff may close any unfinished letter. A reason is always kept.
        corr.MapPost("/{id:guid}/close", async (AppDbContext db, HttpContext ctx, Guid id, CloseCorrespondenceReq req, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Reason) || req.Reason.Trim().Length < 3) return Results.BadRequest(new { error = "A reason of at least 3 characters is required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = await db.Correspondences.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            var me = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            var manager = ctx.User.HasPermission("records:manage");
            if (c is null || me is null || (c.AuthorId != me && !manager) || (c.IsConfidential && !ctx.User.HasPermission("correspondence:confidential") && c.AuthorId != me))
                return Results.NotFound(new { error = "Correspondence not found." });
            if (c.Status is CorrespondenceStatus.Closed or CorrespondenceStatus.Rejected or CorrespondenceStatus.Archived)
                return Results.Conflict(new { error = $"The letter is already {c.Status}." });
            if (c.AuthorId == me && !manager && c.Status is CorrespondenceStatus.Approved)
                return Results.Conflict(new { error = "An approved letter is closed by the records office." });
            var reason = req.Reason.Trim();
            await WorkflowRunner.WithdrawAsync(db, req.TenantId, nameof(Correspondence), c.Id, me, reason, ct);
            db.Entry(c).CurrentValues.SetValues(c with { Status = CorrespondenceStatus.Closed });
            DomainEvents.Record(db, req.TenantId, "CorrespondenceClosed", "CorrespondenceClosed", nameof(Correspondence), c.Id.ToString(), actorId: me, details: reason);
            if (c.AuthorId != me) Notify(db, req.TenantId, c.AuthorId, $"Closed: {c.Number}", $"{c.Subject} was closed by the records office: {reason}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(c with { Status = CorrespondenceStatus.Closed });
        });

        // The whole story: audit trail of the letter and its runs, plus every step with who decided it and why.
        corr.MapGet("/{id:guid}/history", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var c = await db.Correspondences.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            var me = await ActingPersonAsync(db, ctx, tenantId, ct);
            if (c is null) return Results.NotFound(new { error = "Correspondence not found." });
            var approvals = await db.Approvals.Where(a => a.TenantId == tenantId && a.EntityType == nameof(Correspondence) && a.EntityId == id).OrderBy(a => a.DueAt).ToListAsync(ct);
            var isParty = me is { } m && (c.AuthorId == m || approvals.Any(a => a.AssigneeId == m));
            var canRead = ctx.User.HasPermission("correspondence:read") && (!c.IsConfidential || ctx.User.HasPermission("correspondence:confidential"));
            if (!isParty && !canRead) return Results.NotFound(new { error = "Correspondence not found." });
            var instances = await db.WorkflowInstances.Where(i => i.TenantId == tenantId && i.EntityType == nameof(Correspondence) && i.EntityId == id).ToListAsync(ct);
            var ids = new[] { id.ToString() }.Concat(instances.Select(i => i.Id.ToString())).ToList();
            var audit = await db.AuditEvents.Where(a => a.TenantId == tenantId && ids.Contains(a.EntityId) && (a.EntityType == nameof(Correspondence) || a.EntityType == nameof(WorkflowInstance)))
                .OrderBy(a => a.At).ToListAsync(ct);
            var people = audit.Select(a => a.ActorId).Concat(approvals.Select(a => (Guid?)a.AssigneeId)).Concat(approvals.Select(a => a.DecidedBy)).Where(p => p.HasValue).Select(p => p!.Value).Distinct().ToList();   // non-null: filtered above
            var names = await db.People.Where(p => p.TenantId == tenantId && people.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.FullName, ct);
            string? Name(Guid? p) => p is { } g && names.TryGetValue(g, out var n) ? n : null;
            return Results.Ok(new
            {
                correspondence = new { c.Id, c.Number, c.Subject, c.Status, c.TypeCode, c.Amount, c.TargetUnitId },
                events = audit.Select(a => new { a.At, a.Action, a.ActorId, actorName = Name(a.ActorId), a.Details }),
                steps = approvals.Select(a => new { a.Id, a.AssigneeId, assigneeName = Name(a.AssigneeId), a.Status, a.DueAt, a.DecidedAt, a.DecidedBy, decidedByName = Name(a.DecidedBy), a.Comment }),
                runs = instances.Select(i => new { i.Id, i.Status, i.CurrentNodeId, i.UpdatedAt }),
            });
        });

        // Replace the steps of a workflow with an edited list (limits, roles, deadlines). Runs in progress follow the new list from the step they are on.
        app.MapPost("/api/workflows/definitions/{id:guid}/new-version", async (AppDbContext db, HttpContext ctx, Guid id, NewWorkflowVersionReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("workflow:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (ValidateNodes(req.NodesJson) is { } bad) return Results.BadRequest(new { error = bad });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var d = await db.WorkflowDefinitions.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (d is null) return Results.NotFound(new { error = "Workflow not found." });
            var next = d with { NodesJson = req.NodesJson!, Version = d.Version + 1, Name = string.IsNullOrWhiteSpace(req.Name) ? d.Name : req.Name.Trim() };
            db.Entry(d).CurrentValues.SetValues(next);
            DomainEvents.Record(db, req.TenantId, "WorkflowDefinitionVersioned", "WorkflowDefinitionVersioned", nameof(WorkflowDefinition), d.Id.ToString(),
                actorId: await ActingPersonAsync(db, ctx, req.TenantId, ct), details: $"{d.Code} v{next.Version}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(next);
        });
    }

    /// <summary>Null when the steps are usable; otherwise what is wrong.</summary>
    public static string? ValidateNodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "nodesJson is required.";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return "nodesJson must be a non-empty array.";
            var first = true;
            var seen = new HashSet<string>();
            foreach (var n in doc.RootElement.EnumerateArray())
            {
                var type = n.ValueKind == JsonValueKind.Object && n.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type is null || !NodeTypes.Contains(type)) return $"Invalid node type '{type}'.";
                if (first && type != "start") return "The first step must be of type 'start'.";
                first = false;
                if (!n.TryGetProperty("id", out var idp) || string.IsNullOrWhiteSpace(idp.GetString()) || !seen.Add(idp.GetString()!)) return "Every step needs a unique id.";   // non-null: checked just before
                if (n.TryGetProperty("when", out var w) && (w.ValueKind != JsonValueKind.Object || !w.TryGetProperty("op", out var op) || !Ops.Contains(op.GetString()) || !w.TryGetProperty("field", out _) || !w.TryGetProperty("value", out _)))
                    return $"Step '{idp.GetString()}' has an unreadable condition (field, op in {string.Join(' ', Ops)}, value).";
                if (n.TryGetProperty("slaDays", out var sla) && (sla.ValueKind != JsonValueKind.Number || !sla.TryGetInt32(out var days) || days is < 1 or > 60))
                    return $"Step '{idp.GetString()}': slaDays must be a whole number from 1 to 60.";
            }
            return null;
        }
        catch (JsonException) { return "Invalid JSON in nodesJson."; }
    }

    /// <summary>Types that stay inside one college: the sender's department and the receiving unit must share the same top-level unit.</summary>
    internal static readonly string[] CollegeScoped = ["COLLEGE_ADMIN", "COLLEGE_DEPT", "TO_DEAN"];

    /// <summary>Null when the letter may go; otherwise what is wrong.</summary>
    internal static async Task<string?> CollegeCheckAsync(AppDbContext db, Guid tenantId, string? typeCode, Guid authorId, Guid? targetUnitId, CancellationToken ct)
    {
        if (typeCode is null || !CollegeScoped.Contains(typeCode) || targetUnitId is null) return null;
        async Task<Guid?> RootAsync(Guid? unit)
        {
            for (var i = 0; i < 12 && unit is { } id; i++)
            {
                var u = await db.OrganizationalUnits.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
                if (u is null) return null;
                if (u.ParentId is null) return u.Id;
                unit = u.ParentId;
            }
            return null;
        }
        var mine = (await db.People.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == authorId, ct))?.DepartmentId;
        if (mine is null) return "Your profile has no department, so the system cannot tell which college this letter belongs to.";
        var (a, b) = (await RootAsync(mine), await RootAsync(targetUnitId));
        return a is not null && a == b ? null : "This type of letter stays inside one college: the receiving unit must be in the sender's college.";
    }

    internal static WorkingCalendar CalendarFor(IConfiguration config) =>
        new(WorkingCalendar.ParseWeekend(config["Workweek:WeekendDays"]), TimeSpan.FromHours(config.GetValue("Calendar:UtcOffsetHours", 3.0)));

    internal static void Notify(AppDbContext db, Guid tenantId, Guid person, string title, string body) =>
        db.Notifications.Add(new Notification(Guid.NewGuid(), tenantId, person, title, body, NotificationChannel.InApp, NotificationStatus.Sent, DateTimeOffset.UtcNow));

    /// <summary>Starts a round of the letter's workflow and sets its status. Returns a ready answer for the caller when it cannot start.</summary>
    internal static async Task<(IResult? Error, object? Started)> BeginAsync(AppDbContext db, IConfiguration config, Correspondence c, CancellationToken ct)
    {
        var def = c.TypeCode is null ? null : await db.CorrespondenceTypes.FirstOrDefaultAsync(t => t.TenantId == c.TenantId && t.Code == c.TypeCode, ct);
        if (def?.WorkflowCode is null) return (Results.Conflict(new { error = "This letter's type has no workflow." }), null);
        if (def.RequiresTargetUnit && c.TargetUnitId is null) return (Results.BadRequest(new { error = $"{def.NameEn} needs the receiving unit (targetUnitId)." }), null);
        if (def.RequiresAmount && c.Amount is not > 0) return (Results.BadRequest(new { error = $"{def.NameEn} needs an amount greater than zero." }), null);
        if (await CollegeCheckAsync(db, c.TenantId, c.TypeCode, c.AuthorId, c.TargetUnitId, ct) is { } outside) return (Results.BadRequest(new { error = outside }), null);
        var recipient = await db.Correspondents.Where(x => x.TenantId == c.TenantId && x.CorrespondenceId == c.Id && x.Kind == RecipientKind.To && x.PersonId != null)
            .Select(x => x.PersonId).FirstOrDefaultAsync(ct);
        var context = new Dictionary<string, object?>
        {
            ["amount"] = c.Amount, ["priority"] = c.Priority.ToString(), ["isConfidential"] = c.IsConfidential,
            ["targetUnitId"] = c.TargetUnitId?.ToString(), ["recipientId"] = recipient?.ToString(),
        };
        WorkflowRunner.StartResult run;
        try
        {
            run = await WorkflowRunner.StartAsync(db, c.TenantId, def.WorkflowCode, nameof(Correspondence), c.Id, c.AuthorId, ct,
                new WorkflowRunner.Options(context, $"{c.Number}: {c.Subject}", CalendarFor(config)));
        }
        catch (KeyNotFoundException ex)
        {
            // An approver cannot be found (an empty role or a unit with no head): nothing is saved and the sender is told what to fix.
            return (Results.UnprocessableEntity(new { error = ex.Message }), null);
        }
        var status = run.Completed ? (run.Closed ? CorrespondenceStatus.Closed : CorrespondenceStatus.Approved) : CorrespondenceStatus.Submitted;
        db.Entry(c).CurrentValues.SetValues(c with { Status = status });
        DomainEvents.Record(db, c.TenantId, "CorrespondenceSubmitted", "CorrespondenceSubmitted", nameof(Correspondence), c.Id.ToString(),
            actorId: c.AuthorId, payload: new { tenantId = c.TenantId, correspondenceId = c.Id, number = c.Number, workflow = def.WorkflowCode }, details: $"{c.Number} {def.WorkflowCode}");
        return (null, new { correspondence = c with { Status = status }, approvalId = run.PendingApprovalId, instanceId = run.InstanceId, workflow = def.WorkflowCode });
    }
}

public sealed record EditCorrespondenceReq(Guid TenantId, string? Subject, string? Content, string? Priority, decimal? Amount, Guid? TargetUnitId);
public sealed record CloseCorrespondenceReq(Guid TenantId, string? Reason);
public sealed record NewWorkflowVersionReq(Guid TenantId, string? NodesJson, string? Name);
