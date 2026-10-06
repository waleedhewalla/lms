using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

/// <summary>
/// R0.1 detail/list endpoints from BBP §7.2 that need no schema change: person and unit subtree,
/// correspondence detail, communications, request/form/workflow detail, form validation,
/// request cancel, approval history, notification read, and per-entity audit trail.
/// </summary>
public static class ReadEndpoints
{
    public static void MapReadEndpoints(this IEndpointRouteBuilder app)
    {
        // --- People & Organization ---
        app.MapGet("/api/people/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("person:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var person = await db.People.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, ct);
            return person is null ? Results.NotFound(new { error = "Person not found." }) : Results.Ok(person);
        }).RequireAuthorization().WithTags("Directory");

        app.MapGet("/api/organizational-units/{id:guid}/subtree", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("org:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var units = await db.OrganizationalUnits.Where(u => u.TenantId == tenantId).ToListAsync(ct);
            var root = units.FirstOrDefault(u => u.Id == id);
            if (root is null) return Results.NotFound(new { error = "Unit not found." });
            var byParent = units.Where(u => u.ParentId.HasValue).ToLookup(u => u.ParentId!.Value);
            var result = new List<OrganizationalUnit> { root };
            var seen = new HashSet<Guid> { root.Id };
            for (var i = 0; i < result.Count; i++)
                foreach (var child in byParent[result[i].Id])
                    if (seen.Add(child.Id)) result.Add(child);
            return Results.Ok(result);
        }).RequireAuthorization().WithTags("OrganizationalUnits");

        // --- Correspondence ---
        app.MapGet("/api/correspondence/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("correspondence:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var c = await db.Correspondences.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (c is null || (c.IsConfidential && !ctx.User.HasPermission("correspondence:confidential")))
                return Results.NotFound(new { error = "Correspondence not found." });
            var correspondents = await db.Correspondents.Where(x => x.TenantId == tenantId && x.CorrespondenceId == id).ToListAsync(ct);
            var approvals = await db.Approvals.Where(a => a.TenantId == tenantId && a.EntityType == nameof(Correspondence) && a.EntityId == id)
                .OrderBy(a => a.DueAt).ToListAsync(ct);
            return Results.Ok(new { correspondence = c, correspondents, approvals });
        }).RequireAuthorization().WithTags("Correspondence");

        // --- Communications ---
        app.MapGet("/api/communications", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? kind, string? status, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("communication:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Communications.Where(c => c.TenantId == tenantId);
            if (Enum.TryParse<CommunicationKind>(kind, true, out var k)) q = q.Where(c => c.Kind == k);
            if (Enum.TryParse<CommunicationStatus>(status, true, out var s)) q = q.Where(c => c.Status == s);
            return Results.Ok(await q.OrderByDescending(c => c.CreatedAt).Take(200).ToListAsync(ct));
        }).RequireAuthorization().WithTags("Communications");

        app.MapGet("/api/communications/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("communication:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var c = await db.Communications.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "Communication not found." });
            var recipientCount = await db.CommunicationRecipients.CountAsync(r => r.TenantId == tenantId && r.CommunicationId == id, ct);
            return Results.Ok(new { communication = c, recipientCount });
        }).RequireAuthorization().WithTags("Communications");

        app.MapGet("/api/communications/{id:guid}/recipients", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("communication:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (!await db.Communications.AnyAsync(x => x.TenantId == tenantId && x.Id == id, ct))
                return Results.NotFound(new { error = "Communication not found." });
            return Results.Ok(await db.CommunicationRecipients.Where(r => r.TenantId == tenantId && r.CommunicationId == id).ToListAsync(ct));
        }).RequireAuthorization().WithTags("Communications");

        // --- Requests ---
        app.MapGet("/api/requests/categories", (HttpContext ctx) =>
            ctx.User.HasPermission("request:read") ? Results.Ok(Enum.GetNames<RequestCategory>()) : Results.Forbid())
            .RequireAuthorization().WithTags("Requests");

        app.MapGet("/api/requests/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("request:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var r = await db.Requests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (r is null) return Results.NotFound(new { error = "Request not found." });
            var submissions = await db.FormSubmissions.Where(s => s.TenantId == tenantId && s.RequestId == id)
                .OrderByDescending(s => s.SubmittedAt).ToListAsync(ct);
            var approvals = await db.Approvals.Where(a => a.TenantId == tenantId && a.EntityType == nameof(Request) && a.EntityId == id)
                .OrderBy(a => a.DueAt).ToListAsync(ct);
            var workflow = await db.WorkflowInstances.FirstOrDefaultAsync(w => w.TenantId == tenantId && w.EntityType == nameof(Request) && w.EntityId == id, ct);
            return Results.Ok(new { request = r, submissions, approvals, workflow });
        }).RequireAuthorization().WithTags("Requests");

        app.MapPost("/api/requests/{id:guid}/cancel", async (AppDbContext db, HttpContext ctx, Guid id, CancelRequestReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("request:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var r = await db.Requests.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (r is null) return Results.NotFound(new { error = "Request not found." });
            var actor = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            if (actor is null || actor != r.SubmitterId) return Results.Forbid();
            if (r.Status is not (RequestStatus.Draft or RequestStatus.ChangesRequested))
                return Results.Conflict(new { error = $"Only Draft or ChangesRequested requests can be cancelled (status is {r.Status})." });
            db.Entry(r).CurrentValues.SetValues(r with { Status = RequestStatus.Closed });
            DomainEvents.Record(db, req.TenantId, "RequestCancelled", "RequestCancelled", nameof(Request), id.ToString(),
                payload: new { tenantId = req.TenantId, requestId = id, cancelledBy = actor }, actorId: actor, details: req.Reason ?? r.Number);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(r with { Status = RequestStatus.Closed });
        }).RequireAuthorization().WithTags("Requests");

        // --- Forms ---
        app.MapGet("/api/forms", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? category, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("form:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Forms.Where(x => x.TenantId == tenantId);
            if (Enum.TryParse<RequestCategory>(category, true, out var cat)) q = q.Where(x => x.Category == cat);
            return Results.Ok(await q.OrderBy(x => x.Code).ToListAsync(ct));
        }).RequireAuthorization().WithTags("Forms");

        app.MapGet("/api/forms/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("form:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var form = await db.Forms.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            return form is null ? Results.NotFound(new { error = "Form not found." }) : Results.Ok(form);
        }).RequireAuthorization().WithTags("Forms");

        app.MapPost("/api/forms/{id:guid}/validate", async (AppDbContext db, HttpContext ctx, Guid id, ValidateFormReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("form:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var form = await db.Forms.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (form is null) return Results.NotFound(new { error = "Form not found." });
            try
            {
                var errors = MissingRequiredFields(form.SchemaJson, req.DataJson);
                return Results.Ok(new { valid = errors.Count == 0, errors });
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "dataJson (or the form schema) is not valid JSON." });
            }
        }).RequireAuthorization().WithTags("Forms");

        // --- Workflows ---
        app.MapGet("/api/workflows/definitions", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("workflow:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.WorkflowDefinitions.Where(w => w.TenantId == tenantId).OrderBy(w => w.Code).ToListAsync(ct));
        }).RequireAuthorization().WithTags("Workflows");

        app.MapGet("/api/workflows/definitions/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("workflow:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var def = await db.WorkflowDefinitions.FirstOrDefaultAsync(w => w.TenantId == tenantId && w.Id == id, ct);
            return def is null ? Results.NotFound(new { error = "Workflow definition not found." }) : Results.Ok(def);
        }).RequireAuthorization().WithTags("Workflows");

        app.MapGet("/api/workflows/instances/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("workflow:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var inst = await db.WorkflowInstances.FirstOrDefaultAsync(w => w.TenantId == tenantId && w.Id == id, ct);
            if (inst is null) return Results.NotFound(new { error = "Workflow instance not found." });
            var approvals = await db.Approvals.Where(a => a.TenantId == tenantId && a.EntityType == inst.EntityType && a.EntityId == inst.EntityId)
                .OrderBy(a => a.DueAt).ToListAsync(ct);
            return Results.Ok(new { instance = inst, approvals });
        }).RequireAuthorization().WithTags("Workflows");

        // --- Approvals ---
        app.MapGet("/api/approvals/{id:guid}/history", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("approval:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (!await db.Approvals.AnyAsync(a => a.TenantId == tenantId && a.Id == id, ct))
                return Results.NotFound(new { error = "Approval not found." });
            var key = id.ToString();
            return Results.Ok(await db.AuditEvents.Where(e => e.TenantId == tenantId && e.EntityType == nameof(Approval) && e.EntityId == key)
                .OrderBy(e => e.At).ToListAsync(ct));
        }).RequireAuthorization().WithTags("Approvals");

        // --- Notifications ---
        app.MapPost("/api/notifications/{id:guid}/read", async (AppDbContext db, HttpContext ctx, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("notification:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var n = await db.Notifications.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (n is null) return Results.NotFound(new { error = "Notification not found." });
            var actor = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            if (actor is null || actor != n.PersonId) return Results.Forbid();
            if (n.ReadAt is not null) return Results.Ok(n);
            var read = n with { ReadAt = DateTimeOffset.UtcNow };
            db.Entry(n).CurrentValues.SetValues(read);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(read);
        }).RequireAuthorization().WithTags("Notifications");

        // --- Audit ---
        app.MapGet("/api/audit/entities/{type}/{id}", async (AppDbContext db, HttpContext ctx, string type, string id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("audit:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.AuditEvents.Where(e => e.TenantId == tenantId && e.EntityType == type && e.EntityId == id)
                .OrderBy(e => e.At).Take(500).ToListAsync(ct));
        }).RequireAuthorization().WithTags("Audit");
    }

    /// <summary>Keys of fields the form schema marks required that are missing or blank in the data.</summary>
    internal static List<string> MissingRequiredFields(string schemaJson, string? dataJson)
    {
        var missing = new List<string>();
        using var schema = JsonDocument.Parse(string.IsNullOrWhiteSpace(schemaJson) ? "[]" : schemaJson);
        if (schema.RootElement.ValueKind != JsonValueKind.Array) return missing;
        using var data = JsonDocument.Parse(string.IsNullOrWhiteSpace(dataJson) ? "{}" : dataJson);
        foreach (var field in schema.RootElement.EnumerateArray())
        {
            var required = field.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
            if (!required || !field.TryGetProperty("key", out var k) || k.GetString() is not { } key) continue;
            var present = data.RootElement.ValueKind == JsonValueKind.Object && data.RootElement.TryGetProperty(key, out var v)
                && v.ValueKind != JsonValueKind.Null
                && !(v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString()));
            if (!present) missing.Add(key);
        }
        return missing;
    }
}

public sealed record CancelRequestReq(Guid TenantId, string? Reason);
public sealed record ValidateFormReq(Guid TenantId, string? DataJson);
public sealed record TenantOnlyReq(Guid TenantId);
