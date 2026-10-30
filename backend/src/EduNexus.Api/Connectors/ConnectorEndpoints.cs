using System.Text.Json;
using EduNexus.Api.Auth;
using EduNexus.Api.Endpoints;
using EduNexus.Api.Integrations;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static EduNexus.Api.Endpoints.EndpointHelpers;

namespace EduNexus.Api.Connectors;

/// <summary>
/// Read-only links to a learning or student-information system, so approvals and accreditation work can refer to the courses
/// the institution really runs. Off unless Connectors:Enabled is true. Only the course catalogue is read: never students, grades
/// or submissions. The address is checked like a webhook target (no internal addresses unless the site allows them), the
/// credential is sealed at rest and never returned, and no database transaction is held while the other system is called.
/// </summary>
public static class ConnectorEndpoints
{
    public const string EnabledKey = "Connectors:Enabled";
    private static readonly string[] AllowedOptions = ["accountId", "sessionCookie", "siteType", "acceptVersion", "ethosAuthUrl"];
    private static readonly string[] Writers = ["integration:admin", "integration:manage", "integration:write"];
    private static readonly string[] Readers = [.. Writers, "integration:read"];

    private static bool Can(HttpContext ctx, string[] any) => any.Any(ctx.User.HasPermission);
    private static bool Enabled(HttpContext ctx) => ctx.RequestServices.GetRequiredService<IConfiguration>().GetValue<bool>(EnabledKey);
    private static IResult NotEnabled() => Results.NotFound(new { error = "Connectors are not enabled on this server (Connectors:Enabled)." });
    private static string Context(Guid tenantId, Guid id) => $"{tenantId:N}:{id:N}";

    public static void MapConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/connectors").WithTags("Connectors").RequireAuthorization();

