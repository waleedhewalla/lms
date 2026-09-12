using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EduNexus.Api.AI;
using EduNexus.Api.Auth;
using EduNexus.Api.Events;
using EduNexus.Api.Integrations;
using EduNexus.Api.Storage;
using Minio;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// R1 Foundation: OpenAPI + EF Core/Postgres (RLS) for all foundation entities.
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<FoundationStore>(); // stateless SoD helper; persistence is EF
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddHealthChecks();

var conn = Environment.GetEnvironmentVariable("EDUNEXUS_CONNECTION")
    ?? builder.Configuration.GetConnectionString("Foundation")
    ?? "Host=localhost;Port=5433;Database=edunexus;Username=edunexus;Password=edunexus";
builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(conn));

// --- Auth: OIDC when Authority configured, else dev HS256 (R1 slice) ---
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.Section));
var authCfg = builder.Configuration.GetSection(AuthOptions.Section).Get<AuthOptions>() ?? new AuthOptions();
builder.Services.AddSingleton<DevTokenService>();
builder.Services.Configure<RabbitMqOptions>(builder.Configuration.GetSection(RabbitMqOptions.Section));
builder.Services.AddHostedService<EventRelay>();
builder.Services.AddHostedService<NotificationConsumer>();
builder.Services.AddHostedService<SlaMonitor>();

// --- S3-compatible object storage (MinIO reference) ---builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.Section));
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value;
    return new MinioClient().WithEndpoint(o.Endpoint).WithCredentials(o.AccessKey, o.SecretKey).WithSSL(o.Secure).Build();
});
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value;
    var c = sp.GetRequiredService<IMinioClient>();
    return new ObjectStorage(c, o);
});

// --- AI (R5): tenant retrieval index + copilot + webhook fan-out ---
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.Section));
builder.Services.AddHttpClient("ai").SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddHttpClient("integrations").SetHandlerLifetime(TimeSpan.FromMinutes(5));
builder.Services.AddSingleton(sp =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<AiOptions>>().Value;
    var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("ai");
    return new TenantSearchIndex(http, o);
});
builder.Services.AddHostedService<IntegrationDispatcher>();

// --- Observability: traces (OTLP, collector optional) + Prometheus metrics on /metrics ---
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("edunexus-api", serviceVersion: "1.0.0-R1"))
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddOtlpExporter())
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddPrometheusExporter());
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        if (!string.IsNullOrWhiteSpace(authCfg.Authority))
        {
            o.Authority = authCfg.Authority;
            o.Audience = authCfg.Audience;
            // On-prem test IdPs often run plain HTTP; TLS stays mandatory in Production.
            o.RequireHttpsMetadata = builder.Environment.IsProduction();
        }
        else
        {
            o.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "edunexus-dev",
                ValidateAudience = true,
                ValidAudience = authCfg.Audience,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authCfg.DevSigningKey)),
            };
        }
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// 0.5 security gate: never boot Production on dev auth defaults.
if (app.Environment.IsProduction())
{
    var ao = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value;
    if (string.IsNullOrWhiteSpace(ao.Authority))
        throw new InvalidOperationException("Production requires Auth:Authority (OIDC). Dev HS256 is Development-only.");
    if (ao.EnableDevToken)
        throw new InvalidOperationException("Production requires Auth:EnableDevToken=false.");
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
// Security headers (defense in depth; TLS termination is upstream on-prem).
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.XContentTypeOptions = "nosniff";
    ctx.Response.Headers.XFrameOptions = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});
app.MapHealthChecks("/health");
app.MapPrometheusScrapingEndpoint();

app.MapGet("/", () => Results.Ok(new { name = "EduNexus OS V2 API", release = "R1-Foundation", docs = "/openapi/v1.json" }));

static bool IsUniqueConflict(DbUpdateException ex) =>
    ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;

static IResult ForbiddenIfCrossTenant(HttpContext ctx, Guid tenantId) =>
    ctx.User.TenantId() is { } claim && claim != tenantId ? Results.Forbid() : null!;

// --- Dev token (Development only) ---
var auth = app.MapGroup("/api/auth").WithTags("Auth");
auth.MapPost("/dev-token", (DevTokenService tokens, DevTokenReq req, IHostEnvironment env) =>
{
    var opts = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value;
    if (!env.IsDevelopment() || !opts.EnableDevToken) return Results.NotFound();
    if (req.TenantId == Guid.Empty || string.IsNullOrWhiteSpace(req.Subject))
        return Results.BadRequest(new { error = "subject and tenantId required." });
    return Results.Ok(new { token = tokens.Mint(req.Subject, req.TenantId, req.Permissions ?? []) });
}).AllowAnonymous();

// --- Tenants (platform-level table, no RLS) ---
var tenants = app.MapGroup("/api/tenants").WithTags("Tenants").RequireAuthorization();
tenants.MapGet("/", async (AppDbContext db, HttpContext ctx, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("tenant:read")) return Results.Forbid();
    return Results.Ok(await db.Tenants.OrderBy(t => t.Slug).ToListAsync(ct));
});
tenants.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateTenantReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("tenant:create")) return Results.Forbid();
    Tenant tenant;
    try { tenant = Tenant.Create(req.Slug, req.Name); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    // Audit row carries the new tenant id, so scope the tx to it (RLS WITH CHECK).
    await using var scope = await TenantScope.BeginAsync(db, tenant.Id, ct);
    db.Tenants.Add(tenant);
    DomainEvents.Record(db, tenant.Id, "TenantCreated", "TenantCreated",
        nameof(Tenant), tenant.Id.ToString(), details: $"slug={tenant.Slug}");
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (IsUniqueConflict(ex))
    {
        return Results.Conflict(new { error = $"Tenant slug '{req.Slug}' already exists." });
    }
    await scope.CommitAsync(ct);
    return Results.Created($"/api/tenants/{tenant.Slug}", tenant);
});

// --- Organizations (tenant-scoped, RLS) ---
var orgs = app.MapGroup("/api/organizations").WithTags("Organizations").RequireAuthorization();
orgs.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("org:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    return Results.Ok(await db.Organizations.Where(o => o.TenantId == tenantId).OrderBy(o => o.Code).ToListAsync(ct));
});
orgs.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateOrgReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("org:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (!await db.Tenants.AnyAsync(t => t.Id == req.TenantId, ct))
        return Results.NotFound(new { error = "Tenant not found." });
    Organization org;
    try { org = Organization.Create(req.TenantId, req.Code, req.Name); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    db.Organizations.Add(org);
    DomainEvents.Record(db, req.TenantId, "OrganizationCreated", "OrganizationCreated",
        nameof(Organization), org.Id.ToString(), details: req.Code);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (IsUniqueConflict(ex))
    {
        return Results.Conflict(new { error = $"Organization code '{req.Code}' already exists in tenant." });
    }
    await scope.CommitAsync(ct);
    return Results.Created("/api/organizations", org);
});

