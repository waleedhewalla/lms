using System;
using System.Security.Claims;
using EduNexus.Api.Auth;
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

    public static bool IsUniqueConflict(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException p && p.SqlState == PostgresErrorCodes.UniqueViolation;
    }
}
