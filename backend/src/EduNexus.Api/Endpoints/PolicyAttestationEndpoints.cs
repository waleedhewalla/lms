using System;
using System.Collections.Generic;
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

public sealed record QuizQuestionReq(string Question, List<string> Options, int CorrectIndex);
public sealed record PolicyAttestationSettingsReq(Guid TenantId, int? AttestEveryMonths, int? QuizPassPercent, List<QuizQuestionReq>? Questions);
public sealed record AttestPolicyReq(Guid TenantId, List<int>? Answers);

/// <summary>
/// Wave J: policy attestation. Acknowledgements are tied to the policy version, can expire on a cycle,
/// and can require passing a short quiz. The person always comes from the token.
/// </summary>
public static class PolicyAttestationEndpoints
{
    public static void MapPolicyAttestationEndpoints(this IEndpointRouteBuilder app)
    {
        var pol = app.MapGroup("/api/policies").WithTags("Policies").RequireAuthorization();

        // Configure the cycle, pass mark and quiz. Questions, when given, replace the existing set.
        pol.MapPut("/{id:guid}/attestation", async (AppDbContext db, HttpContext ctx, Guid id, PolicyAttestationSettingsReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (req.AttestEveryMonths is { } m && (m < 1 || m > 60)) return Results.BadRequest(new { error = "AttestEveryMonths must be 1–60 (omit for once per version)." });
            if (req.QuizPassPercent is { } pp && (pp < 1 || pp > 100)) return Results.BadRequest(new { error = "QuizPassPercent must be 1–100." });
            var questions = req.Questions ?? [];
            if (questions.Count > 20) return Results.BadRequest(new { error = "At most 20 questions." });
            for (var i = 0; i < questions.Count; i++)
            {
                var q = questions[i];
                var opts = (q.Options ?? []).Select(o => o?.Trim() ?? "").ToList();
                if (string.IsNullOrWhiteSpace(q.Question) || opts.Count is < 2 or > 6 || opts.Any(o => o.Length == 0 || o.Contains('\n')))
                    return Results.BadRequest(new { error = $"Question {i + 1}: a question and 2–6 single-line options are required." });
                if (q.CorrectIndex < 0 || q.CorrectIndex >= opts.Count) return Results.BadRequest(new { error = $"Question {i + 1}: the correct answer is out of range." });
            }
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Policy not found." });
            var updated = p with { AttestEveryMonths = req.AttestEveryMonths, QuizPassPercent = req.QuizPassPercent ?? p.QuizPassPercent };
            db.Entry(p).CurrentValues.SetValues(updated);
            if (req.Questions is not null)
            {
                db.PolicyQuizQuestions.RemoveRange(await db.PolicyQuizQuestions.Where(q => q.TenantId == req.TenantId && q.PolicyId == id).ToListAsync(ct));
                db.PolicyQuizQuestions.AddRange(questions.Select((q, i) => new PolicyQuizQuestion(Guid.NewGuid(), req.TenantId, id, i + 1,
                    q.Question.Trim(), string.Join('\n', q.Options.Select(o => o.Trim())), q.CorrectIndex)));
            }
            DomainEvents.Record(db, req.TenantId, "PolicyAttestationConfigured", "PolicyAttestationConfigured", nameof(Policy), id.ToString(),
                details: $"cycle={req.AttestEveryMonths?.ToString() ?? "version"} pass={updated.QuizPassPercent} questions={(req.Questions is null ? "unchanged" : questions.Count)}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(updated);
        });

        // The quiz. Answers are shown only to people who can edit the policy.
        pol.MapGet("/{id:guid}/quiz", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Policy not found." });
            var editor = ctx.User.HasPermission("policy:create");
            var qs = await db.PolicyQuizQuestions.Where(q => q.TenantId == tenantId && q.PolicyId == id).OrderBy(q => q.Order).ToListAsync(ct);
            return Results.Ok(new
            {
                policyId = id, p.Version, p.AttestEveryMonths, passPercent = p.QuizPassPercent,
                questions = qs.Select(q => new { q.Order, q.Question, options = q.Options, correctIndex = editor ? q.CorrectIndex : (int?)null }),
            });
        });

        pol.MapPost("/{id:guid}/attest", (AppDbContext db, HttpContext ctx, Guid id, AttestPolicyReq req, CancellationToken ct) =>
            AttestAsync(db, ctx, id, req.TenantId, null, req.Answers, ct));

        // What the signed-in person must attest: never attested, a newer version, or the cycle came round.
        pol.MapGet("/my-attestations", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var me = await ActingPersonAsync(db, ctx, tenantId, ct);
            if (me is null) return Results.Json(new { error = "Your account is not linked to a person." }, statusCode: StatusCodes.Status403Forbidden);
            var now = DateTimeOffset.UtcNow;
            var policies = await db.Policies.Where(p => p.TenantId == tenantId && p.Status == PolicyStatus.Published).OrderBy(p => p.Code).ToListAsync(ct);
            var acks = await db.PolicyAcknowledgements.Where(a => a.TenantId == tenantId && a.PersonId == me).ToListAsync(ct);
            var quizCounts = await db.PolicyQuizQuestions.Where(q => q.TenantId == tenantId).GroupBy(q => q.PolicyId)
                .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
            return Results.Ok(policies.Select(p =>
            {
                // The most relevant attestation: the highest version, then the latest.
                var last = acks.Where(a => a.PolicyId == p.Id).OrderByDescending(a => a.PolicyVersion).ThenByDescending(a => a.At).FirstOrDefault();
                var current = last is not null && PolicyAttestation.IsCurrent(last, p, now);
                var reason = current ? null : last is null ? "never" : last.PolicyVersion != p.Version ? "new-version" : "cycle";
                return new
                {
                    policyId = p.Id, p.Code, p.Title, p.Version, current, reason, lastAttestedAt = last?.At, lastVersion = last?.PolicyVersion,
                    dueAgain = current ? PolicyAttestation.DueAgain(last, p) : null, quizQuestions = quizCounts.GetValueOrDefault(p.Id),
                };
            }).OrderBy(x => x.current));
        });