// --- Directory / people (tenant-scoped, RLS) ---
var people = app.MapGroup("/api/people").WithTags("Directory").RequireAuthorization();
people.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? q, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("person:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var query = db.People.Where(p => p.TenantId == tenantId);
    if (!string.IsNullOrWhiteSpace(q))
        query = query.Where(p => p.FullName.Contains(q) || (p.Email != null && p.Email.Contains(q)));
    return Results.Ok(await query.OrderBy(p => p.FullName).ToListAsync(ct));
});
people.MapPost("/", async (AppDbContext db, HttpContext ctx, CreatePersonReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("person:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (!await db.Tenants.AnyAsync(t => t.Id == req.TenantId, ct))
        return Results.NotFound(new { error = "Tenant not found." });
    if (!Enum.TryParse<PersonType>(req.Type, true, out var t))
        return Results.BadRequest(new { error = "Type must be Employee|Student|Other." });
    Person person;
    try { person = Person.Create(req.TenantId, t, req.FullName, req.Email); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    db.People.Add(person);
    DomainEvents.Record(db, req.TenantId, "PersonCreated", "PersonCreated",
        nameof(Person), person.Id.ToString(), details: req.FullName);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created("/api/people", person);
});
// CSV directory import (pilot migration tooling): header fullName,email,type — dryRun validates only.
people.MapPost("/import", async (AppDbContext db, HttpContext ctx, Guid tenantId, bool dryRun, HttpRequest request, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("person:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct))
        return Results.NotFound(new { error = "Tenant not found." });
    using var reader = new StreamReader(request.Body);
    var header = await reader.ReadLineAsync(ct);
    if (header is null || !header.Trim().Equals("fullName,email,type", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "CSV header must be: fullName,email,type" });
    var created = new List<Person>();
    var errors = new List<string>();
    string? line;
    var row = 1;
    while ((line = await reader.ReadLineAsync(ct)) is not null)
    {
        row++;
        if (string.IsNullOrWhiteSpace(line)) continue;
        var parts = line.Split(',');
        if (parts.Length < 2) { errors.Add($"row {row}: need at least fullName,email"); continue; }
        var name = parts[0].Trim();
        var email = parts[1].Trim();
        var typeRaw = parts.Length > 2 ? parts[2].Trim() : "Employee";
        if (string.IsNullOrWhiteSpace(name)) { errors.Add($"row {row}: fullName required"); continue; }
        if (!Enum.TryParse<PersonType>(string.IsNullOrWhiteSpace(typeRaw) ? "Employee" : typeRaw, true, out var ptype))
        { errors.Add($"row {row}: bad type '{typeRaw}'"); continue; }
        try { created.Add(Person.Create(tenantId, ptype, name, string.IsNullOrWhiteSpace(email) ? null : email)); }
        catch (ArgumentException ex) { errors.Add($"row {row}: {ex.Message}"); }
    }
    if (dryRun) return Results.Ok(new { valid = created.Count, errors });
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    foreach (var p in created)
    {
        db.People.Add(p);
        DomainEvents.Record(db, tenantId, "PersonCreated", "PersonCreated",
            nameof(Person), p.Id.ToString(), details: $"csv:{p.FullName}");
    }
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(new { imported = created.Count, errors });
});

// --- Audit trail (tenant-scoped, RLS) ---
app.MapGet("/api/audit", async (AppDbContext db, HttpContext ctx, Guid tenantId, int? limit, CancellationToken ct) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
    if (!ctx.User.HasPermission("audit:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var take = Math.Clamp(limit ?? 100, 1, 1000);
    return Results.Ok(await db.AuditEvents.Where(a => a.TenantId == tenantId)
        .OrderByDescending(a => a.At).Take(take).ToListAsync(ct));
}).RequireAuthorization().WithTags("Audit");

// --- Roles & assignments (FR-AUTH-001/002, SoD enforced) ---
var roles = app.MapGroup("/api/roles").WithTags("Roles").RequireAuthorization();
roles.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("role:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    return Results.Ok(await db.Roles.Where(r => r.TenantId == tenantId).ToListAsync(ct));
});
roles.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateRoleReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("role:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (!await db.Tenants.AnyAsync(t => t.Id == req.TenantId, ct))
        return Results.NotFound(new { error = "Tenant not found." });
    Role role;
    try { role = Role.Create(req.TenantId, req.Code, req.Name, req.Permissions ?? []); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    db.Roles.Add(role);
    DomainEvents.Record(db, req.TenantId, "RoleCreated", "RoleCreated",
        nameof(Role), role.Id.ToString(),
        payload: new { tenantId = req.TenantId, roleId = role.Id, code = role.Code, permissions = role.Permissions },
        details: req.Code);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (IsUniqueConflict(ex))
    {
        return Results.Conflict(new { error = $"Role code '{req.Code}' already exists in tenant." });
    }
    await scope.CommitAsync(ct);
    return Results.Created($"/api/roles/{role.Id}", role);
});
roles.MapPost("/assign", async (AppDbContext db, HttpContext ctx, AssignRoleReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("role:assign")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var role = await db.Roles.FirstOrDefaultAsync(r => r.TenantId == req.TenantId && r.Code == req.RoleCode, ct);
    if (role is null) return Results.NotFound(new { error = $"Role '{req.RoleCode}' not found in tenant." });
    if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.PersonId, ct))
        return Results.NotFound(new { error = "Person not found in tenant." });
    var existingCodes = await db.RoleAssignments
        .Where(a => a.TenantId == req.TenantId && a.PersonId == req.PersonId)
        .Join(db.Roles, a => a.RoleId, r => r.Id, (a, r) => r.Code)
        .ToListAsync(ct);
    try { FoundationStore.CheckSod(existingCodes, role.Code); }
    catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
    if (existingCodes.Contains(role.Code))
        return Results.Conflict(new { error = "Role already assigned to person." });
    var assignment = new RoleAssignment(Guid.NewGuid(), req.TenantId, req.PersonId, role.Id,
        req.Scope ?? $"tenant:{req.TenantId}", req.ExpiresAt, DateTimeOffset.UtcNow);
    db.RoleAssignments.Add(assignment);
    DomainEvents.Record(db, req.TenantId, "RoleAssigned", "RoleAssigned",
        nameof(RoleAssignment), assignment.Id.ToString(),
        payload: new { tenantId = req.TenantId, personId = req.PersonId, roleId = role.Id, roleCode = role.Code, scope = assignment.Scope },
        details: $"{req.PersonId}->{role.Code}");
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/roles/assignments/{assignment.Id}", assignment);
});
roles.MapPost("/revoke", async (AppDbContext db, HttpContext ctx, RevokeRoleReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("role:assign")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var assignment = await db.RoleAssignments
        .Join(db.Roles, a => a.RoleId, r => r.Id, (a, r) => new { Assignment = a, r.Code })
        .Where(x => x.Assignment.TenantId == req.TenantId
            && x.Assignment.PersonId == req.PersonId && x.Code == req.RoleCode)
        .Select(x => x.Assignment)
        .FirstOrDefaultAsync(ct);
    if (assignment is null) return Results.NotFound(new { error = "Assignment not found." });
    db.RoleAssignments.Remove(assignment);
    DomainEvents.Record(db, req.TenantId, "RoleRevoked", "RoleRevoked",
        nameof(RoleAssignment), assignment.Id.ToString(),
        payload: new { tenantId = req.TenantId, personId = req.PersonId, roleCode = req.RoleCode },
        details: $"{req.PersonId}-/->{req.RoleCode}");
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.NoContent();
});
roles.MapGet("/assignments", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? personId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("role:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var q = db.RoleAssignments.Where(a => a.TenantId == tenantId);
    if (personId.HasValue) q = q.Where(a => a.PersonId == personId.Value);
    return Results.Ok(await q.ToListAsync(ct));
});

// ============================ R2 — Correspondence & Workflow ============================

var corr = app.MapGroup("/api/correspondence").WithTags("Correspondence").RequireAuthorization();
corr.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? status, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("correspondence:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    var canSeeConfidential = ctx.User.HasPermission("correspondence:confidential");
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var q = db.Correspondences.Where(c => c.TenantId == tenantId);
    if (!canSeeConfidential) q = q.Where(c => !c.IsConfidential);
    if (Enum.TryParse<CorrespondenceStatus>(status, true, out var s)) q = q.Where(c => c.Status == s);
    return Results.Ok(await q.OrderByDescending(c => c.CreatedAt).ToListAsync(ct));
});
corr.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateCorrespondenceReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("correspondence:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (req.IsConfidential && !ctx.User.HasPermission("correspondence:confidential"))
        return Results.Forbid();
    if (!Enum.TryParse<CorrespondenceType>(req.Type, true, out var type))
        return Results.BadRequest(new { error = "Type must be Incoming|Outgoing|Internal." });
    if (!Enum.TryParse<CorrespondencePriority>(req.Priority ?? "Normal", true, out var priority))
        return Results.BadRequest(new { error = "Priority must be Normal|High|Urgent." });
    if (!await db.Tenants.AnyAsync(t => t.Id == req.TenantId, ct))
        return Results.NotFound(new { error = "Tenant not found." });
    if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.AuthorId, ct))
        return Results.NotFound(new { error = "Author not found in tenant." });
    Correspondence c;
    try { c = Correspondence.Create(req.TenantId, type, req.Subject, req.Content, req.AuthorId, priority, req.IsConfidential); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    // Number reserved at creation (unique per tenant): CORR-2026-000123.
    var seq = await Sequences.NextAsync(db, req.TenantId, "correspondence", ct);
    c = c with { Number = $"CORR-{DateTimeOffset.UtcNow:yyyy}-{seq:D6}" };
    db.Correspondences.Add(c);
    foreach (var r in req.Recipients ?? [])
    {
        Guid? pid = Guid.TryParse(r.PersonId, out var g) ? g : null;
        if (pid.HasValue && !await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == pid.Value, ct))
            return Results.BadRequest(new { error = $"Recipient person {r.PersonId} not found in tenant." });
        db.Correspondents.Add(new Correspondent(Guid.NewGuid(), req.TenantId, c.Id, pid, r.DisplayName, r.IsExternal));
    }
    DomainEvents.Record(db, req.TenantId, "CorrespondenceCreated", "CorrespondenceCreated",
        nameof(Correspondence), c.Id.ToString(),
        payload: new { tenantId = req.TenantId, correspondenceId = c.Id, number = c.Number },
        details: c.Number);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/correspondence/{c.Id}", c);
});
corr.MapPost("/{id:guid}/submit", async (AppDbContext db, HttpContext ctx, Guid id, SubmitCorrespondenceReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("correspondence:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var c = await db.Correspondences.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (c is null) return Results.NotFound(new { error = "Correspondence not found." });
    if (c.Status != CorrespondenceStatus.Draft)
        return Results.Conflict(new { error = $"Only Draft correspondence can be submitted (now {c.Status})." });
    if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.ReviewerId, ct))
        return Results.NotFound(new { error = "Reviewer not found in tenant." });
    // FR-COR-001 steps 10-14: validate → number exists → start workflow → audit → notify reviewer.
    db.Entry(c).CurrentValues.SetValues(c with { Status = CorrespondenceStatus.Submitted });
    var accelerated = c.Priority is CorrespondencePriority.High or CorrespondencePriority.Urgent;
    var approval = new Approval(Guid.NewGuid(), req.TenantId, nameof(Correspondence), c.Id,
        req.ReviewerId, ApprovalStatus.Pending,
        accelerated ? ApprovalPriority.Accelerated : ApprovalPriority.Normal,
        DateTimeOffset.UtcNow.AddDays(accelerated ? 2 : 5), null, null, null);
    db.Approvals.Add(approval);
    db.WorkTasks.Add(new WorkTask(Guid.NewGuid(), req.TenantId, $"Review {c.Number}: {c.Subject}",
        req.ReviewerId, approval.Id, WorkTaskStatus.Open, approval.DueAt, DateTimeOffset.UtcNow));
    DomainEvents.Record(db, req.TenantId, "CorrespondenceSubmitted", "CorrespondenceSubmitted",
        nameof(Correspondence), c.Id.ToString(),
        payload: new { tenantId = req.TenantId, correspondenceId = c.Id, number = c.Number, reviewerId = req.ReviewerId, approvalId = approval.Id },
        details: $"{c.Number}->{req.ReviewerId}");
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(new { correspondence = c, approvalId = approval.Id });
});

