using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EduNexus.Api.AI;
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
/// Wave E — AI governance assistants (BBP §13, AI-02/AI-03) and institutional memory (§12.3).
/// Every assistant output is grounded in the tenant's own records, labelled as a draft that needs human
/// review, and logged as an AiInteraction. Nothing is written to the governance record automatically.
/// </summary>
public static partial class AiGovernanceEndpoints
{
    public static void MapAiGovernanceEndpoints(this IEndpointRouteBuilder app)
    {
        var ai = app.MapGroup("/api/ai").WithTags("AI").RequireAuthorization();

        ai.MapPost("/meetings/{id:guid}/summary", async (AppDbContext db, HttpContext ctx, TextGenerator gen, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("ai:ask") || !ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var dossier = await MeetingDossier.LoadAsync(db, req.TenantId, id, ct);
            if (dossier is null) return Results.NotFound(new { error = "Meeting not found." });
            var (text, model) = await gen.GenerateAsync("Write a concise executive summary of this meeting (max 150 words).",
                dossier.ToContext(), dossier.ExtractiveSummary(), ct);
            var log = await LogAsync(db, ctx, req.TenantId, "meeting-summary", dossier.Meeting.Title, text, model, ct);
            await scope.CommitAsync(ct);
            return Results.Ok(new { summary = text, model, requiresHumanReview = true, interactionId = log.Id, sources = dossier.Sources() });
        });

        ai.MapPost("/meetings/{id:guid}/draft-minutes", async (AppDbContext db, HttpContext ctx, TextGenerator gen, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("ai:ask") || !ctx.User.HasPermission("meeting:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var dossier = await MeetingDossier.LoadAsync(db, req.TenantId, id, ct);
            if (dossier is null) return Results.NotFound(new { error = "Meeting not found." });
            var (text, model) = await gen.GenerateAsync(
                "Draft formal meeting minutes with sections: Attendance, Agenda and discussion, Votes, Decisions, Actions. " +
                "Write each decision on its own line starting with 'Decision:'.",
                dossier.ToContext(), dossier.ExtractiveMinutes(), ct);
            var log = await LogAsync(db, ctx, req.TenantId, "draft-minutes", dossier.Meeting.Title, text, model, ct);
            await scope.CommitAsync(ct);
            // A draft only: the secretary saves it with POST /api/meetings/{id}/minutes after review.
            return Results.Ok(new { draft = text, model, requiresHumanReview = true, interactionId = log.Id, sources = dossier.Sources() });
        });

        ai.MapPost("/minutes/{id:guid}/extract-decisions", async (AppDbContext db, HttpContext ctx, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("ai:ask") || !ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var minutes = await db.MeetingMinutes.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (minutes is null) return Results.NotFound(new { error = "Minutes not found." });
            var proposed = ExtractDecisions(minutes.Content);
            var log = await LogAsync(db, ctx, req.TenantId, "extract-decisions", $"minutes v{minutes.Version}",
                string.Join(" | ", proposed), TextGenerator.ExtractiveModel, ct);
            await scope.CommitAsync(ct);
            // Proposals only: decisions are recorded with POST /api/meetings/{meetingId}/decisions after review.
            return Results.Ok(new { meetingId = minutes.MeetingId, proposedDecisions = proposed, model = TextGenerator.ExtractiveModel,
                requiresHumanReview = true, interactionId = log.Id });
        });

        // --- Search suggest & institutional memory ---
        app.MapGet("/api/search/suggest", async (AppDbContext db, HttpContext ctx, Guid tenantId, string q, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("search:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2) return Results.Ok(Array.Empty<object>());
            var term = $"%{q.Trim()}%";
            var confidential = ctx.User.HasPermission("correspondence:confidential");
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var hits = new List<Suggestion>();
            hits.AddRange(await db.Requests.Where(x => x.TenantId == tenantId && EF.Functions.ILike(x.Title, term)).Take(5)
                .Select(x => new Suggestion(nameof(Request), x.Id, x.Number + " " + x.Title)).ToListAsync(ct));
            hits.AddRange(await db.Correspondences.Where(x => x.TenantId == tenantId && (confidential || !x.IsConfidential) && EF.Functions.ILike(x.Subject, term)).Take(5)
                .Select(x => new Suggestion(nameof(Correspondence), x.Id, x.Number + " " + x.Subject)).ToListAsync(ct));
            hits.AddRange(await db.Policies.Where(x => x.TenantId == tenantId && (EF.Functions.ILike(x.Title, term) || EF.Functions.ILike(x.Code, term))).Take(5)
                .Select(x => new Suggestion(nameof(Policy), x.Id, x.Code + " " + x.Title)).ToListAsync(ct));
            hits.AddRange(await db.Decisions.Where(x => x.TenantId == tenantId && EF.Functions.ILike(x.Text, term)).Take(5)
                .Select(x => new Suggestion(nameof(Decision), x.Id, x.Text)).ToListAsync(ct));
            hits.AddRange(await db.Meetings.Where(x => x.TenantId == tenantId && EF.Functions.ILike(x.Title, term)).Take(5)
                .Select(x => new Suggestion(nameof(Meeting), x.Id, x.Title)).ToListAsync(ct));
            hits.AddRange(await db.Documents.Where(x => x.TenantId == tenantId && EF.Functions.ILike(x.Title, term)).Take(5)
                .Select(x => new Suggestion(nameof(Document), x.Id, x.Title)).ToListAsync(ct));
            return Results.Ok(hits.Take(15).Select(h => h with { Label = h.Label.Length > 120 ? h.Label[..120] : h.Label }));
        }).RequireAuthorization().WithTags("Search");

        app.MapGet("/api/search/memory/{entityType}/{id:guid}", async (AppDbContext db, HttpContext ctx, string entityType, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("search:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var graph = await MemoryAsync(db, tenantId, entityType, id, ct);
            if (graph is null) return Results.NotFound(new { error = $"No {entityType} {id} (supported: Decision, Meeting, Committee, Policy, Request)." });
            var key = id.ToString();
            var history = await db.AuditEvents.Where(e => e.TenantId == tenantId && e.EntityId == key).OrderBy(e => e.At).ToListAsync(ct);
            return Results.Ok(new { root = new { entityType, id }, related = graph, history });
        }).RequireAuthorization().WithTags("Search");
    }

    private static async Task<AiInteraction> LogAsync(AppDbContext db, HttpContext ctx, Guid tenantId, string capability, string input, string output, string model, CancellationToken ct)
    {
        var log = new AiInteraction(Guid.NewGuid(), tenantId, capability, input[..Math.Min(input.Length, 500)],
            output[..Math.Min(output.Length, 1000)], model, model == TextGenerator.ExtractiveModel ? 1.0 : null,
            await ActingPersonAsync(db, ctx, tenantId, ct), DateTimeOffset.UtcNow);
        db.AiInteractions.Add(log);
        await db.SaveChangesAsync(ct);
        return log;
    }

    [GeneratedRegex(@"^\s*(?:[-*•]\s*)?(?:decision|resolved|resolution|قرار|تقرر)\s*[:：\-–]\s*(.+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DecisionLine();

    /// <summary>Lines such as "Decision: …", "Resolved: …" or "قرار: …" become proposed decisions.</summary>
    internal static List<string> ExtractDecisions(string minutes) =>
        DecisionLine().Matches(minutes).Select(m => m.Groups[1].Value.Trim()).Where(s => s.Length > 0).Distinct().ToList();

    private static async Task<List<object>?> MemoryAsync(AppDbContext db, Guid tenantId, string entityType, Guid id, CancellationToken ct)
    {
        var related = new List<object>();
        void Add(string type, Guid rid, string label, string relation) => related.Add(new { type, id = rid, label, relation });
        switch (entityType.ToLowerInvariant())
        {
            case "decision":
            {
                var d = await db.Decisions.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
                if (d is null) return null;
                var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == d.MeetingId, ct);
                if (m is not null)
                {
                    Add(nameof(Meeting), m.Id, m.Title, "decided-in");
                    var c = await db.Committees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == m.CommitteeId, ct);
                    if (c is not null) Add(nameof(Committee), c.Id, c.Name, "by-committee");
                }
                foreach (var a in await db.DecisionActions.Where(x => x.TenantId == tenantId && x.DecisionId == id).ToListAsync(ct))
                {
                    Add(nameof(DecisionAction), a.Id, $"{a.Description} [{a.Status}]", "action");
                    foreach (var e in await db.DecisionActionEvidences.Where(x => x.TenantId == tenantId && x.ActionId == a.Id).ToListAsync(ct))
                        Add(nameof(DecisionActionEvidence), e.Id, e.FileName, "evidence");
                }
                return related;
            }
            case "meeting":
            {
                var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
                if (m is null) return null;
                var c = await db.Committees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == m.CommitteeId, ct);
                if (c is not null) Add(nameof(Committee), c.Id, c.Name, "of-committee");
                foreach (var a in await db.AgendaItems.Where(x => x.TenantId == tenantId && x.MeetingId == id).OrderBy(x => x.Order).ToListAsync(ct))
                    Add(nameof(AgendaItem), a.Id, a.Title, "agenda");
                foreach (var d in await db.Decisions.Where(x => x.TenantId == tenantId && x.MeetingId == id).ToListAsync(ct))
                    Add(nameof(Decision), d.Id, d.Text, "decision");
                foreach (var v in await db.MeetingMinutes.Where(x => x.TenantId == tenantId && x.MeetingId == id).ToListAsync(ct))
                    Add(nameof(MeetingMinutes), v.Id, $"Minutes v{v.Version} [{v.Status}]", "minutes");
                return related;
            }
            case "committee":
            {
                var c = await db.Committees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
                if (c is null) return null;
                var meetings = await db.Meetings.Where(x => x.TenantId == tenantId && x.CommitteeId == id).ToListAsync(ct);
                foreach (var m in meetings) Add(nameof(Meeting), m.Id, m.Title, "meeting");
                var ids = meetings.Select(m => m.Id).ToList();
                foreach (var d in await db.Decisions.Where(x => x.TenantId == tenantId && ids.Contains(x.MeetingId)).ToListAsync(ct))
                    Add(nameof(Decision), d.Id, d.Text, "decision");
                return related;
            }
            case "policy":
            {
                var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
                if (p is null) return null;
                foreach (var v in await db.PolicyVersions.Where(x => x.TenantId == tenantId && x.PolicyId == id).OrderBy(x => x.Version).ToListAsync(ct))
                    Add(nameof(PolicyVersion), v.Id, $"v{v.Version} {v.ChangeNote}".Trim(), "version");
                foreach (var pr in await db.Procedures.Where(x => x.TenantId == tenantId && x.PolicyId == id).ToListAsync(ct))
                    Add(nameof(Procedure), pr.Id, pr.Title, "procedure");
                return related;
            }
            case "request":
            {
                var r = await db.Requests.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
                if (r is null) return null;
                foreach (var a in await db.Approvals.Where(x => x.TenantId == tenantId && x.EntityType == nameof(Request) && x.EntityId == id).ToListAsync(ct))
                    Add(nameof(Approval), a.Id, $"Approval [{a.Status}]", "approval");
                foreach (var w in await db.WorkflowInstances.Where(x => x.TenantId == tenantId && x.EntityType == nameof(Request) && x.EntityId == id).ToListAsync(ct))
                    Add(nameof(WorkflowInstance), w.Id, $"Workflow [{w.Status}]", "workflow");
                return related;
            }
            default:
                return null;
        }
    }

    /// <summary>Everything the assistants may use about one meeting, gathered from the tenant's own records.</summary>
    private sealed record MeetingDossier(
        Meeting Meeting, Committee? Committee, List<AgendaItem> Agenda, List<(string Name, AttendanceStatus Status)> Attendance,
        List<(string Item, int For, int Against, int Abstain)> Votes, List<Decision> Decisions, List<DecisionAction> Actions, MeetingMinutes? LatestMinutes)
    {
        public static async Task<MeetingDossier?> LoadAsync(AppDbContext db, Guid tenantId, Guid meetingId, CancellationToken ct)
        {
            var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == meetingId, ct);
            if (m is null) return null;
            var committee = await db.Committees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == m.CommitteeId, ct);
            var agenda = await db.AgendaItems.Where(x => x.TenantId == tenantId && x.MeetingId == meetingId).OrderBy(x => x.Order).ToListAsync(ct);
            var attendance = await db.Attendances.Where(x => x.TenantId == tenantId && x.MeetingId == meetingId).ToListAsync(ct);
            var personIds = attendance.Select(a => a.PersonId).ToList();
            var names = await db.People.Where(p => p.TenantId == tenantId && personIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.FullName, ct);
            var votes = await db.MeetingVotes.Where(x => x.TenantId == tenantId && x.MeetingId == meetingId).ToListAsync(ct);
            var decisions = await db.Decisions.Where(x => x.TenantId == tenantId && x.MeetingId == meetingId).ToListAsync(ct);
            var decisionIds = decisions.Select(d => d.Id).ToList();
            var actions = await db.DecisionActions.Where(x => x.TenantId == tenantId && decisionIds.Contains(x.DecisionId)).ToListAsync(ct);
            var minutes = await db.MeetingMinutes.Where(x => x.TenantId == tenantId && x.MeetingId == meetingId).OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
            var tally = agenda.Select(a => (a.Title,
                votes.Count(v => v.AgendaItemId == a.Id && v.Choice == VoteChoice.InFavor),
                votes.Count(v => v.AgendaItemId == a.Id && v.Choice == VoteChoice.Against),
                votes.Count(v => v.AgendaItemId == a.Id && v.Choice == VoteChoice.Abstain)))
                .Where(t => t.Item2 + t.Item3 + t.Item4 > 0).ToList();
            return new MeetingDossier(m, committee, agenda,
                attendance.Select(a => (names.GetValueOrDefault(a.PersonId, a.PersonId.ToString()), a.Status)).ToList(),
                tally, decisions, actions, minutes);
        }

        public string ToContext()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Meeting: {Meeting.Title} ({Meeting.StartsAt:yyyy-MM-dd HH:mm} UTC, status {Meeting.Status})");
            if (Committee is not null) sb.AppendLine($"Committee: {Committee.Name}");
            sb.AppendLine("Agenda: " + (Agenda.Count == 0 ? "not recorded" : string.Join("; ", Agenda.Select(a => $"{a.Order}. {a.Title}"))));
            sb.AppendLine("Attendance: " + (Attendance.Count == 0 ? "not recorded" : string.Join("; ", Attendance.Select(a => $"{a.Name} ({a.Status})"))));
            foreach (var v in Votes) sb.AppendLine($"Vote on '{v.Item}': for {v.For}, against {v.Against}, abstain {v.Abstain}");
            foreach (var d in Decisions) sb.AppendLine($"Decision: {d.Text} [{d.Status}]");
            foreach (var a in Actions) sb.AppendLine($"Action: {a.Description}, due {a.DueAt:yyyy-MM-dd}, status {a.Status}");
            if (LatestMinutes is not null) sb.AppendLine($"Latest minutes (v{LatestMinutes.Version}, {LatestMinutes.Status}):\n{LatestMinutes.Content}");
            return sb.ToString();
        }

