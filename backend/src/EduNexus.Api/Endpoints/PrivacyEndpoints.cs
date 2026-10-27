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
}