var approvals = app.MapGroup("/api/approvals").WithTags("Approvals").RequireAuthorization();
approvals.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? assigneeId, string? status, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("approval:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var q = db.Approvals.Where(a => a.TenantId == tenantId);
    if (assigneeId.HasValue) q = q.Where(a => a.AssigneeId == assigneeId.Value);
    if (Enum.TryParse<ApprovalStatus>(status, true, out var s)) q = q.Where(a => a.Status == s);
    return Results.Ok(await q.OrderBy(a => a.DueAt).ToListAsync(ct));
});
approvals.MapPost("/{id:guid}/decide", async (AppDbContext db, HttpContext ctx, Guid id, DecideApprovalReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("approval:decide")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var a = await db.Approvals.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (a is null) return Results.NotFound(new { error = "Approval not found." });
    if (a.Status != ApprovalStatus.Pending)
        return Results.Conflict(new { error = $"Approval already {a.Status}." });
    if (a.AssigneeId != req.DecidedBy)
        return Results.Forbid();
    var decided = a with { Status = req.Approve ? ApprovalStatus.Approved : ApprovalStatus.Rejected, DecidedAt = DateTimeOffset.UtcNow, DecidedBy = req.DecidedBy, Comment = req.Comment };
    db.Entry(a).CurrentValues.SetValues(decided);
    if (a.EntityType == nameof(Correspondence))
    {
        var c = await db.Correspondences.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == a.EntityId, ct);
        if (c is not null)
            db.Entry(c).CurrentValues.SetValues(c with { Status = req.Approve ? CorrespondenceStatus.Approved : CorrespondenceStatus.Rejected });
    }
    var task = await db.WorkTasks.FirstOrDefaultAsync(t => t.TenantId == req.TenantId && t.ApprovalId == a.Id, ct);
    if (task is not null)
        db.Entry(task).CurrentValues.SetValues(task with { Status = WorkTaskStatus.Done });
    DomainEvents.Record(db, req.TenantId, "ApprovalDecided", "ApprovalDecided",
        nameof(Approval), a.Id.ToString(),
        payload: new { tenantId = req.TenantId, approvalId = a.Id, entityType = a.EntityType, entityId = a.EntityId, approved = req.Approve, decidedBy = req.DecidedBy },
        details: $"{a.Id}=>{(req.Approve ? "approved" : "rejected")}");
    // Workflow-driven approvals advance their instance (no-op for ad-hoc approvals).
    await WorkflowRunner.OnApprovalDecidedAsync(db, req.TenantId, decided, req.Approve, ct);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(decided);
});
approvals.MapPost("/{id:guid}/request-changes", async (AppDbContext db, HttpContext ctx, Guid id, RequestChangesReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("approval:decide")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var a = await db.Approvals.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (a is null) return Results.NotFound(new { error = "Approval not found." });
    if (a.Status != ApprovalStatus.Pending) return Results.Conflict(new { error = $"Approval already {a.Status}." });
    if (a.AssigneeId != req.DecidedBy) return Results.Forbid();
    var changed = a with { Status = ApprovalStatus.ChangesRequested, DecidedAt = DateTimeOffset.UtcNow, DecidedBy = req.DecidedBy, Comment = req.Comment };
    db.Entry(a).CurrentValues.SetValues(changed);
    // Return request to submitter for revision if linked entity is a Request
    if (a.EntityType == nameof(Request))
    {
        var r = await db.Requests.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == a.EntityId, ct);
        if (r is not null) db.Entry(r).CurrentValues.SetValues(r with { Status = RequestStatus.ChangesRequested });
    }
    DomainEvents.Record(db, req.TenantId, "ApprovalChangesRequested", "ApprovalChangesRequested",
        nameof(Approval), a.Id.ToString(),
        payload: new { tenantId = req.TenantId, approvalId = a.Id, decidedBy = req.DecidedBy }, details: req.Comment ?? "changes requested");
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(changed);
});
approvals.MapPost("/{id:guid}/delegate", async (AppDbContext db, HttpContext ctx, Guid id, DelegateApprovalReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("approval:decide")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var a = await db.Approvals.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (a is null) return Results.NotFound(new { error = "Approval not found." });
    if (a.Status != ApprovalStatus.Pending) return Results.Conflict(new { error = $"Approval already {a.Status}." });
    if (a.AssigneeId != req.DelegatedBy) return Results.Forbid();
    if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.DelegateTo, ct))
        return Results.NotFound(new { error = "Delegate not found in tenant." });
    var delegated = a with { AssigneeId = req.DelegateTo };
    db.Entry(a).CurrentValues.SetValues(delegated);
    var task = await db.WorkTasks.FirstOrDefaultAsync(t => t.TenantId == req.TenantId && t.ApprovalId == a.Id, ct);
    if (task is not null) db.Entry(task).CurrentValues.SetValues(task with { AssigneeId = req.DelegateTo });
    db.Notifications.Add(new Notification(Guid.NewGuid(), req.TenantId, req.DelegateTo,
        "Approval delegated to you", $"Approval {a.Id} delegated", NotificationChannel.InApp, NotificationStatus.Sent, DateTimeOffset.UtcNow));
    DomainEvents.Record(db, req.TenantId, "ApprovalDelegated", "ApprovalDelegated",
        nameof(Approval), a.Id.ToString(),
        payload: new { tenantId = req.TenantId, approvalId = a.Id, from = req.DelegatedBy, to = req.DelegateTo }, details: $"{req.DelegatedBy}->{req.DelegateTo}");
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(delegated);
});

var tasks = app.MapGroup("/api/tasks").WithTags("Tasks").RequireAuthorization();
tasks.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? assigneeId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("task:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var q = db.WorkTasks.Where(t => t.TenantId == tenantId);
    if (assigneeId.HasValue) q = q.Where(t => t.AssigneeId == assigneeId.Value);
    return Results.Ok(await q.OrderBy(t => t.DueAt).ToListAsync(ct));
});
tasks.MapPost("/{id:guid}/complete", async (AppDbContext db, HttpContext ctx, Guid id, CompleteTaskReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("task:update")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var t = await db.WorkTasks.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (t is null) return Results.NotFound(new { error = "Task not found." });
    if (t.Status == WorkTaskStatus.Done) return Results.Conflict(new { error = "Task already done." });
    db.Entry(t).CurrentValues.SetValues(t with { Status = WorkTaskStatus.Done });
    DomainEvents.Record(db, req.TenantId, "TaskCompleted", "TaskCompleted",
        nameof(WorkTask), t.Id.ToString(),
        payload: new { tenantId = req.TenantId, taskId = t.Id },
        details: t.Title);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(t);
});
tasks.MapPatch("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, UpdateTaskReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("task:update")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (req.Progress is < 0 or > 100) return Results.BadRequest(new { error = "Progress must be 0-100." });
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var t = await db.WorkTasks.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (t is null) return Results.NotFound(new { error = "Task not found." });
    if (t.Status == WorkTaskStatus.Done) return Results.Conflict(new { error = "Task already done." });
    var updated = t with
    {
        Title = string.IsNullOrWhiteSpace(req.Title) ? t.Title : req.Title.Trim(),
        Description = req.Description ?? t.Description,
        Priority = req.Priority is not null && Enum.TryParse<WorkTaskPriority>(req.Priority, true, out var p) ? p : t.Priority,
        Progress = req.Progress ?? t.Progress,
        Status = req.Status is not null && Enum.TryParse<WorkTaskStatus>(req.Status, true, out var s) ? s : t.Status,
    };
    db.Entry(t).CurrentValues.SetValues(updated);
    DomainEvents.Record(db, req.TenantId, "TaskUpdated", "TaskUpdated",
        nameof(WorkTask), t.Id.ToString(),
        payload: new { tenantId = req.TenantId, taskId = t.Id, progress = updated.Progress }, details: updated.Title);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(updated);
});
tasks.MapGet("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("task:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var t = await db.WorkTasks.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
    return t is null ? Results.NotFound(new { error = "Task not found." }) : Results.Ok(t);
});
tasks.MapPost("/{id:guid}/evidence", async (AppDbContext db, HttpContext ctx, Guid id, AddTaskEvidenceReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("task:update")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var t = await db.WorkTasks.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (t is null) return Results.NotFound(new { error = "Task not found." });
    var ev = new TaskEvidence(Guid.NewGuid(), req.TenantId, id, req.UploadedBy, req.ObjectKey, req.FileName, DateTimeOffset.UtcNow);
    db.TaskEvidences.Add(ev);
    DomainEvents.Record(db, req.TenantId, "TaskEvidenceAdded", "TaskEvidenceAdded",
        nameof(TaskEvidence), ev.Id.ToString(),
        payload: new { tenantId = req.TenantId, taskId = id, objectKey = req.ObjectKey }, details: req.FileName);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/tasks/{id}/evidence/{ev.Id}", ev);
});
tasks.MapGet("/{id:guid}/evidence", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("task:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    return Results.Ok(await db.TaskEvidences.Where(e => e.TenantId == tenantId && e.TaskId == id).OrderBy(e => e.At).ToListAsync(ct));
});
tasks.MapPost("/{id:guid}/comments", async (AppDbContext db, HttpContext ctx, Guid id, AddTaskCommentReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("task:update")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new { error = "Text required." });
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var t = await db.WorkTasks.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (t is null) return Results.NotFound(new { error = "Task not found." });
    var c = new TaskComment(Guid.NewGuid(), req.TenantId, id, req.AuthorId, req.Text.Trim(), DateTimeOffset.UtcNow);
    db.TaskComments.Add(c);
    DomainEvents.Record(db, req.TenantId, "TaskCommentAdded", "TaskCommentAdded",
        nameof(TaskComment), c.Id.ToString(),
        payload: new { tenantId = req.TenantId, taskId = id }, details: req.Text[..Math.Min(80, req.Text.Length)]);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/tasks/{id}/comments/{c.Id}", c);
});
tasks.MapGet("/{id:guid}/comments", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("task:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    return Results.Ok(await db.TaskComments.Where(c => c.TenantId == tenantId && c.TaskId == id).OrderBy(c => c.At).ToListAsync(ct));
});

var sla = app.MapGroup("/api/sla").WithTags("SLA").RequireAuthorization();
sla.MapGet("/breaches", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("approval:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    var now = DateTimeOffset.UtcNow;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var approvals = await db.Approvals
        .Where(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending && a.DueAt < now)
        .ToListAsync(ct);
    var taskList = await db.WorkTasks
        .Where(t => t.TenantId == tenantId && t.Status != WorkTaskStatus.Done && t.DueAt < now)
        .ToListAsync(ct);
    return Results.Ok(new { approvals, tasks = taskList });
});

var notifs = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();
notifs.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? personId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("notification:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var q = db.Notifications.Where(n => n.TenantId == tenantId);
    if (personId.HasValue) q = q.Where(n => n.PersonId == personId.Value);
    return Results.Ok(await q.OrderByDescending(n => n.CreatedAt).Take(100).ToListAsync(ct));
});

// ============================ R3 — Governance ============================

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
        payload: new { tenantId = req.TenantId, committeeId = id, personId = req.PersonId }, details: req.PersonId.ToString());
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (IsUniqueConflict(ex))
    {
        return Results.Conflict(new { error = "Person already a member." });
    }
    await scope.CommitAsync(ct);
    return Results.Created($"/api/committees/{id}/members/{m.Id}", m);
});

