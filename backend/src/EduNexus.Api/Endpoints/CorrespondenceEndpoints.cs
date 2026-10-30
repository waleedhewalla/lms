using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using EduNexus.Api.Auth;
using EduNexus.Api.Events;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using static EduNexus.Api.Endpoints.EndpointHelpers;

namespace EduNexus.Api.Endpoints;

public static class CorrespondenceEndpoints
{
    public static void MapCorrespondenceEndpoints(this IEndpointRouteBuilder app)
    {
        var corr = app.MapGroup("/api/correspondence").WithTags("Correspondence").RequireAuthorization();

        // The register: every letter of a year in numbering order, filterable, with who received it and who got a copy.
        // Confidential letters appear only to callers cleared for them. Content is never part of the register.
        corr.MapGet("/register", async (AppDbContext db, HttpContext ctx, IConfiguration config, Guid tenantId, int? year, string? type,
            string? status, string? q, int? skip, int? take, string? typeCode, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            if (BadFilter(type, status) is { } bad) return bad;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var rows = await RegisterRowsAsync(db, ctx, config, tenantId, year, type, status, q, skip ?? 0, Math.Clamp(take ?? 50, 1, 200), ct, typeCode);
            return Results.Ok(rows);
        });

        // The same register as a spreadsheet; calendar=hijri|both adds Umm al-Qura dates (as the report exports do).
        corr.MapGet("/register/export", async (AppDbContext db, HttpContext ctx, IConfiguration config, Guid tenantId, int? year, string? type,
            string? status, string? q, string? calendar, string? typeCode, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            if (BadFilter(type, status) is { } bad) return bad;
            var mode = (calendar ?? "gregorian").Trim().ToLowerInvariant();
            if (mode is not ("gregorian" or "hijri" or "both")) return Results.BadRequest(new { error = "calendar must be gregorian, hijri or both." });
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var page = await RegisterRowsAsync(db, ctx, config, tenantId, year, type, status, q, 0, 10_000, ct, typeCode);
            var offset = UtcOffset(config);
            var table = page.Items.Select(r => new Dictionary<string, object?>
            {
                ["number"] = r.Number, ["type"] = r.Type, ["subject"] = r.Subject, ["status"] = r.Status, ["priority"] = r.Priority,
                ["confidential"] = r.IsConfidential, ["created_at"] = r.CreatedAt,
                ["to"] = string.Join("; ", r.Recipients.Where(x => x.Kind == nameof(RecipientKind.To)).Select(x => x.DisplayName)),
                ["cc"] = string.Join("; ", r.Recipients.Where(x => x.Kind == nameof(RecipientKind.Cc)).Select(x => x.DisplayName)),
                ["type_code"] = r.TypeCode,   // added last so the columns before it keep their positions
            }).ToList();
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ReportEndpoints.ToCsv(ReportEndpoints.WithCalendar(table, mode, offset)))).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"correspondence-register-{year?.ToString() ?? "all"}.csv");
        });

        corr.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? status, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            var canSeeConfidential = CanSeeConfidential(ctx.User);
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Correspondences.Where(c => c.TenantId == tenantId);
            if (!canSeeConfidential) q = q.Where(c => !c.IsConfidential);
            if (Enum.TryParse<CorrespondenceStatus>(status, true, out var s)) q = q.Where(c => c.Status == s);
            return Results.Ok(await q.OrderByDescending(c => c.CreatedAt).ToListAsync(ct));
        });

        corr.MapPost("/", async (AppDbContext db, HttpContext ctx, IConfiguration config, CreateCorrespondenceReq req, CancellationToken ct) =>
        {
            // A typed letter is allowed by the permission its type names (for example students may start only a student request);
            // a letter with no type needs the general permission, as before.
            if (!ctx.User.HasPermission("correspondence:create") && string.IsNullOrWhiteSpace(req.TypeCode)) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (req.IsConfidential && !ctx.User.HasPermission("correspondence:confidential"))
                return Results.Forbid();
            if (!Enum.TryParse<CorrespondenceType>(req.Type, true, out var type))
                return Results.BadRequest(new { error = "Type must be Incoming|Outgoing|Internal." });
            if (!Enum.TryParse<CorrespondencePriority>(req.Priority ?? "Normal", true, out var priority))
                return Results.BadRequest(new { error = "Priority must be Normal|High|Urgent." });
            if (!await db.Tenants.AnyAsync(t => t.Id == req.TenantId, ct))
                return Results.NotFound(new { error = "Tenant not found." });
            if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.AuthorId, ct))
                return Results.NotFound(new { error = "Author not found in tenant." });
            Correspondence c;
            try { c = Correspondence.Create(req.TenantId, type, req.Subject, req.Content, req.AuthorId, priority, req.IsConfidential, req.ParentCorrespondenceId); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            CorrespondenceTypeDef? typeDef = null;
            if (!string.IsNullOrWhiteSpace(req.TypeCode))
            {
                var code = req.TypeCode.Trim().ToUpperInvariant();
                typeDef = await db.CorrespondenceTypes.FirstOrDefaultAsync(t => t.TenantId == req.TenantId && t.Code == code, ct);
                if (typeDef is null || !typeDef.IsActive) return Results.BadRequest(new { error = "Unknown or inactive correspondence type." });
                if (!ctx.User.HasPermission(typeDef.CreatePermission)) return Results.Forbid();
                // A restricted type (for example a student request) is always written as the caller, never on someone else's behalf.
                if (typeDef.CreatePermission != "correspondence:create" && await ActingPersonAsync(db, ctx, req.TenantId, ct) != req.AuthorId) return Results.Forbid();
                if (typeDef.RequiresTargetUnit && req.TargetUnitId is null) return Results.BadRequest(new { error = $"{typeDef.NameEn} needs the receiving unit (targetUnitId)." });
                if (typeDef.RequiresAmount && req.Amount is not > 0) return Results.BadRequest(new { error = $"{typeDef.NameEn} needs an amount greater than zero." });
                if (await CorrespondenceFlowEndpoints.CollegeCheckAsync(db, req.TenantId, typeDef.Code, req.AuthorId, req.TargetUnitId, ct) is { } outside) return Results.BadRequest(new { error = outside });
            }
            if (req.Amount is < 0 or > 1_000_000_000m) return Results.BadRequest(new { error = "Amount must be between 0 and 1,000,000,000." });
            if (req.TargetUnitId is { } unitId && !await db.OrganizationalUnits.AnyAsync(u => u.TenantId == req.TenantId && u.Id == unitId && u.IsActive, ct))
                return Results.BadRequest(new { error = "The receiving unit was not found in this tenant." });
            if (req.ParentCorrespondenceId.HasValue && !await db.Correspondences.AnyAsync(x => x.TenantId == req.TenantId && x.Id == req.ParentCorrespondenceId.Value
                    && (!x.IsConfidential || CanSeeConfidential(ctx.User)), ct))
                return Results.NotFound(new { error = "Parent correspondence not found." });
            // One register per direction and year, as official correspondence is kept: IN-2026-000001, OUT-2026-000001, INT-2026-000001.
            var numberYear = LocalYear(config, c.CreatedAt);
            var seq = await Sequences.NextAsync(db, req.TenantId, $"correspondence:{type}:{numberYear}", ct);
            c = c with { Number = $"{Prefix(type)}-{numberYear}-{seq:D6}", TypeCode = typeDef?.Code, TargetUnitId = req.TargetUnitId, Amount = req.Amount };
            db.Correspondences.Add(c);
            foreach (var r in req.Recipients ?? [])
            {
                if (!Enum.TryParse<RecipientKind>(r.Kind ?? "To", true, out var kind))
                    return Results.BadRequest(new { error = "Recipient kind must be To|Cc." });
                Guid? pid = Guid.TryParse(r.PersonId, out var g) ? g : null;
                if (pid.HasValue && !await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == pid.Value, ct))
                    return Results.BadRequest(new { error = $"Recipient person {r.PersonId} not found in tenant." });
                db.Correspondents.Add(new Correspondent(Guid.NewGuid(), req.TenantId, c.Id, pid, r.DisplayName, r.IsExternal, kind));
            }
            DomainEvents.Record(db, req.TenantId, "CorrespondenceCreated", "CorrespondenceCreated",
                nameof(Correspondence), c.Id.ToString(),
                payload: new { tenantId = req.TenantId, correspondenceId = c.Id, number = c.Number },
                details: c.Number);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/correspondence/{c.Id}", c);
        });

        corr.MapPost("/{id:guid}/submit", async (AppDbContext db, HttpContext ctx, IConfiguration config, Guid id, SubmitCorrespondenceReq req, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = await db.Correspondences.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (c is null || (c.IsConfidential && !CanSeeConfidential(ctx.User))) return Results.NotFound(new { error = "Correspondence not found." });
            var me = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            // A typed letter is sent by its author, through its type's workflow (a student sends only their own request).
            // An untyped letter keeps the original path: the general permission and one named reviewer.
            if (c.TypeCode is not null)
            {
                if (me is null || me != c.AuthorId) return Results.Forbid();
                if (c.Status != CorrespondenceStatus.Draft)
                    return Results.Conflict(new { error = $"Only Draft correspondence can be submitted (now {c.Status})." });
                var (error, started) = await CorrespondenceFlowEndpoints.BeginAsync(db, config, c, ct);
                if (error is not null) return error;
                await db.SaveChangesAsync(ct);
                await scope.CommitAsync(ct);
                return Results.Ok(started);
            }
            if (!ctx.User.HasPermission("correspondence:create")) return Results.Forbid();
            if (c.Status != CorrespondenceStatus.Draft)
                return Results.Conflict(new { error = $"Only Draft correspondence can be submitted (now {c.Status})." });
            if (req.ReviewerId is not { } reviewerId) return Results.BadRequest(new { error = "reviewerId is required for a letter without a type." });
            if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == reviewerId, ct))
                return Results.NotFound(new { error = "Reviewer not found in tenant." });
            db.Entry(c).CurrentValues.SetValues(c with { Status = CorrespondenceStatus.Submitted });
            var accelerated = c.Priority is CorrespondencePriority.High or CorrespondencePriority.Urgent;
            var approval = new Approval(Guid.NewGuid(), req.TenantId, nameof(Correspondence), c.Id,
                reviewerId, ApprovalStatus.Pending,
                accelerated ? ApprovalPriority.Accelerated : ApprovalPriority.Normal,
                DateTimeOffset.UtcNow.AddDays(accelerated ? 2 : 5), null, null, null);
            db.Approvals.Add(approval);
            db.WorkTasks.Add(new WorkTask(Guid.NewGuid(), req.TenantId, $"Review {c.Number}: {c.Subject}",
                reviewerId, approval.Id, WorkTaskStatus.Open, approval.DueAt, DateTimeOffset.UtcNow));
            DomainEvents.Record(db, req.TenantId, "CorrespondenceSubmitted", "CorrespondenceSubmitted",
                nameof(Correspondence), c.Id.ToString(),
                payload: new { tenantId = req.TenantId, correspondenceId = c.Id, number = c.Number, reviewerId = reviewerId, approvalId = approval.Id },
                details: $"{c.Number}->{reviewerId}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(new { correspondence = c, approvalId = approval.Id });
        });

        corr.MapGet("/{id:guid}/thread", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var current = await db.Correspondences.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == id, ct);
            if (current is null || (current.IsConfidential && !CanSeeConfidential(ctx.User))) return Results.NotFound(new { error = "Correspondence not found." });
            var cleared = CanSeeConfidential(ctx.User);

            Correspondence? root = current;
            var visited = new System.Collections.Generic.HashSet<Guid> { current.Id };
            while (root.ParentCorrespondenceId.HasValue && !visited.Contains(root.ParentCorrespondenceId.Value))
            {
                var parent = await db.Correspondences.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == root.ParentCorrespondenceId.Value, ct);
                if (parent is null || (parent.IsConfidential && !cleared)) break;   // the chain stops where the caller's clearance does
                visited.Add(parent.Id);
                root = parent;
            }

            var replies = await db.Correspondences
                .Where(c => c.TenantId == tenantId && c.ParentCorrespondenceId == id && (cleared || !c.IsConfidential))
                .OrderBy(c => c.CreatedAt)
                .ToListAsync(ct);

            return Results.Ok(new { current, root, replies });
        });

        corr.MapGet("/{id:guid}/routing-slips", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (!await db.Correspondences.AnyAsync(x => x.TenantId == tenantId && x.Id == id && (!x.IsConfidential || CanSeeConfidential(ctx.User)), ct))
                return Results.NotFound(new { error = "Correspondence not found." });
            var slips = await db.CorrespondenceRoutingSlips
                .Where(s => s.TenantId == tenantId && s.CorrespondenceId == id)
                .OrderByDescending(s => s.CreatedAt)
                .ToListAsync(ct);
            return Results.Ok(slips);
        });

        corr.MapPost("/{id:guid}/routing-slips", async (AppDbContext db, HttpContext ctx, Guid id, CreateRoutingSlipReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.ActionRequired)) return Results.BadRequest(new { error = "ActionRequired is required." });
            if (string.IsNullOrWhiteSpace(req.Instructions)) return Results.BadRequest(new { error = "Instructions are required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = await db.Correspondences.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (c is null || (c.IsConfidential && !CanSeeConfidential(ctx.User))) return Results.NotFound(new { error = "Correspondence not found." });

            var slip = new CorrespondenceRoutingSlip(
                Guid.NewGuid(), req.TenantId, id, req.FromPersonId, req.ToUnitId, req.ToPersonId,
                req.ActionRequired.Trim(), req.Instructions.Trim(), req.DueAt, DateTimeOffset.UtcNow);
            db.CorrespondenceRoutingSlips.Add(slip);

            if (req.ToPersonId.HasValue)
            {
                db.ScheduledActivities.Add(new ScheduledActivity(
                    Guid.NewGuid(), req.TenantId, nameof(Correspondence), id,
                    ActivityType.ToDo, req.ToPersonId.Value,
                    $"Directive: {req.ActionRequired} ({c.Number})",
                    req.DueAt ?? DateTimeOffset.UtcNow.AddDays(3),
                    false, null, DateTimeOffset.UtcNow));
            }

            DomainEvents.Record(db, req.TenantId, "RoutingSlipCreated", "RoutingSlipCreated",
                nameof(CorrespondenceRoutingSlip), slip.Id.ToString(),
                payload: new { tenantId = req.TenantId, correspondenceId = id, routingSlipId = slip.Id, action = slip.ActionRequired },
                details: $"{c.Number} -> {slip.ActionRequired}");

            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/correspondence/{id}/routing-slips/{slip.Id}", slip);
        });

        corr.MapPost("/{id:guid}/routing-slips/{slipId:guid}/complete", async (AppDbContext db, HttpContext ctx, Guid id, Guid slipId, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (!await db.Correspondences.AnyAsync(x => x.TenantId == tenantId && x.Id == id && (!x.IsConfidential || CanSeeConfidential(ctx.User)), ct))
                return Results.NotFound(new { error = "Correspondence not found." });
            var slip = await db.CorrespondenceRoutingSlips.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.CorrespondenceId == id && s.Id == slipId, ct);
            if (slip is null) return Results.NotFound(new { error = "Routing slip not found." });
            db.Entry(slip).CurrentValues.SetValues(slip with { IsCompleted = true, CompletedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(slip with { IsCompleted = true, CompletedAt = DateTimeOffset.UtcNow });
        });
    }

    private static bool CanSeeConfidential(ClaimsPrincipal user) => user.HasPermission("correspondence:confidential");

    private static string Prefix(CorrespondenceType type) => type switch
    {
        CorrespondenceType.Incoming => "IN",
        CorrespondenceType.Outgoing => "OUT",
        _ => "INT",
    };

    private static TimeSpan UtcOffset(IConfiguration config) => TimeSpan.FromHours(config.GetValue("Calendar:UtcOffsetHours", 3.0));

    /// <summary>The year on the institution's wall clock, so a letter written just after midnight on 1 January joins the new year's register.</summary>
    private static int LocalYear(IConfiguration config, DateTimeOffset at) => at.ToOffset(UtcOffset(config)).Year;

    private static IResult? BadFilter(string? type, string? status)
    {
        if (!string.IsNullOrWhiteSpace(type) && !Enum.TryParse<CorrespondenceType>(type, true, out _))
            return Results.BadRequest(new { error = "Type must be Incoming|Outgoing|Internal." });
        if (!string.IsNullOrWhiteSpace(status) && !Enum.TryParse<CorrespondenceStatus>(status, true, out _))
            return Results.BadRequest(new { error = "Status is not a known correspondence status." });
        return null;
    }

    public sealed record RegisterRecipient(string DisplayName, bool IsExternal, string Kind);
    public sealed record RegisterRow(Guid Id, string Number, string Type, string Subject, string Status, string Priority, bool IsConfidential,
        DateTimeOffset CreatedAt, Guid AuthorId, Guid? ParentCorrespondenceId, List<RegisterRecipient> Recipients, string? TypeCode = null);
    public sealed record RegisterPage(int Total, int Skip, int Take, List<RegisterRow> Items);

    /// <summary>Call inside a TenantScope. Newest first within the year, like a physical register read from the top.</summary>
    private static async Task<RegisterPage> RegisterRowsAsync(AppDbContext db, HttpContext ctx, IConfiguration config, Guid tenantId, int? year,
        string? type, string? status, string? q, int skip, int take, CancellationToken ct, string? typeCode = null)
    {
        var query = db.Correspondences.Where(c => c.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(typeCode)) { var tc = typeCode.Trim().ToUpperInvariant(); query = query.Where(c => c.TypeCode == tc); }
        if (!CanSeeConfidential(ctx.User)) query = query.Where(c => !c.IsConfidential);
        if (year is { } y)
        {
            var offset = UtcOffset(config);
            // The year on the institution's clock, compared as instants (the database only takes UTC).
            var from = new DateTimeOffset(y, 1, 1, 0, 0, 0, offset).ToUniversalTime();
            var to = new DateTimeOffset(y + 1, 1, 1, 0, 0, 0, offset).ToUniversalTime();
            query = query.Where(c => c.CreatedAt >= from && c.CreatedAt < to);
        }
        if (Enum.TryParse<CorrespondenceType>(type, true, out var t)) query = query.Where(c => c.Type == t);
        if (Enum.TryParse<CorrespondenceStatus>(status, true, out var st)) query = query.Where(c => c.Status == st);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(c => EF.Functions.ILike(c.Subject, like) || EF.Functions.ILike(c.Number, like));
        }
        var total = await query.CountAsync(ct);
        var page = await query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Number).Skip(skip).Take(take).ToListAsync(ct);
        var ids = page.Select(c => c.Id).ToList();
        var people = await db.Correspondents.Where(x => x.TenantId == tenantId && ids.Contains(x.CorrespondenceId)).ToListAsync(ct);
        var items = page.Select(c => new RegisterRow(c.Id, c.Number, c.Type.ToString(), c.Subject, c.Status.ToString(), c.Priority.ToString(), c.IsConfidential,
            c.CreatedAt, c.AuthorId, c.ParentCorrespondenceId,
            people.Where(x => x.CorrespondenceId == c.Id).OrderBy(x => x.Kind).ThenBy(x => x.DisplayName)
                .Select(x => new RegisterRecipient(x.DisplayName, x.IsExternal, x.Kind.ToString())).ToList(), c.TypeCode)).ToList();
        return new RegisterPage(total, skip, take, items);
    }
}
