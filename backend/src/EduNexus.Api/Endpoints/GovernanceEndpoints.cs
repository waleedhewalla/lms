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

public static class GovernanceEndpoints
{
    public static void MapGovernanceEndpoints(this IEndpointRouteBuilder app)
    {
        // --- Committees ---
        var committees = app.MapGroup("/api/committees").WithTags("Committees").RequireAuthorization();
        committees.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("committee:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.Committees.Where(c => c.TenantId == tenantId).OrderBy(c => c.Code).ToListAsync(ct));
        });
        committees.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateCommitteeReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("committee:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "Code and Name required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = new Committee(Guid.NewGuid(), req.TenantId, req.Code.Trim(), req.Name.Trim(), true);
            db.Committees.Add(c);
            DomainEvents.Record(db, req.TenantId, "CommitteeCreated", "CommitteeCreated",
                nameof(Committee), c.Id.ToString(),
                payload: new { tenantId = req.TenantId, committeeId = c.Id, code = c.Code }, details: c.Code);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex))
            {
                return Results.Conflict(new { error = $"Committee code '{req.Code}' already exists in tenant." });
            }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/committees/{c.Id}", c);
        });
        committees.MapPost("/{id:guid}/members", async (AppDbContext db, HttpContext ctx, Guid id, AddMemberReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("committee:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Committees.AnyAsync(c => c.TenantId == req.TenantId && c.Id == id, ct))
                return Results.NotFound(new { error = "Committee not found." });
            if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.PersonId, ct))
                return Results.NotFound(new { error = "Person not found in tenant." });
            var m = new CommitteeMember(Guid.NewGuid(), req.TenantId, id, req.PersonId, req.Role ?? "Member", DateTimeOffset.UtcNow);
            db.CommitteeMembers.Add(m);
            DomainEvents.Record(db, req.TenantId, "CommitteeMemberAdded", "CommitteeMemberAdded",
                nameof(CommitteeMember), m.Id.ToString(),
                payload: new { tenantId = req.TenantId, committeeId = id, personId = req.PersonId }, details: req.Role);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex))
            {
                return Results.Conflict(new { error = "Person is already a member of this committee." });
            }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/committees/{id}/members/{m.Id}", m);
        });

        // --- Meetings ---
        var meetings = app.MapGroup("/api/meetings").WithTags("Meetings").RequireAuthorization();
        meetings.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? committeeId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Meetings.Where(m => m.TenantId == tenantId);
            if (committeeId.HasValue) q = q.Where(m => m.CommitteeId == committeeId.Value);
            return Results.Ok(await q.OrderByDescending(m => m.StartsAt).ToListAsync(ct));
        });
        meetings.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateMeetingReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "Title required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Committees.AnyAsync(c => c.TenantId == req.TenantId && c.Id == req.CommitteeId, ct))
                return Results.NotFound(new { error = "Committee not found." });
            var m = new Meeting(Guid.NewGuid(), req.TenantId, req.CommitteeId, req.Title.Trim(), req.StartsAt, MeetingStatus.Scheduled, null);
            db.Meetings.Add(m);
            var order = 1;
            foreach (var a in req.Agenda ?? [])
            {
                if (string.IsNullOrWhiteSpace(a.Title)) continue;
                db.AgendaItems.Add(new AgendaItem(Guid.NewGuid(), req.TenantId, m.Id, order++, a.Title.Trim(), a.Description?.Trim()));
            }
            DomainEvents.Record(db, req.TenantId, "MeetingScheduled", "MeetingScheduled",
                nameof(Meeting), m.Id.ToString(),
                payload: new { tenantId = req.TenantId, meetingId = m.Id, committeeId = req.CommitteeId, title = m.Title }, details: m.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/meetings/{m.Id}", m);
        });
        meetings.MapPost("/{id:guid}/attendance", async (AppDbContext db, HttpContext ctx, Guid id, RecordAttendanceReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enum.TryParse<AttendanceStatus>(req.Status, true, out var st))
                return Results.BadRequest(new { error = "Status must be Present|Absent|Excused." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Meetings.AnyAsync(x => x.TenantId == req.TenantId && x.Id == id, ct))
                return Results.NotFound(new { error = "Meeting not found." });
            var existing = await db.Attendances.FirstOrDefaultAsync(a => a.TenantId == req.TenantId && a.MeetingId == id && a.PersonId == req.PersonId, ct);
            if (existing is not null)
            {
                db.Entry(existing).CurrentValues.SetValues(existing with { Status = st });
            }
            else
            {
                db.Attendances.Add(new Attendance(Guid.NewGuid(), req.TenantId, id, req.PersonId, st));
            }
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok();
        });
        meetings.MapPost("/{id:guid}/conclude", async (AppDbContext db, HttpContext ctx, Guid id, ConcludeMeetingReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (m is null) return Results.NotFound(new { error = "Meeting not found." });
            if (m.Status == MeetingStatus.Concluded) return Results.Conflict(new { error = "Meeting already concluded." });
            db.Entry(m).CurrentValues.SetValues(m with { Status = MeetingStatus.Concluded, Minutes = req.Minutes });
            DomainEvents.Record(db, req.TenantId, "MeetingConcluded", "MeetingConcluded",
                nameof(Meeting), m.Id.ToString(),
                payload: new { tenantId = req.TenantId, meetingId = m.Id }, details: m.Title);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(m);
        });
        meetings.MapPost("/{id:guid}/decisions", async (AppDbContext db, HttpContext ctx, Guid id, CreateDecisionReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("decision:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "Text required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (m is null) return Results.NotFound(new { error = "Meeting not found." });
            if (m.Status == MeetingStatus.Cancelled) return Results.Conflict(new { error = "Meeting cancelled." });
            var d = new Decision(Guid.NewGuid(), req.TenantId, id, req.Text.Trim(), DecisionStatus.Published, DateTimeOffset.UtcNow);
            db.Decisions.Add(d);
            DomainEvents.Record(db, req.TenantId, "DecisionPublished", "DecisionPublished",
                nameof(Decision), d.Id.ToString(),
                payload: new { tenantId = req.TenantId, decisionId = d.Id, meetingId = id, committeeId = m.CommitteeId, text = d.Text }, details: d.Text[..Math.Min(80, d.Text.Length)]);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/decisions/{d.Id}", d);
        });
        meetings.MapGet("/{id:guid}/quorum", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (m is null) return Results.NotFound(new { error = "Meeting not found." });

            var totalMembers = await db.CommitteeMembers.CountAsync(cm => cm.TenantId == tenantId && cm.CommitteeId == m.CommitteeId, ct);
            var attendances = await db.Attendances.Where(a => a.TenantId == tenantId && a.MeetingId == id).ToListAsync(ct);
            var presentCount = attendances.Count(a => a.Status == AttendanceStatus.Present);
            var absentCount = attendances.Count(a => a.Status == AttendanceStatus.Absent);
            var excusedCount = attendances.Count(a => a.Status == AttendanceStatus.Excused);
            var quorumRequired = (int)Math.Ceiling(totalMembers > 0 ? totalMembers / 2.0 : 1);
            var hasQuorum = totalMembers > 0 ? (presentCount >= quorumRequired) : false;

            return Results.Ok(new { meetingId = id, totalMembers, presentCount, absentCount, excusedCount, quorumRequired, hasQuorum });
        });
        meetings.MapGet("/{id:guid}/packet", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (m is null) return Results.NotFound(new { error = "Meeting not found." });

            var committee = await db.Committees.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == m.CommitteeId, ct);
            var members = await db.CommitteeMembers.Where(cm => cm.TenantId == tenantId && cm.CommitteeId == m.CommitteeId).ToListAsync(ct);
            var attendances = await db.Attendances.Where(a => a.TenantId == tenantId && a.MeetingId == id).ToListAsync(ct);
            var totalMembers = members.Count;
            var presentCount = attendances.Count(a => a.Status == AttendanceStatus.Present);
            var absentCount = attendances.Count(a => a.Status == AttendanceStatus.Absent);
            var excusedCount = attendances.Count(a => a.Status == AttendanceStatus.Excused);
            var quorumRequired = (int)Math.Ceiling(totalMembers > 0 ? totalMembers / 2.0 : 1);
            var hasQuorum = totalMembers > 0 ? (presentCount >= quorumRequired) : false;

            var agenda = await db.AgendaItems.Where(a => a.TenantId == tenantId && a.MeetingId == id).OrderBy(a => a.Order).ToListAsync(ct);
            var votes = await db.MeetingVotes.Where(v => v.TenantId == tenantId && v.MeetingId == id).OrderBy(v => v.CastAt).ToListAsync(ct);
            var decisionsList = await db.Decisions.Where(d => d.TenantId == tenantId && d.MeetingId == id).ToListAsync(ct);
            var decisionIds = decisionsList.Select(d => d.Id).ToList();
            var actionItems = await db.DecisionActions.Where(a => a.TenantId == tenantId && decisionIds.Contains(a.DecisionId)).ToListAsync(ct);

            var tallies = votes.GroupBy(v => v.AgendaItemId).Select(g => new
            {
                agendaItemId = g.Key,
                inFavor = g.Count(x => x.Choice == VoteChoice.InFavor),
                against = g.Count(x => x.Choice == VoteChoice.Against),
                abstain = g.Count(x => x.Choice == VoteChoice.Abstain),
                totalVotes = g.Count()
            }).ToList();

            var packet = new
            {
                meeting = m,
                committee,
                governance = new { totalMembers, presentCount, absentCount, excusedCount, quorumRequired, hasQuorum },
                members,
                attendances,
                agenda,
                votes,
                votingTallies = tallies,
                decisions = decisionsList,
                actionItems,
                certification = new { isConcluded = m.Status == MeetingStatus.Concluded, hasMinutes = !string.IsNullOrWhiteSpace(m.Minutes), minutes = m.Minutes },
                generatedAt = DateTimeOffset.UtcNow
            };

            return Results.Ok(packet);
        });
        meetings.MapGet("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var m = await db.Meetings.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (m is null) return Results.NotFound(new { error = "Meeting not found." });
            var agenda = await db.AgendaItems.Where(a => a.TenantId == tenantId && a.MeetingId == id).OrderBy(a => a.Order).ToListAsync(ct);
            return Results.Ok(new
            {
                m.Id, m.TenantId, m.CommitteeId, m.Title, m.StartsAt, m.Status, m.Minutes, agenda
            });
        });
        meetings.MapGet("/{id:guid}/votes", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var votes = await db.MeetingVotes.Where(v => v.TenantId == tenantId && v.MeetingId == id).OrderBy(v => v.CastAt).ToListAsync(ct);
            var tallies = votes.GroupBy(v => v.AgendaItemId).Select(g => new
            {
                agendaItemId = g.Key,
                inFavor = g.Count(v => v.Choice == VoteChoice.InFavor),
                against = g.Count(v => v.Choice == VoteChoice.Against),
                abstain = g.Count(v => v.Choice == VoteChoice.Abstain),
                total = g.Count()
            }).ToList();
            return Results.Ok(new { votes, tallies });
        });
        meetings.MapPost("/{id:guid}/votes", async (AppDbContext db, HttpContext ctx, Guid id, CastMeetingVoteReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("meeting:update") && !ctx.User.HasPermission("meeting:create") && !ctx.User.HasPermission("meeting:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enum.TryParse<VoteChoice>(req.Choice, true, out var choice))
                return Results.BadRequest(new { error = "Choice must be InFavor|Against|Abstain." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var agendaItem = await db.AgendaItems
                .FirstOrDefaultAsync(a => a.TenantId == req.TenantId && a.MeetingId == id && a.Id == req.AgendaItemId, ct);
            if (agendaItem is null) return Results.NotFound(new { error = "Agenda item not found for meeting." });
            if (await db.MeetingVotes.AnyAsync(v => v.TenantId == req.TenantId && v.AgendaItemId == req.AgendaItemId && v.PersonId == req.PersonId, ct))
                return Results.Conflict(new { error = "This person has already voted on this agenda item." });
            var vote = new MeetingVote(Guid.NewGuid(), req.TenantId, id, req.AgendaItemId,
                req.PersonId, choice, DateTimeOffset.UtcNow, req.Remarks?.Trim());
            db.MeetingVotes.Add(vote);
            DomainEvents.Record(db, req.TenantId, "MeetingVoteCast", "MeetingVoteCast",
                nameof(MeetingVote), vote.Id.ToString(),
                payload: new { tenantId = req.TenantId, meetingId = id, agendaItemId = req.AgendaItemId, choice = choice.ToString() }, details: choice.ToString());
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(vote);
        });

        // --- Decisions & Decision Actions ---
        var decisions = app.MapGroup("/api/decisions").WithTags("Decisions").RequireAuthorization();
        decisions.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? meetingId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("decision:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.Decisions.Where(d => d.TenantId == tenantId);
            if (meetingId.HasValue) q = q.Where(d => d.MeetingId == meetingId.Value);
            return Results.Ok(await q.OrderByDescending(d => d.PublishedAt).ToListAsync(ct));
        });
        decisions.MapPost("/{id:guid}/actions", async (AppDbContext db, HttpContext ctx, Guid id, CreateDecisionActionReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("decision:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Description)) return Results.BadRequest(new { error = "Description required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.Decisions.AnyAsync(d => d.TenantId == req.TenantId && d.Id == id, ct))
                return Results.NotFound(new { error = "Decision not found." });
            if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.AssigneeId, ct))
                return Results.NotFound(new { error = "Assignee not found in tenant." });
            var a = new DecisionAction(Guid.NewGuid(), req.TenantId, id, req.AssigneeId, req.Description.Trim(),
                DecisionActionStatus.Assigned, req.DueAt ?? DateTimeOffset.UtcNow.AddDays(14));
            db.DecisionActions.Add(a);
            DomainEvents.Record(db, req.TenantId, "ActionAssigned", "ActionAssigned",
                nameof(DecisionAction), a.Id.ToString(),
                payload: new { tenantId = req.TenantId, actionId = a.Id, decisionId = id, assigneeId = req.AssigneeId, description = a.Description }, details: a.Description);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/decision-actions/{a.Id}", a);
        });

        var actions = app.MapGroup("/api/decision-actions").WithTags("DecisionActions").RequireAuthorization();
        actions.MapPost("/{id:guid}/advance", async (AppDbContext db, HttpContext ctx, Guid id, AdvanceActionReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("action:update")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enum.TryParse<DecisionActionStatus>(req.Status, true, out var st))
                return Results.BadRequest(new { error = "Status must be Assigned|InProgress|Done|Verified." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var a = await db.DecisionActions.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (a is null) return Results.NotFound(new { error = "Action not found." });
            db.Entry(a).CurrentValues.SetValues(a with { Status = st });
            DomainEvents.Record(db, req.TenantId, "ActionAdvanced", "ActionAdvanced",
                nameof(DecisionAction), a.Id.ToString(),
                payload: new { tenantId = req.TenantId, actionId = a.Id, status = st.ToString() }, details: st.ToString());
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(a);
        });

        // --- Policies ---
        var policies = app.MapGroup("/api/policies").WithTags("Policies").RequireAuthorization();
        policies.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.Policies.Where(p => p.TenantId == tenantId).OrderBy(p => p.Code).ToListAsync(ct));
        });
        policies.MapPost("/", async (AppDbContext db, HttpContext ctx, CreatePolicyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Title))
                return Results.BadRequest(new { error = "Code and Title required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var p = new Policy(Guid.NewGuid(), req.TenantId, req.Code.Trim(), req.Title.Trim(), req.Content ?? "", PolicyStatus.Draft, 1);
            db.Policies.Add(p);
            DomainEvents.Record(db, req.TenantId, "PolicyCreated", "PolicyCreated",
                nameof(Policy), p.Id.ToString(),
                payload: new { tenantId = req.TenantId, policyId = p.Id, code = p.Code }, details: p.Code);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex))
            {
                return Results.Conflict(new { error = $"Policy code '{req.Code}' already exists in tenant." });
            }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/policies/{p.Id}", p);
        });
        policies.MapPost("/{id:guid}/publish", async (AppDbContext db, HttpContext ctx, Guid id, PublishPolicyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Policy not found." });
            if (p.Status == PolicyStatus.Published) return Results.Conflict(new { error = "Policy already published." });
            db.Entry(p).CurrentValues.SetValues(p with { Status = PolicyStatus.Published });
            DomainEvents.Record(db, req.TenantId, "PolicyPublished", "PolicyPublished",
                nameof(Policy), p.Id.ToString(),
                payload: new { tenantId = req.TenantId, policyId = p.Id, code = p.Code }, details: p.Code);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(p);
        });
        policies.MapPost("/{id:guid}/acknowledge", async (AppDbContext db, HttpContext ctx, Guid id, AcknowledgePolicyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Policy not found." });
            if (p.Status != PolicyStatus.Published) return Results.Conflict(new { error = "Cannot acknowledge unpublished policy." });
            // People acknowledge for themselves only: the person comes from the token, never the body.
            var actor = await ActingPersonAsync(db, ctx, req.TenantId, ct);
            if (actor is null || (req.PersonId is { } claimed && claimed != actor)) return Results.Forbid();
            var ack = new PolicyAcknowledgement(Guid.NewGuid(), req.TenantId, id, actor.Value, DateTimeOffset.UtcNow);
            db.PolicyAcknowledgements.Add(ack);
            DomainEvents.Record(db, req.TenantId, "PolicyAcknowledged", "PolicyAcknowledged",
                nameof(Policy), id.ToString(),
                payload: new { tenantId = req.TenantId, policyId = id, personId = actor }, actorId: actor, details: actor.ToString());
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex))
            {
                return Results.Conflict(new { error = "Person has already acknowledged this policy." });
            }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/policies/{id}/acknowledgements", ack);
        });
        policies.MapGet("/{id:guid}/pending", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Policy not found." });
            var ackedIds = await db.PolicyAcknowledgements.Where(a => a.TenantId == tenantId && a.PolicyId == id).Select(a => a.PersonId).ToListAsync(ct);
            var pending = await db.People.Where(person => person.TenantId == tenantId && !ackedIds.Contains(person.Id)).ToListAsync(ct);
            return Results.Ok(pending);
        });
    }
}