var meetings = app.MapGroup("/api/meetings").WithTags("Meetings").RequireAuthorization();
meetings.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? committeeId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("meeting:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var q = db.Meetings.Where(m => m.TenantId == tenantId);
    if (committeeId.HasValue) q = q.Where(m => m.CommitteeId == committeeId.Value);
    return Results.Ok(await q.OrderBy(m => m.StartsAt).ToListAsync(ct));
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
    foreach (var (item, i) in (req.Agenda ?? []).Select((a, i) => (a, i)))
        db.AgendaItems.Add(new AgendaItem(Guid.NewGuid(), req.TenantId, m.Id, i + 1, item.Title, item.Description));
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
    if (!await db.Meetings.AnyAsync(m => m.TenantId == req.TenantId && m.Id == id, ct))
        return Results.NotFound(new { error = "Meeting not found." });
    var existing = await db.Attendances.FirstOrDefaultAsync(a => a.TenantId == req.TenantId && a.MeetingId == id && a.PersonId == req.PersonId, ct);
    if (existing is null)
        db.Attendances.Add(new Attendance(Guid.NewGuid(), req.TenantId, id, req.PersonId, st));
    else
        db.Entry(existing).CurrentValues.SetValues(existing with { Status = st });
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
    // BP-ACD-014: Decision → immediate Publication (approval path is R2 workflow when needed).
    var d = new Decision(Guid.NewGuid(), req.TenantId, id, req.Text.Trim(), DecisionStatus.Published, DateTimeOffset.UtcNow);
    db.Decisions.Add(d);
    DomainEvents.Record(db, req.TenantId, "DecisionPublished", "DecisionPublished",
        nameof(Decision), d.Id.ToString(),
        payload: new { tenantId = req.TenantId, decisionId = d.Id, meetingId = id, committeeId = m.CommitteeId, text = d.Text }, details: d.Text[..Math.Min(80, d.Text.Length)]);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/decisions/{d.Id}", d);
});

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
        payload: new { tenantId = req.TenantId, policyId = p.Id, code = p.Code, title = p.Title }, details: p.Code);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(p);
});
policies.MapPost("/{id:guid}/acknowledge", async (AppDbContext db, HttpContext ctx, Guid id, AcknowledgePolicyReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("policy:ack")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var p = await db.Policies.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (p is null) return Results.NotFound(new { error = "Policy not found." });
    if (p.Status != PolicyStatus.Published) return Results.Conflict(new { error = "Only published policies can be acknowledged." });
    if (!await db.People.AnyAsync(x => x.TenantId == req.TenantId && x.Id == req.PersonId, ct))
        return Results.NotFound(new { error = "Person not found in tenant." });
    var ack = new PolicyAcknowledgement(Guid.NewGuid(), req.TenantId, id, req.PersonId, DateTimeOffset.UtcNow);
    db.PolicyAcknowledgements.Add(ack);
    DomainEvents.Record(db, req.TenantId, "PolicyAcknowledged", "PolicyAcknowledged",
        nameof(PolicyAcknowledgement), ack.Id.ToString(),
        payload: new { tenantId = req.TenantId, policyId = id, personId = req.PersonId }, details: id.ToString());
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (IsUniqueConflict(ex))
    {
        return Results.Conflict(new { error = "Already acknowledged." });
    }
    await scope.CommitAsync(ct);
    return Results.Created($"/api/policies/{id}/acks/{ack.Id}", ack);
});
policies.MapGet("/{id:guid}/pending", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("policy:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    if (!await db.Policies.AnyAsync(p => p.TenantId == tenantId && p.Id == id, ct))
        return Results.NotFound(new { error = "Policy not found." });
    var acked = await db.PolicyAcknowledgements
        .Where(a => a.TenantId == tenantId && a.PolicyId == id)
        .Select(a => a.PersonId).ToListAsync(ct);
    var pending = await db.People
        .Where(p => p.TenantId == tenantId && p.IsActive && !acked.Contains(p.Id))
        .Select(p => new { p.Id, p.FullName })
        .ToListAsync(ct);
    return Results.Ok(pending);
});

// ============================ R4 — Institutional Intelligence ============================

var docs = app.MapGroup("/api/documents").WithTags("Documents").RequireAuthorization();
docs.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("document:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    return Results.Ok(await db.Documents.Where(d => d.TenantId == tenantId).OrderBy(d => d.Title).ToListAsync(ct));
});
docs.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateDocumentReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("document:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "Title required." });
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var d = new Document(Guid.NewGuid(), req.TenantId, req.Title.Trim(), DocumentStatus.Draft, 0);
    db.Documents.Add(d);
    DomainEvents.Record(db, req.TenantId, "DocumentCreated", "DocumentCreated",
        nameof(Document), d.Id.ToString(),
        payload: new { tenantId = req.TenantId, documentId = d.Id, title = d.Title }, details: d.Title);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/documents/{d.Id}", d);
});
docs.MapPost("/{id:guid}/upload-url", async (AppDbContext db, HttpContext ctx, ObjectStorage storage, Guid id, UploadUrlReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("document:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    if (!await db.Documents.AnyAsync(d => d.TenantId == req.TenantId && d.Id == id, ct))
        return Results.NotFound(new { error = "Document not found." });
    await storage.EnsureBucketAsync(ct);
    var key = $"{req.TenantId}/{id}/{Guid.NewGuid():N}-{req.FileName}";
    return Results.Ok(new { objectKey = key, putUrl = await storage.PresignedPutAsync(key, ct: ct) });
});
docs.MapPost("/{id:guid}/versions", async (AppDbContext db, HttpContext ctx, Guid id, AddVersionReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("document:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var d = await db.Documents.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (d is null) return Results.NotFound(new { error = "Document not found." });
    var version = d.CurrentVersion + 1;
    db.DocumentVersions.Add(new DocumentVersion(Guid.NewGuid(), req.TenantId, id, version, req.ObjectKey, req.SizeBytes, req.Sha256 ?? "", DateTimeOffset.UtcNow));
    db.Entry(d).CurrentValues.SetValues(d with { CurrentVersion = version, Status = req.Publish ? DocumentStatus.Published : d.Status });
    DomainEvents.Record(db, req.TenantId, "DocumentVersionAdded", "DocumentVersionAdded",
        nameof(Document), d.Id.ToString(),
        payload: new { tenantId = req.TenantId, documentId = id, version }, details: $"v{version}");
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/documents/{id}/versions/{version}", new { version });
});
docs.MapGet("/{id:guid}/download-url", async (AppDbContext db, HttpContext ctx, ObjectStorage storage, Guid id, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("document:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var v = await db.DocumentVersions.Where(x => x.TenantId == tenantId && x.DocumentId == id)
        .OrderByDescending(x => x.Version).FirstOrDefaultAsync(ct);
    if (v is null) return Results.NotFound(new { error = "No versions." });
    return Results.Ok(new { objectKey = v.ObjectKey, getUrl = await storage.PresignedGetAsync(v.ObjectKey, ct: ct), version = v.Version });
});
docs.MapPost("/{id:guid}/share", async (AppDbContext db, HttpContext ctx, Guid id, ShareDocumentReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("document:share")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    if (!await db.Documents.AnyAsync(d => d.TenantId == req.TenantId && d.Id == id, ct))
        return Results.NotFound(new { error = "Document not found." });
    if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.PersonId, ct))
        return Results.NotFound(new { error = "Person not found in tenant." });
    var share = new DocumentShare(Guid.NewGuid(), req.TenantId, id, req.PersonId,
        DateTimeOffset.UtcNow, req.ExpiresAt);
    db.DocumentShares.Add(share);
    DomainEvents.Record(db, req.TenantId, "DocumentShared", "DocumentShared",
        nameof(DocumentShare), share.Id.ToString(),
        payload: new { tenantId = req.TenantId, documentId = id, personId = req.PersonId, expiresAt = req.ExpiresAt },
        details: $"shared with person {req.PersonId}");
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/documents/{id}/shares/{share.Id}", share);
});
docs.MapPatch("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, UpdateDocumentReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("document:update")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var d = await db.Documents.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct);
    if (d is null) return Results.NotFound(new { error = "Document not found." });
    var updated = d with
    {
        Title = string.IsNullOrWhiteSpace(req.Title) ? d.Title : req.Title.Trim(),
        Classification = req.Classification is not null && Enum.TryParse<DocumentClassification>(req.Classification, true, out var c) ? c : d.Classification,
        RetainUntil = req.RetainUntil,
        Status = req.Status is not null && Enum.TryParse<DocumentStatus>(req.Status, true, out var s) ? s : d.Status,
    };
    db.Entry(d).CurrentValues.SetValues(updated);
    DomainEvents.Record(db, tenantId, "DocumentUpdated", "DocumentUpdated",
        nameof(Document), d.Id.ToString(),
        payload: new { tenantId, documentId = id, classification = updated.Classification, retainUntil = updated.RetainUntil }, details: updated.Title);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(updated);
});

var search = app.MapGroup("/api/search").WithTags("Search").RequireAuthorization();
search.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, string q, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("search:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    if (string.IsNullOrWhiteSpace(q) || q.Length < 2) return Results.BadRequest(new { error = "q (min 2 chars) required." });
    var canSeeConfidential = ctx.User.HasPermission("correspondence:confidential");
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    // Trigram-backed ILIKE across R1–R4 entities (OpenSearch sync is the R5 scale path).
    var correspondence = await db.Correspondences
        .Where(c => c.TenantId == tenantId && (canSeeConfidential || !c.IsConfidential) && c.Subject.Contains(q))
        .Select(c => new { kind = "correspondence", id = c.Id, title = c.Subject }).Take(20).ToListAsync(ct);
    var people = await db.People.Where(p => p.TenantId == tenantId && p.FullName.Contains(q))
        .Select(p => new { kind = "person", id = p.Id, title = p.FullName }).Take(20).ToListAsync(ct);
    var decisions = await db.Decisions.Where(d => d.TenantId == tenantId && d.Text.Contains(q))
        .Select(d => new { kind = "decision", id = d.Id, title = d.Text }).Take(20).ToListAsync(ct);
    var documents = await db.Documents.Where(d => d.TenantId == tenantId && d.Title.Contains(q))
        .Select(d => new { kind = "document", id = d.Id, title = d.Title }).Take(20).ToListAsync(ct);
    var policies = await db.Policies.Where(p => p.TenantId == tenantId && p.Title.Contains(q))
        .Select(p => new { kind = "policy", id = p.Id, title = p.Title }).Take(20).ToListAsync(ct);
    return Results.Ok(new { correspondence, people, decisions, documents, policies });
});

