using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EduNexus.Api.Auth;
using EduNexus.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace EduNexus.Api.Endpoints;

public static class EndpointHelpers
{
    public static IResult? ForbiddenIfCrossTenant(HttpContext ctx, Guid targetTenantId)
    {
        var claim = ctx.User.FindFirstValue("tenant_id");
        if (string.IsNullOrWhiteSpace(claim)) return null;
        return Guid.TryParse(claim, out var tId) && tId == targetTenantId
            ? null
            : Results.Forbid();
    }

    /// <summary>
    /// The person the caller acts as, resolved server-side from the token: the <c>person_id</c> claim,
    /// else the person in this tenant whose email matches the <c>email</c> claim. Never trust a person
    /// id from the request body for ownership checks. Call inside a TenantScope.
    /// </summary>
    public static async Task<Guid?> ActingPersonAsync(AppDbContext db, HttpContext ctx, Guid tenantId, CancellationToken ct)
    {
        if (ctx.User.PersonIdClaim() is { } pid)
            return await db.People.AnyAsync(p => p.TenantId == tenantId && p.Id == pid, ct) ? pid : null;
        var email = ctx.User.FindFirstValue(ClaimTypes.Email) ?? ctx.User.FindFirstValue("email");
        if (string.IsNullOrWhiteSpace(email)) return null;
        var normalized = email.Trim().ToLower();
        var matches = await db.People.Where(p => p.TenantId == tenantId && p.Email != null && p.Email.ToLower() == normalized)
            .Select(p => p.Id).Take(2).ToListAsync(ct);
        return matches.Count == 1 ? matches[0] : null;
    }

    public static bool IsUniqueConflict(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException p && p.SqlState == PostgresErrorCodes.UniqueViolation;
    }
}
