using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
        
        corr.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? status, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            var canSeeConfidential = ctx.User.HasPermission("correspondence:confidential");
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Correspondences.Where(c => c.TenantId == tenantId);
            if (!canSeeConfidential) q = q.Where(c => !c.IsConfidential);
            if (Enum.TryParse<CorrespondenceStatus>(status, true, out var s)) q = q.Where(c => c.Status == s);
            return Results.Ok(await q.OrderByDescending(c => c.CreatedAt).ToListAsync(ct));
        });

        corr.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateCorrespondenceReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:create")) return Results.Forbid();
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
            if (req.ParentCorrespondenceId.HasValue && !await db.Correspondences.AnyAsync(x => x.TenantId == req.TenantId && x.Id == req.ParentCorrespondenceId.Value, ct))
                return Results.NotFound(new { error = "Parent correspondence not found." });
            var seq = await Sequences.NextAsync(db, req.TenantId, "correspondence", ct);
            c = c with { Number = $"CORR-{DateTimeOffset.UtcNow:yyyy}-{seq:D6}" };
            db.Correspondences.Add(c);
            foreach (var r in req.Recipients ?? [])
            {
                Guid? pid = Guid.TryParse(r.PersonId, out var g) ? g : null;
                if (pid.HasValue && !await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == pid.Value, ct))
                    return Results.BadRequest(new { error = $"Recipient person {r.PersonId} not found in tenant." });
                db.Correspondents.Add(new Correspondent(Guid.NewGuid(), req.TenantId, c.Id, pid, r.DisplayName, r.IsExternal));
            }
            DomainEvents.Record(db, req.TenantId, "CorrespondenceCreated", "CorrespondenceCreated",
                nameof(Correspondence), c.Id.ToString(),
                payload: new { tenantId = req.TenantId, correspondenceId = c.Id, number = c.Number },
                details: c.Number);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/correspondence/{c.Id}", c);
        });

        corr.MapPost("/{id:guid}/submit", async (AppDbContext db, HttpContext ctx, Guid id, SubmitCorrespondenceReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = await db.Correspondences.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "Correspondence not found." });
            if (c.Status != CorrespondenceStatus.Draft)
                return Results.Conflict(new { error = $"Only Draft correspondence can be submitted (now {c.Status})." });
            if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.ReviewerId, ct))
                return Results.NotFound(new { error = "Reviewer not found in tenant." });
            db.Entry(c).CurrentValues.SetValues(c with { Status = CorrespondenceStatus.Submitted });
            var accelerated = c.Priority is CorrespondencePriority.High or CorrespondencePriority.Urgent;
            var approval = new Approval(Guid.NewGuid(), req.TenantId, nameof(Correspondence), c.Id,
                req.ReviewerId, ApprovalStatus.Pending,
                accelerated ? ApprovalPriority.Accelerated : ApprovalPriority.Normal,
                DateTimeOffset.UtcNow.AddDays(accelerated ? 2 : 5), null, null, null);
            db.Approvals.Add(approval);
            db.WorkTasks.Add(new WorkTask(Guid.NewGuid(), req.TenantId, $"Review {c.Number}: {c.Subject}",
                req.ReviewerId, approval.Id, WorkTaskStatus.Open, approval.DueAt, DateTimeOffset.UtcNow));
            DomainEvents.Record(db, req.TenantId, "CorrespondenceSubmitted", "CorrespondenceSubmitted",
                nameof(Correspondence), c.Id.ToString(),
                payload: new { tenantId = req.TenantId, correspondenceId = c.Id, number = c.Number, reviewerId = req.ReviewerId, approvalId = approval.Id },
                details: $"{c.Number}->{req.ReviewerId}");
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
            if (current is null) return Results.NotFound(new { error = "Correspondence not found." });

            Correspondence? root = current;
            var visited = new System.Collections.Generic.HashSet<Guid> { current.Id };
            while (root.ParentCorrespondenceId.HasValue && !visited.Contains(root.ParentCorrespondenceId.Value))
            {
                var parent = await db.Correspondences.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == root.ParentCorrespondenceId.Value, ct);
                if (parent is null) break;
                visited.Add(parent.Id);
                root = parent;
            }

            var replies = await db.Correspondences
                .Where(c => c.TenantId == tenantId && c.ParentCorrespondenceId == id)
                .OrderBy(c => c.CreatedAt)
                .ToListAsync(ct);

            return Results.Ok(new { current, root, replies });
        });

        corr.MapGet("/{id:guid}/routing-slips", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
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
            if (c is null) return Results.NotFound(new { error = "Correspondence not found." });

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
            var slip = await db.CorrespondenceRoutingSlips.FirstOrDefaultAsync(s => s.TenantId == tenantId && s.CorrespondenceId == id && s.Id == slipId, ct);
            if (slip is null) return Results.NotFound(new { error = "Routing slip not found." });
            db.Entry(slip).CurrentValues.SetValues(slip with { IsCompleted = true, CompletedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(slip with { IsCompleted = true, CompletedAt = DateTimeOffset.UtcNow });
        });
    }
}