var analytics = app.MapGroup("/api/analytics").WithTags("Analytics").RequireAuthorization();
analytics.MapGet("/overview", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("analytics:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    var now = DateTimeOffset.UtcNow;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var corrByStatus = await db.Correspondences.Where(c => c.TenantId == tenantId)
        .GroupBy(c => c.Status).Select(g => new { status = g.Key.ToString(), count = g.Count() }).ToListAsync(ct);
    var pendingApprovals = await db.Approvals.CountAsync(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending, ct);
    var breachedApprovals = await db.Approvals.CountAsync(a => a.TenantId == tenantId && a.Status == ApprovalStatus.Pending && a.DueAt < now, ct);
    var openTasks = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && t.Status != WorkTaskStatus.Done, ct);
    var breachedTasks = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && t.Status == WorkTaskStatus.Breached, ct);
    var decisions = await db.Decisions.CountAsync(d => d.TenantId == tenantId, ct);
    var openActions = await db.DecisionActions.CountAsync(a => a.TenantId == tenantId && a.Status != DecisionActionStatus.Done && a.Status != DecisionActionStatus.Verified, ct);
    var people = await db.People.CountAsync(p => p.TenantId == tenantId && p.IsActive, ct);
    var publishedPolicies = await db.Policies.CountAsync(p => p.TenantId == tenantId && p.Status == PolicyStatus.Published, ct);
    var ackRate = publishedPolicies == 0 ? 1.0 : (double)await db.PolicyAcknowledgements.CountAsync(a => a.TenantId == tenantId, ct)
        / Math.Max(1, publishedPolicies * Math.Max(1, people));
    return Results.Ok(new
    {
        correspondenceByStatus = corrByStatus, pendingApprovals, breachedApprovals,
        openTasks, breachedTasks, decisions, openActions, activePeople = people,
        publishedPolicies, policyAckRate = Math.Round(Math.Min(1, ackRate), 3),
    });
});

var quality = app.MapGroup("/api/quality").WithTags("Quality").RequireAuthorization();
quality.MapPost("/standards", async (AppDbContext db, HttpContext ctx, CreateStandardReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var s = new Standard(Guid.NewGuid(), req.TenantId, req.Code.Trim(), req.Title.Trim());
    db.Standards.Add(s);
    DomainEvents.Record(db, req.TenantId, "StandardCreated", "StandardCreated",
        nameof(Standard), s.Id.ToString(),
        payload: new { tenantId = req.TenantId, standardId = s.Id, code = s.Code }, details: s.Code);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (IsUniqueConflict(ex))
    {
        return Results.Conflict(new { error = $"Standard code '{req.Code}' already exists." });
    }
    await scope.CommitAsync(ct);
    return Results.Created($"/api/quality/standards/{s.Id}", s);
});
quality.MapPost("/criteria", async (AppDbContext db, HttpContext ctx, CreateCriterionReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    if (!await db.Standards.AnyAsync(s => s.TenantId == req.TenantId && s.Id == req.StandardId, ct))
        return Results.NotFound(new { error = "Standard not found." });
    var c = new Criterion(Guid.NewGuid(), req.TenantId, req.StandardId, req.Code.Trim(), req.Text);
    db.Criteria.Add(c);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/quality/criteria/{c.Id}", c);
});
quality.MapPost("/evidence", async (AppDbContext db, HttpContext ctx, AddEvidenceReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    if (!await db.Criteria.AnyAsync(c => c.TenantId == req.TenantId && c.Id == req.CriterionId, ct))
        return Results.NotFound(new { error = "Criterion not found." });
    var e = new Evidence(Guid.NewGuid(), req.TenantId, req.CriterionId, req.EntityType, req.EntityId, req.Note ?? "");
    db.Evidences.Add(e);
    DomainEvents.Record(db, req.TenantId, "EvidenceAdded", "EvidenceAdded",
        nameof(Evidence), e.Id.ToString(),
        payload: new { tenantId = req.TenantId, criterionId = req.CriterionId, entityType = req.EntityType, entityId = req.EntityId }, details: req.EntityType);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/quality/evidence/{e.Id}", e);
});
quality.MapPost("/findings", async (AppDbContext db, HttpContext ctx, CreateFindingReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (!Enum.TryParse<FindingSeverity>(req.Severity, true, out var sev))
        return Results.BadRequest(new { error = "Severity must be Observation|Minor|Major." });
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    if (!await db.Criteria.AnyAsync(c => c.TenantId == req.TenantId && c.Id == req.CriterionId, ct))
        return Results.NotFound(new { error = "Criterion not found." });
    var fnd = new Finding(Guid.NewGuid(), req.TenantId, req.CriterionId, sev, req.Text, false);
    db.Findings.Add(fnd);
    DomainEvents.Record(db, req.TenantId, "FindingOpened", "FindingOpened",
        nameof(Finding), fnd.Id.ToString(),
        payload: new { tenantId = req.TenantId, findingId = fnd.Id, severity = sev.ToString() }, details: sev.ToString());
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/quality/findings/{fnd.Id}", fnd);
});
quality.MapPost("/corrective-actions", async (AppDbContext db, HttpContext ctx, CreateCorrectiveActionReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("quality:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    if (!await db.Findings.AnyAsync(x => x.TenantId == req.TenantId && x.Id == req.FindingId, ct))
        return Results.NotFound(new { error = "Finding not found." });
    if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.AssigneeId, ct))
        return Results.NotFound(new { error = "Assignee not found in tenant." });
    var a = new CorrectiveAction(Guid.NewGuid(), req.TenantId, req.FindingId, req.AssigneeId,
        req.Description, CorrectiveActionStatus.Open, req.DueAt ?? DateTimeOffset.UtcNow.AddDays(30));
    db.CorrectiveActions.Add(a);
    DomainEvents.Record(db, req.TenantId, "CorrectiveActionOpened", "CorrectiveActionOpened",
        nameof(CorrectiveAction), a.Id.ToString(),
        payload: new { tenantId = req.TenantId, actionId = a.Id, findingId = req.FindingId, assigneeId = req.AssigneeId }, details: a.Description);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/quality/corrective-actions/{a.Id}", a);
});

