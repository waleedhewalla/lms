using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EduNexus.Api.Auth;
using EduNexus.Api.Storage;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using static EduNexus.Api.Endpoints.EndpointHelpers;

namespace EduNexus.Api.Endpoints;

public sealed record CreateRetentionScheduleReq(Guid TenantId, string Code, string Name, string EntityType, string RecordClass,
    int? RetentionYears, string Disposition, string? LegalReference);
public sealed record PlaceHoldReq(Guid TenantId, string EntityType, Guid EntityId, string Reason);
public sealed record ProposeDisposalReq(Guid TenantId, Guid ScheduleId);
public sealed record DecideDisposalReq(Guid TenantId, string? Note);

/// <summary>
/// Records retention &amp; disposal (Wave H). Schedules say how long each kind of record is kept; records past
/// retention and not on hold are proposed in a batch, approved by a different person, then executed:
/// destroy (redact correspondence, delete stored document files) or transfer to the national archive.
/// Disposal items are never deleted — they are the disposal register.
/// </summary>
public static class RecordsEndpoints
{
    /// <summary>Upper bound per batch so one approval never covers an unreviewable volume.</summary>
    private const int MaxBatchItems = 1000;

    private sealed record Candidate(string EntityType, Guid EntityId, string Reference, string Title, DateTimeOffset RecordDate, DateTimeOffset EligibleAt);

    private static string Actor(HttpContext ctx) =>
        ctx.User.FindFirstValue("sub") ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

    public static void MapRecordsEndpoints(this WebApplication app)
    {
        var records = app.MapGroup("/api/records").WithTags("Records").RequireAuthorization();

        // --- Schedules ---
        records.MapGet("/schedules", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("records:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.RetentionSchedules.Where(s => s.TenantId == tenantId).OrderBy(s => s.Code).ToListAsync(ct));
        });

        records.MapPost("/schedules", async (AppDbContext db, HttpContext ctx, CreateRetentionScheduleReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("records:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enum.TryParse<RecordClass>(req.RecordClass, true, out var cls))
                return Results.BadRequest(new { error = "RecordClass must be Administrative, Financial or Specialized." });
            if (!Enum.TryParse<Disposition>(req.Disposition, true, out var disposition))
                return Results.BadRequest(new { error = "Disposition must be Destroy, TransferToArchive or Permanent." });
            if (RetentionRules.Validate(req.Code, req.Name, req.EntityType, req.RetentionYears, disposition) is { } invalid)
                return Results.BadRequest(new { error = invalid });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var s = new RetentionSchedule(Guid.NewGuid(), req.TenantId, req.Code.Trim(), req.Name.Trim(), req.EntityType, cls,
                req.RetentionYears, disposition, req.LegalReference?.Trim(), true);
            db.RetentionSchedules.Add(s);
            DomainEvents.Record(db, req.TenantId, "RetentionScheduleCreated", "RetentionScheduleCreated", nameof(RetentionSchedule), s.Id.ToString(),
                payload: new { tenantId = req.TenantId, scheduleId = s.Id, s.Code, s.EntityType, s.RetentionYears, disposition = s.Disposition.ToString() }, details: s.Code);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex)) { return Results.Conflict(new { error = $"Schedule code '{s.Code}' already exists." }); }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/records/schedules/{s.Id}", s);
        });

        // Preview of what a disposal batch for this schedule would contain right now.
        records.MapGet("/schedules/{id:guid}/eligible", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("records:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var s = await db.RetentionSchedules.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (s is null) return Results.NotFound(new { error = "Schedule not found." });
            return Results.Ok(await CandidatesAsync(db, tenantId, s, DateTimeOffset.UtcNow, ct));
        });

