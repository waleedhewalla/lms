using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EduNexus.Api.Auth;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static EduNexus.Api.Endpoints.EndpointHelpers;

namespace EduNexus.Api.Endpoints;

/// <summary>
/// Personal calendar subscription: a link a phone or Outlook can poll without signing in. The link is a bearer secret, so
/// it is shown once, stored only as a hash, expires (CalendarFeed:TokenDays, default 90) and is replaced or revoked on request.
/// The feed holds institutional dates plus what concerns that person: their committees' meetings, their open tasks and
/// their assigned actions.
/// </summary>
public static partial class CalendarFeedEndpoints
{
    // 32 hex characters of tenant id, then 32 random bytes as base64url (43 characters).
    [GeneratedRegex("^[0-9a-f]{32}[A-Za-z0-9_-]{43}$")]
    private static partial Regex TokenShape();

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant();

    private static IResult NotLinked() =>
        Results.Conflict(new { error = "Your account is not linked to a person in the directory, so it has no personal calendar." });

    public static void MapCalendarFeedEndpoints(this IEndpointRouteBuilder app)
    {
        var mine = app.MapGroup("/api/calendar/feed-token").WithTags("Calendar").RequireAuthorization();

        mine.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("calendar:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (await ActingPersonAsync(db, ctx, tenantId, ct) is not { } me) return NotLinked();
            var now = DateTimeOffset.UtcNow;
            var row = await db.CalendarFeedTokens.Where(t => t.TenantId == tenantId && t.PersonId == me && t.RevokedAt == null && t.ExpiresAt > now)
                .OrderByDescending(t => t.CreatedAt).FirstOrDefaultAsync(ct);
            return Results.Ok(row is null ? new { active = false, createdAt = (DateTimeOffset?)null, expiresAt = (DateTimeOffset?)null, lastUsedAt = (DateTimeOffset?)null }
                : new { active = true, createdAt = (DateTimeOffset?)row.CreatedAt, expiresAt = (DateTimeOffset?)row.ExpiresAt, lastUsedAt = row.LastUsedAt });
        });

        // Issues a new link and revokes the previous one. The link is in this response and nowhere else, ever.
        mine.MapPost("/", async (AppDbContext db, HttpContext ctx, IConfiguration config, CalendarFeedReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("calendar:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (await ActingPersonAsync(db, ctx, req.TenantId, ct) is not { } me) return NotLinked();
            var now = DateTimeOffset.UtcNow;
            foreach (var old in await db.CalendarFeedTokens.Where(t => t.TenantId == req.TenantId && t.PersonId == me && t.RevokedAt == null).ToListAsync(ct))
                db.Entry(old).CurrentValues.SetValues(old with { RevokedAt = now });
            var token = req.TenantId.ToString("N") + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            var expires = now.AddDays(Math.Clamp(config.GetValue("CalendarFeed:TokenDays", 90), 1, 365));
            var row = new CalendarFeedToken(Guid.NewGuid(), req.TenantId, me, Hash(token), now, expires);
            db.CalendarFeedTokens.Add(row);
            DomainEvents.Record(db, req.TenantId, "CalendarFeedIssued", "CalendarFeedIssued", nameof(Person), me.ToString(), details: $"expires {expires:O}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { path = $"/api/calendar/feed/{token}.ics", expiresAt = expires });
        });

        mine.MapDelete("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("calendar:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            if (await ActingPersonAsync(db, ctx, tenantId, ct) is not { } me) return NotLinked();
            var now = DateTimeOffset.UtcNow;
            var active = await db.CalendarFeedTokens.Where(t => t.TenantId == tenantId && t.PersonId == me && t.RevokedAt == null).ToListAsync(ct);
            foreach (var t in active) db.Entry(t).CurrentValues.SetValues(t with { RevokedAt = now });
            if (active.Count > 0)
                DomainEvents.Record(db, tenantId, "CalendarFeedRevoked", "CalendarFeedRevoked", nameof(Person), me.ToString(), details: $"{active.Count} link(s)");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.NoContent();
        });

        // Anonymous by design: the secret in the path is the credential. Every failure looks the same (404), so a
        // wrong, expired, revoked or foreign link reveals nothing about which it was.
        app.MapGet("/api/calendar/feed/{token}.ics", async (AppDbContext db, HttpContext ctx, string token, CancellationToken ct) =>
        {
            if (!TokenShape().IsMatch(token) || !Guid.TryParseExact(token[..32], "N", out var tenantId)) return Results.NotFound();
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var now = DateTimeOffset.UtcNow;
            var hash = Hash(token);
            var row = await db.CalendarFeedTokens.FirstOrDefaultAsync(t => t.TenantId == tenantId && t.TokenHash == hash && t.RevokedAt == null && t.ExpiresAt > now, ct);
            if (row is null) return Results.NotFound();
            var person = await db.People.FirstOrDefaultAsync(p => p.TenantId == tenantId && p.Id == row.PersonId && p.IsActive, ct);
            if (person is null) return Results.NotFound();

            var items = await PersonalItemsAsync(db, tenantId, row.PersonId, now.AddDays(-30), now.AddDays(180), ct);
            if (row.LastUsedAt is null || now - row.LastUsedAt > TimeSpan.FromMinutes(10))
            {
                db.Entry(row).CurrentValues.SetValues(row with { LastUsedAt = now });
                await db.SaveChangesAsync(ct);
                await scope.CommitAsync(ct);
            }
            ctx.Response.Headers.CacheControl = "private, max-age=300";
            ctx.Response.Headers["X-Robots-Tag"] = "noindex";
            return Results.Text(GovernanceDepthEndpoints.ToIcs(items, "EduNexus"), "text/calendar; charset=utf-8");
        }).AllowAnonymous().WithTags("Calendar");
    }