var strategy = app.MapGroup("/api/strategy").WithTags("Strategy").RequireAuthorization();
strategy.MapPost("/plans", async (AppDbContext db, HttpContext ctx, CreatePlanReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("strategy:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var p = new StrategicPlan(Guid.NewGuid(), req.TenantId, req.Title.Trim(), req.YearFrom, req.YearTo);
    db.StrategicPlans.Add(p);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/strategy/plans/{p.Id}", p);
});
strategy.MapPost("/objectives", async (AppDbContext db, HttpContext ctx, CreateObjectiveReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("strategy:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    if (!await db.StrategicPlans.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.PlanId, ct))
        return Results.NotFound(new { error = "Plan not found." });
    var o = new Objective(Guid.NewGuid(), req.TenantId, req.PlanId, req.Code.Trim(), req.Text);
    db.Objectives.Add(o);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (IsUniqueConflict(ex))
    {
        return Results.Conflict(new { error = $"Objective code '{req.Code}' already exists in plan." });
    }
    await scope.CommitAsync(ct);
    return Results.Created($"/api/strategy/objectives/{o.Id}", o);
});
strategy.MapPost("/kpis", async (AppDbContext db, HttpContext ctx, CreateKpiReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("strategy:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    if (!await db.Objectives.AnyAsync(o => o.TenantId == req.TenantId && o.Id == req.ObjectiveId, ct))
        return Results.NotFound(new { error = "Objective not found." });
    var k = new Kpi(Guid.NewGuid(), req.TenantId, req.ObjectiveId, req.Name.Trim(), req.Target, req.Current, req.Unit ?? "");
    db.Kpis.Add(k);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/strategy/kpis/{k.Id}", k);
});
strategy.MapPost("/kpis/{id:guid}/reading", async (AppDbContext db, HttpContext ctx, Guid id, KpiReadingReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("strategy:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var k = await db.Kpis.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (k is null) return Results.NotFound(new { error = "KPI not found." });
    db.Entry(k).CurrentValues.SetValues(k with { Current = req.Current });
    DomainEvents.Record(db, req.TenantId, "KpiUpdated", "KpiUpdated",
        nameof(Kpi), k.Id.ToString(),
        payload: new { tenantId = req.TenantId, kpiId = k.Id, current = req.Current, target = k.Target }, details: k.Name);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(k);
});

// ============================ R5 — AI & Ecosystem ============================

var ai = app.MapGroup("/api/ai").WithTags("AI").RequireAuthorization();
ai.MapPost("/index", async (AppDbContext db, HttpContext ctx, TenantSearchIndex index, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("ai:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    await index.EnsureIndexAsync(tenantId, ct);
    var count = 0;
    foreach (var c in await db.Correspondences.Where(x => x.TenantId == tenantId).Take(500).ToListAsync(ct))
    { await index.IndexAsync(tenantId, "correspondence", c.Id, c.Subject, c.Content, ct); count++; }
    foreach (var d in await db.Decisions.Where(x => x.TenantId == tenantId).Take(500).ToListAsync(ct))
    { await index.IndexAsync(tenantId, "decision", d.Id, d.Text[..Math.Min(200, d.Text.Length)], d.Text, ct); count++; }
    foreach (var d in await db.Documents.Where(x => x.TenantId == tenantId).Take(500).ToListAsync(ct))
    { await index.IndexAsync(tenantId, "document", d.Id, d.Title, d.Title, ct); count++; }
    foreach (var p in await db.Policies.Where(x => x.TenantId == tenantId).Take(500).ToListAsync(ct))
    { await index.IndexAsync(tenantId, "policy", p.Id, p.Title, p.Content, ct); count++; }
    DomainEvents.Record(db, tenantId, "TenantIndexed", "TenantIndexed",
        nameof(Tenant), tenantId.ToString(),
        payload: new { tenantId, documents = count }, details: count.ToString());
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(new { indexed = count });
});
ai.MapPost("/ask", async (AppDbContext db, HttpContext ctx, TenantSearchIndex index,
    IHttpClientFactory httpFactory, Microsoft.Extensions.Options.IOptions<AiOptions> aiOpts,
    AskReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("ai:ask")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (string.IsNullOrWhiteSpace(req.Question)) return Results.BadRequest(new { error = "Question required." });
    var capability = req.Capability ?? "ask";
    List<(string Kind, string Title, string Text)> passages;
    try { passages = await index.SearchAsync(req.TenantId, req.Question, 5, ct); }
    catch
    {
        // Retrieval fallback: PG trigram search when OpenSearch is unreachable.
        await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
        passages = (await db.Correspondences.Where(c => c.TenantId == req.TenantId && c.Subject.Contains(req.Question))
                .Select(c => new { K = "correspondence", T = c.Subject }).Take(5).ToListAsync(ct))
            .Select(x => (x.K, x.T, "")).ToList();
    }
    var opts = aiOpts.Value;
    string answer, model;
    double? confidence = null;
    if (opts.Provider.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(opts.BaseUrl) && !string.IsNullOrWhiteSpace(opts.ApiKey))
    {
        (answer, model, confidence) = await GenerateOpenAiAsync(httpFactory, opts, req.Question, passages, ct);
    }
    else
    {
        // Echo provider: extractive, no external LLM — safe default for on-prem/air-gap.
        model = "echo-extractive-v1";
        answer = passages.Count == 0
            ? "No relevant institutional content found for this question."
            : "Relevant passages:\n" + string.Join("\n", passages.Select((p, i) => $"[{i + 1}] ({p.Kind}) {p.Title}"));
    }
    await using (var scope2 = await TenantScope.BeginAsync(db, req.TenantId, ct))
    {
        db.AiInteractions.Add(new AiInteraction(Guid.NewGuid(), req.TenantId, capability,
            req.Question[..Math.Min(500, req.Question.Length)],
            answer[..Math.Min(2000, answer.Length)],
            model, confidence, null, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
        await scope2.CommitAsync(ct);
    }
    return Results.Ok(new { answer, model, passages = passages.Select(p => new { kind = p.Kind, title = p.Title }) });
});
ai.MapGet("/interactions", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("ai:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    return Results.Ok(await db.AiInteractions.Where(a => a.TenantId == tenantId)
        .OrderByDescending(a => a.At).Take(100).ToListAsync(ct));
});

static async Task<(string Answer, string Model, double?)> GenerateOpenAiAsync(
    IHttpClientFactory httpFactory, AiOptions opts, string question,
    List<(string Kind, string Title, string Text)> passages, CancellationToken ct)
{
    var context = string.Join("\n", passages.Select((p, i) => $"[{i + 1}] ({p.Kind}) {p.Title}: {p.Text}"));
    var body = JsonSerializer.Serialize(new
    {
        model = opts.Model,
        messages = new object[]
        {
            new { role = "system", content = "Answer using only the provided institutional passages. If insufficient, say so." },
            new { role = "user", content = $"Question: {question}\nPassages:\n{context}" },
        },
    });
    using var req = new HttpRequestMessage(HttpMethod.Post, opts.BaseUrl.TrimEnd('/') + "/chat/completions")
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
    using var res = await httpFactory.CreateClient("ai").SendAsync(req, ct);
    res.EnsureSuccessStatusCode();
    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    var answer = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    return (answer, opts.Model, null);
}

var integrations = app.MapGroup("/api/integrations").WithTags("Integrations").RequireAuthorization();
integrations.MapGet("/endpoints", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("integration:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    return Results.Ok(await db.IntegrationEndpoints.Where(e => e.TenantId == tenantId).ToListAsync(ct));
});
integrations.MapPost("/endpoints", async (AppDbContext db, HttpContext ctx, RegisterEndpointReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("integration:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (!Uri.TryCreate(req.TargetUrl, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        return Results.BadRequest(new { error = "TargetUrl must be absolute http(s)." });
    if (string.IsNullOrWhiteSpace(req.EventType)) return Results.BadRequest(new { error = "EventType required." });
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var ep = new IntegrationEndpoint(Guid.NewGuid(), req.TenantId, req.EventType.Trim(), req.TargetUrl,
        string.IsNullOrWhiteSpace(req.Secret) ? Guid.NewGuid().ToString("N") : req.Secret, true);
    db.IntegrationEndpoints.Add(ep);
    DomainEvents.Record(db, req.TenantId, "IntegrationEndpointRegistered", "IntegrationEndpointRegistered",
        nameof(IntegrationEndpoint), ep.Id.ToString(),
        payload: new { tenantId = req.TenantId, endpointId = ep.Id, eventType = ep.EventType }, details: ep.EventType);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/integrations/endpoints/{ep.Id}", new { ep.Id, ep.EventType, ep.TargetUrl });
});
integrations.MapDelete("/endpoints/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("integration:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var ep = await db.IntegrationEndpoints.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.Id == id, ct);
    if (ep is null) return Results.NotFound(new { error = "Endpoint not found." });
    db.Entry(ep).CurrentValues.SetValues(ep with { IsActive = false });
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.NoContent();
});
integrations.MapGet("/deliveries", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid? endpointId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("integration:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var q = db.IntegrationDeliveries.Where(d => d.TenantId == tenantId);
    if (endpointId.HasValue) q = q.Where(d => d.EndpointId == endpointId.Value);
    return Results.Ok(await q.OrderByDescending(d => d.At).Take(100).ToListAsync(ct));
});

// ============================ R0.1 Track A — Requests & Dynamic Forms ============================

var forms = app.MapGroup("/api/forms").WithTags("Forms").RequireAuthorization();
forms.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("form:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    return Results.Ok(await db.Forms.Where(f => f.TenantId == tenantId && f.IsActive).OrderBy(f => f.Code).ToListAsync(ct));
});
forms.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateFormReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("form:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (!Enum.TryParse<RequestCategory>(req.Category, true, out var cat))
        return Results.BadRequest(new { error = "Unknown category." });
    if (string.IsNullOrWhiteSpace(req.Code)) return Results.BadRequest(new { error = "Code required." });
    var schemaErrors = FormValidation.Validate(req.SchemaJson, "{}");
    if (schemaErrors.Any(e => e is "invalid form schema" or "form schema must be an array"))
        return Results.BadRequest(new { error = "SchemaJson must be a JSON array of fields." });
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var form = new Form(Guid.NewGuid(), req.TenantId, req.Code.Trim(), req.Name.Trim(), cat, req.SchemaJson, 1, true);
    db.Forms.Add(form);
    DomainEvents.Record(db, req.TenantId, "FormCreated", "FormCreated",
        nameof(Form), form.Id.ToString(),
        payload: new { tenantId = req.TenantId, formId = form.Id, code = form.Code }, details: form.Code);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (IsUniqueConflict(ex))
    {
        return Results.Conflict(new { error = $"Form code '{req.Code}' already exists in tenant." });
    }
    await scope.CommitAsync(ct);
    return Results.Created($"/api/forms/{form.Id}", form);
});
forms.MapPost("/{id:guid}/validate", async (AppDbContext db, HttpContext ctx, Guid id, ValidateSubmissionReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("form:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var form = await db.Forms.FirstOrDefaultAsync(f => f.TenantId == req.TenantId && f.Id == id, ct);
    if (form is null) return Results.NotFound(new { error = "Form not found." });
    return Results.Ok(new { valid = true, errors = FormValidation.Validate(form.SchemaJson, req.DataJson) });
});

var requests = app.MapGroup("/api/requests").WithTags("Requests").RequireAuthorization();
requests.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? status, string? category, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("request:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var q = db.Requests.Where(r => r.TenantId == tenantId);
    if (Enum.TryParse<RequestStatus>(status, true, out var s)) q = q.Where(r => r.Status == s);
    if (Enum.TryParse<RequestCategory>(category, true, out var c)) q = q.Where(r => r.Category == c);
    return Results.Ok(await q.OrderByDescending(r => r.CreatedAt).ToListAsync(ct));
});
requests.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateRequestReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("request:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (!Enum.TryParse<RequestCategory>(req.Category, true, out var cat))
        return Results.BadRequest(new { error = "Unknown category." });
    if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "Title required." });
    if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.SubmitterId, ct))
        return Results.NotFound(new { error = "Submitter not found in tenant." });
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    Form? form = null;
    if (req.FormId.HasValue)
    {
        form = await db.Forms.FirstOrDefaultAsync(f => f.TenantId == req.TenantId && f.Id == req.FormId.Value, ct);
        if (form is null) return Results.NotFound(new { error = "Form not found." });
        var errs = FormValidation.Validate(form.SchemaJson, req.DataJson ?? "{}");
        if (errs.Count > 0) return Results.BadRequest(new { error = "Submission invalid.", errors = errs });
    }
    var seq = await Sequences.NextAsync(db, req.TenantId, "request", ct);
    var r = new Request(Guid.NewGuid(), req.TenantId, $"REQ-{DateTimeOffset.UtcNow:yyyy}-{seq:D6}",
        cat, req.Title.Trim(), form?.Id, req.SubmitterId, RequestStatus.Draft, DateTimeOffset.UtcNow, null);
    db.Requests.Add(r);
    if (form is not null)
        db.FormSubmissions.Add(new FormSubmission(Guid.NewGuid(), req.TenantId, r.Id, form.Id,
            form.Version, req.DataJson ?? "{}", req.SubmitterId, DateTimeOffset.UtcNow));
    DomainEvents.Record(db, req.TenantId, "RequestCreated", "RequestCreated",
        nameof(Request), r.Id.ToString(),
        payload: new { tenantId = req.TenantId, requestId = r.Id, number = r.Number, category = cat.ToString() }, details: r.Number);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/requests/{r.Id}", r);
});
requests.MapPost("/{id:guid}/submit", async (AppDbContext db, HttpContext ctx, Guid id, SubmitRequestReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("request:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var r = await db.Requests.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
    if (r is null) return Results.NotFound(new { error = "Request not found." });
    if (r.Status != RequestStatus.Draft)
        return Results.Conflict(new { error = $"Only Draft requests can be submitted (now {r.Status})." });
    db.Entry(r).CurrentValues.SetValues(r with { Status = RequestStatus.Submitted, SubmittedAt = DateTimeOffset.UtcNow });
    Guid? approvalId = null;
    Guid? instanceId = null;
    if (!string.IsNullOrWhiteSpace(req.WorkflowCode))
    {
        // Workflow-driven path (M06): runner creates the approval chain.
        try
        {
            var started = await WorkflowRunner.StartAsync(db, req.TenantId, req.WorkflowCode,
                nameof(Request), r.Id, r.SubmitterId, ct);
            instanceId = started.InstanceId;
            approvalId = started.PendingApprovalId;
        }
        catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
    }
    else if (req.ReviewerId.HasValue)
    {
        if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.ReviewerId.Value, ct))
            return Results.NotFound(new { error = "Reviewer not found in tenant." });
        var approval = new Approval(Guid.NewGuid(), req.TenantId, nameof(Request), r.Id,
            req.ReviewerId.Value, ApprovalStatus.Pending, ApprovalPriority.Normal,
            DateTimeOffset.UtcNow.AddDays(5), null, null, null);
        db.Approvals.Add(approval);
        db.WorkTasks.Add(new WorkTask(Guid.NewGuid(), req.TenantId, $"Review {r.Number}: {r.Title}",
            req.ReviewerId.Value, approval.Id, WorkTaskStatus.Open, approval.DueAt, DateTimeOffset.UtcNow));
        approvalId = approval.Id;
    }
    DomainEvents.Record(db, req.TenantId, "RequestSubmitted", "RequestSubmitted",
        nameof(Request), r.Id.ToString(),
        payload: new { tenantId = req.TenantId, requestId = r.Id, number = r.Number, approvalId, instanceId }, details: r.Number);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(new { request = r, approvalId, instanceId });
});

