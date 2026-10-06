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
/// Wave C — R0.2 governance depth (BBP §7.2): institutional calendar, committee terms, meeting agenda,
/// check-in and versioned minutes, decision lifecycle with evidence and verification, policy
/// versions/lifecycle/reviews, procedures, notification preferences and SLA policies.
/// </summary>
public static class GovernanceDepthEndpoints
{
    private static readonly Dictionary<PolicyStatus, PolicyStatus[]> PolicyFlow = new()
    {
        [PolicyStatus.Draft] = [PolicyStatus.Review],
        [PolicyStatus.Review] = [PolicyStatus.LegalReview, PolicyStatus.Draft],
        [PolicyStatus.LegalReview] = [PolicyStatus.Approval, PolicyStatus.Review],
        [PolicyStatus.Approval] = [PolicyStatus.Published, PolicyStatus.Review],
        [PolicyStatus.Published] = [PolicyStatus.Review],
    };

    private static readonly Dictionary<DecisionStatus, DecisionStatus> DecisionFlow = new()
    {
        [DecisionStatus.Published] = DecisionStatus.Implemented,
        [DecisionStatus.Implemented] = DecisionStatus.Verified,
        [DecisionStatus.Verified] = DecisionStatus.Closed,
    };

    public static void MapGovernanceDepthEndpoints(this IEndpointRouteBuilder app)
    {
        MapCalendar(app);
        MapCommittees(app);
        MapMeetings(app);
        MapDecisions(app);
        MapPolicies(app);
        MapPreferencesAndSla(app);
    }

    // ------------------------------------------------------------------ Calendar
    private static void MapCalendar(IEndpointRouteBuilder app)
    {
        var cal = app.MapGroup("/api/calendar").WithTags("Calendar").RequireAuthorization();

        cal.MapGet("/events", async (AppDbContext db, HttpContext ctx, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("calendar:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var (start, end) = Window(from, to);
            return Results.Ok(await FeedAsync(db, tenantId, start, end, ct));
        });

        cal.MapPost("/events", async (AppDbContext db, HttpContext ctx, CalendarEventReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("calendar:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (Validate(req) is { } bad) return bad;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var ev = new CalendarEvent(Guid.NewGuid(), req.TenantId, req.Title!.Trim(), Enum.Parse<CalendarEventKind>(req.Kind!, true),
                req.StartsAt!.Value, req.EndsAt!.Value, req.Location?.Trim(), null, null, await ActingPersonAsync(db, ctx, req.TenantId, ct), DateTimeOffset.UtcNow);
            db.CalendarEvents.Add(ev);
            DomainEvents.Record(db, req.TenantId, "CalendarEventCreated", "CalendarEventCreated", nameof(CalendarEvent), ev.Id.ToString(), details: ev.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/calendar/events/{ev.Id}", ev);
        });

        cal.MapPatch("/events/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, CalendarEventReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("calendar:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var ev = await db.CalendarEvents.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (ev is null) return Results.NotFound(new { error = "Event not found." });
            var kind = ev.Kind;
            if (req.Kind is not null && !Enum.TryParse(req.Kind, true, out kind)) return Results.BadRequest(new { error = "Unknown kind." });
            var updated = ev with
            {
                Title = string.IsNullOrWhiteSpace(req.Title) ? ev.Title : req.Title.Trim(),
                Kind = kind,
                StartsAt = req.StartsAt ?? ev.StartsAt,
                EndsAt = req.EndsAt ?? ev.EndsAt,
                Location = req.Location ?? ev.Location,
            };
            if (updated.EndsAt < updated.StartsAt) return Results.BadRequest(new { error = "endsAt must not be before startsAt." });
            db.Entry(ev).CurrentValues.SetValues(updated);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        });

        cal.MapDelete("/events/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("calendar:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var ev = await db.CalendarEvents.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (ev is null) return Results.NotFound(new { error = "Event not found." });
            db.CalendarEvents.Remove(ev);
            DomainEvents.Record(db, tenantId, "CalendarEventDeleted", "CalendarEventDeleted", nameof(CalendarEvent), id.ToString(), details: ev.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.NoContent();
        });

        cal.MapGet("/export.ics", async (AppDbContext db, HttpContext ctx, Guid tenantId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("calendar:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var (start, end) = Window(from, to);
            var items = await FeedAsync(db, tenantId, start, end, ct);
            return Results.Text(ToIcs(items), "text/calendar; charset=utf-8");
        });
    }

    private static (DateTimeOffset, DateTimeOffset) Window(DateTimeOffset? from, DateTimeOffset? to)
    {
        var start = from ?? DateTimeOffset.UtcNow.AddDays(-7);
        var end = to ?? start.AddDays(90);
        return (start, end);
    }

    private static IResult? Validate(CalendarEventReq req)
    {
        if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "Title required." });
        if (!Enum.TryParse<CalendarEventKind>(req.Kind, true, out _))
            return Results.BadRequest(new { error = "Kind must be Meeting|Deadline|PolicyReview|Exam|Training|Holiday|Other." });
        if (req.StartsAt is null || req.EndsAt is null || req.EndsAt < req.StartsAt)
            return Results.BadRequest(new { error = "startsAt and endsAt required; endsAt must not be before startsAt." });
        return null;
    }

