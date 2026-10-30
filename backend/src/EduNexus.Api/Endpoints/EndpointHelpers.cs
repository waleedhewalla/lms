using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using EduNexus.Api.Auth;
using EduNexus.Api.Storage;
using EduNexus.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace EduNexus.Api.Endpoints;

public static class EndpointHelpers
{
    /// <summary>
    /// Client-supplied object-storage keys must live under the tenant's own prefix ("{tenantId}/…") and
    /// contain no path traversal, so a stored key can never address another tenant's file.
    /// </summary>
    public static IResult? BadRequestIfForeignObjectKey(Guid tenantId, string? objectKey) =>
        objectKey is null || !objectKey.StartsWith($"{tenantId}/", StringComparison.OrdinalIgnoreCase)
            || objectKey.Contains("..") || objectKey.Contains('\\')
            ? Results.BadRequest(new { error = "objectKey must start with the tenant id prefix ({tenantId}/)." })
            : null;

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

    public static IResult StorageUnavailable() =>
        Results.Json(new { error = "File storage is unavailable. Try again shortly." }, statusCode: StatusCodes.Status503ServiceUnavailable);

    /// <summary>
    /// A client says it uploaded a file: look in storage before recording it. Null means it is there (and, when a size was
    /// declared, matches); otherwise the response to return: 422 when it is missing or the wrong size, 503 when storage
    /// cannot say. Call this before opening a database transaction, since it waits on the network.
    /// </summary>
    public static async Task<IResult?> RequireStoredObjectAsync(IObjectStorage storage, ILoggerFactory logs, string objectKey, long? declaredSize, CancellationToken ct)
    {
        try
        {
            var found = await storage.StatAsync(objectKey, ct);
            if (found is null)
                return Results.Json(new { error = "The file is not in storage. Upload it with the upload URL first." }, statusCode: StatusCodes.Status422UnprocessableEntity);
            if (declaredSize is { } want && found.Size is { } got && got != want)
                return Results.Json(new { error = $"The stored file is {got} bytes but {want} were declared." }, statusCode: StatusCodes.Status422UnprocessableEntity);
            return null;
        }
        catch (StorageUnavailableException ex)
        {
            logs.CreateLogger("Storage").LogError(ex, "Object storage could not verify {ObjectKey}", objectKey);
            return StorageUnavailable();
        }
    }

    /// <summary>The leaf of a client-supplied file name, safe inside an object key; null if nothing usable is left.</summary>
    public static string? SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var leaf = name.Replace('\\', '/').Split('/')[^1];
        var cleaned = new string(leaf.Where(c => !char.IsControl(c)).Select(c => "\"<>|?*#%:".Contains(c) ? '_' : c).ToArray())
            .Replace("..", "_").Trim().Trim('.');
        if (cleaned.Length > 120) cleaned = cleaned[^120..];   // keep the end: that is where the extension is
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>The caller's subject (token "sub"), used to record who did something and for segregation of duties.</summary>
    public static string SubjectOf(HttpContext ctx) =>
        ctx.User.FindFirst("sub")?.Value ?? ctx.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "unknown";

    public static bool IsUniqueConflict(DbUpdateException ex)
    {
        return ex.InnerException is PostgresException p && p.SqlState == PostgresErrorCodes.UniqueViolation;
    }
}
