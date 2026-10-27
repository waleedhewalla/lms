using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
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

public sealed record ImportFrameworkReq(Guid TenantId);
public sealed record ImportCriteriaCsvReq(Guid TenantId, string Framework, string Csv);
public sealed record MapEvidenceReq(Guid TenantId, string EntityType, Guid EntityId, List<Guid> CriterionIds, string? Note);
public sealed record CorrectiveActionStatusReq(Guid TenantId, string Status);

/// <summary>
/// Wave I: standards library, evidence crosswalk, readiness and the self-study report; strategy scorecard.
/// Builds on the quality and strategy tables created in R3.
/// </summary>
public static class AccreditationEndpoints
{
    /// <summary>Records that can be cited as evidence, and how to name them.</summary>
    private static readonly string[] EvidenceTypes = ["Document", "Policy", "Procedure", "Decision", "Meeting", "Committee", "Task", "Kpi", "Correspondence"];

    public static void MapAccreditationEndpoints(this IEndpointRouteBuilder app)
    {
        var quality = app.MapGroup("/api/quality").WithTags("Quality").RequireAuthorization();

        quality.MapGet("/frameworks", (HttpContext ctx) =>
        {
            if (!ctx.User.HasPermission("quality:read")) return Results.Forbid();
            return Results.Ok(AccreditationLibrary.Frameworks.Select(f => new
            {
                f.Code, f.Name, f.Body, standards = f.Standards.Count, criteria = f.Standards.Sum(s => s.Criteria.Count),
            }));
        });

        // Load a built-in framework. Idempotent: existing codes are left alone.
        quality.MapPost("/frameworks/{code}/import", async (AppDbContext db, HttpContext ctx, string code, ImportFrameworkReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            var fw = AccreditationLibrary.Find(code);
            if (fw is null) return Results.NotFound(new { error = $"Unknown framework '{code}'." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var rows = fw.Standards.SelectMany(s => s.Criteria.Select(c => (s.Code, s.Title, c.Code, c.Text))).ToList();
            var (stdNew, crNew, _) = await UpsertAsync(db, req.TenantId, fw.Code, rows, overwrite: false, ct);
            DomainEvents.Record(db, req.TenantId, "FrameworkImported", "FrameworkImported", nameof(Standard), fw.Code,
                payload: new { tenantId = req.TenantId, framework = fw.Code }, details: $"standards+{stdNew} criteria+{crNew}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(new { framework = fw.Code, standardsCreated = stdNew, criteriaCreated = crNew });
        });

        // Load or refresh official criteria text: standardCode,standardTitle,criterionCode,criterionText.
        quality.MapPost("/standards/import", async (AppDbContext db, HttpContext ctx, ImportCriteriaCsvReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Framework) || req.Framework.Trim().Length > 40)
                return Results.BadRequest(new { error = "Framework (1–40 characters) is required." });
            var parsed = CsvText.Parse(req.Csv ?? "");
            if (parsed.Count < 2) return Results.BadRequest(new { error = "Expected a header row and at least one data row." });
            var header = parsed[0].Select(h => h.Trim().ToLowerInvariant()).ToArray();
            string[] want = ["standardcode", "standardtitle", "criterioncode", "criteriontext"];
            if (!header.Take(4).SequenceEqual(want))
                return Results.BadRequest(new { error = "Header must be: standardCode,standardTitle,criterionCode,criterionText" });
            var errors = new List<string>();
            var rows = new List<(string, string, string, string)>();
            for (var i = 1; i < parsed.Count; i++)
            {
                var r = parsed[i].Select(x => x.Trim()).ToArray();
                if (r.Length < 4 || r[..4].Any(string.IsNullOrEmpty)) { errors.Add($"Line {i + 1}: all four columns are required."); continue; }
                if (r[0].Length > 100 || r[2].Length > 100) { errors.Add($"Line {i + 1}: codes must be at most 100 characters."); continue; }
                rows.Add((r[0], r[1], r[2], r[3]));
            }
            if (errors.Count > 0) return Results.BadRequest(new { error = "The file has errors; nothing was imported.", details = errors });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var (stdNew, crNew, updated) = await UpsertAsync(db, req.TenantId, req.Framework.Trim(), rows, overwrite: true, ct);
            if (stdNew < 0) return Results.Conflict(new { error = "A standard code in the file already belongs to a different framework." });
            DomainEvents.Record(db, req.TenantId, "CriteriaImported", "CriteriaImported", nameof(Standard), req.Framework.Trim(),
                payload: new { tenantId = req.TenantId, framework = req.Framework.Trim() }, details: $"standards+{stdNew} criteria+{crNew} updated={updated}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(new { standardsCreated = stdNew, criteriaCreated = crNew, updated });
        });

        quality.MapGet("/criteria", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? standardId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Criteria.Where(c => c.TenantId == tenantId);
            if (standardId is { } sid) q = q.Where(c => c.StandardId == sid);
            return Results.Ok(await q.OrderBy(c => c.Code).ToListAsync(ct));
        });

        quality.MapGet("/evidence", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? criterionId, string? entityType, Guid? entityId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Evidences.Where(e => e.TenantId == tenantId);
            if (criterionId is { } cid) q = q.Where(e => e.CriterionId == cid);
            if (!string.IsNullOrWhiteSpace(entityType)) q = q.Where(e => e.EntityType == entityType);
            if (entityId is { } eid) q = q.Where(e => e.EntityId == eid);
            var list = await q.ToListAsync(ct);
            var titles = await TitlesAsync(db, tenantId, list.Select(e => (e.EntityType, e.EntityId)), ct);
            return Results.Ok(list.Select(e => new { e.Id, e.CriterionId, e.EntityType, e.EntityId, e.Note, title = titles.GetValueOrDefault((e.EntityType, e.EntityId)) }));
        });

        // Crosswalk: cite one record against many criteria (across frameworks) in one step.
        quality.MapPost("/evidence/map", async (AppDbContext db, HttpContext ctx, MapEvidenceReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            var type = EvidenceTypes.FirstOrDefault(t => string.Equals(t, req.EntityType, StringComparison.OrdinalIgnoreCase));
            if (type is null) return Results.BadRequest(new { error = $"EntityType must be one of {string.Join(", ", EvidenceTypes)}." });
            var ids = (req.CriterionIds ?? []).Distinct().ToList();
            if (ids.Count == 0) return Results.BadRequest(new { error = "Choose at least one criterion." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var titles = await TitlesAsync(db, req.TenantId, [(type, req.EntityId)], ct);
            if (!titles.ContainsKey((type, req.EntityId))) return Results.NotFound(new { error = $"{type} not found." });
            var known = await db.Criteria.Where(c => c.TenantId == req.TenantId && ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct);
            if (known.Count != ids.Count) return Results.NotFound(new { error = "One or more criteria were not found." });
            var already = await db.Evidences.Where(e => e.TenantId == req.TenantId && e.EntityType == type && e.EntityId == req.EntityId && ids.Contains(e.CriterionId))
                .Select(e => e.CriterionId).ToListAsync(ct);
            var added = ids.Except(already).Select(cid => new Evidence(Guid.NewGuid(), req.TenantId, cid, type, req.EntityId, req.Note?.Trim() ?? "")).ToList();
            db.Evidences.AddRange(added);
            if (added.Count > 0)
                DomainEvents.Record(db, req.TenantId, "EvidenceAdded", "EvidenceMapped", type, req.EntityId.ToString(),
                    payload: new { tenantId = req.TenantId, criteria = added.Select(a => a.CriterionId) }, details: $"{added.Count} criteria");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(new { added = added.Count, skipped = already.Count, title = titles[(type, req.EntityId)] });
        });

        quality.MapDelete("/evidence/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var ev = await db.Evidences.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == id, ct);
            if (ev is null) return Results.NotFound();
            db.Evidences.Remove(ev);
            DomainEvents.Record(db, tenantId, "EvidenceRemoved", "EvidenceRemoved", ev.EntityType, ev.EntityId.ToString(), details: $"criterion:{ev.CriterionId}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.NoContent();
        });

        quality.MapGet("/findings", async (AppDbContext db, HttpContext ctx, Guid tenantId, bool? open, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Findings.Where(x => x.TenantId == tenantId);
            if (open is { } o) q = q.Where(x => x.IsClosed != o);
            var findings = await q.ToListAsync(ct);
            var fids = findings.Select(x => x.Id).ToList();
            var actions = await db.CorrectiveActions.Where(a => a.TenantId == tenantId && fids.Contains(a.FindingId)).ToListAsync(ct);
            return Results.Ok(findings.OrderBy(x => x.IsClosed).ThenByDescending(x => x.Severity)
                .Select(x => new { x.Id, x.CriterionId, x.Severity, x.Text, x.IsClosed, actions = actions.Where(a => a.FindingId == x.Id).OrderBy(a => a.DueAt) }));
        });

        // A finding closes only when every corrective action on it is verified.
        quality.MapPost("/findings/{id:guid}/close", async (AppDbContext db, HttpContext ctx, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var fd = await db.Findings.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (fd is null) return Results.NotFound(new { error = "Finding not found." });
            var pending = await db.CorrectiveActions.CountAsync(a => a.TenantId == req.TenantId && a.FindingId == id && a.Status != CorrectiveActionStatus.Verified, ct);
            if (pending > 0) return Results.Conflict(new { error = $"{pending} corrective action(s) are not verified yet." });
            db.Entry(fd).CurrentValues.SetValues(fd with { IsClosed = true });
            DomainEvents.Record(db, req.TenantId, "FindingClosed", "FindingClosed", nameof(Finding), id.ToString(), details: fd.Severity.ToString());
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(fd with { IsClosed = true });
        });

        // The assignee reports progress; someone else verifies.
        quality.MapPost("/corrective-actions/{id:guid}/status", async (AppDbContext db, HttpContext ctx, Guid id, CorrectiveActionStatusReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enum.TryParse<CorrectiveActionStatus>(req.Status, true, out var status))
                return Results.BadRequest(new { error = "Status must be Open|InProgress|Done|Verified." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var ca = await db.CorrectiveActions.FirstOrDefaultAsync(a => a.TenantId == req.TenantId && a.Id == id, ct);
            if (ca is null) return Results.NotFound(new { error = "Corrective action not found." });
            if (status == CorrectiveActionStatus.Verified)
            {
                if (ca.Status != CorrectiveActionStatus.Done) return Results.Conflict(new { error = "Only a done action can be verified." });
                if (await ActingPersonAsync(db, ctx, req.TenantId, ct) == ca.AssigneeId)
                    return Results.Json(new { error = "Segregation of duties: the assignee cannot verify their own action." }, statusCode: StatusCodes.Status403Forbidden);
            }
            db.Entry(ca).CurrentValues.SetValues(ca with { Status = status });
            DomainEvents.Record(db, req.TenantId, "CorrectiveActionUpdated", "CorrectiveActionUpdated", nameof(CorrectiveAction), id.ToString(), details: status.ToString());
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(ca with { Status = status });
        });

        quality.MapGet("/crosswalk", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? framework, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var m = await ReadinessAsync(db, tenantId, framework, ct);
            // Records cited against more than one criterion, across all frameworks: the reuse a crosswalk is for.
            var all = string.IsNullOrWhiteSpace(framework) ? m : await ReadinessAsync(db, tenantId, null, ct);
            var inView = m.Evidence.Select(e => (e.EntityType, e.EntityId)).ToHashSet();
            var shared = all.Evidence.Where(e => inView.Contains((e.EntityType, e.EntityId))).GroupBy(e => (e.EntityType, e.EntityId)).Where(g => g.Select(e => e.CriterionId).Distinct().Count() > 1)
                .Select(g => new
                {
                    entityType = g.Key.EntityType, entityId = g.Key.EntityId, title = all.Titles.GetValueOrDefault(g.Key),
                    criteria = g.Select(e => all.CriterionCode(e.CriterionId)).Distinct().OrderBy(c => c).ToList(),
                }).OrderByDescending(x => x.criteria.Count).ToList();
            return Results.Ok(new
            {
                framework = framework ?? "all",
                readiness = Accreditation.ReadinessPercent(m.Rows.Select(r => r.Status).ToList()),
                met = m.Rows.Count(r => r.Status == CriterionStatus.Met),
                partial = m.Rows.Count(r => r.Status == CriterionStatus.Partial),
                notMet = m.Rows.Count(r => r.Status == CriterionStatus.NotMet),
                rows = m.Rows.Select(r => new
                {
                    standardId = r.Standard.Id, standardCode = r.Standard.Code, standardTitle = r.Standard.Title, r.Standard.Framework,
                    criterionId = r.Criterion.Id, criterionCode = r.Criterion.Code, criterionText = r.Criterion.Text,
                    evidence = r.Evidence.Count, evidenceByType = r.Evidence.GroupBy(e => e.EntityType).ToDictionary(g => g.Key, g => g.Count()),
                    openMajor = r.OpenMajor, openMinor = r.OpenMinor, status = r.Status.ToString(),
                }),
                shared,
            });
        });

        // A printable self-study evidence report (open in a browser, print to PDF).
        quality.MapGet("/self-study", async (AppDbContext db, HttpContext ctx, Guid tenantId, string framework, string? lang, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("quality:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var m = await ReadinessAsync(db, tenantId, framework, ct);
            if (m.Rows.Count == 0) return Results.NotFound(new { error = $"No criteria loaded for framework '{framework}'." });
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            var findingIds = m.Findings.Select(x => x.Id).ToList();
            var actions = await db.CorrectiveActions.Where(a => a.TenantId == tenantId && findingIds.Contains(a.FindingId)).ToListAsync(ct);
            var kpiIds = m.Evidence.Where(e => e.EntityType == "Kpi").Select(e => e.EntityId).Distinct().ToList();
            var kpis = await db.Kpis.Where(k => k.TenantId == tenantId && kpiIds.Contains(k.Id)).ToDictionaryAsync(k => k.Id, ct);
            var html = SelfStudyHtml(m, tenant?.Name ?? "", framework, lang == "ar", actions, kpis);
            DomainEvents.Record(db, tenantId, "SelfStudyGenerated", "SelfStudyGenerated", nameof(Standard), framework, details: $"criteria={m.Rows.Count}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            var file = $"self-study-{new string(framework.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMdd}.html";
            return Results.File(Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8", file);
        });

        // --- Strategy scorecard ---
        var strategy = app.MapGroup("/api/strategy").WithTags("Strategy").RequireAuthorization();

        strategy.MapGet("/plans/{id:guid}/scorecard", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("strategy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var plan = await db.StrategicPlans.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, ct);
            if (plan is null) return Results.NotFound(new { error = "Plan not found." });
            var objectives = await db.Objectives.Where(o => o.TenantId == tenantId && o.PlanId == id).OrderBy(o => o.Code).ToListAsync(ct);
            var oids = objectives.Select(o => o.Id).ToList();
            var kpis = await db.Kpis.Where(k => k.TenantId == tenantId && oids.Contains(k.ObjectiveId)).OrderBy(k => k.Name).ToListAsync(ct);
            var kids = kpis.Select(k => k.Id).ToList();
            var readings = await db.KpiReadings.Where(r => r.TenantId == tenantId && kids.Contains(r.KpiId)).OrderBy(r => r.RecordedAt).ToListAsync(ct);
            var cited = await db.Evidences.Where(e => e.TenantId == tenantId && e.EntityType == "Kpi" && kids.Contains(e.EntityId)).Select(e => e.EntityId).ToListAsync(ct);
            object Card(Kpi k)
            {
                var hist = readings.Where(r => r.KpiId == k.Id).TakeLast(8).Select(r => new { r.Period, r.Value, r.RecordedAt }).ToList();
                var fraction = KpiProgress.Fraction(k);
                return new
                {
                    k.Id, k.Name, k.Unit, k.Baseline, k.Target, k.Current, k.LowerIsBetter,
                    progress = fraction is { } x ? (int?)Math.Round(100 * Math.Clamp(x, 0, 1)) : null,
                    status = KpiProgress.Status(k).ToString(),
                    trend = hist.Count < 2 ? "flat"
                        : (hist[^1].Value - hist[^2].Value) * (k.LowerIsBetter ? -1 : 1) is var d && d > 0 ? "improving" : d < 0 ? "worsening" : "flat",
                    readings = hist,
                    accreditationEvidence = cited.Count(c => c == k.Id),
                };
            }
            var statuses = kpis.Select(KpiProgress.Status).ToList();
            return Results.Ok(new
            {
                plan,
                summary = Enum.GetValues<KpiStatus>().ToDictionary(s => s.ToString(), s => statuses.Count(x => x == s)),
                objectives = objectives.Select(o => new { o.Id, o.Code, o.Text, kpis = kpis.Where(k => k.ObjectiveId == o.Id).Select(Card).ToList() }),
            });
        });

        strategy.MapGet("/kpis/{id:guid}/readings", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("strategy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (!await db.Kpis.AnyAsync(k => k.TenantId == tenantId && k.Id == id, ct)) return Results.NotFound(new { error = "KPI not found." });
            return Results.Ok(await db.KpiReadings.Where(r => r.TenantId == tenantId && r.KpiId == id).OrderBy(r => r.RecordedAt).ToListAsync(ct));
        });
    }

    /// <summary>Creates missing standards/criteria; with <paramref name="overwrite"/> also refreshes titles and text. Returns -1 creates on a framework clash.</summary>
    private static async Task<(int Standards, int Criteria, int Updated)> UpsertAsync(AppDbContext db, Guid tenantId, string framework,
        IReadOnlyList<(string StdCode, string StdTitle, string CrCode, string CrText)> rows, bool overwrite, CancellationToken ct)
    {
        var codes = rows.Select(r => r.StdCode).Distinct().ToList();
        var standards = await db.Standards.Where(s => s.TenantId == tenantId && codes.Contains(s.Code)).ToDictionaryAsync(s => s.Code, ct);
        if (overwrite && standards.Values.Any(s => !string.Equals(s.Framework, framework, StringComparison.OrdinalIgnoreCase))) return (-1, 0, 0);
        var sids = standards.Values.Select(s => s.Id).ToList();
        var criteria = await db.Criteria.Where(c => c.TenantId == tenantId && sids.Contains(c.StandardId)).ToListAsync(ct);
        int stdNew = 0, crNew = 0, updated = 0;
        foreach (var g in rows.GroupBy(r => r.StdCode))
        {
            var title = g.First().StdTitle;
            if (!standards.TryGetValue(g.Key, out var st))
            {
                st = new Standard(Guid.NewGuid(), tenantId, g.Key, title, framework);
                db.Standards.Add(st); standards[g.Key] = st; stdNew++;
            }
            else if (overwrite && st.Title != title) { db.Entry(st).CurrentValues.SetValues(st with { Title = title }); updated++; }
            foreach (var r in g.GroupBy(x => x.CrCode).Select(x => x.Last()))
            {
                var cr = criteria.FirstOrDefault(c => c.StandardId == st.Id && c.Code == r.CrCode);
                if (cr is null) { db.Criteria.Add(new Criterion(Guid.NewGuid(), tenantId, st.Id, r.CrCode, r.CrText)); crNew++; }
                else if (overwrite && cr.Text != r.CrText) { db.Entry(cr).CurrentValues.SetValues(cr with { Text = r.CrText }); updated++; }
            }
        }
        return (stdNew, crNew, updated);
    }

    private sealed record ReadinessRow(Standard Standard, Criterion Criterion, List<Evidence> Evidence, int OpenMajor, int OpenMinor, CriterionStatus Status);

    private sealed record Readiness(List<ReadinessRow> Rows, List<Evidence> Evidence, List<Finding> Findings, Dictionary<(string, Guid), string> Titles)
    {
        public string CriterionCode(Guid id) => Rows.FirstOrDefault(r => r.Criterion.Id == id) is { } r ? $"{r.Standard.Code} {r.Criterion.Code}" : id.ToString();
    }

    private static async Task<Readiness> ReadinessAsync(AppDbContext db, Guid tenantId, string? framework, CancellationToken ct)
    {
        var sq = db.Standards.Where(s => s.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(framework)) sq = sq.Where(s => s.Framework == framework);
        var standards = await sq.OrderBy(s => s.Code).ToListAsync(ct);
        var sids = standards.Select(s => s.Id).ToList();
        var criteria = await db.Criteria.Where(c => c.TenantId == tenantId && sids.Contains(c.StandardId)).ToListAsync(ct);
        var cids = criteria.Select(c => c.Id).ToList();
        var evidence = await db.Evidences.Where(e => e.TenantId == tenantId && cids.Contains(e.CriterionId)).ToListAsync(ct);
        var findings = await db.Findings.Where(x => x.TenantId == tenantId && cids.Contains(x.CriterionId)).ToListAsync(ct);
        var rows = new List<ReadinessRow>();
        foreach (var s in standards)
            foreach (var c in criteria.Where(c => c.StandardId == s.Id).OrderBy(c => c.Code, StringComparer.Ordinal))
            {
                var ev = evidence.Where(e => e.CriterionId == c.Id).ToList();
                var open = findings.Where(x => x.CriterionId == c.Id && !x.IsClosed).ToList();
                int major = open.Count(x => x.Severity == FindingSeverity.Major), minor = open.Count(x => x.Severity == FindingSeverity.Minor);
                rows.Add(new ReadinessRow(s, c, ev, major, minor, Accreditation.Assess(ev.Count, major, minor)));
            }
        var titles = await TitlesAsync(db, tenantId, evidence.Select(e => (e.EntityType, e.EntityId)), ct);
        return new Readiness(rows, evidence, findings, titles);
    }

    /// <summary>Display names for cited records, by type. Records that no longer exist are absent.</summary>
    private static async Task<Dictionary<(string, Guid), string>> TitlesAsync(AppDbContext db, Guid t, IEnumerable<(string Type, Guid Id)> refs, CancellationToken ct)
    {
        var byType = refs.GroupBy(r => r.Type).ToDictionary(g => g.Key, g => g.Select(r => r.Id).Distinct().ToList());
        var result = new Dictionary<(string, Guid), string>();
        async Task Add(string type, Func<List<Guid>, Task<List<(Guid Id, string Title)>>> load)
        {
            if (!byType.TryGetValue(type, out var ids)) return;
            foreach (var (id, title) in await load(ids)) result[(type, id)] = title;
        }
        await Add("Document", async ids => (await db.Documents.Where(x => x.TenantId == t && ids.Contains(x.Id)).Select(x => new { x.Id, x.Title }).ToListAsync(ct)).Select(x => (x.Id, x.Title)).ToList());
        await Add("Policy", async ids => (await db.Policies.Where(x => x.TenantId == t && ids.Contains(x.Id)).Select(x => new { x.Id, x.Code, x.Title }).ToListAsync(ct)).Select(x => (x.Id, $"{x.Code} — {x.Title}")).ToList());
        await Add("Procedure", async ids => (await db.Procedures.Where(x => x.TenantId == t && ids.Contains(x.Id)).Select(x => new { x.Id, x.Code, x.Title }).ToListAsync(ct)).Select(x => (x.Id, $"{x.Code} — {x.Title}")).ToList());
        await Add("Decision", async ids => (await db.Decisions.Where(x => x.TenantId == t && ids.Contains(x.Id)).Select(x => new { x.Id, x.Text }).ToListAsync(ct)).Select(x => (x.Id, x.Text.Length > 120 ? x.Text[..120] + "…" : x.Text)).ToList());
        await Add("Meeting", async ids => (await db.Meetings.Where(x => x.TenantId == t && ids.Contains(x.Id)).Select(x => new { x.Id, x.Title, x.StartsAt }).ToListAsync(ct)).Select(x => (x.Id, $"{x.Title} ({x.StartsAt:yyyy-MM-dd})")).ToList());
        await Add("Committee", async ids => (await db.Committees.Where(x => x.TenantId == t && ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)).Select(x => (x.Id, x.Name)).ToList());
        await Add("Task", async ids => (await db.WorkTasks.Where(x => x.TenantId == t && ids.Contains(x.Id)).Select(x => new { x.Id, x.Title }).ToListAsync(ct)).Select(x => (x.Id, x.Title)).ToList());
        await Add("Kpi", async ids => (await db.Kpis.Where(x => x.TenantId == t && ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToListAsync(ct)).Select(x => (x.Id, x.Name)).ToList());
        await Add("Correspondence", async ids => (await db.Correspondences.Where(x => x.TenantId == t && ids.Contains(x.Id)).Select(x => new { x.Id, x.Number, x.Subject }).ToListAsync(ct)).Select(x => (x.Id, $"{x.Number} — {x.Subject}")).ToList());
        return result;
    }

    private static string SelfStudyHtml(Readiness m, string institution, string framework, bool ar, List<CorrectiveAction> actions, Dictionary<Guid, Kpi> kpis)
    {
        string L(string en, string arabic) => ar ? arabic : en;
        static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
        string StatusLabel(CriterionStatus s) => s switch
        {
            CriterionStatus.Met => L("Met", "مستوفى"), CriterionStatus.Partial => L("Partially met", "مستوفى جزئيًا"), _ => L("Not met", "غير مستوفى"),
        };
        var fwName = AccreditationLibrary.Find(framework)?.Name ?? framework;
        var now = DateTimeOffset.UtcNow;
        var sb = new StringBuilder();
        sb.Append($"<!doctype html><html lang=\"{(ar ? "ar" : "en")}\" dir=\"{(ar ? "rtl" : "ltr")}\"><head><meta charset=\"utf-8\">");
        sb.Append($"<title>{H(L("Self-study", "الدراسة الذاتية"))} — {H(fwName)}</title><style>");
        sb.Append("body{font-family:system-ui,'Segoe UI',Tahoma,sans-serif;max-width:960px;margin:2rem auto;padding:0 1rem;color:#0f172a;line-height:1.5}");
        sb.Append("h1{margin-bottom:.25rem}h2{border-bottom:2px solid #0f766e;padding-bottom:.25rem;margin-top:2rem}h3{margin-bottom:.25rem}");
        sb.Append("table{border-collapse:collapse;width:100%}td,th{border:1px solid #cbd5e1;padding:.4rem .6rem;text-align:start}");
        sb.Append(".b{display:inline-block;padding:.1rem .5rem;border-radius:999px;font-size:.8rem;font-weight:600}.Met{background:#dcfce7}.Partial{background:#fef9c3}.NotMet{background:#fee2e2}");
        sb.Append(".muted{color:#64748b;font-size:.85rem}@media print{h2{break-before:page}h2:first-of-type{break-before:auto}}</style></head><body>");
        sb.Append($"<h1>{H(institution)}</h1><p><strong>{H(fwName)}</strong> — {H(L("self-study evidence report", "تقرير شواهد الدراسة الذاتية"))}</p>");
        sb.Append($"<p class=\"muted\">{H(L("Generated", "أُنشئ في"))} {now:yyyy-MM-dd} ({H(HijriDate.Format(now))} {H(L("AH", "هـ"))})</p>");
        sb.Append($"<table><tr><th>{H(L("Readiness", "الجاهزية"))}</th><th>{H(L("Met", "مستوفى"))}</th><th>{H(L("Partially met", "مستوفى جزئيًا"))}</th><th>{H(L("Not met", "غير مستوفى"))}</th><th>{H(L("Open findings", "ملاحظات مفتوحة"))}</th></tr>");
        sb.Append($"<tr><td><strong>{Accreditation.ReadinessPercent(m.Rows.Select(r => r.Status).ToList())}%</strong></td><td>{m.Rows.Count(r => r.Status == CriterionStatus.Met)}</td><td>{m.Rows.Count(r => r.Status == CriterionStatus.Partial)}</td><td>{m.Rows.Count(r => r.Status == CriterionStatus.NotMet)}</td><td>{m.Findings.Count(x => !x.IsClosed)}</td></tr></table>");
        foreach (var std in m.Rows.GroupBy(r => r.Standard.Id))
        {
            var s = std.First().Standard;
            sb.Append($"<h2>{H(s.Code)} — {H(s.Title)}</h2>");
            foreach (var r in std)
            {
                sb.Append($"<h3>{H(r.Criterion.Code)} <span class=\"b {r.Status}\">{H(StatusLabel(r.Status))}</span></h3><p>{H(r.Criterion.Text)}</p>");
                if (r.Evidence.Count == 0) sb.Append($"<p class=\"muted\">{H(L("No evidence linked yet.", "لم تُربط شواهد بعد."))}</p>");
                else
                {
                    sb.Append($"<p><strong>{H(L("Evidence", "الشواهد"))}</strong></p><ul>");
                    foreach (var e in r.Evidence.OrderBy(e => e.EntityType))
                    {
                        var title = m.Titles.GetValueOrDefault((e.EntityType, e.EntityId)) ?? L("(record no longer available)", "(السجل غير متاح)");
                        var extra = e.EntityType == "Kpi" && kpis.TryGetValue(e.EntityId, out var k)
                            ? $" — {k.Current:0.##} / {k.Target:0.##} {H(k.Unit)} ({KpiProgress.Status(k)})" : "";
                        sb.Append($"<li>[{H(e.EntityType)}] {H(title)}{extra}{(string.IsNullOrWhiteSpace(e.Note) ? "" : $" <span class=\"muted\">— {H(e.Note)}</span>")}</li>");
                    }
                    sb.Append("</ul>");
                }
                var fs = m.Findings.Where(x => x.CriterionId == r.Criterion.Id).ToList();
                if (fs.Count > 0)
                {
                    sb.Append($"<p><strong>{H(L("Findings and corrective actions", "الملاحظات والإجراءات التصحيحية"))}</strong></p><ul>");
                    foreach (var x in fs)
                    {
                        sb.Append($"<li>{H(x.Severity.ToString())}: {H(x.Text)} — {H(x.IsClosed ? L("closed", "مغلقة") : L("open", "مفتوحة"))}");
                        var acts = actions.Where(a => a.FindingId == x.Id).ToList();
                        if (acts.Count > 0)
                            sb.Append("<ul>").Append(string.Concat(acts.Select(a => $"<li>{H(a.Description)} — {H(a.Status.ToString())}, {H(L("due", "الاستحقاق"))} {a.DueAt:yyyy-MM-dd}</li>"))).Append("</ul>");
                        sb.Append("</li>");
                    }
                    sb.Append("</ul>");
                }
            }
        }
        sb.Append($"<p class=\"muted\">{H(L("Generated by EduNexus. Evidence items refer to records held in EduNexus.", "أُنشئ بواسطة EduNexus. تشير الشواهد إلى سجلات محفوظة في النظام."))}</p></body></html>");
        return sb.ToString();
    }
}
