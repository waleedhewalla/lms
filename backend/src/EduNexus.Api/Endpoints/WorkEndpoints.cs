using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EduNexus.Api.Auth;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using static EduNexus.Api.Endpoints.EndpointHelpers;

namespace EduNexus.Api.Endpoints;

/// <summary>
/// R0.1 wave A (BBP §7.2): identity self-service, permission catalog, directory edits,
/// manual tasks with verification, "submitted by me", inbox counters, SLA compliance and
/// communication archive. No schema change.
/// </summary>
public static class WorkEndpoints
{
    public static void MapWorkEndpoints(this IEndpointRouteBuilder app)
    {
        // --- Identity ---
        app.MapGet("/api/auth/me", async (AppDbContext db, HttpContext ctx, CancellationToken ct) =>
        {
            var tenantId = ctx.User.TenantId();
            Person? person = null;
            if (tenantId is { } tid)
            {
                await using var scope = await TenantScope.BeginAsync(db, tid, ct);
                if (await ActingPersonAsync(db, ctx, tid, ct) is { } pid)
                    person = await db.People.FirstOrDefaultAsync(p => p.TenantId == tid && p.Id == pid, ct);
            }
            return Results.Ok(new
            {
                subject = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.FindFirstValue("sub"),
                tenantId,
                permissions = ctx.User.FindAll(DevTokenService.PermissionClaim).Select(c => c.Value).Distinct().OrderBy(c => c),
                person,
            });
        }).RequireAuthorization().WithTags("Auth");

        app.MapGet("/api/permissions", (HttpContext ctx) =>
            ctx.User.HasPermission("role:read") ? Results.Ok(PermissionCatalog.All) : Results.Forbid())
            .RequireAuthorization().WithTags("Roles");

        // --- Directory ---
        app.MapPatch("/api/people/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, UpdatePersonReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("person:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (req.FullName is not null && string.IsNullOrWhiteSpace(req.FullName))
                return Results.BadRequest(new { error = "fullName cannot be blank." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var p = await db.People.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Person not found." });
            if (req.DepartmentId is { } dept && !await db.OrganizationalUnits.AnyAsync(u => u.TenantId == req.TenantId && u.Id == dept, ct))
                return Results.BadRequest(new { error = "Department not found in this tenant." });
            var updated = p with
            {
                FullName = req.FullName?.Trim() ?? p.FullName,
                Email = req.Email is null ? p.Email : (string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim()),
                DepartmentId = req.DepartmentId ?? p.DepartmentId,
                AcademicRank = req.AcademicRank ?? p.AcademicRank,
                IsActive = req.IsActive ?? p.IsActive,
            };
            db.Entry(p).CurrentValues.SetValues(updated);
            DomainEvents.Record(db, req.TenantId, "PersonUpdated", "PersonUpdated", nameof(Person), id.ToString(),
                actorId: await ActingPersonAsync(db, ctx, req.TenantId, ct), details: updated.FullName);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        }).RequireAuthorization().WithTags("Directory");

        app.MapGet("/api/people/{id:guid}/direct-reports", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("person:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            // A person's reports are the members of the units they lead (excluding themselves).
            var units = await db.OrganizationalUnits.Where(u => u.TenantId == tenantId && u.LeaderPersonId == id).Select(u => u.Id).ToListAsync(ct);
            var reports = await db.People.Where(p => p.TenantId == tenantId && p.Id != id && p.DepartmentId != null && units.Contains(p.DepartmentId.Value))
                .OrderBy(p => p.FullName).ToListAsync(ct);
            return Results.Ok(reports);
        }).RequireAuthorization().WithTags("Directory");

        // --- Tasks ---
        app.MapPost("/api/tasks", async (AppDbContext db, HttpContext ctx, CreateTaskReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("task:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "Title required." });
            var priority = WorkTaskPriority.Normal;
            if (req.Priority is not null && !Enum.TryParse(req.Priority, true, out priority))
                return Results.BadRequest(new { error = "Priority must be Low|Normal|High|Urgent." });
            if (req.DueAt <= DateTimeOffset.UtcNow) return Results.BadRequest(new { error = "dueAt must be in the future." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.AssigneeId, ct))
                return Results.BadRequest(new { error = "Assignee not found in this tenant." });
            var task = new WorkTask(Guid.NewGuid(), req.TenantId, req.Title.Trim(), req.AssigneeId, null, WorkTaskStatus.Open,
                req.DueAt, DateTimeOffset.UtcNow, req.Description?.Trim() ?? "", priority);
            db.WorkTasks.Add(task);
            DomainEvents.Record(db, req.TenantId, "TaskCreated", "TaskCreated", nameof(WorkTask), task.Id.ToString(),
                payload: new { tenantId = req.TenantId, taskId = task.Id, assigneeId = req.AssigneeId },
                actorId: await ActingPersonAsync(db, ctx, req.TenantId, ct), details: task.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/tasks/{task.Id}", task);
        }).RequireAuthorization().WithTags("Tasks");

        app.MapPost("/api/tasks/{id:guid}/verify", async (AppDbContext db, HttpContext ctx, Guid id, VerifyTaskReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("task:verify")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var t = await db.WorkTasks.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (t is null) return Results.NotFound(new { error = "Task not found." });
            var actor = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            // Segregation of duties: someone other than the assignee verifies the work.
            if (actor is null || actor == t.AssigneeId) return Results.Forbid();
            if (t.Status != WorkTaskStatus.Done) return Results.Conflict(new { error = $"Only Done tasks can be verified (status is {t.Status})." });
            var verified = t with { Status = WorkTaskStatus.Verified };
            db.Entry(t).CurrentValues.SetValues(verified);
            DomainEvents.Record(db, req.TenantId, "TaskVerified", "TaskVerified", nameof(WorkTask), id.ToString(),
                payload: new { tenantId = req.TenantId, taskId = id, verifiedBy = actor }, actorId: actor, details: req.Comment ?? t.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(verified);
        }).RequireAuthorization().WithTags("Tasks");

        // --- Approvals I submitted ---
        app.MapGet("/api/approvals/submitted", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("approval:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (await ActingPersonAsync(db, ctx, tenantId, ct) is not { } me) return Results.Forbid();
            var myRequests = db.Requests.Where(r => r.TenantId == tenantId && r.SubmitterId == me).Select(r => r.Id);
            var myCorrespondence = db.Correspondences.Where(c => c.TenantId == tenantId && c.AuthorId == me).Select(c => c.Id);
            var approvals = await db.Approvals.Where(a => a.TenantId == tenantId &&
                    ((a.EntityType == nameof(Request) && myRequests.Contains(a.EntityId)) ||
                     (a.EntityType == nameof(Correspondence) && myCorrespondence.Contains(a.EntityId))))
                .OrderByDescending(a => a.DueAt).ToListAsync(ct);
            return Results.Ok(approvals);
        }).RequireAuthorization().WithTags("Approvals");

        // --- Inbox counters for the caller ---
        app.MapGet("/api/inbox/counts", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("inbox:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (await ActingPersonAsync(db, ctx, tenantId, ct) is not { } me) return Results.Forbid();
            var now = DateTimeOffset.UtcNow;
            var openStatuses = new[] { WorkTaskStatus.Open, WorkTaskStatus.InProgress, WorkTaskStatus.Breached };
            return Results.Ok(new
            {
                approvals = await db.Approvals.CountAsync(a => a.TenantId == tenantId && a.AssigneeId == me && a.Status == ApprovalStatus.Pending, ct),
                tasks = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && t.AssigneeId == me && openStatuses.Contains(t.Status), ct),
                overdue = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && t.AssigneeId == me && openStatuses.Contains(t.Status) && t.DueAt < now, ct),
                unreadNotifications = await db.Notifications.CountAsync(n => n.TenantId == tenantId && n.PersonId == me && n.ReadAt == null, ct),
                activities = await db.ScheduledActivities.CountAsync(a => a.TenantId == tenantId && a.AssigneeId == me && !a.IsCompleted, ct),
            });
        }).RequireAuthorization().WithTags("Inbox");

        // --- SLA compliance ---
        app.MapGet("/api/sla/compliance", async (AppDbContext db, HttpContext ctx, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("approval:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var start = from ?? DateTimeOffset.UtcNow.AddDays(-30);
            var end = to ?? DateTimeOffset.UtcNow;
            var now = DateTimeOffset.UtcNow;
            var decided = await db.Approvals.Where(a => a.TenantId == tenantId && a.DecidedAt != null && a.DecidedAt >= start && a.DecidedAt <= end)
                .Select(a => new { a.DecidedAt, a.DueAt }).ToListAsync(ct);
            var onTime = decided.Count(a => a.DecidedAt <= a.DueAt);
            var pendingOverdue = await db.Approvals.CountAsync(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending && a.DueAt < now, ct);
            var breachedTasks = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && t.Status == WorkTaskStatus.Breached, ct);
            return Results.Ok(new
            {
                from = start, to = end,
                decided = decided.Count, onTime, late = decided.Count - onTime,
                compliancePct = decided.Count == 0 ? (double?)null : Math.Round(100.0 * onTime / decided.Count, 1),
                pendingOverdue, breachedTasks,
            });
        }).RequireAuthorization().WithTags("SLA");

        // --- Communications archive ---
        app.MapPost("/api/communications/{id:guid}/archive", async (AppDbContext db, HttpContext ctx, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("communication:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = await db.Communications.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "Communication not found." });
            if (c.Status == CommunicationStatus.Archived) return Results.Conflict(new { error = "Communication is already archived." });
            var archived = c with { Status = CommunicationStatus.Archived };
            db.Entry(c).CurrentValues.SetValues(archived);
            DomainEvents.Record(db, req.TenantId, "CommunicationArchived", "CommunicationArchived", nameof(Communication), id.ToString(),
                actorId: await ActingPersonAsync(db, ctx, req.TenantId, ct), details: c.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(archived);
        }).RequireAuthorization().WithTags("Communications");
    }
}

public sealed record UpdatePersonReq(Guid TenantId, string? FullName, string? Email, Guid? DepartmentId, string? AcademicRank, bool? IsActive);
public sealed record CreateTaskReq(Guid TenantId, string Title, Guid AssigneeId, DateTimeOffset DueAt, string? Description, string? Priority);
public sealed record VerifyTaskReq(Guid TenantId, string? Comment);
