using System;
using System.Collections.Generic;
using System.IO;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static EduNexus.Api.Endpoints.EndpointHelpers;

namespace EduNexus.Api.Endpoints;

public static class FoundationEndpoints
{
    public static void MapFoundationEndpoints(this IEndpointRouteBuilder app)
    {
        // --- Dev token (Development only) ---
        var auth = app.MapGroup("/api/auth").WithTags("Auth");
        auth.MapPost("/dev-token", (DevTokenService tokens, DevTokenReq req, IHostEnvironment env, IServiceProvider services) =>
        {
            var opts = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuthOptions>>().Value;
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

        // --- Organizational Units & Academic Leadership ---
        var units = app.MapGroup("/api/organizational-units").WithTags("OrganizationalUnits").RequireAuthorization();
        units.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("org:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            return Results.Ok(await db.OrganizationalUnits.Where(u => u.TenantId == tenantId).OrderBy(u => u.Code).ToListAsync(ct));
        });
        units.MapPost("/", async (AppDbContext db, HttpContext ctx, CreateUnitReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("org:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            OrganizationalUnit unit;
            try { unit = OrganizationalUnit.Create(req.TenantId, req.Code, req.Name, req.ParentId, req.LeaderPersonId, req.DeputyPersonId); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            db.OrganizationalUnits.Add(unit);
            DomainEvents.Record(db, req.TenantId, "OrganizationalUnitCreated", "OrganizationalUnitCreated",
                nameof(OrganizationalUnit), unit.Id.ToString(), details: req.Code);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException ex) when (IsUniqueConflict(ex))
            {
                return Results.Conflict(new { error = $"Unit code '{req.Code}' already exists in tenant." });
            }
            await scope.CommitAsync(ct);
            return Results.Created($"/api/organizational-units/{unit.Id}", unit);
        });

        // --- Directory / people ---
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
            try { person = Person.Create(req.TenantId, t, req.FullName, req.Email, req.DepartmentId, req.AcademicRank); }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            db.People.Add(person);
            DomainEvents.Record(db, req.TenantId, "PersonCreated", "PersonCreated",
                nameof(Person), person.Id.ToString(), details: req.FullName);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created("/api/people", person);
        });
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
        people.MapGet("/{id:guid}/delegations", async (AppDbContext db, HttpContext ctx, Guid id, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("person:read")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var list = await db.AuthorityDelegations
                .Where(d => d.TenantId == tenantId && (d.FromPersonId == id || d.ToPersonId == id))
                .OrderByDescending(d => d.ExpiresAt)
                .ToListAsync(ct);
            return Results.Ok(list);
        });
        people.MapPost("/{id:guid}/delegations", async (AppDbContext db, HttpContext ctx, Guid id, CreateAuthorityDelegationReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("person:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            if (req.FromPersonId != id) return Results.BadRequest(new { error = "FromPersonId must match person ID in URL." });
            if (req.FromPersonId == req.ToPersonId) return Results.BadRequest(new { error = "Cannot delegate authority to self." });
            if (req.ExpiresAt <= DateTimeOffset.UtcNow) return Results.BadRequest(new { error = "ExpiresAt must be in the future." });

            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.FromPersonId, ct))
                return Results.NotFound(new { error = "Delegator person not found." });
            if (!await db.People.AnyAsync(p => p.TenantId == req.TenantId && p.Id == req.ToPersonId, ct))
                return Results.NotFound(new { error = "Deputy person not found." });

            var delegation = new AuthorityDelegation(Guid.NewGuid(), req.TenantId, req.FromPersonId, req.ToPersonId, string.IsNullOrWhiteSpace(req.Scope) ? "All" : req.Scope.Trim(), req.ExpiresAt);
            db.AuthorityDelegations.Add(delegation);
            DomainEvents.Record(db, req.TenantId, "AuthorityDelegated", "AuthorityDelegated",
                nameof(AuthorityDelegation), delegation.Id.ToString(),
                payload: new { tenantId = req.TenantId, from = req.FromPersonId, to = req.ToPersonId, scope = delegation.Scope, expiresAt = delegation.ExpiresAt },
                details: $"{delegation.FromPersonId} -> {delegation.ToPersonId} ({delegation.Scope})");
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/people/{id}/delegations/{delegation.Id}", delegation);
        });
        people.MapDelete("/{id:guid}/delegations/{delegationId:guid}", async (AppDbContext db, HttpContext ctx, Guid id, Guid delegationId, Guid tenantId, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("person:create")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var d = await db.AuthorityDelegations.FirstOrDefaultAsync(x => x.TenantId == tenantId && x.Id == delegationId && x.FromPersonId == id, ct);
            if (d is null) return Results.NotFound(new { error = "Delegation not found." });
            db.AuthorityDelegations.Remove(d);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.NoContent();
        });

        // --- Audit trail ---
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

        // --- Roles & assignments ---
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
    }
}
