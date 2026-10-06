using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EduNexus.Api.AI;
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

public static class IntelligenceEndpoints
{
    public static void MapIntelligenceEndpoints(this IEndpointRouteBuilder app)
    {
        // --- Quality & Accreditation ---
        var quality = app.MapGroup("/api/quality").WithTags("Quality").RequireAuthorization();
        quality.MapGet("/standards", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.Standards.Where(s => s.TenantId == tenantId).OrderBy(s => s.Code).ToListAsync(ct));
        });
        quality.MapPost("/standards", async (AppDbContext db, HttpContext ctx, CreateStandardReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Code and Title required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var st = new Standard(Guid.NewGuid(), req.TenantId, req.Code.Trim(), req.Title.Trim());
            db.Standards.Add(st);
            DomainEvents.Record(db, req.TenantId, "StandardCreated", "StandardCreated",
                nameof(Standard), st.Id.ToString(), details: st.Code);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex))
            { return Results.Conflict(new { error = $"Standard code '{req.Code}' already exists." }); }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/quality/standards/{st.Id}", st);
        });
        quality.MapPost("/criteria", async (AppDbContext db, HttpContext ctx, CreateCriterionReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Standards.AnyAsync(s => s.TenantId == req.TenantId && s.Id == req.StandardId, ct))
                return Results.NotFound(new { error = "Standard not found." });
            var cr = new Criterion(Guid.NewGuid(), req.TenantId, req.StandardId, req.Code.Trim(), req.Text.Trim());
            db.Criteria.Add(cr);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/quality/criteria/{cr.Id}", cr);
        });
        quality.MapPost("/evidence", async (AppDbContext db, HttpContext ctx, AddEvidenceReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Criteria.AnyAsync(c => c.TenantId == req.TenantId && c.Id == req.CriterionId, ct))
                return Results.NotFound(new { error = "Criterion not found." });
            var ev = new Evidence(Guid.NewGuid(), req.TenantId, req.CriterionId, req.EntityType, req.EntityId, req.Note ?? "");
            db.Evidences.Add(ev);
            DomainEvents.Record(db, req.TenantId, "EvidenceAdded", "EvidenceAdded",
                req.EntityType, req.EntityId.ToString(), details: $"criterion:{req.CriterionId}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/quality/evidence/{ev.Id}", ev);
        });
        quality.MapPost("/findings", async (AppDbContext db, HttpContext ctx, CreateFindingReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enum.TryParse<FindingSeverity>(req.Severity, true, out var sev))
                return Results.BadRequest(new { error = "Severity must be Observation|Minor|Major." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Criteria.AnyAsync(c => c.TenantId == req.TenantId && c.Id == req.CriterionId, ct))
                return Results.NotFound(new { error = "Criterion not found." });
            var fd = new Finding(Guid.NewGuid(), req.TenantId, req.CriterionId, sev, req.Text.Trim(), false);
            db.Findings.Add(fd);
            DomainEvents.Record(db, req.TenantId, "FindingCreated", "FindingCreated",
                nameof(Finding), fd.Id.ToString(), details: sev.ToString());
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/quality/findings/{fd.Id}", fd);
        });
        quality.MapPost("/corrective-actions", async (AppDbContext db, HttpContext ctx, CreateCorrectiveActionReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Findings.AnyAsync(f => f.TenantId == req.TenantId && f.Id == req.FindingId, ct))
                return Results.NotFound(new { error = "Finding not found." });
            var ca = new CorrectiveAction(Guid.NewGuid(), req.TenantId, req.FindingId, req.AssigneeId,
                req.Description.Trim(), CorrectiveActionStatus.Open, req.DueAt ?? DateTimeOffset.UtcNow.AddDays(30));
            db.CorrectiveActions.Add(ca);
            DomainEvents.Record(db, req.TenantId, "CorrectiveActionCreated", "CorrectiveActionCreated",
                nameof(CorrectiveAction), ca.Id.ToString(), details: ca.Description);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/quality/corrective-actions/{ca.Id}", ca);
        });

        // --- Strategy & KPIs ---
        var strategy = app.MapGroup("/api/strategy").WithTags("Strategy").RequireAuthorization();
        strategy.MapGet("/plans", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("strategy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.StrategicPlans.Where(p => p.TenantId == tenantId).OrderByDescending(p => p.YearFrom).ToListAsync(ct));
        });
        strategy.MapPost("/plans", async (AppDbContext db, HttpContext ctx, CreatePlanReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("strategy:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var plan = new StrategicPlan(Guid.NewGuid(), req.TenantId, req.Title.Trim(), req.YearFrom, req.YearTo);
            db.StrategicPlans.Add(plan);
            DomainEvents.Record(db, req.TenantId, "StrategicPlanCreated", "StrategicPlanCreated",
                nameof(StrategicPlan), plan.Id.ToString(), details: plan.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/strategy/plans/{plan.Id}", plan);
        });
        strategy.MapPost("/objectives", async (AppDbContext db, HttpContext ctx, CreateObjectiveReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("strategy:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.StrategicPlans.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.PlanId, ct))
                return Results.NotFound(new { error = "Plan not found." });
            var obj = new Objective(Guid.NewGuid(), req.TenantId, req.PlanId, req.Code.Trim(), req.Text.Trim());
            db.Objectives.Add(obj);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/strategy/objectives/{obj.Id}", obj);
        });
        strategy.MapPost("/kpis", async (AppDbContext db, HttpContext ctx, CreateKpiReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("strategy:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Objectives.AnyAsync(o => o.TenantId == req.TenantId && o.Id == req.ObjectiveId, ct))
                return Results.NotFound(new { error = "Objective not found." });
            var kpi = new Kpi(Guid.NewGuid(), req.TenantId, req.ObjectiveId, req.Name.Trim(), req.Target, req.Current, req.Unit ?? "%");
            db.Kpis.Add(kpi);
            DomainEvents.Record(db, req.TenantId, "KpiCreated", "KpiCreated",
                nameof(Kpi), kpi.Id.ToString(), details: kpi.Name);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/strategy/kpis/{kpi.Id}", kpi);
        });
        strategy.MapPost("/kpis/{id:guid}/reading", async (AppDbContext db, HttpContext ctx, Guid id, KpiReadingReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("strategy:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var kpi = await db.Kpis.FirstOrDefaultAsync(k => k.TenantId == req.TenantId && k.Id == id, ct);
            if (kpi is null) return Results.NotFound(new { error = "KPI not found." });
            db.Entry(kpi).CurrentValues.SetValues(kpi with { Current = req.Current });
            DomainEvents.Record(db, req.TenantId, "KpiUpdated", "KpiUpdated",
                nameof(Kpi), id.ToString(), details: $"current={req.Current}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(kpi with { Current = req.Current });
        });

        // --- Search & Analytics ---
        app.MapGet("/api/search", async (AppDbContext db, HttpContext ctx, Guid tenantId, string q, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("search:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
                return Results.BadRequest(new { error = "q must be at least 2 chars." });
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var term = q.Trim();
            var people = await db.People.Where(p => p.TenantId == tenantId && (p.FullName.Contains(term) || (p.Email != null && p.Email.Contains(term)))).Take(20).ToListAsync(ct);
            var docs = await db.Documents.Where(d => d.TenantId == tenantId && d.Title.Contains(term)).Take(20).ToListAsync(ct);
            var corrs = await db.Correspondences.Where(c => c.TenantId == tenantId && (c.Subject.Contains(term) || c.Number.Contains(term))).Take(20).ToListAsync(ct);
            return Results.Ok(new { people, documents = docs, correspondence = corrs });
        }).RequireAuthorization().WithTags("Search");

        app.MapGet("/api/analytics/overview", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("analytics:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var totalPeople = await db.People.CountAsync(p => p.TenantId == tenantId, ct);
            var activePeople = totalPeople;
            var totalDocs = await db.Documents.CountAsync(d => d.TenantId == tenantId, ct);
            var totalCorrespondences = await db.Correspondences.CountAsync(c => c.TenantId == tenantId, ct);
            var pendingApprovals = await db.Approvals.CountAsync(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending, ct);
            var openTasks = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && t.Status != WorkTaskStatus.Done, ct);
            return Results.Ok(new { totalPeople, activePeople, totalDocs, totalCorrespondences, pendingApprovals, openTasks });
        }).RequireAuthorization().WithTags("Analytics");

        // --- AI ---
        var ai = app.MapGroup("/api/ai").WithTags("AI").RequireAuthorization();
        ai.MapPost("/index", async (AppDbContext db, HttpContext ctx, TenantSearchIndex indexer, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("ai:admin") && !ctx.User.HasPermission("ai:manage") && !ctx.User.HasPermission("ai:write") && !ctx.User.HasPermission("ai:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var docList = await db.Documents.Where(d => d.TenantId == tenantId && d.Status == DocumentStatus.Published).ToListAsync(ct);
            var indexed = 0;
            foreach (var d in docList)
            {
                await indexer.IndexAsync(tenantId, "document", d.Id, d.Title, d.Title, ct);
                indexed++;
            }
            return Results.Ok(new { tenantId, indexedDocuments = indexed });
        });
        ai.MapPost("/ask", async (AppDbContext db, HttpContext ctx, TenantSearchIndex indexer, AskReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("ai:ask")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Question))
                return Results.BadRequest(new { error = "Question required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var hits = await indexer.SearchAsync(req.TenantId, req.Question, 5, ct);
            var excerpt = string.Join(" | ", hits.Select(h => h.Title));
            var answer = hits.Count == 0
                ? $"No indexed documents matched '{req.Question}'. (R5 retrieval baseline)"
                : $"Found {hits.Count} reference documents: {excerpt}. (R5 retrieval baseline)";
            var interaction = new AiInteraction(
                Guid.NewGuid(), req.TenantId, req.Capability ?? "ask",
                req.Question[..Math.Min(req.Question.Length, 500)],
                answer[..Math.Min(answer.Length, 1000)],
                "echo-extractive-v1", 0.85, ctx.User.TenantId(), DateTimeOffset.UtcNow);
            db.AiInteractions.Add(interaction);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(new { answer, hits, model = "echo-extractive-v1", interactionId = interaction.Id });
        });
        ai.MapGet("/interactions", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("ai:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.AiInteractions.Where(i => i.TenantId == tenantId).OrderByDescending(i => i.At).ToListAsync(ct));
        });

        // --- Integrations ---
        var integrations = app.MapGroup("/api/integrations").WithTags("Integrations").RequireAuthorization();
        integrations.MapPost("/endpoints", async (AppDbContext db, HttpContext ctx, RegisterEndpointReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("integration:admin") && !ctx.User.HasPermission("integration:manage") && !ctx.User.HasPermission("integration:write")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.EventType) || string.IsNullOrWhiteSpace(req.TargetUrl))
                return Results.BadRequest(new { error = "EventType and TargetUrl required." });
            if (!Uri.TryCreate(req.TargetUrl, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
                return Results.BadRequest(new { error = "TargetUrl must be a valid HTTP/HTTPS URL." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var ep = new IntegrationEndpoint(Guid.NewGuid(), req.TenantId, req.EventType.Trim(), req.TargetUrl.Trim(), req.Secret ?? "sec-default", true);
            db.IntegrationEndpoints.Add(ep);
            DomainEvents.Record(db, req.TenantId, "IntegrationEndpointRegistered", "IntegrationEndpointRegistered",
                nameof(IntegrationEndpoint), ep.Id.ToString(), details: ep.TargetUrl);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/integrations/endpoints/{ep.Id}", ep);
        });
        integrations.MapGet("/deliveries", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("integration:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.IntegrationDeliveries.Where(d => d.TenantId == tenantId).OrderByDescending(d => d.At).ToListAsync(ct));
        });

        // --- Dynamic Forms & Requests ---
        var forms = app.MapGroup("/api/forms").WithTags("Forms").RequireAuthorization();
        forms.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateFormReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("form:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Code and Name required." });
            if (!Enum.TryParse<RequestCategory>(req.Category, true, out var cat))
                return Results.BadRequest(new { error = "Category must be Academic|Administrative|HR|Finance|Procurement|IT|Facilities|StudentAffairs|Research|Quality|Other." });
            if (!string.IsNullOrWhiteSpace(req.SchemaJson))
            {
                try { using var doc = System.Text.Json.JsonDocument.Parse(req.SchemaJson); }
                catch { return Results.BadRequest(new { error = "Invalid JSON in schemaJson." }); }
            }
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var form = new Form(Guid.NewGuid(), req.TenantId, req.Code.Trim(), req.Name.Trim(), cat, req.SchemaJson ?? "[]", 1, true);
            db.Forms.Add(form);
            DomainEvents.Record(db, req.TenantId, "FormCreated", "FormCreated",
                nameof(Form), form.Id.ToString(), details: form.Code);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex))
            { return Results.Conflict(new { error = $"Form code '{req.Code}' already exists." }); }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/forms/{form.Id}", form);
        });

        var requests = app.MapGroup("/api/requests").WithTags("Requests").RequireAuthorization();
        requests.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateRequestReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("request:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "Title required." });
            if (!Enum.TryParse<RequestCategory>(req.Category, true, out var cat))
                return Results.BadRequest(new { error = "Invalid Category." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (req.FormId.HasValue && !string.IsNullOrWhiteSpace(req.DataJson))
            {
                var form = await db.Forms.FirstOrDefaultAsync(f => f.TenantId == req.TenantId && f.Id == req.FormId.Value, ct);
                if (form != null && !string.IsNullOrWhiteSpace(form.SchemaJson))
                {
                    try
                    {
                        using var schemaDoc = System.Text.Json.JsonDocument.Parse(form.SchemaJson);
                        using var dataDoc = System.Text.Json.JsonDocument.Parse(req.DataJson);
                        if (schemaDoc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var field in schemaDoc.RootElement.EnumerateArray())
                            {
                                var isReq = field.TryGetProperty("required", out var rProp) && rProp.GetBoolean();
                                if (isReq && field.TryGetProperty("key", out var kProp))
                                {
                                    var key = kProp.GetString();
                                    if (key != null && (!dataDoc.RootElement.TryGetProperty(key, out var val) || val.ValueKind == System.Text.Json.JsonValueKind.Null || (val.ValueKind == System.Text.Json.JsonValueKind.String && string.IsNullOrWhiteSpace(val.GetString()))))
                                    {
                                        return Results.BadRequest(new { error = $"Required form field '{key}' is missing." });
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not BadHttpRequestException)
                    {
                        return Results.BadRequest(new { error = "Form validation failed." });
                    }
                }
            }
            var seq = await Sequences.NextAsync(db, req.TenantId, "request", ct);
            var number = $"REQ-{DateTimeOffset.UtcNow:yyyy}-{seq:D6}";
            var request = new Request(Guid.NewGuid(), req.TenantId, number, cat, req.Title.Trim(), req.FormId, req.SubmitterId, RequestStatus.Draft, DateTimeOffset.UtcNow, null);
            db.Requests.Add(request);
            if (req.FormId.HasValue && !string.IsNullOrWhiteSpace(req.DataJson))
            {
                db.FormSubmissions.Add(new FormSubmission(Guid.NewGuid(), req.TenantId, request.Id, req.FormId.Value, 1, req.DataJson, req.SubmitterId, DateTimeOffset.UtcNow));
            }
            DomainEvents.Record(db, req.TenantId, "RequestCreated", "RequestCreated",
                nameof(Request), request.Id.ToString(), details: number);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/requests/{request.Id}", request);
        });

        requests.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? status, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("request:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Requests.Where(r => r.TenantId == tenantId);
            if (Enum.TryParse<RequestStatus>(status, true, out var s)) q = q.Where(r => r.Status == s);
            return Results.Ok(await q.OrderByDescending(r => r.CreatedAt).ToListAsync(ct));
        });

        requests.MapPost("/{id:guid}/submit", async (AppDbContext db, HttpContext ctx, Guid id, SubmitRequestReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("request:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var r = await db.Requests.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (r is null) return Results.NotFound(new { error = "Request not found." });
            if (r.Status == RequestStatus.InReview || r.Status == RequestStatus.Submitted) return Results.Conflict(new { error = "Request already submitted." });

            db.Entry(r).CurrentValues.SetValues(r with { Status = RequestStatus.Submitted, SubmittedAt = DateTimeOffset.UtcNow });

            if (!string.IsNullOrWhiteSpace(req.WorkflowCode))
            {
                var startResult = await WorkflowRunner.StartAsync(db, req.TenantId, req.WorkflowCode.Trim(), nameof(Request), id, r.SubmitterId, ct);
                await db.SaveChangesAsync(ct);
                await scope.CommitAsync(ct);
                return Results.Ok(new { instanceId = startResult.InstanceId, approvalId = startResult.PendingApprovalId });
            }
            else if (req.ReviewerId.HasValue)
            {
                var approval = new Approval(Guid.NewGuid(), req.TenantId, nameof(Request), id, req.ReviewerId.Value, ApprovalStatus.Pending, ApprovalPriority.Normal, DateTimeOffset.UtcNow.AddDays(3), null, null, null);
                db.Approvals.Add(approval);
                db.WorkTasks.Add(new WorkTask(Guid.NewGuid(), req.TenantId, $"Review Request {r.Number}", req.ReviewerId.Value, approval.Id, WorkTaskStatus.Open, approval.DueAt, DateTimeOffset.UtcNow));
                DomainEvents.Record(db, req.TenantId, "RequestSubmitted", "RequestSubmitted", nameof(Request), id.ToString(), payload: new { tenantId = req.TenantId, requestId = id, reviewerId = req.ReviewerId.Value }, details: r.Number);
                await db.SaveChangesAsync(ct);
                await scope.CommitAsync(ct);
                return Results.Ok(new { instanceId = (Guid?)null, approvalId = approval.Id });
            }
            else
            {
                return Results.BadRequest(new { error = "Either workflowCode or reviewerId is required." });
            }
        });

        // --- Workflows ---
        var workflows = app.MapGroup("/api/workflows").WithTags("Workflows").RequireAuthorization();
        workflows.MapGet("/instances", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? status, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("workflow:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.WorkflowInstances.Where(i => i.TenantId == tenantId);
            if (Enum.TryParse<WorkflowInstanceStatus>(status, true, out var st)) q = q.Where(i => i.Status == st);
            return Results.Ok(await q.ToListAsync(ct));
        });
        workflows.MapPost("/definitions", async (AppDbContext db, HttpContext ctx, CreateWorkflowReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("workflow:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Code and Name required." });

            if (!string.IsNullOrWhiteSpace(req.NodesJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(req.NodesJson);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        var validTypes = new[] { "start", "approval", "task", "notification", "end" };
                        foreach (var node in doc.RootElement.EnumerateArray())
                        {
                            if (node.TryGetProperty("type", out var tProp) && tProp.ValueKind == JsonValueKind.String)
                            {
                                var tStr = tProp.GetString();
                                if (!validTypes.Contains(tStr))
                                    return Results.BadRequest(new { error = $"Invalid node type '{tStr}'." });
                            }
                        }
                    }
                }
                catch
                {
                    return Results.BadRequest(new { error = "Invalid JSON in nodesJson." });
                }
            }

            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var wf = new WorkflowDefinition(Guid.NewGuid(), req.TenantId, req.Code.Trim(), req.Name.Trim(), 1, req.NodesJson ?? "[]", true);
            db.WorkflowDefinitions.Add(wf);
            DomainEvents.Record(db, req.TenantId, "WorkflowDefinitionCreated", "WorkflowDefinitionCreated",
                nameof(WorkflowDefinition), wf.Id.ToString(), details: wf.Code);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/workflows/definitions/{wf.Id}", wf);
        });

        // --- Communications & Notifications ---
        var comms = app.MapGroup("/api/communications").WithTags("Communications").RequireAuthorization();
        comms.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateCommunicationReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("communication:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enum.TryParse<CommunicationKind>(req.Kind, true, out var kind))
                return Results.BadRequest(new { error = "Kind must be Announcement|Circular|Directive." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = new Communication(Guid.NewGuid(), req.TenantId, kind, req.Title.Trim(), req.Body ?? "", req.AuthorId, req.RequiresAction, req.DueAt, CommunicationStatus.Draft, DateTimeOffset.UtcNow, null);
            db.Communications.Add(c);
            foreach (var pid in req.TargetPersonIds ?? [])
            {
                db.CommunicationRecipients.Add(new CommunicationRecipient(Guid.NewGuid(), req.TenantId, c.Id, pid));
            }
            DomainEvents.Record(db, req.TenantId, "CommunicationCreated", "CommunicationCreated",
                nameof(Communication), c.Id.ToString(), details: c.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/communications/{c.Id}", c);
        });
        comms.MapPost("/{id:guid}/publish", async (AppDbContext db, HttpContext ctx, Guid id, PublishCommunicationReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("communication:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = await db.Communications.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "Communication not found." });
            if (c.Status == CommunicationStatus.Published) return Results.Conflict(new { error = "Communication already published." });

            db.Entry(c).CurrentValues.SetValues(c with { Status = CommunicationStatus.Published });
            var recipients = await db.CommunicationRecipients.Where(r => r.TenantId == req.TenantId && r.CommunicationId == id).ToListAsync(ct);

            var tasksCreated = 0;
            if (c.RequiresAction)
            {
                foreach (var r in recipients)
                {
                    db.WorkTasks.Add(new WorkTask(Guid.NewGuid(), req.TenantId, $"Directive Action: {c.Title}", r.PersonId, null, WorkTaskStatus.Open, c.DueAt ?? DateTimeOffset.UtcNow.AddDays(7), DateTimeOffset.UtcNow));
                    tasksCreated++;
                }
            }

            DomainEvents.Record(db, req.TenantId, "CommunicationPublished", "CommunicationPublished", nameof(Communication), c.Id.ToString(), payload: new { tenantId = req.TenantId, communicationId = c.Id, tasksCreated }, details: c.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(new { communication = c, tasksCreated });
        });

        var templates = app.MapGroup("/api/notification-templates").WithTags("NotificationTemplates").RequireAuthorization();
        templates.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateNotifTemplateReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("notification:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.BodyTemplate))
                return Results.BadRequest(new { error = "Code and BodyTemplate required." });
            if (!Enum.TryParse<NotificationChannel>(req.Channel, true, out var ch))
                return Results.BadRequest(new { error = "Invalid channel." });

            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var tpl = new NotificationTemplate(Guid.NewGuid(), req.TenantId, req.Code.Trim(), ch, req.Subject?.Trim() ?? "", req.BodyTemplate.Trim(), true);
            db.NotificationTemplates.Add(tpl);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex))
            { return Results.Conflict(new { error = "Template code already exists." }); }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/notification-templates/{tpl.Id}", tpl);
        });
        templates.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("notification:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.NotificationTemplates.Where(t => t.TenantId == tenantId).ToListAsync(ct));
        });

        var notifs = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();
        notifs.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? personId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("notification:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Notifications.Where(n => n.TenantId == tenantId);
            if (personId.HasValue) q = q.Where(n => n.PersonId == personId.Value);
            return Results.Ok(await q.OrderByDescending(n => n.CreatedAt).Take(100).ToListAsync(ct));
        });

        app.MapGet("/api/notification-receipts", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("notification:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.Notifications.Where(n => n.TenantId == tenantId).ToListAsync(ct));
        }).RequireAuthorization().WithTags("NotificationReceipts");

        // --- Approvals & Tasks ---
        var approvals = app.MapGroup("/api/approvals").WithTags("Approvals").RequireAuthorization();
        approvals.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? assigneeId, string? status, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("approval:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Approvals.Where(a => a.TenantId == tenantId);
            if (assigneeId.HasValue) q = q.Where(a => a.AssigneeId == assigneeId.Value);
            if (Enum.TryParse<ApprovalStatus>(status, true, out var s)) q = q.Where(a => a.Status == s);
            return Results.Ok(await q.OrderBy(a => a.DueAt).ToListAsync(ct));
        });
        approvals.MapGet("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("approval:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var a = await db.Approvals.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (a is null) return Results.NotFound(new { error = "Approval not found." });
            return Results.Ok(a);
        });
        approvals.MapPost("/{id:guid}/decide", async (AppDbContext db, HttpContext ctx, Guid id, DecideApprovalReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("approval:decide")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var a = await db.Approvals.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (a is null) return Results.NotFound(new { error = "Approval not found." });
            var actor = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            if (actor is null || (req.DecidedBy is { } claimed && claimed != actor) || a.AssigneeId != actor) return Results.Forbid();
            if (a.Status != ApprovalStatus.Pending) return Results.Conflict(new { error = $"Approval is already {a.Status}." });
            var newStatus = req.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected;
            db.Entry(a).CurrentValues.SetValues(a with { Status = newStatus, DecidedAt = DateTimeOffset.UtcNow, DecidedBy = actor, Comment = req.Comment });
            if (a.EntityType == nameof(Correspondence))
            {
                var c = await db.Correspondences.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == a.EntityId, ct);
                if (c != null)
                {
                    var status = req.Approve ? CorrespondenceStatus.Approved : CorrespondenceStatus.Rejected;
                    db.Entry(c).CurrentValues.SetValues(c with { Status = status });
                }
            }
            else if (a.EntityType == nameof(Request))
            {
                var r = await db.Requests.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == a.EntityId, ct);
                if (r != null)
                {
                    var status = req.Approve ? RequestStatus.Approved : RequestStatus.Rejected;
                    db.Entry(r).CurrentValues.SetValues(r with { Status = status });
                }
            }
            DomainEvents.Record(db, req.TenantId, "ApprovalDecided", "ApprovalDecided",
                nameof(Approval), a.Id.ToString(), details: newStatus.ToString());
            await WorkflowRunner.OnApprovalDecidedAsync(db, req.TenantId, a, req.Approve, ct);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(a);
        });
        approvals.MapPost("/{id:guid}/request-changes", async (AppDbContext db, HttpContext ctx, Guid id, DecideApprovalReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("approval:decide")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var a = await db.Approvals.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (a is null) return Results.NotFound(new { error = "Approval not found." });
            var actor = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            if (actor is null || (req.DecidedBy is { } claimed && claimed != actor) || a.AssigneeId != actor) return Results.Forbid();
            if (a.Status != ApprovalStatus.Pending) return Results.Conflict(new { error = $"Approval is already {a.Status}." });
            db.Entry(a).CurrentValues.SetValues(a with { Status = ApprovalStatus.Rejected, DecidedAt = DateTimeOffset.UtcNow, DecidedBy = actor, Comment = req.Comment });

            if (a.EntityType == nameof(Request))
            {
                var r = await db.Requests.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == a.EntityId, ct);
                if (r != null)
                {
                    db.Entry(r).CurrentValues.SetValues(r with { Status = RequestStatus.ChangesRequested });
                }
            }

            DomainEvents.Record(db, req.TenantId, "ApprovalChangesRequested", "ApprovalChangesRequested", nameof(Approval), a.Id.ToString(), details: req.Comment ?? "Changes requested");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(a);
        });
        approvals.MapPost("/{id:guid}/delegate", async (AppDbContext db, HttpContext ctx, Guid id, DelegateApprovalReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("approval:decide")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var a = await db.Approvals.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (a is null) return Results.NotFound(new { error = "Approval not found." });

            var actor = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            var claimed = req.DelegatedBy ?? req.DecidedBy;
            if (actor is null || (claimed is { } c && c != actor) || a.AssigneeId != actor) return Results.Forbid();
            if (a.Status != ApprovalStatus.Pending) return Results.Conflict(new { error = $"Approval is already {a.Status}." });

            var deputyId = req.DelegateTo ?? req.DeputyPersonId ?? Guid.Empty;
            if (deputyId == Guid.Empty) return Results.BadRequest(new { error = "Delegate person id required." });
            if (deputyId == actor) return Results.BadRequest(new { error = "Cannot delegate to yourself." });
            if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == deputyId, ct))
                return Results.BadRequest(new { error = "Delegate person not found in this tenant." });

            db.Entry(a).CurrentValues.SetValues(a with { AssigneeId = deputyId });

            var workTasks = await db.WorkTasks.Where(t => t.TenantId == req.TenantId && t.ApprovalId == id).ToListAsync(ct);
            foreach (var wt in workTasks)
            {
                db.Entry(wt).CurrentValues.SetValues(wt with { AssigneeId = deputyId });
            }

            DomainEvents.Record(db, req.TenantId, "ApprovalDelegated", "ApprovalDelegated", nameof(Approval), a.Id.ToString(), details: $"Delegated to {deputyId}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(a);
        });

        var tasks = app.MapGroup("/api/tasks").WithTags("Tasks").RequireAuthorization();
        tasks.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? assigneeId, string? status, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("task:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.WorkTasks.Where(t => t.TenantId == tenantId);
            if (assigneeId.HasValue) q = q.Where(t => t.AssigneeId == assigneeId.Value);
            if (Enum.TryParse<WorkTaskStatus>(status, true, out var s)) q = q.Where(t => t.Status == s);
            return Results.Ok(await q.OrderBy(t => t.DueAt).ToListAsync(ct));
        });
        tasks.MapGet("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("task:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var t = await db.WorkTasks.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (t is null) return Results.NotFound(new { error = "Task not found." });
            return Results.Ok(t);
        });
        tasks.MapGet("/{id:guid}/comments", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("task:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var list = await db.TaskComments.Where(c => c.TenantId == tenantId && c.TaskId == id).OrderBy(c => c.At).ToListAsync(ct);
            return Results.Ok(list);
        });
        tasks.MapGet("/{id:guid}/evidence", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("task:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var list = await db.TaskEvidences.Where(e => e.TenantId == tenantId && e.TaskId == id).OrderBy(e => e.At).ToListAsync(ct);
            return Results.Ok(list);
        });
        tasks.MapPost("/{id:guid}/complete", async (AppDbContext db, HttpContext ctx, Guid id, CompleteTaskReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("task:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var t = await db.WorkTasks.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (t is null) return Results.NotFound(new { error = "Task not found." });
            var updated = t with { Status = WorkTaskStatus.Done, Progress = 100 };
            db.Entry(t).CurrentValues.SetValues(updated);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        });
        tasks.MapPatch("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, UpdateTaskReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("task:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (req.Progress.HasValue && (req.Progress.Value < 0 || req.Progress.Value > 100))
                return Results.BadRequest(new { error = "Progress must be between 0 and 100." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var t = await db.WorkTasks.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (t is null) return Results.NotFound(new { error = "Task not found." });
            var updated = t with
            {
                Description = string.IsNullOrWhiteSpace(req.Description) ? t.Description : req.Description.Trim(),
                Priority = req.Priority is not null && Enum.TryParse<WorkTaskPriority>(req.Priority, true, out var p) ? p : t.Priority,
                Progress = req.Progress ?? t.Progress,
            };
            db.Entry(t).CurrentValues.SetValues(updated);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        });
        tasks.MapPost("/{id:guid}/comments", async (AppDbContext db, HttpContext ctx, Guid id, AddTaskCommentReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("task:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var comment = new TaskComment(Guid.NewGuid(), req.TenantId, id, req.AuthorId, req.Text.Trim(), DateTimeOffset.UtcNow);
            db.TaskComments.Add(comment);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/tasks/{id}/comments/{comment.Id}", comment);
        });
        tasks.MapPost("/{id:guid}/evidence", async (AppDbContext db, HttpContext ctx, Guid id, AddTaskEvidenceReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("task:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var ev = new TaskEvidence(Guid.NewGuid(), req.TenantId, id, req.UploadedBy, req.ObjectKey, req.FileName, DateTimeOffset.UtcNow);
            db.TaskEvidences.Add(ev);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/tasks/{id}/evidence/{ev.Id}", ev);
        });

        // --- Chatter ---
        var chatter = app.MapGroup("/api/chatter").WithTags("Chatter").RequireAuthorization();
        chatter.MapPost("/comments", async (AppDbContext db, HttpContext ctx, CreateChatterCommentReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("chatter:write")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Content)) return Results.BadRequest(new { error = "Content required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var comment = new RecordComment(Guid.NewGuid(), req.TenantId, req.EntityType.Trim(), req.EntityId, req.AuthorId, req.Content.Trim(), req.IsInternalOnly, DateTimeOffset.UtcNow);
            db.RecordComments.Add(comment);
            DomainEvents.Record(db, req.TenantId, "ChatterCommentAdded", "ChatterCommentAdded", req.EntityType, req.EntityId.ToString(), details: comment.Content[..Math.Min(50, comment.Content.Length)]);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/chatter/comments/{comment.Id}", comment);
        });
        chatter.MapGet("/comments", async (AppDbContext db, HttpContext ctx, Guid tenantId, string entityType, Guid entityId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("chatter:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var list = await db.RecordComments
                .Where(c => c.TenantId == tenantId && c.EntityType == entityType && c.EntityId == entityId)
                .OrderBy(c => c.CreatedAt)
                .ToListAsync(ct);
            return Results.Ok(list);
        });
        chatter.MapPost("/follow", async (AppDbContext db, HttpContext ctx, FollowChatterReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("chatter:write")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var follower = new EntityFollower(Guid.NewGuid(), req.TenantId, req.EntityType.Trim(), req.EntityId, req.PersonId, DateTimeOffset.UtcNow);
            db.EntityFollowers.Add(follower);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex))
            {
                return Results.Conflict(new { error = "Person is already following this entity." });
            }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/chatter/followers/{follower.Id}", follower);
        });
        chatter.MapGet("/followers", async (AppDbContext db, HttpContext ctx, Guid tenantId, string entityType, Guid entityId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("chatter:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var list = await db.EntityFollowers
                .Where(f => f.TenantId == tenantId && f.EntityType == entityType && f.EntityId == entityId)
                .ToListAsync(ct);
            return Results.Ok(list);
        });

        // --- Activities ---
        var activities = app.MapGroup("/api/activities").WithTags("Activities").RequireAuthorization();
        activities.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateActivityReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("activity:write")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enum.TryParse<ActivityType>(req.Type, true, out var atype))
                return Results.BadRequest(new { error = "Invalid activity type." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var act = new ScheduledActivity(Guid.NewGuid(), req.TenantId, req.EntityType.Trim(), req.EntityId, atype, req.AssigneeId, req.Summary.Trim(), req.DueDate ?? DateTimeOffset.UtcNow.AddDays(7), false, null, DateTimeOffset.UtcNow);
            db.ScheduledActivities.Add(act);
            DomainEvents.Record(db, req.TenantId, "ActivityScheduled", "ActivityScheduled", req.EntityType, req.EntityId.ToString(), details: act.Summary);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/activities/{act.Id}", act);
        });
        activities.MapGet("/my", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid personId, bool? completed, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("activity:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.ScheduledActivities.Where(a => a.TenantId == tenantId && a.AssigneeId == personId);
            if (completed.HasValue) q = q.Where(a => a.IsCompleted == completed.Value);
            return Results.Ok(await q.OrderBy(a => a.DueDate).ToListAsync(ct));
        });
        activities.MapPatch("/{id:guid}/complete", async (AppDbContext db, HttpContext ctx, Guid id, CompleteActivityReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("activity:write")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var act = await db.ScheduledActivities.FirstOrDefaultAsync(a => a.TenantId == req.TenantId && a.Id == id, ct);
            if (act is null) return Results.NotFound(new { error = "Activity not found." });
            db.Entry(act).CurrentValues.SetValues(act with { IsCompleted = true, CompletedAt = DateTimeOffset.UtcNow });
            DomainEvents.Record(db, req.TenantId, "ActivityCompleted", "ActivityCompleted", act.EntityType, act.EntityId.ToString(), details: act.Summary);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(act);
        });

        var sla = app.MapGroup("/api/sla").WithTags("SLA").RequireAuthorization();
        sla.MapGet("/breaches", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("approval:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var now = DateTimeOffset.UtcNow;
            var approvalBreaches = await db.Approvals.Where(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending && a.DueAt < now).ToListAsync(ct);
            var taskBreaches = await db.WorkTasks.Where(t => t.TenantId == tenantId && t.Status != WorkTaskStatus.Done && t.DueAt < now).ToListAsync(ct);
            return Results.Ok(new { approvals = approvalBreaches, tasks = taskBreaches });
        });

        app.MapGet("/api/inbox", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid personId, string? filter, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("inbox:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var tasks = await db.WorkTasks.Where(t => t.TenantId == tenantId && t.AssigneeId == personId && t.Status != WorkTaskStatus.Done).ToListAsync(ct);
            var approvals = await db.Approvals.Where(a => a.TenantId == tenantId && a.AssigneeId == personId && a.Status == ApprovalStatus.Pending).ToListAsync(ct);
            var notifications = await db.Notifications.Where(n => n.TenantId == tenantId && n.PersonId == personId).Take(50).ToListAsync(ct);
            int total = tasks.Count + approvals.Count + notifications.Count;
            return Results.Ok(new { total, tasks, approvals, notifications });
        }).RequireAuthorization().WithTags("Inbox");

        app.MapGet("/api/my-work", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid personId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("inbox:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var myApprovals = await db.Approvals.Where(a => a.TenantId == tenantId && a.AssigneeId == personId && a.Status == ApprovalStatus.Pending).ToListAsync(ct);
            var myTasks = await db.WorkTasks.Where(t => t.TenantId == tenantId && t.AssigneeId == personId && t.Status != WorkTaskStatus.Done).ToListAsync(ct);
            var myActivities = await db.ScheduledActivities.Where(a => a.TenantId == tenantId && a.AssigneeId == personId && !a.IsCompleted).ToListAsync(ct);
            var counts = new { openApprovals = myApprovals.Count, openTasks = myTasks.Count, pendingActivities = myActivities.Count };
            return Results.Ok(new { counts, approvals = myApprovals, tasks = myTasks, activities = myActivities });
        }).RequireAuthorization().WithTags("MyWork");
    }
}

public sealed record SubmitRequestReq(Guid TenantId, Guid? ReviewerId, string? WorkflowCode);
public sealed record PublishCommunicationReq(Guid TenantId);
public sealed record DelegateApprovalReq(Guid TenantId, Guid? DecidedBy, Guid? DelegatedBy, Guid? DelegateTo, Guid? DeputyPersonId, string? Scope);
public sealed record CreateChatterCommentReq(Guid TenantId, string EntityType, Guid EntityId, Guid AuthorId, string Content, bool IsInternalOnly);
public sealed record FollowChatterReq(Guid TenantId, string EntityType, Guid EntityId, Guid PersonId);
public sealed record CreateActivityReq(Guid TenantId, string EntityType, Guid EntityId, string Type, Guid AssigneeId, string Summary, DateTimeOffset? DueDate);
public sealed record CompleteActivityReq(Guid TenantId);
public sealed record CompleteTaskReq(Guid TenantId);
public sealed record UpdateTaskReq(Guid TenantId, string? Description, string? Priority, int? Progress);
public sealed record AddTaskCommentReq(Guid TenantId, Guid AuthorId, string Text);
public sealed record AddTaskEvidenceReq(Guid TenantId, Guid UploadedBy, string ObjectKey, string FileName);
public sealed record CreateNotifTemplateReq(Guid TenantId, string Code, string Channel, string? Subject, string BodyTemplate);
