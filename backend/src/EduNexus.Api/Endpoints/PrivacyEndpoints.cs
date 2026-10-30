using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
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

public sealed record ProposeErasureReq(Guid TenantId, Guid PersonId, string Reason);
public sealed record DecideErasureReq(Guid TenantId, string? Note);

/// <summary>
/// PDPL support: the data subject's right of access and to a copy (G1), and the public privacy notice (G3).
/// </summary>
public static class PrivacyEndpoints
{
    /// <summary>Policy code an institution uses for its privacy notice (authored and approved like any policy).</summary>
    public const string PrivacyNoticeCode = "PRIVACY";
    private const int MaxAuditRows = 5000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static void MapPrivacyEndpoints(this WebApplication app)
    {
        // Everything EduNexus holds about one person, as a downloadable JSON file. The person may export their own
        // data; anyone else needs privacy:manage. Every export is audited.
        app.MapGet("/api/people/{id:guid}/personal-data", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var me = await ActingPersonAsync(db, ctx, tenantId, ct);
            if (me != id && !ctx.User.HasPermission("privacy:manage")) return Results.Forbid();
            var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == id, ct);
            if (person is null) return Results.NotFound(new { error = "Person not found." });

            var t = tenantId;
            var key = id.ToString();
            var sections = new Dictionary<string, object>
            {
                ["person"] = person,
                ["roleAssignments"] = await db.RoleAssignments.AsNoTracking().Where(x => x.TenantId == t && x.PersonId == id).ToListAsync(ct),
                ["delegations"] = await db.AuthorityDelegations.AsNoTracking().Where(x => x.TenantId == t && (x.FromPersonId == id || x.ToPersonId == id)).ToListAsync(ct),
                ["committeeMemberships"] = await db.CommitteeMembers.AsNoTracking().Where(x => x.TenantId == t && x.PersonId == id).ToListAsync(ct),
                ["meetingAttendance"] = await db.Attendances.AsNoTracking().Where(x => x.TenantId == t && x.PersonId == id).ToListAsync(ct),
                ["meetingVotes"] = await db.MeetingVotes.AsNoTracking().Where(x => x.TenantId == t && x.PersonId == id).ToListAsync(ct),
                ["minutesAuthoredOrApproved"] = await db.MeetingMinutes.AsNoTracking().Where(x => x.TenantId == t && (x.AuthorId == id || x.ApprovedBy == id))
                    .Select(x => new { x.Id, x.MeetingId, x.AuthorId, x.ApprovedBy, x.CreatedAt, x.ApprovedAt }).ToListAsync(ct),
                ["decisionActions"] = await db.DecisionActions.AsNoTracking().Where(x => x.TenantId == t && x.AssigneeId == id).ToListAsync(ct),
                ["decisionActionEvidence"] = await db.DecisionActionEvidences.AsNoTracking().Where(x => x.TenantId == t && x.UploadedBy == id).ToListAsync(ct),
                ["tasks"] = await db.WorkTasks.AsNoTracking().Where(x => x.TenantId == t && x.AssigneeId == id).ToListAsync(ct),
                ["taskComments"] = await db.TaskComments.AsNoTracking().Where(x => x.TenantId == t && x.AuthorId == id).ToListAsync(ct),
                ["taskEvidence"] = await db.TaskEvidences.AsNoTracking().Where(x => x.TenantId == t && x.UploadedBy == id).ToListAsync(ct),
                ["approvals"] = await db.Approvals.AsNoTracking().Where(x => x.TenantId == t && (x.AssigneeId == id || x.DecidedBy == id)).ToListAsync(ct),
                ["requestsSubmitted"] = await db.Requests.AsNoTracking().Where(x => x.TenantId == t && x.SubmitterId == id).ToListAsync(ct),
                ["formSubmissions"] = await db.FormSubmissions.AsNoTracking().Where(x => x.TenantId == t && x.SubmittedBy == id).ToListAsync(ct),
                // Correspondence content belongs to the institution; the data subject gets the metadata about them.
                ["correspondenceAuthored"] = await db.Correspondences.AsNoTracking().Where(x => x.TenantId == t && x.AuthorId == id)
                    .Select(x => new { x.Id, x.Number, x.Type, x.Subject, x.IsConfidential, x.Status, x.CreatedAt }).ToListAsync(ct),
                ["correspondenceParticipant"] = await db.Correspondents.AsNoTracking().Where(x => x.TenantId == t && x.PersonId == id).ToListAsync(ct),
                ["correspondenceRouting"] = await db.CorrespondenceRoutingSlips.AsNoTracking().Where(x => x.TenantId == t && (x.FromPersonId == id || x.ToPersonId == id)).ToListAsync(ct),
                ["communicationsAuthored"] = await db.Communications.AsNoTracking().Where(x => x.TenantId == t && x.AuthorId == id).ToListAsync(ct),
                ["communicationsReceived"] = await db.CommunicationRecipients.AsNoTracking().Where(x => x.TenantId == t && x.PersonId == id).ToListAsync(ct),
                ["notifications"] = await db.Notifications.AsNoTracking().Where(x => x.TenantId == t && x.PersonId == id).ToListAsync(ct),
                ["notificationPreferences"] = await db.NotificationPreferences.AsNoTracking().Where(x => x.TenantId == t && x.PersonId == id).ToListAsync(ct),
                ["policyAcknowledgements"] = await db.PolicyAcknowledgements.AsNoTracking().Where(x => x.TenantId == t && x.PersonId == id).ToListAsync(ct),
                ["policiesOwned"] = await db.Policies.AsNoTracking().Where(x => x.TenantId == t && x.OwnerId == id).Select(x => new { x.Id, x.Code, x.Title, x.Status }).ToListAsync(ct),
                ["documentShares"] = await db.DocumentShares.AsNoTracking().Where(x => x.TenantId == t && x.PersonId == id).ToListAsync(ct),
                ["calendarEventsOwned"] = await db.CalendarEvents.AsNoTracking().Where(x => x.TenantId == t && x.OwnerId == id).ToListAsync(ct),
                ["scheduledActivities"] = await db.ScheduledActivities.AsNoTracking().Where(x => x.TenantId == t && x.AssigneeId == id).ToListAsync(ct),
                ["comments"] = await db.RecordComments.AsNoTracking().Where(x => x.TenantId == t && x.AuthorId == id).ToListAsync(ct),
                ["following"] = await db.EntityFollowers.AsNoTracking().Where(x => x.TenantId == t && x.PersonId == id).ToListAsync(ct),
                ["correctiveActions"] = await db.CorrectiveActions.AsNoTracking().Where(x => x.TenantId == t && x.AssigneeId == id).ToListAsync(ct),
                ["aiInteractions"] = await db.AiInteractions.AsNoTracking().Where(x => x.TenantId == t && x.RequestedBy == id).ToListAsync(ct),
                ["auditTrail"] = await db.AuditEvents.AsNoTracking()
                    .Where(x => x.TenantId == t && (x.ActorId == id || (x.EntityType == nameof(Person) && x.EntityId == key)))
                    .OrderByDescending(x => x.At).Take(MaxAuditRows).ToListAsync(ct),
            };

            DomainEvents.Record(db, tenantId, "PersonalDataExported", "PersonalDataExported", nameof(Person), key,
                payload: new { tenantId, personId = id, bySelf = me == id }, actorId: me,
                details: me == id ? "Self-service export" : "Exported by privacy officer");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);