        public string ExtractiveSummary()
        {
            var present = Attendance.Count(a => a.Status == AttendanceStatus.Present);
            var sb = new StringBuilder();
            sb.Append($"{Meeting.Title}");
            if (Committee is not null) sb.Append($" ({Committee.Name})");
            sb.Append($" on {Meeting.StartsAt:yyyy-MM-dd}: {Agenda.Count} agenda item(s), {present} of {Attendance.Count} recorded attendee(s) present");
            sb.Append($", {Decisions.Count} decision(s) and {Actions.Count} action(s).");
            if (Decisions.Count > 0) sb.Append(" Decisions: " + string.Join("; ", Decisions.Select(d => d.Text)) + ".");
            var open = Actions.Count(a => a.Status is DecisionActionStatus.Assigned or DecisionActionStatus.InProgress);
            if (open > 0) sb.Append($" {open} action(s) still open.");
            return sb.ToString();
        }

        public string ExtractiveMinutes()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"MINUTES — {Meeting.Title}");
            if (Committee is not null) sb.AppendLine($"Committee: {Committee.Name}");
            sb.AppendLine($"Date: {Meeting.StartsAt:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine().AppendLine("Attendance");
            if (Attendance.Count == 0) sb.AppendLine("- Not recorded");
            foreach (var a in Attendance) sb.AppendLine($"- {a.Name}: {a.Status}");
            sb.AppendLine().AppendLine("Agenda and discussion");
            if (Agenda.Count == 0) sb.AppendLine("- Not recorded");
            foreach (var a in Agenda) sb.AppendLine($"{a.Order}. {a.Title}{(string.IsNullOrWhiteSpace(a.Description) ? "" : " — " + a.Description)}");
            if (Votes.Count > 0)
            {
                sb.AppendLine().AppendLine("Votes");
                foreach (var v in Votes) sb.AppendLine($"- {v.Item}: for {v.For}, against {v.Against}, abstain {v.Abstain}");
            }
            sb.AppendLine().AppendLine("Decisions");
            if (Decisions.Count == 0) sb.AppendLine("- None recorded");
            foreach (var d in Decisions) sb.AppendLine($"Decision: {d.Text}");
            sb.AppendLine().AppendLine("Actions");
            if (Actions.Count == 0) sb.AppendLine("- None recorded");
            foreach (var a in Actions) sb.AppendLine($"- {a.Description} (due {a.DueAt:yyyy-MM-dd}, {a.Status})");
            return sb.ToString().TrimEnd();
        }

        public object[] Sources() =>
            [new { type = nameof(Meeting), id = Meeting.Id }, .. Decisions.Select(d => (object)new { type = nameof(Decision), id = d.Id })];
    }
}

public sealed record Suggestion(string Type, Guid Id, string Label);
