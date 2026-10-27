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

public sealed record DeclareRecusalReq(Guid TenantId, Guid? PersonId, string Reason);
public sealed record DeclareInterestsReq(Guid TenantId, string Period, bool NoInterests, string? Interests);
public sealed record CreateResolutionReq(Guid TenantId, Guid CommitteeId, string Title, string Text, DateTimeOffset ClosesAt, bool RequiresUnanimous = false);
public sealed record ResolutionVoteReq(Guid TenantId, string Choice, string? Remarks);
public sealed record ResolutionRecuseReq(Guid TenantId, string Reason);

/// <summary>
/// Wave J: board integrity. Votes tied to members present and not conflicted; per-committee quorum rules;
/// conflict-of-interest recusals and the interest register; written resolutions by circulation.
/// </summary>
public static class BoardEndpoints
{
    public sealed record Quorum(List<CommitteeMember> Members, List<Attendance> Attendances, HashSet<Guid> Present,
        int TotalMembers, int PresentCount, int AbsentCount, int ExcusedCount, int QuorumRequired, bool HasQuorum, string QuorumRule, double? Percent);

    /// <summary>Quorum counts only members whose term covers the meeting, and only their attendance.</summary>
    public static async Task<Quorum> QuorumAsync(AppDbContext db, Guid tenantId, Meeting m, CancellationToken ct)
    {
        var committee = await db.Committees.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == m.CommitteeId, ct);
        var members = (await db.CommitteeMembers.Where(cm => cm.TenantId == tenantId && cm.CommitteeId == m.CommitteeId).ToListAsync(ct))
            .Where(cm => BoardRules.IsCurrent(cm, m.StartsAt)).ToList();
        var ids = members.Select(x => x.PersonId).ToHashSet();
        var attendances = await db.Attendances.Where(a => a.TenantId == tenantId && a.MeetingId == m.Id).ToListAsync(ct);
        var mine = attendances.Where(a => ids.Contains(a.PersonId)).ToList();
        var present = mine.Where(a => a.Status == AttendanceStatus.Present).Select(a => a.PersonId).ToHashSet();
        var required = BoardRules.QuorumRequired(members.Count, committee?.QuorumPercent);
        var rule = committee?.QuorumPercent is { } p ? $"at least {p:0.##}% of members" : "majority of members";
        return new Quorum(members, attendances, present, members.Count, present.Count,
            mine.Count(a => a.Status == AttendanceStatus.Absent), mine.Count(a => a.Status == AttendanceStatus.Excused),
            required, members.Count > 0 && present.Count >= required, rule, committee?.QuorumPercent);
    }

    /// <summary>
    /// A member votes for themselves; the secretary (meeting:create) may record a vote cast in the room for a member.
    /// Either way the voter must be a current member, marked present, and not recused on the item.
    /// </summary>
    public static async Task<IResult> CastMeetingVoteAsync(AppDbContext db, HttpContext ctx, Guid id, CastMeetingVoteReq req, CancellationToken ct)
    {
        if (!ctx.User.HasPermission("meeting:read") && !ctx.User.HasPermission("meeting:create")) return Results.Forbid();
        if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
        if (!Enum.TryParse<VoteChoice>(req.Choice, true, out var choice))
            return Results.BadRequest(new { error = "Choice must be InFavor|Against|Abstain." });
        await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
        var meeting = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
        if (meeting is null) return Results.NotFound(new { error = "Meeting not found." });
        if (meeting.Status == MeetingStatus.Cancelled) return Results.Conflict(new { error = "The meeting was cancelled." });
        var item = await db.AgendaItems.FirstOrDefaultAsync(a => a.TenantId == req.TenantId && a.MeetingId == id && a.Id == req.AgendaItemId, ct);
        if (item is null) return Results.NotFound(new { error = "Agenda item not found for meeting." });

        var actor = await ActingPersonAsync(db, ctx, req.TenantId, ct);
        var voter = req.PersonId ?? actor;
        if (voter is null) return Results.Json(new { error = "Your account is not linked to a person, so you cannot vote." }, statusCode: StatusCodes.Status403Forbidden);
        var recorded = voter != actor;
        if (recorded && !ctx.User.HasPermission("meeting:create"))
            return Results.Json(new { error = "Only the meeting secretary can record a vote for another member." }, statusCode: StatusCodes.Status403Forbidden);

        var q = await QuorumAsync(db, req.TenantId, meeting, ct);
        if (q.Members.All(m => m.PersonId != voter)) return Results.Json(new { error = "Only current members of the committee can vote." }, statusCode: StatusCodes.Status403Forbidden);
        if (!q.Present.Contains(voter.Value)) return Results.Conflict(new { error = "The member must be marked present to vote." });
        if (await db.AgendaRecusals.AnyAsync(r => r.TenantId == req.TenantId && r.AgendaItemId == item.Id && r.PersonId == voter, ct))
            return Results.Conflict(new { error = "The member declared a conflict of interest on this item and cannot vote." });
        if (await db.MeetingVotes.AnyAsync(v => v.TenantId == req.TenantId && v.AgendaItemId == item.Id && v.PersonId == voter, ct))
            return Results.Conflict(new { error = "This person has already voted on this agenda item." });

        var vote = new MeetingVote(Guid.NewGuid(), req.TenantId, id, item.Id, voter.Value, choice, DateTimeOffset.UtcNow, req.Remarks?.Trim(),
            recorded ? SubjectOf(ctx) : null);
        db.MeetingVotes.Add(vote);
        DomainEvents.Record(db, req.TenantId, "MeetingVoteCast", "MeetingVoteCast", nameof(MeetingVote), vote.Id.ToString(),
            payload: new { tenantId = req.TenantId, meetingId = id, agendaItemId = item.Id, choice = choice.ToString(), recorded },
            actorId: actor, details: choice.ToString());
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueConflict(ex)) { return Results.Conflict(new { error = "This person has already voted on this agenda item." }); }
        await scope.CommitAsync(ct);
        return Results.Ok(vote);
    }

    public static void MapBoardEndpoints(this IEndpointRouteBuilder app)
    {
        var meetings = app.MapGroup("/api/meetings").WithTags("Meetings").RequireAuthorization();

        // Per-item result: who could vote, the tally, and whether it carried.
        meetings.MapGet("/{id:guid}/results", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (m is null) return Results.NotFound(new { error = "Meeting not found." });
            var q = await QuorumAsync(db, tenantId, m, ct);
            var items = await db.AgendaItems.Where(a => a.TenantId == tenantId && a.MeetingId == id).OrderBy(a => a.Order).ToListAsync(ct);
            var votes = await db.MeetingVotes.Where(v => v.TenantId == tenantId && v.MeetingId == id).ToListAsync(ct);
            var recusals = await db.AgendaRecusals.Where(r => r.TenantId == tenantId && r.MeetingId == id).ToListAsync(ct);
            return Results.Ok(new
            {
                meetingId = id, q.TotalMembers, q.PresentCount, q.QuorumRequired, q.HasQuorum, q.QuorumRule,
                items = items.Select(i =>
                {
                    var recused = recusals.Where(r => r.AgendaItemId == i.Id).Select(r => r.PersonId).ToHashSet();
                    // Conflicted members leave the room: they are not counted towards this item's quorum.
                    var eligibleMembers = q.Members.Count(x => !recused.Contains(x.PersonId));
                    var eligiblePresent = q.Present.Count(p => !recused.Contains(p));
                    var required = BoardRules.QuorumRequired(eligibleMembers, q.Percent);
                    var iv = votes.Where(v => v.AgendaItemId == i.Id).ToList();
                    int fv = iv.Count(v => v.Choice == VoteChoice.InFavor), av = iv.Count(v => v.Choice == VoteChoice.Against), ab = iv.Count(v => v.Choice == VoteChoice.Abstain);
                    var quorate = eligibleMembers > 0 && eligiblePresent >= required;
                    return new
                    {
                        agendaItemId = i.Id, i.Order, i.Title, inFavor = fv, against = av, abstain = ab,
                        recused = recused.Count, eligiblePresent, quorumRequired = required, quorate,
                        notVoted = q.Present.Count(p => !recused.Contains(p) && iv.All(v => v.PersonId != p)),
                        outcome = BoardRules.Outcome(fv, av, ab, quorate).ToString(),
                    };
                }),
            });
        });

        // Declare a conflict of interest on an item: for yourself, or recorded by the secretary.
        meetings.MapPost("/{id:guid}/agenda/{itemId:guid}/recusals", async (AppDbContext db, HttpContext ctx, Guid id, Guid itemId, DeclareRecusalReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read") && !ctx.User.HasPermission("meeting:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Reason)) return Results.BadRequest(new { error = "State the nature of the interest." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            var item = await db.AgendaItems.FirstOrDefaultAsync(a => a.TenantId == req.TenantId && a.MeetingId == id && a.Id == itemId, ct);
            if (m is null || item is null) return Results.NotFound(new { error = "Agenda item not found for meeting." });
            var actor = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            var who = req.PersonId ?? actor;
            if (who is null) return Results.Json(new { error = "Your account is not linked to a person." }, statusCode: StatusCodes.Status403Forbidden);
            if (who != actor && !ctx.User.HasPermission("meeting:create"))
                return Results.Json(new { error = "Only the meeting secretary can record a declaration for another member." }, statusCode: StatusCodes.Status403Forbidden);
            var q = await QuorumAsync(db, req.TenantId, m, ct);
            if (q.Members.All(x => x.PersonId != who)) return Results.NotFound(new { error = "Not a current member of the committee." });
            if (await db.MeetingVotes.AnyAsync(v => v.TenantId == req.TenantId && v.AgendaItemId == itemId && v.PersonId == who, ct))
                return Results.Conflict(new { error = "The member has already voted on this item; declare interests before voting." });
            if (await db.AgendaRecusals.AnyAsync(r => r.TenantId == req.TenantId && r.AgendaItemId == itemId && r.PersonId == who, ct))
                return Results.Conflict(new { error = "Already declared." });
            var rec = new AgendaRecusal(Guid.NewGuid(), req.TenantId, id, itemId, who.Value, req.Reason.Trim(), SubjectOf(ctx), DateTimeOffset.UtcNow);
            db.AgendaRecusals.Add(rec);
            DomainEvents.Record(db, req.TenantId, "ConflictDeclared", "ConflictDeclared", nameof(AgendaItem), itemId.ToString(),
                payload: new { tenantId = req.TenantId, meetingId = id, personId = who }, actorId: actor, details: rec.Reason);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/meetings/{id}/agenda/{itemId}/recusals/{rec.Id}", rec);
        });

        meetings.MapGet("/{id:guid}/recusals", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.AgendaRecusals.Where(r => r.TenantId == tenantId && r.MeetingId == id).OrderBy(r => r.DeclaredAt).ToListAsync(ct));
        });

        // --- Interest register ---
        var interests = app.MapGroup("/api/governance/interests").WithTags("Governance").RequireAuthorization();

        interests.MapPost("/", async (AppDbContext db, HttpContext ctx, DeclareInterestsReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("committee:read") && !ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            var period = req.Period?.Trim() ?? "";
            if (period.Length is 0 or > 20) return Results.BadRequest(new { error = "Period (e.g. 2026 or 1447) is required." });
            if (!req.NoInterests && string.IsNullOrWhiteSpace(req.Interests))
                return Results.BadRequest(new { error = "List your interests, or declare that you have none." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var actor = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            if (actor is null) return Results.Json(new { error = "Your account is not linked to a person." }, statusCode: StatusCodes.Status403Forbidden);
            var d = new InterestDeclaration(Guid.NewGuid(), req.TenantId, actor.Value, period, req.NoInterests,
                req.NoInterests ? null : req.Interests!.Trim(), DateTimeOffset.UtcNow);
            db.InterestDeclarations.Add(d);
            DomainEvents.Record(db, req.TenantId, "InterestsDeclared", "InterestsDeclared", nameof(Person), actor.Value.ToString(),
                payload: new { tenantId = req.TenantId, period }, actorId: actor, details: period);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/governance/interests/{d.Id}", d);
        });

        // Register for a committee and period: each current member's latest declaration, or missing.
        interests.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid committeeId, string period, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("committee:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var now = DateTimeOffset.UtcNow;
            var members = (await db.CommitteeMembers.Where(m => m.TenantId == tenantId && m.CommitteeId == committeeId).ToListAsync(ct))
                .Where(m => BoardRules.IsCurrent(m, now)).ToList();
            var pids = members.Select(m => m.PersonId).ToList();
            var people = await db.People.Where(p => p.TenantId == tenantId && pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.FullName, ct);
            var decl = await db.InterestDeclarations.Where(d => d.TenantId == tenantId && d.Period == period && pids.Contains(d.PersonId)).ToListAsync(ct);
            var rows = members.Select(m =>
            {
                var latest = decl.Where(d => d.PersonId == m.PersonId).MaxBy(d => d.DeclaredAt);
                return new { m.PersonId, name = people.GetValueOrDefault(m.PersonId), m.Role, declared = latest is not null, latest?.NoInterests, latest?.Interests, latest?.DeclaredAt };
            }).OrderBy(r => r.declared).ThenBy(r => r.name).ToList();
            return Results.Ok(new { committeeId, period, members = rows.Count, declared = rows.Count(r => r.declared), rows });
        });

        // --- Written resolutions (by circulation) ---
        var res = app.MapGroup("/api/resolutions").WithTags("Governance").RequireAuthorization();

        res.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateResolutionReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Text))
                return Results.BadRequest(new { error = "Title and resolution text are required." });
            var now = DateTimeOffset.UtcNow;
            if (req.ClosesAt <= now.AddMinutes(30) || req.ClosesAt > now.AddDays(60))
                return Results.BadRequest(new { error = "The voting window must close between 30 minutes and 60 days from now." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Committees.AnyAsync(c => c.TenantId == req.TenantId && c.Id == req.CommitteeId, ct))
                return Results.NotFound(new { error = "Committee not found." });
            var members = (await db.CommitteeMembers.Where(m => m.TenantId == req.TenantId && m.CommitteeId == req.CommitteeId).ToListAsync(ct))
                .Where(m => BoardRules.IsCurrent(m, now)).ToList();
            if (members.Count == 0) return Results.Conflict(new { error = "The committee has no current members." });
            var r = new CircularResolution(Guid.NewGuid(), req.TenantId, req.CommitteeId, req.Title.Trim(), req.Text.Trim(), req.RequiresUnanimous,
                ResolutionStatus.Open, SubjectOf(ctx), now, req.ClosesAt, null, null);
            db.CircularResolutions.Add(r);
            foreach (var m in members)
                db.Notifications.Add(new Notification(Guid.NewGuid(), req.TenantId, m.PersonId, $"Written resolution for your vote: {r.Title}",
                    $"Please vote by {r.ClosesAt:yyyy-MM-dd HH:mm} UTC.", NotificationChannel.InApp, NotificationStatus.Sent, now, NotificationPriority.Important));
            DomainEvents.Record(db, req.TenantId, "ResolutionCirculated", "ResolutionCirculated", nameof(CircularResolution), r.Id.ToString(),
                payload: new { tenantId = req.TenantId, committeeId = req.CommitteeId, members = members.Count }, details: r.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/resolutions/{r.Id}", r);
        });

        res.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? committeeId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.CircularResolutions.Where(r => r.TenantId == tenantId);
            if (committeeId is { } cid) q = q.Where(r => r.CommitteeId == cid);
            var list = await q.OrderByDescending(r => r.CreatedAt).Take(200).ToListAsync(ct);
            var result = new List<object>();
            foreach (var r in list) result.Add(await ViewAsync(db, r, persist: true, ct));
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(result);
        });

        res.MapGet("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var r = await db.CircularResolutions.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (r is null) return Results.NotFound(new { error = "Resolution not found." });
            var view = await ViewAsync(db, r, persist: true, ct);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(view);
        });

        res.MapPost("/{id:guid}/vote", (AppDbContext db, HttpContext ctx, Guid id, ResolutionVoteReq req, CancellationToken ct) =>
            RespondAsync(db, ctx, id, req.TenantId, req.Choice, req.Remarks, recuse: false, ct));
        res.MapPost("/{id:guid}/recuse", (AppDbContext db, HttpContext ctx, Guid id, ResolutionRecuseReq req, CancellationToken ct) =>
            RespondAsync(db, ctx, id, req.TenantId, null, req.Reason, recuse: true, ct));

        res.MapPost("/{id:guid}/withdraw", async (AppDbContext db, HttpContext ctx, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var r = await db.CircularResolutions.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (r is null) return Results.NotFound(new { error = "Resolution not found." });
            if (r.Status != ResolutionStatus.Open) return Results.Conflict(new { error = $"The resolution is already {r.Status}." });
            db.Entry(r).CurrentValues.SetValues(r with { Status = ResolutionStatus.Withdrawn, ClosedAt = DateTimeOffset.UtcNow, Outcome = "Withdrawn by the secretary." });
            DomainEvents.Record(db, req.TenantId, "ResolutionWithdrawn", "ResolutionWithdrawn", nameof(CircularResolution), id.ToString(), details: r.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(r with { Status = ResolutionStatus.Withdrawn });
        });

        // Certified record of the resolution and every member's response (printable).
        res.MapGet("/{id:guid}/certificate", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var r = await db.CircularResolutions.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (r is null) return Results.NotFound(new { error = "Resolution not found." });
            await ViewAsync(db, r, persist: true, ct);
            if (r.Status is ResolutionStatus.Open) return Results.Conflict(new { error = "The resolution is still open." });
            var committee = await db.Committees.AsNoTracking().FirstAsync(c => c.Id == r.CommitteeId, ct);
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct);
            var votes = await db.ResolutionVotes.Where(v => v.TenantId == tenantId && v.ResolutionId == id).OrderBy(v => v.CastAt).ToListAsync(ct);
            var pids = votes.Select(v => v.PersonId).ToList();
            var names = await db.People.Where(p => p.TenantId == tenantId && pids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.FullName, ct);
            static string H(string? s) => WebUtility.HtmlEncode(s ?? "");
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>Written resolution</title><style>body{font-family:system-ui,sans-serif;max-width:800px;margin:2rem auto;padding:0 1rem;line-height:1.5}table{border-collapse:collapse;width:100%}td,th{border:1px solid #cbd5e1;padding:.4rem;text-align:start}</style></head><body>");
            sb.Append($"<h1>{H(tenant?.Name)}</h1><h2>{H(committee.Name)} — written resolution</h2><h3>{H(r.Title)}</h3><p>{H(r.Text)}</p>");
            sb.Append($"<p><strong>Result:</strong> {H(r.Status.ToString())}. {H(r.Outcome)}</p>");
            sb.Append($"<p>Circulated {r.CreatedAt:yyyy-MM-dd HH:mm} UTC ({H(HijriDate.Format(r.CreatedAt))} AH); closed {r.ClosedAt:yyyy-MM-dd HH:mm} UTC. Rule: {(r.RequiresUnanimous ? "unanimous consent" : "majority of all eligible members")}.</p>");
            sb.Append("<table><tr><th>Member</th><th>Response</th><th>When (UTC)</th><th>Remarks</th></tr>");
            foreach (var v in votes)
                sb.Append($"<tr><td>{H(names.GetValueOrDefault(v.PersonId) ?? v.PersonId.ToString())}</td><td>{H(v.Recused ? "Recused (conflict of interest)" : v.Choice.ToString())}</td><td>{v.CastAt:yyyy-MM-dd HH:mm}</td><td>{H(v.Remarks)}</td></tr>");
            sb.Append("</table><p style=\"color:#64748b;font-size:.85rem\">Generated by EduNexus from the audit-logged voting record.</p></body></html>");
            DomainEvents.Record(db, tenantId, "ResolutionCertified", "ResolutionCertified", nameof(CircularResolution), id.ToString(), details: r.Status.ToString());
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/html; charset=utf-8", $"resolution-{id.ToString("N")[..8]}.html");
        });
    }

    private static async Task<IResult> RespondAsync(AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, string? choiceText, string? remarks, bool recuse, CancellationToken ct)
    {
        if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
        if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
        VoteChoice? choice = null;
        if (!recuse)
        {
            if (!Enum.TryParse<VoteChoice>(choiceText, true, out var c)) return Results.BadRequest(new { error = "Choice must be InFavor|Against|Abstain." });
            choice = c;
        }
        else if (string.IsNullOrWhiteSpace(remarks)) return Results.BadRequest(new { error = "State the nature of the interest." });
        await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
        var r = await db.CircularResolutions.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (r is null) return Results.NotFound(new { error = "Resolution not found." });
        await ViewAsync(db, r, persist: true, ct);
        if (r.Status != ResolutionStatus.Open)
        {
            await db.SaveChangesAsync(ct); await scope.CommitAsync(ct);
            return Results.Conflict(new { error = "Voting on this resolution has closed." });
        }
        // Members respond for themselves only; written consent is personal.
        var actor = await ActingPersonAsync(db, ctx, tenantId, ct);
        if (actor is null) return Results.Json(new { error = "Your account is not linked to a person." }, statusCode: StatusCodes.Status403Forbidden);
        var now = DateTimeOffset.UtcNow;
        var isMember = (await db.CommitteeMembers.Where(m => m.TenantId == tenantId && m.CommitteeId == r.CommitteeId && m.PersonId == actor).ToListAsync(ct))
            .Any(m => BoardRules.IsCurrent(m, now));
        if (!isMember) return Results.Json(new { error = "Only current members of the committee can respond." }, statusCode: StatusCodes.Status403Forbidden);
        if (await db.ResolutionVotes.AnyAsync(v => v.TenantId == tenantId && v.ResolutionId == id && v.PersonId == actor, ct))
            return Results.Conflict(new { error = "You have already responded to this resolution." });
        db.ResolutionVotes.Add(new ResolutionVote(Guid.NewGuid(), tenantId, id, actor.Value, choice, recuse, remarks?.Trim(), now));
        DomainEvents.Record(db, tenantId, recuse ? "ResolutionRecused" : "ResolutionVoteCast", recuse ? "ResolutionRecused" : "ResolutionVoteCast",
            nameof(CircularResolution), id.ToString(), actorId: actor, details: recuse ? "recused" : choice.ToString());
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsUniqueConflict(ex)) { return Results.Conflict(new { error = "You have already responded to this resolution." }); }
        var view = await ViewAsync(db, r, persist: true, ct);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return Results.Ok(view);
    }

    /// <summary>Tally a resolution and, if the result is now certain or the window has closed, record the outcome.</summary>
    private static async Task<object> ViewAsync(AppDbContext db, CircularResolution r, bool persist, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var members = (await db.CommitteeMembers.Where(m => m.TenantId == r.TenantId && m.CommitteeId == r.CommitteeId).ToListAsync(ct))
            .Where(m => BoardRules.IsCurrent(m, r.ClosedAt ?? (now < r.ClosesAt ? now : r.ClosesAt))).Select(m => m.PersonId).ToHashSet();
        var votes = await db.ResolutionVotes.Where(v => v.TenantId == r.TenantId && v.ResolutionId == r.Id).ToListAsync(ct);
        var recused = votes.Count(v => v.Recused && members.Contains(v.PersonId));
        var counted = votes.Where(v => !v.Recused && members.Contains(v.PersonId)).ToList();
        int inFavor = counted.Count(v => v.Choice == VoteChoice.InFavor), against = counted.Count(v => v.Choice == VoteChoice.Against), abstain = counted.Count(v => v.Choice == VoteChoice.Abstain);
        var eligible = members.Count - recused;
        var status = r.Status;
        if (status == ResolutionStatus.Open)
        {
            var decided = BoardRules.Decide(eligible, inFavor, against + abstain, r.RequiresUnanimous, closed: now >= r.ClosesAt);
            if (decided != ResolutionStatus.Open && persist)
            {
                var outcome = $"{inFavor} in favour, {against} against, {abstain} abstaining, {recused} recused, of {members.Count} members.";
                db.Entry(r).CurrentValues.SetValues(r with { Status = decided, ClosedAt = now, Outcome = outcome });
                DomainEvents.Record(db, r.TenantId, "Resolution" + decided, "Resolution" + decided, nameof(CircularResolution), r.Id.ToString(), details: outcome);
                status = decided;
            }
        }
        return new
        {
            r.Id, r.CommitteeId, r.Title, r.Text, r.RequiresUnanimous, status = status.ToString(), r.CreatedAt, r.ClosesAt, r.ClosedAt, r.Outcome,
            members = members.Count, eligible, inFavor, against, abstain, recused, awaiting = eligible - counted.Count,
            needed = r.RequiresUnanimous ? eligible : eligible / 2 + 1,
            responded = votes.Select(v => v.PersonId).ToList(),
        };
    }
}
