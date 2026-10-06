using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
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
/// Wave B — BBP §7.2 Reports &amp; Dashboards, computed from operational data (no schema change).
/// Dashboards return KPI objects; tabular reports return rows and export as CSV.
/// </summary>
public static class ReportEndpoints
{
    private static readonly WorkTaskStatus[] OpenTask = [WorkTaskStatus.Open, WorkTaskStatus.InProgress, WorkTaskStatus.Breached];

    private delegate Task<List<Dictionary<string, object?>>> TabularReport(AppDbContext db, Guid tenantId, CancellationToken ct);

    private static readonly Dictionary<string, TabularReport> Tabular = new(StringComparer.OrdinalIgnoreCase)
    {
        ["approval-bottlenecks"] = ApprovalBottlenecksAsync,
        ["workflow-cycle-time"] = WorkflowCycleTimeAsync,
        ["committees"] = CommitteesAsync,
        ["decisions"] = DecisionsAsync,
        ["policies"] = PoliciesAsync,
    };

    public static void MapReportEndpoints(this IEndpointRouteBuilder app)
    {
        var dashboards = app.MapGroup("/api/dashboards").WithTags("Dashboards").RequireAuthorization();
        var reports = app.MapGroup("/api/reports").WithTags("Reports").RequireAuthorization();

        dashboards.MapGet("/executive", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("analytics:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var now = DateTimeOffset.UtcNow;
            var since = now.AddDays(-30);
            var requestsByStatus = await db.Requests.Where(r => r.TenantId == tenantId)
                .GroupBy(r => r.Status).Select(g => new { status = g.Key.ToString(), count = g.Count() }).ToListAsync(ct);
            return Results.Ok(new
            {
                generatedAt = now,
                requests = new { byStatus = requestsByStatus, createdLast30Days = await db.Requests.CountAsync(r => r.TenantId == tenantId && r.CreatedAt >= since, ct) },
                approvals = new
                {
                    pending = await db.Approvals.CountAsync(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending, ct),
                    overdue = await db.Approvals.CountAsync(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending && a.DueAt < now, ct),
                    slaCompliancePct30d = await SlaPctAsync(db, tenantId, since, now, ct),
                },
                tasks = new
                {
                    open = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && OpenTask.Contains(t.Status), ct),
                    overdue = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && OpenTask.Contains(t.Status) && t.DueAt < now, ct),
                    breached = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && t.Status == WorkTaskStatus.Breached, ct),
                },
                communicationsPublished30d = await db.Communications.CountAsync(c => c.TenantId == tenantId && c.PublishedAt >= since, ct),
                governance = await GovernanceKpisAsync(db, tenantId, now, ct),
            });
        });

        dashboards.MapGet("/department/{orgUnitId:guid}", async (AppDbContext db, HttpContext ctx, Guid orgUnitId, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("analytics:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var unit = await db.OrganizationalUnits.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.Id == orgUnitId, ct);
            if (unit is null) return Results.NotFound(new { error = "Unit not found." });
            var now = DateTimeOffset.UtcNow;
            var members = db.People.Where(p => p.TenantId == tenantId && p.DepartmentId == orgUnitId).Select(p => p.Id);
            return Results.Ok(new
            {
                unit,
                members = await members.CountAsync(ct),
                pendingApprovals = await db.Approvals.CountAsync(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending && members.Contains(a.AssigneeId), ct),
                overdueApprovals = await db.Approvals.CountAsync(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending && a.DueAt < now && members.Contains(a.AssigneeId), ct),
                openTasks = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && OpenTask.Contains(t.Status) && members.Contains(t.AssigneeId), ct),
                overdueTasks = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && OpenTask.Contains(t.Status) && t.DueAt < now && members.Contains(t.AssigneeId), ct),
                requestsSubmitted = await db.Requests.CountAsync(r => r.TenantId == tenantId && members.Contains(r.SubmitterId), ct),
            });
        });

        dashboards.MapGet("/governance", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("analytics:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await GovernanceKpisAsync(db, tenantId, DateTimeOffset.UtcNow, ct));
        });

        reports.MapGet("/my-performance", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("analytics:read") && !ctx.User.HasPermission("inbox:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (await ActingPersonAsync(db, ctx, tenantId, ct) is not { } me) return Results.Forbid();
            var decided = await db.Approvals.Where(a => a.TenantId == tenantId && a.DecidedBy == me && a.DecidedAt != null)
                .Select(a => new { a.DueAt, a.DecidedAt }).ToListAsync(ct);
            var tasks = await db.WorkTasks.Where(t => t.TenantId == tenantId && t.AssigneeId == me)
                .Select(t => new { t.Status, t.DueAt }).ToListAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var onTime = decided.Count(a => a.DecidedAt <= a.DueAt);
            return Results.Ok(new
            {
                personId = me,
                approvalsDecided = decided.Count,
                approvalsOnTimePct = decided.Count == 0 ? (double?)null : Math.Round(100.0 * onTime / decided.Count, 1),
                pendingApprovals = await db.Approvals.CountAsync(a => a.TenantId == tenantId && a.AssigneeId == me && a.Status == ApprovalStatus.Pending, ct),
                tasksCompleted = tasks.Count(t => t.Status is WorkTaskStatus.Done or WorkTaskStatus.Verified),
                tasksVerified = tasks.Count(t => t.Status == WorkTaskStatus.Verified),
                tasksOpen = tasks.Count(t => OpenTask.Contains(t.Status)),
                tasksOverdue = tasks.Count(t => OpenTask.Contains(t.Status) && t.DueAt < now),
            });
        });

        reports.MapGet("/sla-compliance", async (AppDbContext db, HttpContext ctx, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("analytics:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var start = from ?? DateTimeOffset.UtcNow.AddDays(-30);
            var end = to ?? DateTimeOffset.UtcNow;
            var rows = await db.Approvals.Where(a => a.TenantId == tenantId && a.DecidedAt != null && a.DecidedAt >= start && a.DecidedAt <= end)
                .Select(a => new { a.EntityType, a.DueAt, a.DecidedAt }).ToListAsync(ct);
            var byType = rows.GroupBy(r => r.EntityType).Select(g => new
            {
                entityType = g.Key,
                decided = g.Count(),
                onTime = g.Count(r => r.DecidedAt <= r.DueAt),
                compliancePct = Math.Round(100.0 * g.Count(r => r.DecidedAt <= r.DueAt) / g.Count(), 1),
            }).OrderBy(x => x.entityType).ToList();
            return Results.Ok(new { from = start, to = end, overallPct = await SlaPctAsync(db, tenantId, start, end, ct), byEntityType = byType });
        });

        reports.MapGet("/{code}", async (AppDbContext db, HttpContext ctx, string code, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("analytics:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            if (!Tabular.TryGetValue(code, out var report)) return Results.NotFound(new { error = $"Unknown report '{code}'.", available = Tabular.Keys });
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await report(db, tenantId, ct));
        });

        reports.MapGet("/{code}/export", async (AppDbContext db, HttpContext ctx, string code, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("analytics:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            if (!Tabular.TryGetValue(code, out var report)) return Results.NotFound(new { error = $"Unknown report '{code}'.", available = Tabular.Keys });
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var rows = await report(db, tenantId, ct);
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ToCsv(rows))).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"{code.ToLowerInvariant()}-{DateTime.UtcNow:yyyyMMdd}.csv");
        });
    }

    private static async Task<double?> SlaPctAsync(AppDbContext db, Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var decided = await db.Approvals.Where(a => a.TenantId == tenantId && a.DecidedAt != null && a.DecidedAt >= from && a.DecidedAt <= to)
            .Select(a => new { a.DueAt, a.DecidedAt }).ToListAsync(ct);
        return decided.Count == 0 ? null : Math.Round(100.0 * decided.Count(a => a.DecidedAt <= a.DueAt) / decided.Count, 1);
    }

    private static async Task<object> GovernanceKpisAsync(AppDbContext db, Guid tenantId, DateTimeOffset now, CancellationToken ct)
    {
        var actions = await db.DecisionActions.Where(a => a.TenantId == tenantId).Select(a => new { a.Status, a.DueAt }).ToListAsync(ct);
        var completed = actions.Count(a => a.Status is DecisionActionStatus.Done or DecisionActionStatus.Verified);
        var publishedPolicies = await db.Policies.Where(p => p.TenantId == tenantId && p.Status == PolicyStatus.Published).Select(p => p.Id).ToListAsync(ct);
        var activePeople = await db.People.CountAsync(p => p.TenantId == tenantId && p.IsActive, ct);
        var acks = await db.PolicyAcknowledgements.CountAsync(a => a.TenantId == tenantId && publishedPolicies.Contains(a.PolicyId), ct);
        var expectedAcks = publishedPolicies.Count * activePeople;
        return new
        {
            committees = await db.Committees.CountAsync(c => c.TenantId == tenantId && c.IsActive, ct),
            meetingsUpcoming = await db.Meetings.CountAsync(m => m.TenantId == tenantId && m.Status == MeetingStatus.Scheduled && m.StartsAt >= now, ct),
            meetingsConcluded = await db.Meetings.CountAsync(m => m.TenantId == tenantId && m.Status == MeetingStatus.Concluded, ct),
            decisions = await db.Decisions.CountAsync(d => d.TenantId == tenantId, ct),
            decisionActions = actions.Count,
            decisionActionsCompleted = completed,
            decisionActionsOverdue = actions.Count(a => a.Status is DecisionActionStatus.Assigned or DecisionActionStatus.InProgress && a.DueAt < now),
            implementationRatePct = actions.Count == 0 ? (double?)null : Math.Round(100.0 * completed / actions.Count, 1),
            policiesPublished = publishedPolicies.Count,
            policyAcknowledgementPct = expectedAcks == 0 ? (double?)null : Math.Round(100.0 * acks / expectedAcks, 1),
        };
    }

    private static async Task<List<Dictionary<string, object?>>> ApprovalBottlenecksAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = await db.Approvals.Where(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending)
            .Select(a => new { a.AssigneeId, a.DueAt }).ToListAsync(ct);
        var ids = pending.Select(p => p.AssigneeId).Distinct().ToList();
        var names = await db.People.Where(p => p.TenantId == tenantId && ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.FullName, ct);
        return pending.GroupBy(p => p.AssigneeId)
            .Select(g => new Dictionary<string, object?>
            {
                ["assigneeId"] = g.Key,
                ["assignee"] = names.GetValueOrDefault(g.Key),
                ["pending"] = g.Count(),
                ["overdue"] = g.Count(x => x.DueAt < now),
                ["oldestDueAt"] = g.Min(x => x.DueAt),
            })
            .OrderByDescending(r => (int)r["overdue"]!).ThenByDescending(r => (int)r["pending"]!).ToList();
    }

    private static async Task<List<Dictionary<string, object?>>> WorkflowCycleTimeAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var finished = await db.Requests.Where(r => r.TenantId == tenantId && r.SubmittedAt != null &&
                (r.Status == RequestStatus.Approved || r.Status == RequestStatus.Rejected || r.Status == RequestStatus.Closed))
            .Select(r => new { r.Id, r.Category, r.SubmittedAt }).ToListAsync(ct);
        var ids = finished.Select(r => r.Id).ToList();
        var lastDecision = await db.Approvals.Where(a => a.TenantId == tenantId && a.EntityType == nameof(Request) && ids.Contains(a.EntityId) && a.DecidedAt != null)
            .GroupBy(a => a.EntityId).Select(g => new { id = g.Key, at = g.Max(a => a.DecidedAt) }).ToDictionaryAsync(x => x.id, x => x.at, ct);
        return finished.Where(r => lastDecision.ContainsKey(r.Id))
            .Select(r => new { r.Category, hours = (lastDecision[r.Id]!.Value - r.SubmittedAt!.Value).TotalHours })
            .GroupBy(r => r.Category)
            .Select(g =>
            {
                var hours = g.Select(x => x.hours).OrderBy(h => h).ToList();
                var median = hours.Count % 2 == 1 ? hours[hours.Count / 2] : (hours[hours.Count / 2 - 1] + hours[hours.Count / 2]) / 2;
                return new Dictionary<string, object?>
                {
                    ["category"] = g.Key.ToString(),
                    ["requests"] = hours.Count,
                    ["avgHours"] = Math.Round(hours.Average(), 2),
                    ["medianHours"] = Math.Round(median, 2),
                    ["maxHours"] = Math.Round(hours.Max(), 2),
                };
            })
            .OrderBy(r => (string)r["category"]!).ToList();
    }

    private static async Task<List<Dictionary<string, object?>>> CommitteesAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var committees = await db.Committees.Where(c => c.TenantId == tenantId).OrderBy(c => c.Code).ToListAsync(ct);
        var members = await db.CommitteeMembers.Where(m => m.TenantId == tenantId).GroupBy(m => m.CommitteeId)
            .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var meetings = await db.Meetings.Where(m => m.TenantId == tenantId).Select(m => new { m.Id, m.CommitteeId, m.Status }).ToListAsync(ct);
        var meetingIds = meetings.Select(m => m.Id).ToList();
        var decisionsByMeeting = await db.Decisions.Where(d => d.TenantId == tenantId && meetingIds.Contains(d.MeetingId))
            .GroupBy(d => d.MeetingId).Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        return committees.Select(c =>
        {
            var mine = meetings.Where(m => m.CommitteeId == c.Id).ToList();
            return new Dictionary<string, object?>
            {
                ["code"] = c.Code,
                ["name"] = c.Name,
                ["active"] = c.IsActive,
                ["members"] = members.GetValueOrDefault(c.Id),
                ["meetings"] = mine.Count,
                ["meetingsConcluded"] = mine.Count(m => m.Status == MeetingStatus.Concluded),
                ["decisions"] = mine.Sum(m => decisionsByMeeting.GetValueOrDefault(m.Id)),
            };
        }).ToList();
    }

    private static async Task<List<Dictionary<string, object?>>> DecisionsAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var decisions = await db.Decisions.Where(d => d.TenantId == tenantId).OrderByDescending(d => d.PublishedAt).ToListAsync(ct);
        var actions = await db.DecisionActions.Where(a => a.TenantId == tenantId).ToListAsync(ct);
        return decisions.Select(d =>
        {
            var mine = actions.Where(a => a.DecisionId == d.Id).ToList();
            var done = mine.Count(a => a.Status is DecisionActionStatus.Done or DecisionActionStatus.Verified);
            return new Dictionary<string, object?>
            {
                ["decisionId"] = d.Id,
                ["text"] = d.Text,
                ["status"] = d.Status.ToString(),
                ["publishedAt"] = d.PublishedAt,
                ["actions"] = mine.Count,
                ["actionsDone"] = done,
                ["actionsOverdue"] = mine.Count(a => a.Status is DecisionActionStatus.Assigned or DecisionActionStatus.InProgress && a.DueAt < now),
                ["implementationPct"] = mine.Count == 0 ? null : Math.Round(100.0 * done / mine.Count, 1),
            };
        }).ToList();
    }

    private static async Task<List<Dictionary<string, object?>>> PoliciesAsync(AppDbContext db, Guid tenantId, CancellationToken ct)
    {
        var policies = await db.Policies.Where(p => p.TenantId == tenantId).OrderBy(p => p.Code).ToListAsync(ct);
        var acks = await db.PolicyAcknowledgements.Where(a => a.TenantId == tenantId).GroupBy(a => a.PolicyId)
            .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
        var activePeople = await db.People.CountAsync(p => p.TenantId == tenantId && p.IsActive, ct);
        return policies.Select(p => new Dictionary<string, object?>
        {
            ["code"] = p.Code,
            ["title"] = p.Title,
            ["status"] = p.Status.ToString(),
            ["version"] = p.Version,
            ["acknowledgements"] = acks.GetValueOrDefault(p.Id),
            ["acknowledgementPct"] = p.Status != PolicyStatus.Published || activePeople == 0
                ? null : Math.Round(100.0 * acks.GetValueOrDefault(p.Id) / activePeople, 1),
        }).ToList();
    }

    internal static string ToCsv(List<Dictionary<string, object?>> rows)
    {
        if (rows.Count == 0) return "";
        var headers = rows[0].Keys.ToList();
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", headers.Select(Escape)));
        foreach (var row in rows)
            sb.AppendLine(string.Join(",", headers.Select(h => Escape(Format(row.GetValueOrDefault(h))))));
        return sb.ToString();

        static string Format(object? v) => v switch
        {
            null => "",
            DateTimeOffset d => d.ToString("O", CultureInfo.InvariantCulture),
            IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
            _ => v.ToString() ?? "",
        };
        // Quote when needed; prefix formula-leading cells so spreadsheets don't execute them.
        static string Escape(string s)
        {
            if (s.Length > 0 && "=+-@".Contains(s[0]) && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) s = "'" + s;
            return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }
    }
}