var workflows = app.MapGroup("/api/workflows").WithTags("Workflows").RequireAuthorization();
workflows.MapGet("/definitions", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("workflow:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    return Results.Ok(await db.WorkflowDefinitions.Where(d => d.TenantId == tenantId).OrderBy(d => d.Code).ToListAsync(ct));
});
workflows.MapPost("/definitions", async (AppDbContext db, HttpContext ctx, CreateWorkflowReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("workflow:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (string.IsNullOrWhiteSpace(req.Code)) return Results.BadRequest(new { error = "Code required." });
    try { WorkflowDefinitionValidator.Validate(req.NodesJson); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var def = new WorkflowDefinition(Guid.NewGuid(), req.TenantId, req.Code.Trim(),
        req.Name.Trim(), 1, req.NodesJson, true);
    db.WorkflowDefinitions.Add(def);
    DomainEvents.Record(db, req.TenantId, "WorkflowDefinitionCreated", "WorkflowDefinitionCreated",
        nameof(WorkflowDefinition), def.Id.ToString(),
        payload: new { tenantId = req.TenantId, definitionId = def.Id, code = def.Code }, details: def.Code);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (IsUniqueConflict(ex))
    {
        return Results.Conflict(new { error = $"Workflow code '{req.Code}' already exists in tenant." });
    }
    await scope.CommitAsync(ct);
    return Results.Created($"/api/workflows/definitions/{def.Id}", def);
});
workflows.MapGet("/instances", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? status, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("workflow:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var q = db.WorkflowInstances.Where(i => i.TenantId == tenantId);
    if (Enum.TryParse<WorkflowInstanceStatus>(status, true, out var s)) q = q.Where(i => i.Status == s);
    return Results.Ok(await q.OrderByDescending(i => i.UpdatedAt).ToListAsync(ct));
});

// ============================ R0.1 Tracks C+D — Communications, Inbox, My Work ============================

var communications = app.MapGroup("/api/communications").WithTags("Communications").RequireAuthorization();
communications.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, string? kind, string? status, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("communication:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var q = db.Communications.Where(c => c.TenantId == tenantId);
    if (Enum.TryParse<CommunicationKind>(kind, true, out var k)) q = q.Where(c => c.Kind == k);
    if (Enum.TryParse<CommunicationStatus>(status, true, out var s)) q = q.Where(c => c.Status == s);
    return Results.Ok(await q.OrderByDescending(c => c.CreatedAt).ToListAsync(ct));
});
communications.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateCommunicationReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("communication:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (!Enum.TryParse<CommunicationKind>(req.Kind, true, out var kind))
        return Results.BadRequest(new { error = "Kind must be Announcement|Circular|Directive." });
    if (string.IsNullOrWhiteSpace(req.Title)) return Results.BadRequest(new { error = "Title required." });
    if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.AuthorId, ct))
        return Results.NotFound(new { error = "Author not found in tenant." });
    var recipients = req.TargetPersonIds ?? [];
    foreach (var pid in recipients.Distinct())
        if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == pid, ct))
            return Results.BadRequest(new { error = $"Target person {pid} not found in tenant." });
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var comm = new Communication(Guid.NewGuid(), req.TenantId, kind, req.Title.Trim(), req.Body ?? "",
        req.AuthorId, req.RequiresAction, req.DueAt, CommunicationStatus.Draft, DateTimeOffset.UtcNow, null);
    db.Communications.Add(comm);
    foreach (var pid in recipients.Distinct())
        db.CommunicationRecipients.Add(new CommunicationRecipient(Guid.NewGuid(), req.TenantId, comm.Id, pid));
    DomainEvents.Record(db, req.TenantId, "CommunicationCreated", "CommunicationCreated",
        nameof(Communication), comm.Id.ToString(),
        payload: new { tenantId = req.TenantId, communicationId = comm.Id, kind = kind.ToString(), requiresAction = req.RequiresAction },
        details: comm.Title);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Created($"/api/communications/{comm.Id}", comm);
});
communications.MapPost("/{id:guid}/publish", async (AppDbContext db, HttpContext ctx, Guid id, PublishCommunicationReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("communication:create")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var comm = await db.Communications.FirstOrDefaultAsync(c => c.TenantId == req.TenantId && c.Id == id, ct);
    if (comm is null) return Results.NotFound(new { error = "Communication not found." });
    if (comm.Status == CommunicationStatus.Published)
        return Results.Conflict(new { error = "Already published." });
    db.Entry(comm).CurrentValues.SetValues(comm with { Status = CommunicationStatus.Published, PublishedAt = DateTimeOffset.UtcNow });
    var recipients = await db.CommunicationRecipients.Where(r => r.TenantId == req.TenantId && r.CommunicationId == id)
        .Select(r => r.PersonId).ToListAsync(ct);
    var due = comm.DueAt ?? DateTimeOffset.UtcNow.AddDays(14);
    foreach (var pid in recipients)
    {
        if (comm.RequiresAction)
        {
            var task = new WorkTask(Guid.NewGuid(), req.TenantId, $"[{comm.Kind}] {comm.Title}", pid,
                null, WorkTaskStatus.Open, due, DateTimeOffset.UtcNow);
            db.WorkTasks.Add(task);
            db.Notifications.Add(new Notification(Guid.NewGuid(), req.TenantId, pid,
                $"New {comm.Kind}: {comm.Title}", comm.Body.Length > 200 ? comm.Body[..200] : comm.Body,
                NotificationChannel.InApp, NotificationStatus.Sent, DateTimeOffset.UtcNow));
        }
        else
        {
            db.Notifications.Add(new Notification(Guid.NewGuid(), req.TenantId, pid,
                $"New {comm.Kind}: {comm.Title}", comm.Body.Length > 200 ? comm.Body[..200] : comm.Body,
                NotificationChannel.InApp, NotificationStatus.Sent, DateTimeOffset.UtcNow));
        }
    }
    DomainEvents.Record(db, req.TenantId, "CommunicationPublished", "CommunicationPublished",
        nameof(Communication), comm.Id.ToString(),
        payload: new { tenantId = req.TenantId, communicationId = comm.Id, kind = comm.Kind.ToString(), recipients = recipients.Count, tasksCreated = comm.RequiresAction ? recipients.Count : 0 },
        details: comm.Title);
    if (comm.RequiresAction && recipients.Count > 0)
        DomainEvents.Record(db, req.TenantId, "DirectiveTasksCreated", "DirectiveTasksCreated",
            nameof(WorkTask), id.ToString(),
            payload: new { tenantId = req.TenantId, communicationId = id, tasks = recipients.Count }, details: comm.Title);
    await db.SaveChangesAsync(ct);
    await scope.CommitAsync(ct);
    return Results.Ok(new { communication = comm, tasksCreated = comm.RequiresAction ? recipients.Count : 0 });
});

var templates = app.MapGroup("/api/notification-templates").WithTags("NotificationTemplates").RequireAuthorization();
templates.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("notification:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    return Results.Ok(await db.NotificationTemplates.Where(t => t.TenantId == tenantId).OrderBy(t => t.Code).ToListAsync(ct));
});
templates.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateTemplateReq req, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("notification:manage")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
    if (!Enum.TryParse<NotificationChannel>(req.Channel, true, out var ch))
        return Results.BadRequest(new { error = "Channel must be InApp|Email|Sms." });
    if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.BodyTemplate))
        return Results.BadRequest(new { error = "Code and BodyTemplate required." });
    await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
    var t = new NotificationTemplate(Guid.NewGuid(), req.TenantId, req.Code.Trim(), ch,
        req.Subject ?? "", req.BodyTemplate, true);
    db.NotificationTemplates.Add(t);
    DomainEvents.Record(db, req.TenantId, "NotificationTemplateCreated", "NotificationTemplateCreated",
        nameof(NotificationTemplate), t.Id.ToString(),
        payload: new { tenantId = req.TenantId, templateId = t.Id, code = t.Code }, details: t.Code);
    try { await db.SaveChangesAsync(ct); }
    catch (DbUpdateException ex) when (IsUniqueConflict(ex))
    {
        return Results.Conflict(new { error = $"Template '{req.Code}/{req.Channel}' already exists." });
    }
    await scope.CommitAsync(ct);
    return Results.Created($"/api/notification-templates/{t.Id}", t);
});
var receipts = app.MapGroup("/api/notification-receipts").WithTags("NotificationReceipts").RequireAuthorization();
receipts.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("notification:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    return Results.Ok(await db.NotificationReceipts.Where(r => r.TenantId == tenantId)
        .OrderByDescending(r => r.At).Take(100).ToListAsync(ct));
});