        // --- Legal holds ---
        records.MapGet("/holds", async (AppDbContext db, HttpContext ctx, Guid tenantId, bool? activeOnly, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("records:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var q = db.RecordHolds.Where(h => h.TenantId == tenantId);
            if (activeOnly ?? true) q = q.Where(h => h.ReleasedAt == null);
            return Results.Ok(await q.OrderByDescending(h => h.PlacedAt).ToListAsync(ct));
        });

        records.MapPost("/holds", async (AppDbContext db, HttpContext ctx, PlaceHoldReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("records:hold")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!RetentionRules.EntityTypes.Contains(req.EntityType)) return Results.BadRequest(new { error = $"EntityType must be one of: {string.Join(", ", RetentionRules.EntityTypes)}." });
            if (string.IsNullOrWhiteSpace(req.Reason)) return Results.BadRequest(new { error = "Reason is required." });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await RecordExistsAsync(db, req.TenantId, req.EntityType, req.EntityId, ct)) return Results.NotFound(new { error = "Record not found." });
            var h = new RecordHold(Guid.NewGuid(), req.TenantId, req.EntityType, req.EntityId, req.Reason.Trim(), Actor(ctx), DateTimeOffset.UtcNow, null, null);
            db.RecordHolds.Add(h);
            DomainEvents.Record(db, req.TenantId, "RecordHoldPlaced", "RecordHoldPlaced", req.EntityType, req.EntityId.ToString(),
                payload: new { tenantId = req.TenantId, holdId = h.Id, req.EntityType, req.EntityId }, details: h.Reason);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/records/holds/{h.Id}", h);
        });

        records.MapPost("/holds/{id:guid}/release", async (AppDbContext db, HttpContext ctx, Guid id, DecideDisposalReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("records:hold")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var h = await db.RecordHolds.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (h is null) return Results.NotFound(new { error = "Hold not found." });
            if (!h.IsActive) return Results.Conflict(new { error = "Hold already released." });
            var released = h with { ReleasedBy = Actor(ctx), ReleasedAt = DateTimeOffset.UtcNow };
            db.Entry(h).CurrentValues.SetValues(released);
            DomainEvents.Record(db, req.TenantId, "RecordHoldReleased", "RecordHoldReleased", h.EntityType, h.EntityId.ToString(),
                payload: new { tenantId = req.TenantId, holdId = h.Id }, details: req.Note ?? h.Reason);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(released);
        });

        // --- Disposal batches ---
        records.MapGet("/disposals", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("records:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var batches = await db.DisposalBatches.Where(b => b.TenantId == tenantId).OrderByDescending(b => b.ProposedAt).ToListAsync(ct);
            var counts = await db.DisposalItems.Where(i => i.TenantId == tenantId).GroupBy(i => i.BatchId)
                .Select(g => new { g.Key, n = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.n, ct);
            return Results.Ok(batches.Select(b => new { batch = b, items = counts.GetValueOrDefault(b.Id) }));
        });

        records.MapGet("/disposals/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("records:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var b = await db.DisposalBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (b is null) return Results.NotFound(new { error = "Disposal batch not found." });
            var items = await db.DisposalItems.Where(i => i.TenantId == tenantId && i.BatchId == id).OrderBy(i => i.RecordDate).ToListAsync(ct);
            return Results.Ok(new { batch = b, items });
        });

        records.MapPost("/disposals", async (AppDbContext db, HttpContext ctx, ProposeDisposalReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("records:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var s = await db.RetentionSchedules.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == req.ScheduleId, ct);
            if (s is null) return Results.NotFound(new { error = "Schedule not found." });
            if (!s.IsActive || s.Disposition == Disposition.Permanent)
                return Results.Conflict(new { error = "This schedule keeps records permanently (or is inactive); nothing to dispose." });
            var candidates = await CandidatesAsync(db, req.TenantId, s, DateTimeOffset.UtcNow, ct);
            if (candidates.Count == 0) return Results.Conflict(new { error = "No records are eligible for disposal under this schedule." });
            var batch = new DisposalBatch(Guid.NewGuid(), req.TenantId, s.Id, s.Disposition, DisposalStatus.Proposed,
                Actor(ctx), DateTimeOffset.UtcNow, null, null, null, null);
            db.DisposalBatches.Add(batch);
            foreach (var c in candidates.Take(MaxBatchItems))
                db.DisposalItems.Add(new DisposalItem(Guid.NewGuid(), req.TenantId, batch.Id, c.EntityType, c.EntityId, c.Reference, c.Title,
                    c.RecordDate, c.EligibleAt, DisposalOutcome.Pending, null, null));
            var count = Math.Min(candidates.Count, MaxBatchItems);
            DomainEvents.Record(db, req.TenantId, "DisposalProposed", "DisposalProposed", nameof(DisposalBatch), batch.Id.ToString(),
                payload: new { tenantId = req.TenantId, batchId = batch.Id, scheduleId = s.Id, items = count }, details: $"{s.Code}: {count} records");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/records/disposals/{batch.Id}", new { batch, items = count });
        });

        records.MapPost("/disposals/{id:guid}/approve", (AppDbContext db, HttpContext ctx, Guid id, DecideDisposalReq req, CancellationToken ct) =>
            DecideAsync(db, ctx, id, req, approve: true, ct));
        records.MapPost("/disposals/{id:guid}/reject", (AppDbContext db, HttpContext ctx, Guid id, DecideDisposalReq req, CancellationToken ct) =>
            DecideAsync(db, ctx, id, req, approve: false, ct));

        records.MapPost("/disposals/{id:guid}/execute", async (AppDbContext db, HttpContext ctx, IObjectStorage storage, ILoggerFactory logs,
            Guid id, DecideDisposalReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("records:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var b = await db.DisposalBatches.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (b is null) return Results.NotFound(new { error = "Disposal batch not found." });
            // ExecutedWithErrors can be re-run: only items still pending storage cleanup are retried.
            if (b.Status is not (DisposalStatus.Approved or DisposalStatus.ExecutedWithErrors))
                return Results.Conflict(new { error = $"Only approved batches can be executed (status is {b.Status})." });
            var schedule = await db.RetentionSchedules.FirstAsync(x => x.TenantId == req.TenantId && x.Id == b.ScheduleId, ct);
            var log = logs.CreateLogger("Records");
            var now = DateTimeOffset.UtcNow;
            var items = await db.DisposalItems.Where(i => i.TenantId == req.TenantId && i.BatchId == id
                && (i.Outcome == DisposalOutcome.Pending || i.Outcome == DisposalOutcome.StoragePending)).ToListAsync(ct);
            var held = (await db.RecordHolds.Where(h => h.TenantId == req.TenantId && h.ReleasedAt == null)
                .Select(h => new { h.EntityType, h.EntityId }).ToListAsync(ct)).Select(h => (h.EntityType, h.EntityId)).ToHashSet();

            var processed = new Dictionary<Guid, DisposalOutcome>();
            foreach (var item in items)
            {
                DisposalItem result;
                if (held.Contains((item.EntityType, item.EntityId)))
                    result = item with { Outcome = DisposalOutcome.SkippedHold, ProcessedAt = now, Note = "A legal hold was placed after the batch was proposed." };
                else if (b.Disposition == Disposition.TransferToArchive)
                {
                    await ArchiveAsync(db, req.TenantId, item, ct);
                    result = item with { Outcome = DisposalOutcome.Transferred, ProcessedAt = now, Note = "Marked for transfer to the National Center for Archives and Records." };
                }
                else
                    result = await DestroyAsync(db, storage, log, req.TenantId, schedule, b, item, now, ct);
                db.Entry(item).CurrentValues.SetValues(result);
                processed[item.Id] = result.Outcome;
            }

            // Whole-batch outcome: this run's results plus items settled by earlier runs.
            var earlier = await db.DisposalItems.AsNoTracking().Where(i => i.TenantId == req.TenantId && i.BatchId == id)
                .Select(i => new { i.Id, i.Outcome }).ToListAsync(ct);
            var final = earlier.Select(i => processed.TryGetValue(i.Id, out var o) ? o : i.Outcome).ToList();
            var summary = final.GroupBy(o => o.ToString()).ToDictionary(g => g.Key, g => g.Count());
            var executed = b with
            {
                Status = final.Contains(DisposalOutcome.StoragePending) ? DisposalStatus.ExecutedWithErrors : DisposalStatus.Executed,
                ExecutedAt = now,
            };
            db.Entry(b).CurrentValues.SetValues(executed);
            DomainEvents.Record(db, req.TenantId, "DisposalExecuted", "DisposalExecuted", nameof(DisposalBatch), b.Id.ToString(),
                payload: new { tenantId = req.TenantId, batchId = b.Id, outcomes = summary }, details: $"{schedule.Code}: {executed.Status}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(new { batch = executed, outcomes = summary });
        });

        // Disposal certificate / archive handover list, with Hijri dates for official use.
        records.MapGet("/disposals/{id:guid}/manifest.csv", async (AppDbContext db, HttpContext ctx, IConfiguration config, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("records:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var b = await db.DisposalBatches.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (b is null) return Results.NotFound(new { error = "Disposal batch not found." });
            var s = await db.RetentionSchedules.FirstAsync(x => x.TenantId == tenantId && x.Id == b.ScheduleId, ct);
            var rows = (await db.DisposalItems.Where(i => i.TenantId == tenantId && i.BatchId == id).OrderBy(i => i.RecordDate).ToListAsync(ct))
                .Select(i => new Dictionary<string, object?>
                {
                    ["schedule"] = s.Code, ["recordClass"] = s.RecordClass.ToString(), ["legalReference"] = s.LegalReference,
                    ["entityType"] = i.EntityType, ["reference"] = i.Reference, ["title"] = i.Title,
                    ["recordDate"] = i.RecordDate, ["eligibleAt"] = i.EligibleAt, ["outcome"] = i.Outcome.ToString(),
                    ["processedAt"] = i.ProcessedAt, ["proposedBy"] = b.ProposedBy, ["approvedBy"] = b.DecidedBy, ["note"] = i.Note,
                }).ToList();
            var offset = TimeSpan.FromHours(config.GetValue("Calendar:UtcOffsetHours", 3.0));
            var csv = ReportEndpoints.ToCsv(ReportEndpoints.WithCalendar(rows, "both", offset));
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
            return Results.File(bytes, "text/csv; charset=utf-8", $"disposal-{s.Code}-{b.ProposedAt:yyyyMMdd}.csv");
        });
    }

    private static async Task<IResult> DecideAsync(AppDbContext db, HttpContext ctx, Guid id, DecideDisposalReq req, bool approve, CancellationToken ct)
    {
        if (!ctx.User.HasPermission("records:approve")) return Results.Forbid();
        if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
        if (!approve && string.IsNullOrWhiteSpace(req.Note)) return Results.BadRequest(new { error = "A reason is required to reject a disposal." });
        await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
        var b = await db.DisposalBatches.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
        if (b is null) return Results.NotFound(new { error = "Disposal batch not found." });
        if (b.Status != DisposalStatus.Proposed) return Results.Conflict(new { error = $"Only proposed batches can be decided (status is {b.Status})." });
        var actor = Actor(ctx);
        // Segregation of duties: the person who proposed a disposal cannot approve or reject it.
        if (string.Equals(actor, b.ProposedBy, StringComparison.Ordinal))
            return Results.Json(new { error = "Segregation of duties: the proposer cannot decide their own disposal batch." }, statusCode: StatusCodes.Status403Forbidden);
        var decided = b with { Status = approve ? DisposalStatus.Approved : DisposalStatus.Rejected, DecidedBy = actor, DecidedAt = DateTimeOffset.UtcNow, DecisionNote = req.Note?.Trim() };
        db.Entry(b).CurrentValues.SetValues(decided);
        var evt = approve ? "DisposalApproved" : "DisposalRejected";
        DomainEvents.Record(db, req.TenantId, evt, evt, nameof(DisposalBatch), b.Id.ToString(),
            payload: new { tenantId = req.TenantId, batchId = b.Id }, details: req.Note);
        await db.SaveChangesAsync(ct);
        await scope.CommitAsync(ct);
        return Results.Ok(decided);
    }

    /// <summary>Records past retention, closed, not held, and not already in a batch that is pending or done.</summary>
    private static async Task<List<Candidate>> CandidatesAsync(AppDbContext db, Guid tenantId, RetentionSchedule s, DateTimeOffset now, CancellationToken ct)
    {
        if (s.Disposition == Disposition.Permanent) return [];
        var held = await db.RecordHolds.Where(h => h.TenantId == tenantId && h.EntityType == s.EntityType && h.ReleasedAt == null)
            .Select(h => h.EntityId).ToListAsync(ct);
        var openBatches = db.DisposalBatches.Where(b => b.TenantId == tenantId && (b.Status == DisposalStatus.Proposed || b.Status == DisposalStatus.Approved))
            .Select(b => b.Id);
        var taken = await db.DisposalItems.Where(i => i.TenantId == tenantId && i.EntityType == s.EntityType
                && (openBatches.Contains(i.BatchId) || i.Outcome == DisposalOutcome.Destroyed || i.Outcome == DisposalOutcome.Transferred || i.Outcome == DisposalOutcome.StoragePending))
            .Select(i => i.EntityId).ToListAsync(ct);
        var excluded = held.Concat(taken).ToHashSet();

        List<Candidate> all;
        if (s.EntityType == "Correspondence")
        {
            var rows = await db.Correspondences.Where(c => c.TenantId == tenantId)
                .Select(c => new { c.Id, c.Number, c.Subject, c.CreatedAt, c.Status }).ToListAsync(ct);
            all = rows.Where(c => RetentionRules.IsClosed(c.Status))
                .Select(c => RetentionRules.EligibleAt(c.CreatedAt, s.RetentionYears, s.Disposition) is { } at
                    ? new Candidate("Correspondence", c.Id, c.Number, c.Subject, c.CreatedAt, at) : null)
                .OfType<Candidate>().ToList();
        }
        else
        {
            var docs = await db.Documents.Where(d => d.TenantId == tenantId).Select(d => new { d.Id, d.Title, d.RetainUntil }).ToListAsync(ct);
            var created = await db.DocumentVersions.Where(v => v.TenantId == tenantId).GroupBy(v => v.DocumentId)
                .Select(g => new { g.Key, first = g.Min(v => v.CreatedAt) }).ToDictionaryAsync(x => x.Key, x => x.first, ct);
            all = docs.Where(d => created.ContainsKey(d.Id))
                .Select(d => RetentionRules.EligibleAt(created[d.Id], s.RetentionYears, s.Disposition, d.RetainUntil) is { } at
                    ? new Candidate("Document", d.Id, "DOC-" + d.Id.ToString("N")[..8].ToUpperInvariant(), d.Title, created[d.Id], at) : null)
                .OfType<Candidate>().ToList();
        }
        return all.Where(c => c.EligibleAt <= now && !excluded.Contains(c.EntityId)).OrderBy(c => c.EligibleAt).ToList();
    }

    private static Task<bool> RecordExistsAsync(AppDbContext db, Guid tenantId, string entityType, Guid id, CancellationToken ct) =>
        entityType == "Correspondence"
            ? db.Correspondences.AnyAsync(c => c.TenantId == tenantId && c.Id == id, ct)
            : db.Documents.AnyAsync(d => d.TenantId == tenantId && d.Id == id, ct);

    private static async Task ArchiveAsync(AppDbContext db, Guid tenantId, DisposalItem item, CancellationToken ct)
    {
        if (item.EntityType == "Correspondence")
        {
            if (await db.Correspondences.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == item.EntityId, ct) is { } c)
                db.Entry(c).CurrentValues.SetValues(c with { Status = CorrespondenceStatus.Archived });
        }
        else if (await db.Documents.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == item.EntityId, ct) is { } d)
            db.Entry(d).CurrentValues.SetValues(d with { Status = DocumentStatus.Archived });
    }

    /// <summary>
    /// Correspondence: the content is redacted; number, subject and dates stay for the register. Documents: every
    /// stored version is deleted from object storage and the deletion confirmed independently; if any deletion
    /// fails or cannot be confirmed the item is StoragePending (never reported as destroyed while a copy may
    /// remain) and the batch can be executed again to retry.
    /// </summary>
    private static async Task<DisposalItem> DestroyAsync(AppDbContext db, IObjectStorage storage, ILogger log, Guid tenantId,
        RetentionSchedule schedule, DisposalBatch b, DisposalItem item, DateTimeOffset now, CancellationToken ct)
    {
        var stamp = $"[Destroyed {now:yyyy-MM-dd} under retention schedule {schedule.Code}, disposal batch {b.Id}]";
        if (item.EntityType == "Correspondence")
        {
            if (await db.Correspondences.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.Id == item.EntityId, ct) is { } c)
                db.Entry(c).CurrentValues.SetValues(c with { Content = stamp, Status = CorrespondenceStatus.Archived });
            return item with { Outcome = DisposalOutcome.Destroyed, ProcessedAt = now, Note = "Content redacted; register entry kept." };
        }

        var keys = await db.DocumentVersions.Where(v => v.TenantId == tenantId && v.DocumentId == item.EntityId).Select(v => v.ObjectKey).ToListAsync(ct);
        var failures = new List<string>();
        foreach (var key in keys)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                await storage.RemoveAsync(key, timeout.Token);
                if (!await storage.ConfirmDeletedAsync(key, timeout.Token)) failures.Add(key);
            }
            // Storage trouble (including our own 15 s limit firing) leaves the item pending; anything else is a bug and must surface.
            catch (Exception ex) when ((ex is StorageUnavailableException or OperationCanceledException) && !ct.IsCancellationRequested)
            {
                log.LogError(ex, "Disposal batch {Batch}: could not delete or confirm deletion of object {Key}; item stays pending", b.Id, key);
                failures.Add(key);
            }
        }
        if (failures.Count > 0)
            return item with { Outcome = DisposalOutcome.StoragePending, ProcessedAt = now, Note = $"{failures.Count} of {keys.Count} stored file(s) could not be deleted; execute again to retry." };
        if (await db.Documents.FirstOrDefaultAsync(d => d.TenantId == tenantId && d.Id == item.EntityId, ct) is { } d)
            db.Entry(d).CurrentValues.SetValues(d with { Status = DocumentStatus.Archived });
        return item with { Outcome = DisposalOutcome.Destroyed, ProcessedAt = now, Note = $"{keys.Count} stored file(s) deleted." };
    }
}