        g.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!Can(ctx, Readers)) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            if (!Enabled(ctx)) return NotEnabled();
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var items = await db.ConnectorConfigs.Where(c => c.TenantId == tenantId).OrderBy(c => c.Name).ToListAsync(ct);
            var counts = await db.ExternalCourses.Where(c => c.TenantId == tenantId && c.IsActive).GroupBy(c => c.ConnectorId).Select(x => new { x.Key, N = x.Count() }).ToListAsync(ct);
            return Results.Ok(items.Select(c => View(c, counts.FirstOrDefault(x => x.Key == c.Id)?.N ?? 0)));
        });

        g.MapPost("/", async (AppDbContext db, HttpContext ctx, SecretBox box, IConfiguration config, CreateConnectorReq req, CancellationToken ct) =>
        {
            if (!Can(ctx, Writers)) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enabled(ctx)) return NotEnabled();
            if (!Enum.TryParse<ConnectorSystem>(req.System, true, out var system)) return Results.BadRequest(new { error = "System must be Moodle|Canvas|Sakai|Banner|Csv." });
            if (string.IsNullOrWhiteSpace(req.Name) || req.Name.Trim().Length > 100) return Results.BadRequest(new { error = "Name is required (at most 100 characters)." });
            var options = new Dictionary<string, string>();
            foreach (var (k, v) in req.Options ?? [])
            {
                if (!AllowedOptions.Contains(k)) return Results.BadRequest(new { error = $"Unknown option '{k}'." });
                if (string.IsNullOrWhiteSpace(v) || v.Length > 300) return Results.BadRequest(new { error = $"Option '{k}' is empty or too long." });
                options[k] = v.Trim();
            }
            string? baseUrl = null;
            if (system != ConnectorSystem.Csv)
            {
                if (string.IsNullOrWhiteSpace(req.BaseUrl) || string.IsNullOrWhiteSpace(req.Credential))
                    return Results.BadRequest(new { error = "BaseUrl and Credential are required for a live connection." });
                baseUrl = req.BaseUrl.Trim().TrimEnd('/');
                var allowPrivate = config.GetValue<bool>(OutboundUrlGuard.AllowPrivateKey);
                foreach (var url in new[] { baseUrl }.Concat(options.TryGetValue("ethosAuthUrl", out var eu) ? [eu] : []))
                    if (OutboundUrlGuard.Validate(url) is { } urlError && !(allowPrivate && urlError.Contains("loopback")))
                        return Results.BadRequest(new { error = urlError });
                if (!box.IsConfigured)
                    return Results.Json(new { error = $"{SecretBox.KeyConfig} is not set on the server, so a credential cannot be stored safely." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var name = req.Name.Trim();
            if (await db.ConnectorConfigs.AnyAsync(c => c.TenantId == req.TenantId && c.Name == name, ct))
                return Results.Conflict(new { error = "A connector with this name already exists." });
            var id = Guid.NewGuid();
            var sealedCredential = system == ConnectorSystem.Csv ? null : box.Seal(req.Credential!, Context(req.TenantId, id));
            var c = new ConnectorConfig(id, req.TenantId, system, name, baseUrl, sealedCredential, JsonSerializer.Serialize(options), true, DateTimeOffset.UtcNow);
            db.ConnectorConfigs.Add(c);
            DomainEvents.Record(db, req.TenantId, "ConnectorCreated", "ConnectorCreated", nameof(ConnectorConfig), id.ToString(), actorId: await ActingPersonAsync(db, ctx, req.TenantId, ct), details: $"{system} {name}");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/connectors/{id}", View(c, 0));
        });

        g.MapDelete("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!Can(ctx, Writers)) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            if (!Enabled(ctx)) return NotEnabled();
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var c = await db.ConnectorConfigs.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "Connector not found." });
            db.ExternalCourses.RemoveRange(await db.ExternalCourses.Where(x => x.TenantId == tenantId && x.ConnectorId == id).ToListAsync(ct));
            db.ConnectorConfigs.Remove(c);
            DomainEvents.Record(db, tenantId, "ConnectorDeleted", "ConnectorDeleted", nameof(ConnectorConfig), id.ToString(), actorId: await ActingPersonAsync(db, ctx, tenantId, ct), details: c.Name);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.NoContent();
        });

        // Reads the catalogue now. Three short database steps with the network call between them, so no transaction waits on another system.
        g.MapPost("/{id:guid}/sync", async (AppDbContext db, HttpContext ctx, SecretBox box, ICatalogClients clients, IHttpClientFactory http, ILoggerFactory logs, Guid id, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!Can(ctx, Writers)) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enabled(ctx)) return NotEnabled();
            ConnectorConfig c;
            await using (var scope = await TenantScope.BeginAsync(db, req.TenantId, ct))
            {
                var found = await db.ConnectorConfigs.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
                if (found is null) return Results.NotFound(new { error = "Connector not found." });
                if (found.System == ConnectorSystem.Csv) return Results.BadRequest(new { error = "A file connector is loaded with /import, not synced." });
                if (found.LastSyncStatus == "Running" && found.LastSyncAt > DateTimeOffset.UtcNow.AddMinutes(-10))
                    return Results.Conflict(new { error = "A sync is already running." });
                c = found with { LastSyncStatus = "Running", LastSyncAt = DateTimeOffset.UtcNow, LastSyncMessage = null };
                db.Entry(found).CurrentValues.SetValues(c);
                await db.SaveChangesAsync(ct);
                await scope.CommitAsync(ct);
            }

            IReadOnlyList<CatalogCourse>? fetched = null;
            string? failure = null;
            try
            {
                var credential = box.Open(c.CredentialSealed ?? "", Context(c.TenantId, c.Id));
                var options = JsonSerializer.Deserialize<Dictionary<string, string>>(c.OptionsJson) ?? [];
                fetched = await clients.For(c.System).FetchAsync(c, credential, options, http.CreateClient("integrations"), ct);
            }
            catch (ConnectorException ex) { failure = ex.Message; }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                logs.CreateLogger("Connectors").LogError(ex, "Could not open the sealed credential of connector {ConnectorId}", c.Id);
                failure = $"The stored credential cannot be opened (was {SecretBox.KeyConfig} changed?). Delete and create the connector again.";
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logs.CreateLogger("Connectors").LogError(ex, "Sync of connector {ConnectorId} failed unexpectedly", c.Id);
                failure = "Unexpected error while reading the other system; see the server log.";
            }

            await using (var scope = await TenantScope.BeginAsync(db, req.TenantId, ct))
            {
                var current = await db.ConnectorConfigs.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
                if (current is null) return Results.NotFound(new { error = "Connector was deleted during the sync." });
                var now = DateTimeOffset.UtcNow;
                if (failure is not null)
                {
                    db.Entry(current).CurrentValues.SetValues(current with { LastSyncStatus = "Failed", LastSyncAt = now, LastSyncMessage = Truncate(failure) });
                    DomainEvents.Record(db, req.TenantId, "ConnectorSyncFailed", "ConnectorSyncFailed", nameof(ConnectorConfig), id.ToString(), details: Truncate(failure));
                    await db.SaveChangesAsync(ct);
                    await scope.CommitAsync(ct);
                    return Results.Json(new { error = failure }, statusCode: StatusCodes.Status502BadGateway);
                }
                var summary = await UpsertAsync(db, req.TenantId, id, fetched!, now, ct);
                db.Entry(current).CurrentValues.SetValues(current with { LastSyncStatus = "Succeeded", LastSyncAt = now, LastSyncMessage = summary.Text });
                DomainEvents.Record(db, req.TenantId, "ConnectorSynced", "ConnectorSynced", nameof(ConnectorConfig), id.ToString(),
                    payload: new { tenantId = req.TenantId, connectorId = id, summary.Created, summary.Updated, summary.Deactivated, summary.Total }, details: summary.Text);
                await db.SaveChangesAsync(ct);
                await scope.CommitAsync(ct);
                return Results.Ok(summary);
            }
        });

        // A course list exported from any system, for sites that cannot or do not want a live link.
        g.MapPost("/{id:guid}/import", async (AppDbContext db, HttpContext ctx, Guid id, ImportCoursesReq req, CancellationToken ct) =>
        {
            if (!Can(ctx, Writers)) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (!Enabled(ctx)) return NotEnabled();
            var parsed = CourseCsv.Parse(req.Csv ?? "");
            if (parsed.Errors.Count > 0) return Results.BadRequest(new { error = "The file was not imported.", problems = parsed.Errors.Take(20), more = Math.Max(0, parsed.Errors.Count - 20) });
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var c = await db.ConnectorConfigs.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (c is null) return Results.NotFound(new { error = "Connector not found." });
            if (c.System != ConnectorSystem.Csv) return Results.BadRequest(new { error = "Only a file connector takes an import; use /sync for a live connection." });
            var now = DateTimeOffset.UtcNow;
            var summary = await UpsertAsync(db, req.TenantId, id, parsed.Courses, now, ct);
            db.Entry(c).CurrentValues.SetValues(c with { LastSyncStatus = "Succeeded", LastSyncAt = now, LastSyncMessage = summary.Text });
            DomainEvents.Record(db, req.TenantId, "ConnectorSynced", "ConnectorSynced", nameof(ConnectorConfig), id.ToString(),
                payload: new { tenantId = req.TenantId, connectorId = id, summary.Created, summary.Updated, summary.Deactivated, summary.Total }, details: summary.Text);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(summary);
        });

        g.MapGet("/courses", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? connectorId, string? q, bool? active, int? skip, int? take, CancellationToken ct) =>
        {
            if (!Can(ctx, Readers)) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            if (!Enabled(ctx)) return NotEnabled();
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var query = db.ExternalCourses.Where(c => c.TenantId == tenantId);
            if (connectorId is { } cid) query = query.Where(c => c.ConnectorId == cid);
            if (active is { } a) query = query.Where(c => c.IsActive == a);
            if (!string.IsNullOrWhiteSpace(q))
            {
                var like = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                query = query.Where(c => EF.Functions.ILike(c.Code, like) || EF.Functions.ILike(c.Title, like) || EF.Functions.ILike(c.InstructorNames, like));
            }
            var total = await query.CountAsync(ct);
            var items = await query.OrderBy(c => c.Code).Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 50, 1, 200)).ToListAsync(ct);
            return Results.Ok(new { total, items });
        });
    }

    private static string Truncate(string s) => s.Length <= 500 ? s : s[..497] + "...";

    /// <summary>The connector as the API shows it: never the credential, only whether one is stored.</summary>
    private static object View(ConnectorConfig c, int courses) => new
    {
        c.Id, system = c.System.ToString(), c.Name, c.BaseUrl, hasCredential = c.CredentialSealed is not null,
        options = JsonSerializer.Deserialize<Dictionary<string, string>>(c.OptionsJson) ?? [], c.IsActive,
        c.LastSyncAt, c.LastSyncStatus, c.LastSyncMessage, courses,
    };

    public sealed record SyncSummary(int Created, int Updated, int Deactivated, int Total)
    {
        public string Text => $"{Total} course(s): {Created} new, {Updated} updated, {Deactivated} no longer listed.";
    }

    /// <summary>Courses are matched on the external id; ones the system no longer lists become inactive (never deleted, so history survives).</summary>
    private static async Task<SyncSummary> UpsertAsync(AppDbContext db, Guid tenantId, Guid connectorId, IReadOnlyList<CatalogCourse> fetched, DateTimeOffset now, CancellationToken ct)
    {
        var existing = (await db.ExternalCourses.Where(x => x.TenantId == tenantId && x.ConnectorId == connectorId).ToListAsync(ct)).ToDictionary(x => x.ExternalId);
        int created = 0, updated = 0, deactivated = 0;
        var seen = new HashSet<string>();
        foreach (var f in fetched)
        {
            var externalId = f.ExternalId.Length > 200 ? f.ExternalId[..200] : f.ExternalId;
            if (!seen.Add(externalId)) continue;
            var next = new ExternalCourse(existing.TryGetValue(externalId, out var cur) ? cur.Id : Guid.NewGuid(), tenantId, connectorId, externalId,
                Clip(f.Code, 100), Clip(f.Title, 400), f.Term is null ? null : Clip(f.Term, 100), f.StartsOn, f.EndsOn, f.EnrolledCount,
                Clip(string.Join("; ", f.Instructors), 1000), f.IsActive, now);
            if (cur is null) { db.ExternalCourses.Add(next); created++; }
            else
            {
                if (cur with { SyncedAt = now } != next) updated++;
                db.Entry(cur).CurrentValues.SetValues(next);
            }
        }
        foreach (var (externalId, cur) in existing)
            if (!seen.Contains(externalId) && cur.IsActive) { db.Entry(cur).CurrentValues.SetValues(cur with { IsActive = false, SyncedAt = now }); deactivated++; }
        return new SyncSummary(created, updated, deactivated, seen.Count);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}

public sealed record CreateConnectorReq(Guid TenantId, string System, string Name, string? BaseUrl, string? Credential, Dictionary<string, string>? Options);
public sealed record ImportCoursesReq(Guid TenantId, string? Csv);