var inbox = app.MapGroup("/api/inbox").WithTags("Inbox").RequireAuthorization();
inbox.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid personId, string? filter, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("inbox:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var now = DateTimeOffset.UtcNow;
    var approvals = await db.Approvals.Where(a => a.TenantId == tenantId && a.AssigneeId == personId && a.Status == ApprovalStatus.Pending)
        .Select(a => new { type = "Approval", id = a.Id, title = $"{a.EntityType} approval", dueAt = (DateTimeOffset?)a.DueAt, status = a.Status.ToString() }).ToListAsync(ct);
    var tasks = await db.WorkTasks.Where(t => t.TenantId == tenantId && t.AssigneeId == personId && t.Status != WorkTaskStatus.Done)
        .Select(t => new { type = "Task", id = t.Id, title = t.Title, dueAt = (DateTimeOffset?)t.DueAt, status = t.Status.ToString() }).ToListAsync(ct);
    var corrs = await (from cr in db.CommunicationRecipients
                       join c in db.Communications on cr.CommunicationId equals c.Id
                       where cr.TenantId == tenantId && cr.PersonId == personId && c.Status == CommunicationStatus.Published
                       select new { type = "Communication", id = c.Id, title = c.Title, dueAt = c.DueAt, status = c.Status.ToString() }).ToListAsync(ct);
    var requests = await db.Requests.Where(r => r.TenantId == tenantId && r.SubmitterId == personId && r.Status != RequestStatus.Closed)
        .Select(r => new { type = "Request", id = r.Id, title = r.Title, dueAt = (DateTimeOffset?)null, status = r.Status.ToString() }).ToListAsync(ct);
    var notifs = await db.Notifications.Where(n => n.TenantId == tenantId && n.PersonId == personId && n.Status == NotificationStatus.Sent)
        .OrderByDescending(n => n.CreatedAt).Take(20)
        .Select(n => new { type = "Notification", id = n.Id, title = n.Title, dueAt = (DateTimeOffset?)null, status = n.Status.ToString() }).ToListAsync(ct);
    var all = approvals.Cast<object>().Concat(tasks).Concat(corrs).Concat(requests).Concat(notifs).ToList();
    // Unified filter mirroring BBP M04: All | Action Required | Approval | Correspondence | Task | Request | Overdue
    var filterKey = (filter ?? "All").Trim().ToLowerInvariant();
    IEnumerable<object> filtered = filterKey switch
    {
        "action required" => approvals.Cast<object>().Concat(tasks),
        "approval" => approvals,
        "correspondence" or "communication" => corrs,
        "task" => tasks,
        "request" => requests,
        "overdue" => tasks.Where(t => t.dueAt != null && t.dueAt < now).Concat(approvals.Where(a => a.dueAt != null && a.dueAt < now)),
        _ => all,
    };
    var items = filtered.ToList();
    return Results.Ok(new { total = items.Count, items });
});

var myWork = app.MapGroup("/api/my-work").WithTags("MyWork").RequireAuthorization();
myWork.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, Guid personId, CancellationToken ct) =>
{
    if (!ctx.User.HasPermission("inbox:read")) return Results.Forbid();
    if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
    await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
    var now = DateTimeOffset.UtcNow;
    var pendingApprovals = await db.Approvals.CountAsync(a => a.TenantId == tenantId && a.AssigneeId == personId && a.Status == ApprovalStatus.Pending, ct);
    var openTasks = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && t.AssigneeId == personId && t.Status != WorkTaskStatus.Done, ct);
    var overdueTasks = await db.WorkTasks.CountAsync(t => t.TenantId == tenantId && t.AssigneeId == personId && t.Status != WorkTaskStatus.Done && t.DueAt < now, ct);
    var myRequests = await db.Requests.CountAsync(r => r.TenantId == tenantId && r.SubmitterId == personId && r.Status != RequestStatus.Closed, ct);
    var waitingFor = await db.Requests.CountAsync(r => r.TenantId == tenantId && r.SubmitterId == personId && r.Status == RequestStatus.Submitted, ct);
    var unreadNotifications = await db.Notifications.CountAsync(n => n.TenantId == tenantId && n.PersonId == personId && n.Status == NotificationStatus.Sent, ct);
    var recentComms = await (from cr in db.CommunicationRecipients
                             join c in db.Communications on cr.CommunicationId equals c.Id
                             where cr.TenantId == tenantId && cr.PersonId == personId && c.Status == CommunicationStatus.Published
                             orderby c.PublishedAt descending
                             select new { id = c.Id, title = c.Title, kind = c.Kind.ToString(), publishedAt = c.PublishedAt }).Take(5).ToListAsync(ct);
    var priorityTasks = await db.WorkTasks.Where(t => t.TenantId == tenantId && t.AssigneeId == personId && t.Status != WorkTaskStatus.Done)
        .OrderBy(t => t.DueAt).Take(5).Select(t => new { id = t.Id, title = t.Title, dueAt = t.DueAt, status = t.Status.ToString() }).ToListAsync(ct);
    return Results.Ok(new
    {
        personId,
        counts = new { pendingApprovals, openTasks, myRequests, overdueTasks, waitingFor, unreadNotifications },
        priorityWork = priorityTasks,
        recentCommunications = recentComms,
    });
});

app.Run();

public sealed record CreateTenantReq(string Slug, string Name);
public sealed record CreateOrgReq(Guid TenantId, string Code, string Name);
public sealed record CreatePersonReq(Guid TenantId, string Type, string FullName, string? Email);
public sealed record DevTokenReq(string Subject, Guid TenantId, string[]? Permissions);
public sealed record CreateRoleReq(Guid TenantId, string Code, string Name, string[]? Permissions);
public sealed record AssignRoleReq(Guid TenantId, Guid PersonId, string RoleCode, string? Scope, DateTimeOffset? ExpiresAt);
public sealed record RevokeRoleReq(Guid TenantId, Guid PersonId, string RoleCode);
public sealed record RecipientReq(string? PersonId, string DisplayName, bool IsExternal);
public sealed record CreateCorrespondenceReq(Guid TenantId, string Type, string Subject, string Content, Guid AuthorId, string? Priority, bool IsConfidential, RecipientReq[]? Recipients);
public sealed record SubmitCorrespondenceReq(Guid TenantId, Guid ReviewerId);
public sealed record DecideApprovalReq(Guid TenantId, Guid DecidedBy, bool Approve, string? Comment);
public sealed record RequestChangesReq(Guid TenantId, Guid DecidedBy, string? Comment);
public sealed record DelegateApprovalReq(Guid TenantId, Guid DelegatedBy, Guid DelegateTo);
public sealed record CompleteTaskReq(Guid TenantId);
public sealed record UpdateTaskReq(Guid TenantId, string? Title, string? Description, string? Priority, int? Progress, string? Status);
public sealed record AddTaskEvidenceReq(Guid TenantId, Guid UploadedBy, string ObjectKey, string FileName);
public sealed record AddTaskCommentReq(Guid TenantId, Guid AuthorId, string Text);
public sealed record CreateCommitteeReq(Guid TenantId, string Code, string Name);
public sealed record AddMemberReq(Guid TenantId, Guid PersonId, string? Role);
public sealed record AgendaReq(string Title, string? Description);
public sealed record CreateMeetingReq(Guid TenantId, Guid CommitteeId, string Title, DateTimeOffset StartsAt, AgendaReq[]? Agenda);
public sealed record RecordAttendanceReq(Guid TenantId, Guid PersonId, string Status);
public sealed record ConcludeMeetingReq(Guid TenantId, string? Minutes);
public sealed record CreateDecisionReq(Guid TenantId, string Text);
public sealed record CreateDecisionActionReq(Guid TenantId, Guid AssigneeId, string Description, DateTimeOffset? DueAt);
public sealed record AdvanceActionReq(Guid TenantId, string Status);
public sealed record CreatePolicyReq(Guid TenantId, string Code, string Title, string? Content);
public sealed record PublishPolicyReq(Guid TenantId);
public sealed record AcknowledgePolicyReq(Guid TenantId, Guid PersonId);
public sealed record CreateDocumentReq(Guid TenantId, string Title);
public sealed record UploadUrlReq(Guid TenantId, string FileName);
public sealed record AddVersionReq(Guid TenantId, string ObjectKey, long SizeBytes, string? Sha256, bool Publish);
public sealed record CreateStandardReq(Guid TenantId, string Code, string Title);
public sealed record CreateCriterionReq(Guid TenantId, Guid StandardId, string Code, string Text);
public sealed record AddEvidenceReq(Guid TenantId, Guid CriterionId, string EntityType, Guid EntityId, string? Note);
public sealed record CreateFindingReq(Guid TenantId, Guid CriterionId, string Severity, string Text);
public sealed record CreateCorrectiveActionReq(Guid TenantId, Guid FindingId, Guid AssigneeId, string Description, DateTimeOffset? DueAt);
public sealed record CreatePlanReq(Guid TenantId, string Title, int YearFrom, int YearTo);
public sealed record CreateObjectiveReq(Guid TenantId, Guid PlanId, string Code, string Text);
public sealed record CreateKpiReq(Guid TenantId, Guid ObjectiveId, string Name, double Target, double Current, string? Unit);
public sealed record KpiReadingReq(Guid TenantId, double Current);
public sealed record AskReq(Guid TenantId, string Question, string? Capability);
public sealed record ShareDocumentReq(Guid TenantId, Guid PersonId, DateTimeOffset? ExpiresAt);
public sealed record UpdateDocumentReq(Guid TenantId, string? Title, string? Classification, DateTimeOffset? RetainUntil, string? Status);
public sealed record RegisterEndpointReq(Guid TenantId, string EventType, string TargetUrl, string? Secret);
public sealed record CreateFormReq(Guid TenantId, string Code, string Name, string Category, string SchemaJson);
public sealed record ValidateSubmissionReq(Guid TenantId, string DataJson);
public sealed record CreateRequestReq(Guid TenantId, string Category, string Title, Guid SubmitterId, Guid? FormId, string? DataJson);
public sealed record SubmitRequestReq(Guid TenantId, Guid? ReviewerId, string? WorkflowCode);
public sealed record CreateWorkflowReq(Guid TenantId, string Code, string Name, string NodesJson);
public sealed record CreateCommunicationReq(Guid TenantId, string Kind, string Title, string? Body, Guid AuthorId, bool RequiresAction, DateTimeOffset? DueAt, Guid[]? TargetPersonIds);
public sealed record PublishCommunicationReq(Guid TenantId);
public sealed record CreateTemplateReq(Guid TenantId, string Code, string Channel, string? Subject, string BodyTemplate);
