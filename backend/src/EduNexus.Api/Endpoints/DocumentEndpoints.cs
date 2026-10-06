using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EduNexus.Api.Auth;
using EduNexus.Api.Events;
using EduNexus.Api.Storage;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using static EduNexus.Api.Endpoints.EndpointHelpers;

namespace EduNexus.Api.Endpoints;

public static class DocumentEndpoints
{
    public static void MapDocumentEndpoints(this IEndpointRouteBuilder app)
    {
        var workspaces = app.MapGroup("/api/document-workspaces").WithTags("DocumentWorkspaces").RequireAuthorization();
        workspaces.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var list = await db.DocumentWorkspaces.Where(w => w.TenantId == tenantId).OrderBy(w => w.Name).ToListAsync(ct);
            return Results.Ok(list);
        });
        workspaces.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateWorkspaceReq req, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Code and Name required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var ws = new DocumentWorkspace(Guid.NewGuid(), req.TenantId, req.Code.Trim(), req.Name.Trim(), req.Description?.Trim(), true);
            db.DocumentWorkspaces.Add(ws);
            DomainEvents.Record(db, req.TenantId, "WorkspaceCreated", "WorkspaceCreated",
                nameof(DocumentWorkspace), ws.Id.ToString(),
                payload: new { tenantId = req.TenantId, code = ws.Code, name = ws.Name }, details: ws.Code);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex))
            {
                return Results.Conflict(new { error = $"Workspace code '{req.Code}' already exists." });
            }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/document-workspaces/{ws.Id}", ws);
        });

        var docs = app.MapGroup("/api/documents").WithTags("Documents").RequireAuthorization();
        docs.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("document:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.Documents.Where(d => d.TenantId == tenantId).OrderBy(d => d.Title).ToListAsync(ct));
        });
        docs.MapGet("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("document:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var d = await db.Documents.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (d is null) return Results.NotFound(new { error = "Document not found." });
            return Results.Ok(d);
        });
        docs.MapPatch("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, UpdateDocumentReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("document:update") && !ctx.User.HasPermission("document:manage") && !ctx.User.HasPermission("document:write")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var d = await db.Documents.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (d is null) return Results.NotFound(new { error = "Document not found." });
            var updated = d with
            {
                Title = string.IsNullOrWhiteSpace(req.Title) ? d.Title : req.Title.Trim(),
                Classification = req.Classification is not null && Enum.TryParse<DocumentClassification>(req.Classification, true, out var c) ? c : d.Classification,
                RetainUntil = req.RetainUntil ?? d.RetainUntil,
                Status = req.Status is not null && Enum.TryParse<DocumentStatus>(req.Status, true, out var s) ? s : d.Status,
            };
            db.Entry(d).CurrentValues.SetValues(updated);
            DomainEvents.Record(db, tenantId, "DocumentUpdated", "DocumentUpdated",
                nameof(Document), d.Id.ToString(),
                payload: new { tenantId, documentId = id, classification = updated.Classification, retainUntil = updated.RetainUntil }, details: updated.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        });
        docs.MapPost("/{id:guid}/share", async (AppDbContext db, HttpContext ctx, Guid id, ShareDocumentReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("document:share") && !ctx.User.HasPermission("document:manage") && !ctx.User.HasPermission("document:write")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Documents.AnyAsync(d => d.TenantId == req.TenantId && d.Id == id, ct))
                return Results.NotFound(new { error = "Document not found." });
            if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.PersonId, ct))
                return Results.NotFound(new { error = "Person not found in tenant." });
            var share = new DocumentShare(Guid.NewGuid(), req.TenantId, id, req.PersonId,
                DateTimeOffset.UtcNow, req.ExpiresAt);
            db.DocumentShares.Add(share);
            DomainEvents.Record(db, req.TenantId, "DocumentShared", "DocumentShared",
                nameof(DocumentShare), share.Id.ToString(),
                payload: new { tenantId = req.TenantId, documentId = id, personId = req.PersonId, expiresAt = req.ExpiresAt },
                details: $"shared with person {req.PersonId}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/documents/{id}/shares/{share.Id}", share);
        });
        docs.MapGet("/{id:guid}/shares", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var shares = await db.DocumentShares.Where(s => s.TenantId == tenantId && s.DocumentId == id).ToListAsync(ct);
            return Results.Ok(shares);
        });
        docs.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateDocumentReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("document:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "Title required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var d = new Document(Guid.NewGuid(), req.TenantId, req.Title.Trim(), DocumentStatus.Draft, 0);
            db.Documents.Add(d);
            DomainEvents.Record(db, req.TenantId, "DocumentCreated", "DocumentCreated",
                nameof(Document), d.Id.ToString(),
                payload: new { tenantId = req.TenantId, documentId = d.Id, title = d.Title }, details: d.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/documents/{d.Id}", d);
        });
        docs.MapPost("/{id:guid}/upload-url", async (AppDbContext db, HttpContext ctx, ObjectStorage storage, Guid id, UploadUrlReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("document:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Documents.AnyAsync(d => d.TenantId == req.TenantId && d.Id == id, ct))
                return Results.NotFound(new { error = "Document not found." });
            await storage.EnsureBucketAsync(ct);
            var key = $"{req.TenantId}/{id}/{Guid.NewGuid():N}-{req.FileName}";
            return Results.Ok(new { objectKey = key, putUrl = await storage.PresignedPutAsync(key, ct: ct) });
        });
        docs.MapPost("/{id:guid}/versions", async (AppDbContext db, HttpContext ctx, Guid id, AddVersionReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("document:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var d = await db.Documents.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (d is null) return Results.NotFound(new { error = "Document not found." });
            var version = d.CurrentVersion + 1;
            db.DocumentVersions.Add(new DocumentVersion(Guid.NewGuid(), req.TenantId, id, version, req.ObjectKey, req.SizeBytes, req.Sha256 ?? "", DateTimeOffset.UtcNow));
            db.Entry(d).CurrentValues.SetValues(d with { CurrentVersion = version, Status = req.Publish ? DocumentStatus.Published : d.Status });
            DomainEvents.Record(db, req.TenantId, "DocumentVersionAdded", "DocumentVersionAdded",
                nameof(Document), d.Id.ToString(),
                payload: new { tenantId = req.TenantId, documentId = id, version }, details: $"v{version}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/documents/{id}/versions/{version}", new { version });
        });

        // Retention policy
        docs.MapPost("/{id:guid}/retention-policy", async (AppDbContext db, HttpContext ctx, Guid id, SetDocumentRetentionReq req, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enum.TryParse<DispositionAction>(req.DispositionAction, true, out var disp))
                return Results.BadRequest(new { error = "DispositionAction must be Archive|PermanentPreservation|ReviewRequired|Disposal." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Documents.AnyAsync(d => d.TenantId == req.TenantId && d.Id == id, ct))
                return Results.NotFound(new { error = "Document not found." });

            var policy = new DocumentRetentionPolicy(
                Guid.NewGuid(), req.TenantId, id, req.Standard.Trim(),
                req.RetentionPeriodMonths, disp, req.ReviewIntervalMonths,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMonths(req.ReviewIntervalMonths),
                req.ReviewedByPersonId, req.Notes?.Trim());
            db.DocumentRetentionPolicies.Add(policy);
            DomainEvents.Record(db, req.TenantId, "RetentionPolicyApplied", "RetentionPolicyApplied",
                nameof(DocumentRetentionPolicy), policy.Id.ToString(),
                payload: new { tenantId = req.TenantId, documentId = id, standard = policy.Standard, months = policy.RetentionPeriodMonths },
                details: $"{policy.Standard}:{policy.RetentionPeriodMonths}m");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(policy);
        });

        var docTags = app.MapGroup("/api/documents").WithTags("DocumentTags").RequireAuthorization();
        docTags.MapPost("/{id:guid}/tags", async (AppDbContext db, HttpContext ctx, Guid id, AddDocumentTagReq req, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Category) || string.IsNullOrWhiteSpace(req.Value))
                return Results.BadRequest(new { error = "Category and Value are required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Documents.AnyAsync(d => d.TenantId == req.TenantId && d.Id == id, ct))
                return Results.NotFound(new { error = "Document not found." });

            var tag = new DocumentTag(Guid.NewGuid(), req.TenantId, id, req.Category.Trim(), req.Value.Trim());
            db.DocumentTags.Add(tag);
            DomainEvents.Record(db, req.TenantId, "DocumentTagAdded", "DocumentTagAdded",
                nameof(Document), id.ToString(),
                payload: new { tagId = tag.Id, category = tag.TagCategory, value = tag.TagValue },
                details: $"{tag.TagCategory}:{tag.TagValue}");

            // Automated Action Rules execution
            var matchingRules = await db.DocumentActionRules
                .Where(r => r.TenantId == req.TenantId && r.IsActive && r.TriggerCategory == tag.TagCategory && r.TriggerValue == tag.TagValue)
                .ToListAsync(ct);

            foreach (var rule in matchingRules)
            {
                if (rule.ActionType.Equals("AutoRetention", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var docJson = JsonDocument.Parse(rule.TargetValue ?? "{}");
                        var root = docJson.RootElement;
                        var standard = root.TryGetProperty("standard", out var sProp) ? sProp.GetString() ?? "ISO-9001" : "ISO-9001";
                        var months = root.TryGetProperty("retentionMonths", out var mProp) ? mProp.GetInt32() : 60;
                        var reviewInterval = root.TryGetProperty("reviewIntervalMonths", out var rProp) ? rProp.GetInt32() : 12;
                        var dispositionStr = root.TryGetProperty("disposition", out var dProp) ? dProp.GetString() ?? "Archive" : "Archive";
                        Enum.TryParse<DispositionAction>(dispositionStr, true, out var dispAction);

                        var doc = await db.Documents.FirstOrDefaultAsync(d => d.TenantId == req.TenantId && d.Id == id, ct);
                        if (doc != null)
                        {
                            var retainUntil = DateTimeOffset.UtcNow.AddMonths(months);
                            db.Entry(doc).CurrentValues.SetValues(doc with { RetainUntil = retainUntil });
                            var existingPolicy = await db.DocumentRetentionPolicies.FirstOrDefaultAsync(p => p.TenantId == req.TenantId && p.DocumentId == id, ct);
                            if (existingPolicy is null)
                            {
                                db.DocumentRetentionPolicies.Add(new DocumentRetentionPolicy(
                                    Guid.NewGuid(), req.TenantId, id, standard, months, dispAction, reviewInterval,
                                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMonths(reviewInterval), null, $"Automated rule: {rule.ActionType}"));
                            }
                        }
                    }
                    catch (Exception ruleEx)
                    {
                        DomainEvents.Record(db, req.TenantId, "DocumentActionRuleFailed", "DocumentActionRuleFailed",
                            nameof(DocumentActionRule), rule.Id.ToString(),
                            payload: new { ruleId = rule.Id, actionType = rule.ActionType, error = ruleEx.Message },
                            details: $"Rule config error: {ruleEx.Message}");
                    }
                }
                else if (rule.ActionType.Equals("ScheduleReviewActivity", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var docJson = JsonDocument.Parse(rule.TargetValue ?? "{}");
                        var root = docJson.RootElement;
                        var summary = root.TryGetProperty("summary", out var sProp) ? sProp.GetString() ?? "Document Review Required" : "Document Review Required";
                        var dueDays = root.TryGetProperty("dueDays", out var dProp) ? dProp.GetInt32() : 7;
                        Guid? assignee = root.TryGetProperty("assigneeId", out var aProp) && Guid.TryParse(aProp.GetString(), out var aGuid) ? aGuid : null;
                        if (assignee.HasValue)
                        {
                            db.ScheduledActivities.Add(new ScheduledActivity(
                                Guid.NewGuid(), req.TenantId, nameof(Document), id,
                                ActivityType.Review, assignee.Value, summary, DateTimeOffset.UtcNow.AddDays(dueDays), false, null, DateTimeOffset.UtcNow));
                        }
                    }
                    catch (Exception ruleEx)
                    {
                        DomainEvents.Record(db, req.TenantId, "DocumentActionRuleFailed", "DocumentActionRuleFailed",
                            nameof(DocumentActionRule), rule.Id.ToString(),
                            payload: new { ruleId = rule.Id, actionType = rule.ActionType, error = ruleEx.Message },
                            details: $"Rule config error: {ruleEx.Message}");
                    }
                }

                DomainEvents.Record(db, req.TenantId, "DocumentActionRuleTriggered", "DocumentActionRuleTriggered",
                    nameof(DocumentActionRule), rule.Id.ToString(),
                    payload: new { tenantId = req.TenantId, documentId = id, ruleId = rule.Id, actionType = rule.ActionType },
                    details: $"Rule {rule.ActionType} fired on document {id}");
            }

            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/documents/{id}/tags", tag);
        });
        docTags.MapGet("/{id:guid}/tags", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var tags = await db.DocumentTags.Where(t => t.TenantId == tenantId && t.DocumentId == id).ToListAsync(ct);
            return Results.Ok(tags);
        });
        docTags.MapGet("/{id:guid}/retention-policy", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var policy = await db.DocumentRetentionPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.DocumentId == id, ct);
            if (policy is null) return Results.NotFound(new { error = "Retention policy not found for document." });
            return Results.Ok(policy);
        });
        docTags.MapGet("/action-rules", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var rules = await db.DocumentActionRules.Where(r => r.TenantId == tenantId).ToListAsync(ct);
            return Results.Ok(rules);
        });
        docTags.MapPost("/action-rules", async (AppDbContext db, HttpContext ctx, CreateDocActionRuleReq req, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.TriggerCategory) || string.IsNullOrWhiteSpace(req.TriggerValue))
                return Results.BadRequest(new { error = "Trigger category and value are required." });
            if (string.IsNullOrWhiteSpace(req.ActionType)) return Results.BadRequest(new { error = "ActionType is required." });

            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var rule = new DocumentActionRule(
                Guid.NewGuid(), req.TenantId, req.TriggerCategory.Trim(),
                req.TriggerValue.Trim(), req.ActionType.Trim(),
                string.IsNullOrWhiteSpace(req.TargetValue) ? "{}" : req.TargetValue.Trim(),
                true);
            db.DocumentActionRules.Add(rule);
            DomainEvents.Record(db, req.TenantId, "DocumentActionRuleCreated", "DocumentActionRuleCreated",
                nameof(DocumentActionRule), rule.Id.ToString(),
                payload: new { tenantId = req.TenantId, trigger = $"{rule.TriggerCategory}:{rule.TriggerValue}", actionType = rule.ActionType },
                details: rule.ActionType);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/documents/action-rules/{rule.Id}", rule);
        });
        docTags.MapDelete("/action-rules/{ruleId:guid}", async (AppDbContext db, HttpContext ctx, Guid ruleId, Guid tenantId, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var rule = await db.DocumentActionRules.FirstOrDefaultAsync(r => r.TenantId == tenantId && r.Id == ruleId, ct);
            if (rule is null) return Results.NotFound(new { error = "Action rule not found." });
            db.DocumentActionRules.Remove(rule);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.NoContent();
        });
    }
}