    /// <summary>Stored events plus projected meetings, policy reviews and decision-action deadlines.</summary>
    private static async Task<List<CalendarItem>> FeedAsync(AppDbContext db, Guid tenantId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var items = new List<CalendarItem>();
        items.AddRange((await db.CalendarEvents.Where(e => e.TenantId == tenantId && e.EndsAt >= start && e.StartsAt <= end).ToListAsync(ct))
            .Select(e => new CalendarItem(e.Id, e.Title, e.Kind.ToString(), e.StartsAt, e.EndsAt, e.Location, e.SourceType ?? nameof(CalendarEvent), e.SourceId ?? e.Id)));
        items.AddRange((await db.Meetings.Where(m => m.TenantId == tenantId && m.Status != MeetingStatus.Cancelled && m.StartsAt >= start && m.StartsAt <= end).ToListAsync(ct))
            .Select(m => new CalendarItem(m.Id, m.Title, nameof(CalendarEventKind.Meeting), m.StartsAt, m.StartsAt.AddHours(2), null, nameof(Meeting), m.Id)));
        items.AddRange((await db.Policies.Where(p => p.TenantId == tenantId && p.NextReviewAt != null && p.NextReviewAt >= start && p.NextReviewAt <= end).ToListAsync(ct))
            .Select(p => new CalendarItem(p.Id, $"Policy review: {p.Code}", nameof(CalendarEventKind.PolicyReview), p.NextReviewAt!.Value, p.NextReviewAt!.Value, null, nameof(Policy), p.Id)));
        items.AddRange((await db.DecisionActions.Where(a => a.TenantId == tenantId && a.DueAt >= start && a.DueAt <= end &&
                (a.Status == DecisionActionStatus.Assigned || a.Status == DecisionActionStatus.InProgress)).ToListAsync(ct))
            .Select(a => new CalendarItem(a.Id, $"Action due: {a.Description}", nameof(CalendarEventKind.Deadline), a.DueAt, a.DueAt, null, nameof(DecisionAction), a.Id)));
        return items.OrderBy(i => i.StartsAt).ToList();
    }

    internal static string ToIcs(IEnumerable<CalendarItem> items)
    {
        static string Utc(DateTimeOffset d) => d.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");
        var sb = new StringBuilder();
        sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//EduNexus OS//Institutional Calendar//EN\r\nCALSCALE:GREGORIAN\r\n");
        var stamp = Utc(DateTimeOffset.UtcNow);
        foreach (var i in items)
        {
            sb.Append("BEGIN:VEVENT\r\n");
            sb.Append($"UID:{i.SourceType}-{i.Id}@edunexus\r\nDTSTAMP:{stamp}\r\n");
            sb.Append($"DTSTART:{Utc(i.StartsAt)}\r\nDTEND:{Utc(i.EndsAt > i.StartsAt ? i.EndsAt : i.StartsAt.AddMinutes(30))}\r\n");
            sb.Append($"SUMMARY:{Esc(i.Title)}\r\nCATEGORIES:{Esc(i.Kind)}\r\n");
            if (!string.IsNullOrEmpty(i.Location)) sb.Append($"LOCATION:{Esc(i.Location)}\r\n");
            sb.Append("END:VEVENT\r\n");
        }
        sb.Append("END:VCALENDAR\r\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ Committees
    private static void MapCommittees(IEndpointRouteBuilder app)
    {
        var com = app.MapGroup("/api/committees").WithTags("Committees").RequireAuthorization();

        com.MapGet("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("committee:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var c = await db.Committees.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "Committee not found." });
            var members = await db.CommitteeMembers.Where(m => m.TenantId == tenantId && m.CommitteeId == id).OrderBy(m => m.JoinedAt).ToListAsync(ct);
            var meetings = await db.Meetings.Where(m => m.TenantId == tenantId && m.CommitteeId == id).OrderByDescending(m => m.StartsAt).ToListAsync(ct);
            return Results.Ok(new { committee = c, members, meetings });
        });

