using System.Text;
using EduNexus.Api.Auth;
using EduNexus.Api.Events;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// R1 Foundation: OpenAPI + EF Core/Postgres (RLS) for all foundation entities.
builder.Services.AddOpenApi();
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
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        if (!string.IsNullOrWhiteSpace(authCfg.Authority))
        {
            o.Authority = authCfg.Authority;
            o.Audience = authCfg.Audience;
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
app.MapHealthChecks("/health");

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

app.Run();

public sealed record CreateTenantReq(string Slug, string Name);
public sealed record CreateOrgReq(Guid TenantId, string Code, string Name);
public sealed record CreatePersonReq(Guid TenantId, string Type, string FullName, string? Email);
public sealed record DevTokenReq(string Subject, Guid TenantId, string[]? Permissions);
public sealed record CreateRoleReq(Guid TenantId, string Code, string Name, string[]? Permissions);
public sealed record AssignRoleReq(Guid TenantId, Guid PersonId, string RoleCode, string? Scope, DateTimeOffset? ExpiresAt);
public sealed record RevokeRoleReq(Guid TenantId, Guid PersonId, string RoleCode);