        // Compliance per published policy: current attestations among active people.
        pol.MapGet("/attestation-status", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var now = DateTimeOffset.UtcNow;
            var people = await db.People.Where(p => p.TenantId == tenantId && p.IsActive).Select(p => p.Id).ToListAsync(ct);
            var policies = await db.Policies.Where(p => p.TenantId == tenantId && p.Status == PolicyStatus.Published).OrderBy(p => p.Code).ToListAsync(ct);
            var pids = policies.Select(p => p.Id).ToList();
            var acks = await db.PolicyAcknowledgements.Where(a => a.TenantId == tenantId && pids.Contains(a.PolicyId)).ToListAsync(ct);
            var quiz = await db.PolicyQuizQuestions.Where(q => q.TenantId == tenantId && pids.Contains(q.PolicyId)).GroupBy(q => q.PolicyId)
                .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
            return Results.Ok(policies.Select(p =>
            {
                var current = CurrentAttesters(acks, p, now).Intersect(people).Count();
                return new
                {
                    policyId = p.Id, p.Code, p.Title, p.Version, p.AttestEveryMonths, passPercent = p.QuizPassPercent, quizQuestions = quiz.GetValueOrDefault(p.Id),
                    activePeople = people.Count, current, outstanding = people.Count - current,
                    percent = people.Count == 0 ? 0 : (int)Math.Round(100.0 * current / people.Count),
                };
            }));
        });

        // Remind everyone without a current attestation (in-app notification).
        pol.MapPost("/{id:guid}/remind", async (AppDbContext db, HttpContext ctx, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("policy:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (p is null) return Results.NotFound(new { error = "Policy not found." });
            if (p.Status != PolicyStatus.Published) return Results.Conflict(new { error = "Only published policies are attested." });
            var now = DateTimeOffset.UtcNow;
            var done = CurrentAttesters(await db.PolicyAcknowledgements.Where(a => a.TenantId == req.TenantId && a.PolicyId == id).ToListAsync(ct), p, now);
            var outstanding = await db.People.Where(x => x.TenantId == req.TenantId && x.IsActive && !done.Contains(x.Id)).Select(x => x.Id).ToListAsync(ct);
            foreach (var person in outstanding)
                db.Notifications.Add(new Notification(Guid.NewGuid(), req.TenantId, person, $"Please acknowledge: {p.Code} {p.Title}",
                    $"Version {p.Version} of this policy needs your acknowledgement.", NotificationChannel.InApp, NotificationStatus.Sent, now, NotificationPriority.Important));
            DomainEvents.Record(db, req.TenantId, "PolicyAttestationReminded", "PolicyAttestationReminded", nameof(Policy), id.ToString(), details: $"{outstanding.Count} people");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(new { reminded = outstanding.Count });
        });
    }

    public static List<Guid> CurrentAttesters(IEnumerable<PolicyAcknowledgement> acks, Policy p, DateTimeOffset now) =>
        acks.Where(a => a.PolicyId == p.Id && PolicyAttestation.IsCurrent(a, p, now)).Select(a => a.PersonId).Distinct().ToList();

    /// <summary>
    /// Record an attestation for the caller: the policy must be published, any quiz passed, and no current attestation exist.
    /// A failed quiz is audited but stores nothing; the response gives the score, never the answers.
    /// </summary>
    public static async Task<IResult> AttestAsync(AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, Guid? claimedPerson, List<int>? answers, CancellationToken ct)
    {
        if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
        if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
        await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
        var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
        if (p is null) return Results.NotFound(new { error = "Policy not found." });
        if (p.Status != PolicyStatus.Published) return Results.Conflict(new { error = "Cannot acknowledge unpublished policy." });
        // People acknowledge for themselves only: the person comes from the token, never the body.
        var actor = await ActingPersonAsync(db, ctx, tenantId, ct);
        if (actor is null || (claimedPerson is { } claimed && claimed != actor)) return Results.Forbid();
        var now = DateTimeOffset.UtcNow;
        var mine = await db.PolicyAcknowledgements.Where(a => a.TenantId == tenantId && a.PolicyId == id && a.PersonId == actor).ToListAsync(ct);
        if (mine.Any(a => PolicyAttestation.IsCurrent(a, p, now)))
            return Results.Conflict(new { error = "Person has already acknowledged this policy." });
        var questions = await db.PolicyQuizQuestions.Where(q => q.TenantId == tenantId && q.PolicyId == id).ToListAsync(ct);
        int? score = null;
        if (questions.Count > 0)
        {
            score = PolicyAttestation.Score(questions, answers);
            if (score < p.QuizPassPercent)
            {
                DomainEvents.Record(db, tenantId, "PolicyQuizFailed", "PolicyQuizFailed", nameof(Policy), id.ToString(), actorId: actor, details: $"score={score}");
                await db.SaveChangesAsync(ct);
                await scope.CommitAsync(ct);
                return Results.UnprocessableEntity(new { error = $"Quiz score {score}% is below the pass mark of {p.QuizPassPercent}%. Re-read the policy and try again.", score, passPercent = p.QuizPassPercent });
            }
        }
        var ack = new PolicyAcknowledgement(Guid.NewGuid(), tenantId, id, actor.Value, now, p.Version, score);
        db.PolicyAcknowledgements.Add(ack);
        DomainEvents.Record(db, tenantId, "PolicyAcknowledged", "PolicyAcknowledged", nameof(Policy), id.ToString(),
            payload: new { tenantId, policyId = id, personId = actor, version = p.Version, score }, actorId: actor, details: actor.ToString());
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return Results.Created($"/api/policies/{id}/acknowledgements", ack);
    }
}