        com.MapPatch("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, UpdateCommitteeReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("committee:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = await db.Committees.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "Committee not found." });
            var updated = c with { Name = string.IsNullOrWhiteSpace(req.Name) ? c.Name : req.Name.Trim(), IsActive = req.IsActive ?? c.IsActive };
            db.Entry(c).CurrentValues.SetValues(updated);
            DomainEvents.Record(db, req.TenantId, "CommitteeUpdated", "CommitteeUpdated", nameof(Committee), id.ToString(), details: updated.Name);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        });

        com.MapGet("/{id:guid}/members", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("committee:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.CommitteeMembers.Where(m => m.TenantId == tenantId && m.CommitteeId == id).OrderBy(m => m.JoinedAt).ToListAsync(ct));
        });

        com.MapDelete("/{id:guid}/members/{memberId:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid memberId, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("committee:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var m = await db.CommitteeMembers.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.CommitteeId == id && x.Id == memberId, ct);
            if (m is null) return Results.NotFound(new { error = "Member not found." });
            db.CommitteeMembers.Remove(m);
            DomainEvents.Record(db, tenantId, "CommitteeMemberRemoved", "CommitteeMemberRemoved", nameof(CommitteeMember), memberId.ToString(),
                payload: new { tenantId, committeeId = id, personId = m.PersonId }, details: m.Role);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.NoContent();
        });

        com.MapPost("/{id:guid}/members/{memberId:guid}/term", async (AppDbContext db, HttpContext ctx, Guid id, Guid memberId, SetTermReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("committee:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var m = await db.CommitteeMembers.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.CommitteeId == id && x.Id == memberId, ct);
            if (m is null) return Results.NotFound(new { error = "Member not found." });
            if (req.TermEndsAt is { } end && end <= m.JoinedAt) return Results.BadRequest(new { error = "Term must end after the member joined." });
            var updated = m with { TermEndsAt = req.TermEndsAt, Role = string.IsNullOrWhiteSpace(req.Role) ? m.Role : req.Role.Trim() };
            db.Entry(m).CurrentValues.SetValues(updated);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        });
    }

    // ------------------------------------------------------------------ Meetings, agenda, check-in, minutes
    private static void MapMeetings(IEndpointRouteBuilder app)
    {
        var mtg = app.MapGroup("/api/meetings").WithTags("Meetings").RequireAuthorization();

        mtg.MapPatch("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, UpdateMeetingReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (m is null) return Results.NotFound(new { error = "Meeting not found." });
            if (m.Status != MeetingStatus.Scheduled) return Results.Conflict(new { error = $"Only Scheduled meetings can be changed (status is {m.Status})." });
            var updated = m with
            {
                Title = string.IsNullOrWhiteSpace(req.Title) ? m.Title : req.Title.Trim(),
                StartsAt = req.StartsAt ?? m.StartsAt,
                Status = req.Cancel == true ? MeetingStatus.Cancelled : m.Status,
            };
            db.Entry(m).CurrentValues.SetValues(updated);
            DomainEvents.Record(db, req.TenantId, req.Cancel == true ? "MeetingCancelled" : "MeetingUpdated", req.Cancel == true ? "MeetingCancelled" : "MeetingUpdated",
                nameof(Meeting), id.ToString(), details: updated.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        });

        mtg.MapGet("/{id:guid}/agenda", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.AgendaItems.Where(a => a.TenantId == tenantId && a.MeetingId == id).OrderBy(a => a.Order).ToListAsync(ct));
        });

        mtg.MapPost("/{id:guid}/agenda-items", async (AppDbContext db, HttpContext ctx, Guid id, AgendaItemReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "Title required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (m is null) return Results.NotFound(new { error = "Meeting not found." });
            if (m.Status is MeetingStatus.Concluded or MeetingStatus.Cancelled) return Results.Conflict(new { error = $"Meeting is {m.Status}." });
            var next = (await db.AgendaItems.Where(a => a.TenantId == req.TenantId && a.MeetingId == id).MaxAsync(a => (int?)a.Order, ct) ?? 0) + 1;
            var item = new AgendaItem(Guid.NewGuid(), req.TenantId, id, next, req.Title.Trim(), req.Description?.Trim());
            db.AgendaItems.Add(item);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/agenda-items/{item.Id}", item);
        });

        app.MapPatch("/api/agenda-items/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, AgendaItemReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var item = await db.AgendaItems.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (item is null) return Results.NotFound(new { error = "Agenda item not found." });
            var updated = item with
            {
                Title = string.IsNullOrWhiteSpace(req.Title) ? item.Title : req.Title.Trim(),
                Description = req.Description ?? item.Description,
            };
            db.Entry(item).CurrentValues.SetValues(updated);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        }).RequireAuthorization().WithTags("Meetings");

        mtg.MapPost("/{id:guid}/check-in", async (AppDbContext db, HttpContext ctx, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (m is null) return Results.NotFound(new { error = "Meeting not found." });
            if (m.Status is MeetingStatus.Concluded or MeetingStatus.Cancelled) return Results.Conflict(new { error = $"Meeting is {m.Status}." });
            if (await ActingPersonAsync(db, ctx, req.TenantId, ct) is not { } me) return Results.Forbid();
            if (!await db.CommitteeMembers.AnyAsync(x => x.TenantId == req.TenantId && x.CommitteeId == m.CommitteeId && x.PersonId == me, ct))
                return Results.Forbid();
            var existing = await db.Attendances.FirstOrDefaultAsync(a => a.TenantId == req.TenantId && a.MeetingId == id && a.PersonId == me, ct);
            Attendance att;
            if (existing is null)
            {
                att = new Attendance(Guid.NewGuid(), req.TenantId, id, me, AttendanceStatus.Present);
                db.Attendances.Add(att);
            }
            else
            {
                att = existing with { Status = AttendanceStatus.Present };
                db.Entry(existing).CurrentValues.SetValues(att);
            }
            DomainEvents.Record(db, req.TenantId, "MeetingCheckIn", "MeetingCheckIn", nameof(Meeting), id.ToString(), actorId: me, details: me.ToString());
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(att);
        });

        mtg.MapGet("/{id:guid}/minutes", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.MeetingMinutes.Where(x => x.TenantId == tenantId && x.MeetingId == id).OrderByDescending(x => x.Version).ToListAsync(ct));
        });

        mtg.MapPost("/{id:guid}/minutes", async (AppDbContext db, HttpContext ctx, Guid id, SaveMinutesReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Content)) return Results.BadRequest(new { error = "Content required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Meetings.AnyAsync(x => x.TenantId == req.TenantId && x.Id == id, ct)) return Results.NotFound(new { error = "Meeting not found." });
            if (await ActingPersonAsync(db, ctx, req.TenantId, ct) is not { } me) return Results.Forbid();
            if (await db.MeetingMinutes.AnyAsync(x => x.TenantId == req.TenantId && x.MeetingId == id && x.Status == MinutesStatus.Approved, ct))
                return Results.Conflict(new { error = "Minutes are already approved." });
            var version = (await db.MeetingMinutes.Where(x => x.TenantId == req.TenantId && x.MeetingId == id).MaxAsync(x => (int?)x.Version, ct) ?? 0) + 1;
            var minutes = new MeetingMinutes(Guid.NewGuid(), req.TenantId, id, version, req.Content.Trim(),
                req.Submit == true ? MinutesStatus.Submitted : MinutesStatus.Draft, me, DateTimeOffset.UtcNow);
            db.MeetingMinutes.Add(minutes);
            DomainEvents.Record(db, req.TenantId, req.Submit == true ? "MinutesSubmitted" : "MinutesDrafted", req.Submit == true ? "MinutesSubmitted" : "MinutesDrafted",
                nameof(MeetingMinutes), minutes.Id.ToString(), actorId: me, details: $"v{version}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/minutes/{minutes.Id}", minutes);
        });

        app.MapPost("/api/minutes/{id:guid}/approve", async (AppDbContext db, HttpContext ctx, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("minutes:approve")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var minutes = await db.MeetingMinutes.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (minutes is null) return Results.NotFound(new { error = "Minutes not found." });
            if (await ActingPersonAsync(db, ctx, req.TenantId, ct) is not { } me || me == minutes.AuthorId) return Results.Forbid();
            if (minutes.Status != MinutesStatus.Submitted) return Results.Conflict(new { error = $"Only Submitted minutes can be approved (status is {minutes.Status})." });
            var approved = minutes with { Status = MinutesStatus.Approved, ApprovedBy = me, ApprovedAt = DateTimeOffset.UtcNow };
            db.Entry(minutes).CurrentValues.SetValues(approved);
            var meeting = await db.Meetings.FirstAsync(x => x.TenantId == req.TenantId && x.Id == minutes.MeetingId, ct);
            db.Entry(meeting).CurrentValues.SetValues(meeting with { Minutes = approved.Content });
            DomainEvents.Record(db, req.TenantId, "MinutesApproved", "MinutesApproved", nameof(MeetingMinutes), id.ToString(),
                payload: new { tenantId = req.TenantId, meetingId = minutes.MeetingId, version = minutes.Version }, actorId: me, details: $"v{minutes.Version}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(approved);
        }).RequireAuthorization().WithTags("Meetings");
    }

    // ------------------------------------------------------------------ Decisions
    private static void MapDecisions(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/decisions/overdue", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("decision:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var now = DateTimeOffset.UtcNow;
            var overdue = await db.DecisionActions.Where(a => a.TenantId == tenantId && a.DueAt < now &&
                    (a.Status == DecisionActionStatus.Assigned || a.Status == DecisionActionStatus.InProgress))
                .OrderBy(a => a.DueAt).ToListAsync(ct);
            var ids = overdue.Select(a => a.DecisionId).Distinct().ToList();
            var texts = await db.Decisions.Where(d => d.TenantId == tenantId && ids.Contains(d.Id)).ToDictionaryAsync(d => d.Id, d => d.Text, ct);
            return Results.Ok(overdue.Select(a => new { action = a, decision = texts.GetValueOrDefault(a.DecisionId), daysOverdue = (int)(now - a.DueAt).TotalDays }));
        }).RequireAuthorization().WithTags("Decisions");

        app.MapGet("/api/decisions/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("decision:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var d = await db.Decisions.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (d is null) return Results.NotFound(new { error = "Decision not found." });
            var actions = await db.DecisionActions.Where(a => a.TenantId == tenantId && a.DecisionId == id).OrderBy(a => a.DueAt).ToListAsync(ct);
            var actionIds = actions.Select(a => a.Id).ToList();
            var evidence = await db.DecisionActionEvidences.Where(e => e.TenantId == tenantId && actionIds.Contains(e.ActionId)).OrderBy(e => e.At).ToListAsync(ct);
            var done = actions.Count(a => a.Status is DecisionActionStatus.Done or DecisionActionStatus.Verified);
            return Results.Ok(new
            {
                decision = d, actions, evidence,
                implementationPct = actions.Count == 0 ? (double?)null : Math.Round(100.0 * done / actions.Count, 1),
            });
        }).RequireAuthorization().WithTags("Decisions");

        app.MapPost("/api/decisions/{id:guid}/transition", async (AppDbContext db, HttpContext ctx, Guid id, TransitionReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("decision:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enum.TryParse<DecisionStatus>(req.Status, true, out var target)) return Results.BadRequest(new { error = "Unknown status." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var d = await db.Decisions.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (d is null) return Results.NotFound(new { error = "Decision not found." });
            if (!DecisionFlow.TryGetValue(d.Status, out var next) || next != target)
                return Results.Conflict(new { error = $"Cannot move a {d.Status} decision to {target}." });
            if (target == DecisionStatus.Implemented &&
                await db.DecisionActions.AnyAsync(a => a.TenantId == req.TenantId && a.DecisionId == id &&
                    a.Status != DecisionActionStatus.Done && a.Status != DecisionActionStatus.Verified, ct))
                return Results.Conflict(new { error = "All actions must be Done or Verified before the decision is Implemented." });
            if (target == DecisionStatus.Verified &&
                await db.DecisionActions.AnyAsync(a => a.TenantId == req.TenantId && a.DecisionId == id && a.Status != DecisionActionStatus.Verified, ct))
                return Results.Conflict(new { error = "All actions must be Verified before the decision is Verified." });
            var updated = d with { Status = target };
            db.Entry(d).CurrentValues.SetValues(updated);
            DomainEvents.Record(db, req.TenantId, $"Decision{target}", $"Decision{target}", nameof(Decision), id.ToString(),
                payload: new { tenantId = req.TenantId, decisionId = id, status = target.ToString() }, actorId: await ActingPersonAsync(db, ctx, req.TenantId, ct), details: target.ToString());
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        }).RequireAuthorization().WithTags("Decisions");

        app.MapPost("/api/decision-actions/{id:guid}/evidence", async (AppDbContext db, HttpContext ctx, Guid id, ActionEvidenceReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("action:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.ObjectKey) || string.IsNullOrWhiteSpace(req.FileName))
                return Results.BadRequest(new { error = "objectKey and fileName required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var a = await db.DecisionActions.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (a is null) return Results.NotFound(new { error = "Action not found." });
            if (await ActingPersonAsync(db, ctx, req.TenantId, ct) is not { } me || me != a.AssigneeId) return Results.Forbid();
            var ev = new DecisionActionEvidence(Guid.NewGuid(), req.TenantId, id, req.ObjectKey.Trim(), req.FileName.Trim(), req.Note?.Trim(), me, DateTimeOffset.UtcNow);
            db.DecisionActionEvidences.Add(ev);
            DomainEvents.Record(db, req.TenantId, "ActionEvidenceAdded", "ActionEvidenceAdded", nameof(DecisionAction), id.ToString(), actorId: me, details: ev.FileName);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/decision-actions/{id}/evidence/{ev.Id}", ev);
        }).RequireAuthorization().WithTags("DecisionActions");

        app.MapPost("/api/decision-actions/{id:guid}/verify", async (AppDbContext db, HttpContext ctx, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("action:verify")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var a = await db.DecisionActions.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (a is null) return Results.NotFound(new { error = "Action not found." });
            if (await ActingPersonAsync(db, ctx, req.TenantId, ct) is not { } me || me == a.AssigneeId) return Results.Forbid();
            if (a.Status != DecisionActionStatus.Done) return Results.Conflict(new { error = $"Only Done actions can be verified (status is {a.Status})." });
            if (!await db.DecisionActionEvidences.AnyAsync(e => e.TenantId == req.TenantId && e.ActionId == id, ct))
                return Results.Conflict(new { error = "Attach evidence before verification." });
            var verified = a with { Status = DecisionActionStatus.Verified };
            db.Entry(a).CurrentValues.SetValues(verified);
            DomainEvents.Record(db, req.TenantId, "ActionVerified", "ActionVerified", nameof(DecisionAction), id.ToString(),
                payload: new { tenantId = req.TenantId, actionId = id, verifiedBy = me }, actorId: me, details: a.Description);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(verified);
        }).RequireAuthorization().WithTags("DecisionActions");
    }

    // ------------------------------------------------------------------ Policies & procedures
    private static void MapPolicies(IEndpointRouteBuilder app)
    {
        var pol = app.MapGroup("/api/policies").WithTags("Policies").RequireAuthorization();

        pol.MapGet("/reviews/upcoming", async (AppDbContext db, HttpContext ctx, Guid tenantId, int? days, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var until = DateTimeOffset.UtcNow.AddDays(Math.Clamp(days ?? 30, 1, 365));
            return Results.Ok(await db.Policies.Where(p => p.TenantId == tenantId && p.Status != PolicyStatus.Retired &&
                    p.NextReviewAt != null && p.NextReviewAt <= until).OrderBy(p => p.NextReviewAt).ToListAsync(ct));
        });

        pol.MapGet("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Policy not found." });
            var versions = await db.PolicyVersions.Where(v => v.TenantId == tenantId && v.PolicyId == id).OrderByDescending(v => v.Version).ToListAsync(ct);
            var acknowledgements = await db.PolicyAcknowledgements.CountAsync(a => a.TenantId == tenantId && a.PolicyId == id, ct);
            var procedures = await db.Procedures.Where(x => x.TenantId == tenantId && x.PolicyId == id).OrderBy(x => x.Code).ToListAsync(ct);
            return Results.Ok(new { policy = p, versions, acknowledgements, procedures });
        });

        pol.MapPost("/{id:guid}/versions", async (AppDbContext db, HttpContext ctx, Guid id, PolicyVersionReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Content)) return Results.BadRequest(new { error = "Content required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Policy not found." });
            if (p.Status == PolicyStatus.Retired) return Results.Conflict(new { error = "Policy is retired." });
            var me = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            // Keep the outgoing text as an immutable snapshot the first time a policy is revised.
            if (!await db.PolicyVersions.AnyAsync(v => v.TenantId == req.TenantId && v.PolicyId == id && v.Version == p.Version, ct))
                db.PolicyVersions.Add(new PolicyVersion(Guid.NewGuid(), req.TenantId, id, p.Version, p.Title, p.Content, "Original", p.OwnerId, DateTimeOffset.UtcNow));
            var revised = p with
            {
                Version = p.Version + 1,
                Title = string.IsNullOrWhiteSpace(req.Title) ? p.Title : req.Title.Trim(),
                Content = req.Content.Trim(),
                Status = PolicyStatus.Draft,
                NextReviewAt = req.NextReviewAt ?? p.NextReviewAt,
            };
            db.PolicyVersions.Add(new PolicyVersion(Guid.NewGuid(), req.TenantId, id, revised.Version, revised.Title, revised.Content, req.ChangeNote?.Trim(), me, DateTimeOffset.UtcNow));
            db.Entry(p).CurrentValues.SetValues(revised);
            DomainEvents.Record(db, req.TenantId, "PolicyRevised", "PolicyRevised", nameof(Policy), id.ToString(),
                payload: new { tenantId = req.TenantId, policyId = id, version = revised.Version }, actorId: me, details: $"v{revised.Version}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(revised);
        });

        pol.MapPost("/{id:guid}/transition", async (AppDbContext db, HttpContext ctx, Guid id, TransitionReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enum.TryParse<PolicyStatus>(req.Status, true, out var target) || target == PolicyStatus.Retired)
                return Results.BadRequest(new { error = "Status must be Draft|Review|LegalReview|Approval|Published (use /retire to retire)." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Policy not found." });
            if (!PolicyFlow.TryGetValue(p.Status, out var allowed) || !allowed.Contains(target))
                return Results.Conflict(new { error = $"Cannot move a {p.Status} policy to {target}." });
            var updated = p with { Status = target };
            db.Entry(p).CurrentValues.SetValues(updated);
            var evt = target == PolicyStatus.Published ? "PolicyPublished" : "PolicyTransitioned";
            DomainEvents.Record(db, req.TenantId, evt, evt, nameof(Policy), id.ToString(),
                payload: new { tenantId = req.TenantId, policyId = id, code = p.Code, status = target.ToString() },
                actorId: await ActingPersonAsync(db, ctx, req.TenantId, ct), details: target.ToString());
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        });

        pol.MapPost("/{id:guid}/retire", async (AppDbContext db, HttpContext ctx, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Policy not found." });
            if (p.Status == PolicyStatus.Retired) return Results.Conflict(new { error = "Policy is already retired." });
            var retired = p with { Status = PolicyStatus.Retired, NextReviewAt = null };
            db.Entry(p).CurrentValues.SetValues(retired);
            DomainEvents.Record(db, req.TenantId, "PolicyRetired", "PolicyRetired", nameof(Policy), id.ToString(),
                actorId: await ActingPersonAsync(db, ctx, req.TenantId, ct), details: p.Code);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(retired);
        });

        pol.MapPost("/{id:guid}/review-schedule", async (AppDbContext db, HttpContext ctx, Guid id, ReviewScheduleReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Policy not found." });
            if (req.OwnerId is { } owner && !await db.People.AnyAsync(x => x.TenantId == req.TenantId && x.Id == owner, ct))
                return Results.BadRequest(new { error = "Owner not found in this tenant." });
            var updated = p with { NextReviewAt = req.NextReviewAt, OwnerId = req.OwnerId ?? p.OwnerId };
            db.Entry(p).CurrentValues.SetValues(updated);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        });

        pol.MapGet("/{id:guid}/acknowledgements", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.PolicyAcknowledgements.Where(a => a.TenantId == tenantId && a.PolicyId == id).OrderBy(a => a.At).ToListAsync(ct));
        });

        var proc = app.MapGroup("/api/procedures").WithTags("Procedures").RequireAuthorization();
        proc.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? policyId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Procedures.Where(x => x.TenantId == tenantId);
            if (policyId.HasValue) q = q.Where(x => x.PolicyId == policyId);
            return Results.Ok(await q.OrderBy(x => x.Code).ToListAsync(ct));
        });
        proc.MapGet("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var x = await db.Procedures.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, ct);
            return x is null ? Results.NotFound(new { error = "Procedure not found." }) : Results.Ok(x);
        });
        proc.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateProcedureReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Title) || string.IsNullOrWhiteSpace(req.Steps))
                return Results.BadRequest(new { error = "Code, Title and Steps required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (req.PolicyId is { } pid && !await db.Policies.AnyAsync(p => p.TenantId == req.TenantId && p.Id == pid, ct))
                return Results.BadRequest(new { error = "Policy not found in this tenant." });
            var x = new Procedure(Guid.NewGuid(), req.TenantId, req.PolicyId, req.Code.Trim(), req.Title.Trim(), req.Steps.Trim(), 1, true);
            db.Procedures.Add(x);
            DomainEvents.Record(db, req.TenantId, "ProcedureCreated", "ProcedureCreated", nameof(Procedure), x.Id.ToString(), details: x.Code);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex)) { return Results.Conflict(new { error = $"Procedure code '{req.Code}' already exists." }); }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/procedures/{x.Id}", x);
        });
    }

    // ------------------------------------------------------------------ Notification preferences & SLA policies
    private static void MapPreferencesAndSla(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/notifications/preferences", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("notification:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (await ActingPersonAsync(db, ctx, tenantId, ct) is not { } me) return Results.Forbid();
            var saved = await db.NotificationPreferences.Where(p => p.TenantId == tenantId && p.PersonId == me).ToListAsync(ct);
            // Every channel is listed; channels without a saved row use the defaults (enabled, any priority, no quiet hours).
            return Results.Ok(Enum.GetValues<NotificationChannel>().Select(c => saved.FirstOrDefault(p => p.Channel == c)
                ?? new NotificationPreference(Guid.Empty, tenantId, me, c, true, NotificationPriority.FYI, null, null)));
        }).RequireAuthorization().WithTags("Notifications");

        app.MapPut("/api/notifications/preferences", async (AppDbContext db, HttpContext ctx, SavePreferencesReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("notification:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (await ActingPersonAsync(db, ctx, req.TenantId, ct) is not { } me) return Results.Forbid();
            var result = new List<NotificationPreference>();
            foreach (var item in req.Preferences ?? [])
            {
                if (!Enum.TryParse<NotificationChannel>(item.Channel, true, out var channel)) return Results.BadRequest(new { error = $"Unknown channel '{item.Channel}'." });
                var min = NotificationPriority.FYI;
                if (item.MinPriority is not null && !Enum.TryParse(item.MinPriority, true, out min)) return Results.BadRequest(new { error = $"Unknown priority '{item.MinPriority}'." });
                if (item.QuietFromHour is < 0 or > 23 || item.QuietToHour is < 0 or > 23) return Results.BadRequest(new { error = "Quiet hours must be 0-23 (UTC)." });
                var existing = await db.NotificationPreferences.FirstOrDefaultAsync(p => p.TenantId == req.TenantId && p.PersonId == me && p.Channel == channel, ct);
                var pref = new NotificationPreference(existing?.Id ?? Guid.NewGuid(), req.TenantId, me, channel, item.Enabled, min, item.QuietFromHour, item.QuietToHour);
                if (existing is null) db.NotificationPreferences.Add(pref); else db.Entry(existing).CurrentValues.SetValues(pref);
                result.Add(pref);
            }
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(result);
        }).RequireAuthorization().WithTags("Notifications");

        app.MapGet("/api/sla/policies", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("approval:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.SlaPolicies.Where(p => p.TenantId == tenantId).OrderBy(p => p.EntityType).ToListAsync(ct));
        }).RequireAuthorization().WithTags("SLA");

        app.MapPost("/api/sla/policies", async (AppDbContext db, HttpContext ctx, SlaPolicyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("sla:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.EntityType) || req.ResponseHours <= 0 || req.EscalateAfterHours < req.ResponseHours)
                return Results.BadRequest(new { error = "entityType required; responseHours > 0; escalateAfterHours >= responseHours." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var existing = await db.SlaPolicies.FirstOrDefaultAsync(p => p.TenantId == req.TenantId && p.EntityType == req.EntityType.Trim(), ct);
            var policy = new SlaPolicy(existing?.Id ?? Guid.NewGuid(), req.TenantId, req.EntityType.Trim(), req.ResponseHours, req.EscalateAfterHours, req.IsActive ?? true);
            if (existing is null) db.SlaPolicies.Add(policy); else db.Entry(existing).CurrentValues.SetValues(policy);
            DomainEvents.Record(db, req.TenantId, "SlaPolicySaved", "SlaPolicySaved", nameof(SlaPolicy), policy.Id.ToString(), details: $"{policy.EntityType}:{policy.ResponseHours}h");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(policy);
        }).RequireAuthorization().WithTags("SLA");
    }

    /// <summary>Due date for a new approval: the active SLA policy for the entity type, else the fallback.</summary>
    public static async Task<DateTimeOffset> DueAtAsync(AppDbContext db, Guid tenantId, string entityType, TimeSpan fallback, CancellationToken ct)
    {
        var policy = await db.SlaPolicies.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.EntityType == entityType && p.IsActive, ct);
        return DateTimeOffset.UtcNow.Add(policy is null ? fallback : TimeSpan.FromHours(policy.ResponseHours));
    }
}

public sealed record CalendarItem(Guid Id, string Title, string Kind, DateTimeOffset StartsAt, DateTimeOffset EndsAt, string? Location, string SourceType, Guid SourceId);
public sealed record CalendarEventReq(Guid TenantId, string? Title, string? Kind, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, string? Location);
public sealed record UpdateCommitteeReq(Guid TenantId, string? Name, bool? IsActive);
public sealed record SetTermReq(Guid TenantId, DateTimeOffset? TermEndsAt, string? Role);
public sealed record UpdateMeetingReq(Guid TenantId, string? Title, DateTimeOffset? StartsAt, bool? Cancel);
public sealed record AgendaItemReq(Guid TenantId, string? Title, string? Description);
public sealed record SaveMinutesReq(Guid TenantId, string? Content, bool? Submit);
public sealed record TransitionReq(Guid TenantId, string? Status);
public sealed record ActionEvidenceReq(Guid TenantId, string? ObjectKey, string? FileName, string? Note);
public sealed record PolicyVersionReq(Guid TenantId, string? Title, string? Content, string? ChangeNote, DateTimeOffset? NextReviewAt);
public sealed record ReviewScheduleReq(Guid TenantId, DateTimeOffset? NextReviewAt, Guid? OwnerId);
public sealed record CreateProcedureReq(Guid TenantId, Guid? PolicyId, string? Code, string? Title, string? Steps);
public sealed record PreferenceItem(string Channel, bool Enabled, string? MinPriority, int? QuietFromHour, int? QuietToHour);
public sealed record SavePreferencesReq(Guid TenantId, PreferenceItem[]? Preferences);
public sealed record SlaPolicyReq(Guid TenantId, string EntityType, int ResponseHours, int EscalateAfterHours, bool? IsActive);