    /// <summary>Institutional dates for everyone, plus this person's meetings, open tasks and assigned actions. Call inside a TenantScope.</summary>
    internal static async Task<List<CalendarItem>> PersonalItemsAsync(AppDbContext db, Guid tenantId, Guid personId, DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var items = new List<CalendarItem>();
        items.AddRange((await db.CalendarEvents.Where(e => e.TenantId == tenantId && e.EndsAt >= start && e.StartsAt <= end).ToListAsync(ct))
            .Select(e => new CalendarItem(e.Id, e.Title, e.Kind.ToString(), e.StartsAt, e.EndsAt, e.Location, e.SourceType ?? nameof(CalendarEvent), e.SourceId ?? e.Id)));

        var committees = await db.CommitteeMembers.Where(m => m.TenantId == tenantId && m.PersonId == personId && (m.TermEndsAt == null || m.TermEndsAt > now))
            .Select(m => m.CommitteeId).ToListAsync(ct);
        items.AddRange((await db.Meetings.Where(m => m.TenantId == tenantId && committees.Contains(m.CommitteeId) && m.Status != MeetingStatus.Cancelled && m.StartsAt >= start && m.StartsAt <= end).ToListAsync(ct))
            .Select(m => new CalendarItem(m.Id, m.Title, nameof(CalendarEventKind.Meeting), m.StartsAt, m.StartsAt.AddHours(2), null, nameof(Meeting), m.Id)));

        items.AddRange((await db.Policies.Where(p => p.TenantId == tenantId && p.NextReviewAt != null && p.NextReviewAt >= start && p.NextReviewAt <= end).ToListAsync(ct))
            .Select(p => new CalendarItem(p.Id, $"Policy review: {p.Code}", nameof(CalendarEventKind.PolicyReview), p.NextReviewAt!.Value, p.NextReviewAt!.Value, null, nameof(Policy), p.Id)));

        items.AddRange((await db.DecisionActions.Where(a => a.TenantId == tenantId && a.AssigneeId == personId && a.DueAt >= start && a.DueAt <= end &&
                (a.Status == DecisionActionStatus.Assigned || a.Status == DecisionActionStatus.InProgress)).ToListAsync(ct))
            .Select(a => new CalendarItem(a.Id, $"Action due: {a.Description}", nameof(CalendarEventKind.Deadline), a.DueAt, a.DueAt, null, nameof(DecisionAction), a.Id)));

        items.AddRange((await db.WorkTasks.Where(t => t.TenantId == tenantId && t.AssigneeId == personId && t.DueAt >= start && t.DueAt <= end &&
                (t.Status == WorkTaskStatus.Open || t.Status == WorkTaskStatus.InProgress || t.Status == WorkTaskStatus.Breached)).ToListAsync(ct))
            .Select(t => new CalendarItem(t.Id, $"Task due: {t.Title}", nameof(CalendarEventKind.Deadline), t.DueAt, t.DueAt, null, nameof(WorkTask), t.Id)));

        return items.OrderBy(i => i.StartsAt).ToList();
    }
}

public sealed record CalendarFeedReq(Guid TenantId);