            var export = new
            {
                exportedAt = DateTimeOffset.UtcNow,
                tenantId,
                personId = id,
                notice = "Personal data held about you in EduNexus (PDPL right of access). Correspondence content is institutional and listed as metadata only.",
                sections,
            };
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(export, Json), "application/json; charset=utf-8", $"personal-data-{id:N}.json");
        }).RequireAuthorization().WithTags("Privacy");

        // --- Right to destruction (G2): erase a person's identity, keep institutional records ---
        var erasure = app.MapGroup("/api/privacy/erasure-requests").WithTags("Privacy").RequireAuthorization();

        erasure.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("privacy:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.ErasureRequests.Where(r => r.TenantId == tenantId).OrderByDescending(r => r.ProposedAt).ToListAsync(ct));
        });

        erasure.MapPost("/", async (AppDbContext db, HttpContext ctx, ProposeErasureReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("privacy:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (string.IsNullOrWhiteSpace(req.Reason)) return Results.BadRequest(new { error = "A reason (e.g. the data subject's request reference) is required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.PersonId, ct)) return Results.NotFound(new { error = "Person not found." });
            if (await db.ErasureRequests.AnyAsync(r => r.TenantId == req.TenantId && r.PersonId == req.PersonId
                    && (r.Status == ErasureStatus.Proposed || r.Status == ErasureStatus.Approved || r.Status == ErasureStatus.Executed), ct))
                return Results.Conflict(new { error = "An erasure for this person is already open or done." });
            if (await HeldRecordsAsync(db, req.TenantId, req.PersonId, ct) is > 0 and var held)
                return Results.Conflict(new { error = $"{held} record(s) involving this person are under a legal hold; release the hold(s) first." });
            var r = new ErasureRequest(Guid.NewGuid(), req.TenantId, req.PersonId, Erasure.PseudonymFor(req.PersonId), req.Reason.Trim(),
                ErasureStatus.Proposed, Actor(ctx), DateTimeOffset.UtcNow, null, null, null, null, null);
            db.ErasureRequests.Add(r);
            DomainEvents.Record(db, req.TenantId, "ErasureProposed", "ErasureProposed", nameof(Person), req.PersonId.ToString(),
                payload: new { tenantId = req.TenantId, requestId = r.Id }, details: r.Reason);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/privacy/erasure-requests/{r.Id}", r);
        });

        erasure.MapPost("/{id:guid}/approve", (AppDbContext db, HttpContext ctx, Guid id, DecideErasureReq req, CancellationToken ct) =>
            DecideErasureAsync(db, ctx, id, req, approve: true, ct));
        erasure.MapPost("/{id:guid}/reject", (AppDbContext db, HttpContext ctx, Guid id, DecideErasureReq req, CancellationToken ct) =>
            DecideErasureAsync(db, ctx, id, req, approve: false, ct));

        erasure.MapPost("/{id:guid}/execute", async (AppDbContext db, HttpContext ctx, Guid id, DecideErasureReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("privacy:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var r = await db.ErasureRequests.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (r is null) return Results.NotFound(new { error = "Erasure request not found." });
            if (r.Status != ErasureStatus.Approved) return Results.Conflict(new { error = $"Only approved requests can be executed (status is {r.Status})." });
            // A hold placed after approval still wins.
            if (await HeldRecordsAsync(db, req.TenantId, r.PersonId, ct) is > 0 and var held)
                return Results.Conflict(new { error = $"{held} record(s) involving this person went under a legal hold after approval; release it first." });

            var t = req.TenantId; var pid = r.PersonId; var now = DateTimeOffset.UtcNow;
            var person = await db.People.FirstAsync(p => p.TenantId == t && p.Id == pid, ct);
            db.Entry(person).CurrentValues.SetValues(Erasure.Pseudonymise(person));

            // Personal, non-institutional data: deleted.
            var roles = await db.RoleAssignments.Where(x => x.TenantId == t && x.PersonId == pid).ToListAsync(ct);
            var delegations = await db.AuthorityDelegations.Where(x => x.TenantId == t && (x.FromPersonId == pid || x.ToPersonId == pid)).ToListAsync(ct);
            var prefs = await db.NotificationPreferences.Where(x => x.TenantId == t && x.PersonId == pid).ToListAsync(ct);
            var notes = await db.Notifications.Where(x => x.TenantId == t && x.PersonId == pid).ToListAsync(ct);
            var follows = await db.EntityFollowers.Where(x => x.TenantId == t && x.PersonId == pid).ToListAsync(ct);
            var shares = await db.DocumentShares.Where(x => x.TenantId == t && x.PersonId == pid).ToListAsync(ct);
            var feeds = await db.CalendarFeedTokens.Where(x => x.TenantId == t && x.PersonId == pid).ToListAsync(ct);
            db.RemoveRange(roles); db.RemoveRange(delegations); db.RemoveRange(prefs); db.RemoveRange(notes); db.RemoveRange(follows); db.RemoveRange(shares); db.RemoveRange(feeds);

            // Institutional records kept, identity replaced.
            var correspondents = await db.Correspondents.Where(x => x.TenantId == t && x.PersonId == pid).ToListAsync(ct);
            foreach (var c in correspondents) db.Entry(c).CurrentValues.SetValues(c with { DisplayName = r.Pseudonym });
            var ai = await db.AiInteractions.Where(x => x.TenantId == t && x.RequestedBy == pid).ToListAsync(ct);
            foreach (var a in ai) db.Entry(a).CurrentValues.SetValues(a with { InputExcerpt = "[erased]", OutputExcerpt = "[erased]" });
            var memberships = await db.CommitteeMembers.Where(x => x.TenantId == t && x.PersonId == pid && x.TermEndsAt == null).ToListAsync(ct);
            foreach (var m in memberships) db.Entry(m).CurrentValues.SetValues(m with { TermEndsAt = now });

            var summary = $"Directory record pseudonymised as '{r.Pseudonym}'. Deleted: {roles.Count} role assignment(s), {delegations.Count} delegation(s), "
                + $"{prefs.Count} notification preference(s), {notes.Count} notification(s), {follows.Count} follow(s), {shares.Count} document share(s), {feeds.Count} calendar link(s). "
                + $"Renamed in {correspondents.Count} correspondence participant row(s); {ai.Count} AI interaction(s) redacted; {memberships.Count} committee term(s) ended. "
                + "Votes, approvals, minutes and the audit trail are kept and now refer to the pseudonym. Remove or disable the account in the identity provider.";
            var executed = r with { Status = ErasureStatus.Executed, ExecutedAt = now, Summary = summary };
            db.Entry(r).CurrentValues.SetValues(executed);
            DomainEvents.Record(db, t, "PersonErased", "PersonErased", nameof(Person), pid.ToString(),
                payload: new { tenantId = t, requestId = r.Id }, details: r.Pseudonym);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(executed);
        });

        // The institution's published privacy notice, readable before sign-in (tenant by slug or id).
        app.MapGet("/api/public/privacy-notice", async (AppDbContext db, string tenant, CancellationToken ct) =>
        {
            var t = Guid.TryParse(tenant, out var tid)
                ? await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tid && x.IsActive, ct)
                : await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == tenant && x.IsActive, ct);
            if (t is null) return Results.NotFound(new { error = "Institution not found." });
            await using var scope = await TenantScope.BeginAsync(db, t.Id, ct);
            var notice = await db.Policies.AsNoTracking()
                .Where(p => p.TenantId == t.Id && p.Code == PrivacyNoticeCode && p.Status == PolicyStatus.Published)
                .Select(p => new { p.Title, p.Content, p.Version }).FirstOrDefaultAsync(ct);
            if (notice is null) return Results.NotFound(new { error = "This institution has not published a privacy notice yet." });
            return Results.Ok(new { institution = t.Name, notice.Title, notice.Content, notice.Version });
        }).AllowAnonymous().WithTags("Privacy");
    }

    private static string Actor(HttpContext ctx) =>
        ctx.User.FindFirst("sub")?.Value ?? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "unknown";

    /// <summary>Active legal holds on correspondence the person wrote or took part in.</summary>
    private static async Task<int> HeldRecordsAsync(AppDbContext db, Guid tenantId, Guid personId, CancellationToken ct)
    {
        var authored = db.Correspondences.Where(c => c.TenantId == tenantId && c.AuthorId == personId).Select(c => c.Id);
        var participated = db.Correspondents.Where(c => c.TenantId == tenantId && c.PersonId == personId).Select(c => c.CorrespondenceId);
        return await db.RecordHolds.CountAsync(h => h.TenantId == tenantId && h.ReleasedAt == null && h.EntityType == "Correspondence"
            && (authored.Contains(h.EntityId) || participated.Contains(h.EntityId)), ct);
    }

    private static async Task<IResult> DecideErasureAsync(AppDbContext db, HttpContext ctx, Guid id, DecideErasureReq req, bool approve, CancellationToken ct)
    {
        if (!ctx.User.HasPermission("privacy:manage")) return Results.Forbid();
        if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
        if (!approve && string.IsNullOrWhiteSpace(req.Note)) return Results.BadRequest(new { error = "A reason is required to reject an erasure." });
        await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
        var r = await db.ErasureRequests.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
        if (r is null) return Results.NotFound(new { error = "Erasure request not found." });
        if (r.Status != ErasureStatus.Proposed) return Results.Conflict(new { error = $"Only proposed requests can be decided (status is {r.Status})." });
        var actor = Actor(ctx);
        if (string.Equals(actor, r.ProposedBy, StringComparison.Ordinal))
            return Results.Json(new { error = "Segregation of duties: the proposer cannot decide their own erasure request." }, statusCode: StatusCodes.Status403Forbidden);
        var decided = r with { Status = approve ? ErasureStatus.Approved : ErasureStatus.Rejected, DecidedBy = actor, DecidedAt = DateTimeOffset.UtcNow, DecisionNote = req.Note?.Trim() };
        db.Entry(r).CurrentValues.SetValues(decided);
        var evt = approve ? "ErasureApproved" : "ErasureRejected";
        DomainEvents.Record(db, req.TenantId, evt, evt, nameof(Person), r.PersonId.ToString(), payload: new { tenantId = req.TenantId, requestId = r.Id }, details: req.Note);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return Results.Ok(decided);
    }
}
