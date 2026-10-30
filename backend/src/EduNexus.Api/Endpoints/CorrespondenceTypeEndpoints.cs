using System.Text.RegularExpressions;
using EduNexus.Api.Auth;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static EduNexus.Api.Endpoints.EndpointHelpers;

namespace EduNexus.Api.Endpoints;

/// <summary>
/// The catalogue of internal correspondence types a university runs (administrative memo, financial letter, student request, ...).
/// Each type says who may start one, what it needs, and which workflow it follows. The university owns the catalogue: it can add
/// types, rename them, switch them off and re-route them. <c>seed-defaults</c> creates the standard nine for a tenant.
/// </summary>
public static partial class CorrespondenceTypeEndpoints
{
    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,39}$")]
    private static partial Regex CodeShape();

    public const string General = "correspondence:create";
    public const string Student = "correspondence:student-request";

    public static void MapCorrespondenceTypeEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/correspondence/types").WithTags("Correspondence").RequireAuthorization();

        // Readers see every active type; anyone else sees only the types whose permission they hold (a student sees "student request").
        g.MapGet("/", async (AppDbContext db, HttpContext ctx, Guid tenantId, bool? includeInactive, CancellationToken ct) =>
        {
            if (ForbiddenIfCrossTenant(ctx, tenantId) is { } f) return f;
            var canRead = ctx.User.HasPermission("correspondence:read");
            var canManage = ctx.User.HasPermission("workflow:manage");
            await using var scope = await TenantScope.BeginAsync(db, tenantId, ct);
            var all = await db.CorrespondenceTypes.Where(t => t.TenantId == tenantId).OrderBy(t => t.SortOrder).ThenBy(t => t.Code).ToListAsync(ct);
            var visible = all.Where(t => (t.IsActive || (includeInactive == true && canManage)) && (canRead || ctx.User.HasPermission(t.CreatePermission))).ToList();
            if (visible.Count == 0 && !canRead && !all.Any(t => ctx.User.HasPermission(t.CreatePermission))) return Results.Forbid();
            return Results.Ok(visible);
        });

        g.MapPost("/", async (AppDbContext db, HttpContext ctx, TypeReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("workflow:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            var code = (req.Code ?? "").Trim().ToUpperInvariant();
            if (!CodeShape().IsMatch(code)) return Results.BadRequest(new { error = "Code must be 2 to 40 characters: capital letters, digits and _, starting with a letter." });
            if (Validate(req) is { } bad) return bad;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            if (await db.CorrespondenceTypes.AnyAsync(t => t.TenantId == req.TenantId && t.Code == code, ct)) return Results.Conflict(new { error = "A type with this code already exists." });
            var t = new CorrespondenceTypeDef(Guid.NewGuid(), req.TenantId, code, req.NameEn!.Trim(), req.NameAr!.Trim(), req.Purpose!.Trim(),
                req.SenderDescription!.Trim(), req.ReceiverDescription!.Trim(), (req.CreatePermission ?? General).Trim(),
                req.RequiresTargetUnit ?? false, req.RequiresAmount ?? false, string.IsNullOrWhiteSpace(req.WorkflowCode) ? null : req.WorkflowCode.Trim(),
                req.IsActive ?? true, req.SortOrder ?? 100);
            db.CorrespondenceTypes.Add(t);
            DomainEvents.Record(db, req.TenantId, "CorrespondenceTypeCreated", "CorrespondenceTypeCreated", nameof(CorrespondenceTypeDef), t.Id.ToString(),
                actorId: await ActingPersonAsync(db, ctx, req.TenantId, ct), details: t.Code);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Created($"/api/correspondence/types/{t.Id}", t);
        });

        // Only the fields sent are changed; the code never changes (letters refer to it).
        g.MapPatch("/{id:guid}", async (AppDbContext db, HttpContext ctx, Guid id, TypeReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("workflow:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var t = await db.CorrespondenceTypes.FirstOrDefaultAsync(x => x.TenantId == req.TenantId && x.Id == id, ct);
            if (t is null) return Results.NotFound(new { error = "Type not found." });
            var next = t with
            {
                NameEn = req.NameEn?.Trim() ?? t.NameEn, NameAr = req.NameAr?.Trim() ?? t.NameAr, Purpose = req.Purpose?.Trim() ?? t.Purpose,
                SenderDescription = req.SenderDescription?.Trim() ?? t.SenderDescription, ReceiverDescription = req.ReceiverDescription?.Trim() ?? t.ReceiverDescription,
                CreatePermission = req.CreatePermission?.Trim() ?? t.CreatePermission, RequiresTargetUnit = req.RequiresTargetUnit ?? t.RequiresTargetUnit,
                RequiresAmount = req.RequiresAmount ?? t.RequiresAmount, IsActive = req.IsActive ?? t.IsActive, SortOrder = req.SortOrder ?? t.SortOrder,
                WorkflowCode = req.WorkflowCode is null ? t.WorkflowCode : string.IsNullOrWhiteSpace(req.WorkflowCode) ? null : req.WorkflowCode.Trim(),
            };
            if (Validate(new TypeReq(req.TenantId, next.Code, next.NameEn, next.NameAr, next.Purpose, next.SenderDescription, next.ReceiverDescription, next.CreatePermission,
                    next.RequiresTargetUnit, next.RequiresAmount, next.WorkflowCode, next.IsActive, next.SortOrder)) is { } bad) return bad;
            db.Entry(t).CurrentValues.SetValues(next);
            DomainEvents.Record(db, req.TenantId, "CorrespondenceTypeUpdated", "CorrespondenceTypeUpdated", nameof(CorrespondenceTypeDef), t.Id.ToString(),
                actorId: await ActingPersonAsync(db, ctx, req.TenantId, ct), details: t.Code);
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(next);
        });

        // Creates the standard types that are missing; ones that exist (possibly edited) are left alone, so it is safe to repeat.
        g.MapPost("/seed-defaults", async (AppDbContext db, HttpContext ctx, TenantOnlyReq req, CancellationToken ct) =>
        {
            if (!ctx.User.HasPermission("workflow:manage")) return Results.Forbid();
            if (ForbiddenIfCrossTenant(ctx, req.TenantId) is { } f) return f;
            await using var scope = await TenantScope.BeginAsync(db, req.TenantId, ct);
            var have = (await db.CorrespondenceTypes.Where(t => t.TenantId == req.TenantId).Select(t => t.Code).ToListAsync(ct)).ToHashSet();
            var created = new List<string>();
            foreach (var d in Defaults.Where(d => !have.Contains(d.Code)))
            {
                db.CorrespondenceTypes.Add(d with { Id = Guid.NewGuid(), TenantId = req.TenantId });
                created.Add(d.Code);
            }
            if (created.Count > 0)
                DomainEvents.Record(db, req.TenantId, "CorrespondenceTypesSeeded", "CorrespondenceTypesSeeded", nameof(CorrespondenceTypeDef), req.TenantId.ToString(),
                    actorId: await ActingPersonAsync(db, ctx, req.TenantId, ct), details: string.Join(",", created));
            await db.SaveChangesAsync(ct);
            await scope.CommitAsync(ct);
            return Results.Ok(new { created, existing = have.OrderBy(x => x).ToList() });
        });
    }

    private static IResult? Validate(TypeReq r)
    {
        if (string.IsNullOrWhiteSpace(r.NameEn) || string.IsNullOrWhiteSpace(r.NameAr)) return Results.BadRequest(new { error = "The name is required in English and Arabic." });
        if (string.IsNullOrWhiteSpace(r.Purpose) || string.IsNullOrWhiteSpace(r.SenderDescription) || string.IsNullOrWhiteSpace(r.ReceiverDescription))
            return Results.BadRequest(new { error = "Purpose, sender and receiver descriptions are required." });
        if (r.NameEn.Trim().Length > 120 || r.NameAr.Trim().Length > 120 || r.Purpose.Trim().Length > 500 || r.SenderDescription.Trim().Length > 300 || r.ReceiverDescription.Trim().Length > 300)
            return Results.BadRequest(new { error = "A text is too long." });
        var perm = (r.CreatePermission ?? General).Trim();
        if (!PermissionCatalog.All.Contains(perm) || !perm.StartsWith("correspondence:", StringComparison.Ordinal) || perm is "correspondence:read" or "correspondence:confidential")
            return Results.BadRequest(new { error = $"CreatePermission must be {General} or {Student}." });
        if (r.WorkflowCode is { Length: > 60 }) return Results.BadRequest(new { error = "WorkflowCode is too long." });
        return null;
    }

    /// <summary>The standard catalogue. Workflows are named here and defined with them (see CorrespondenceWorkflows).</summary>
    internal static readonly CorrespondenceTypeDef[] Defaults =
    [
        T("ADMIN_MEMO", "Administrative memo", "مذكرة إدارية", "Inform or ask for an action inside the administration.", "A staff member or unit", "A unit or official", General, false, false, 10),
        T("ACADEMIC", "Academic correspondence", "مخاطبة أكاديمية", "Courses, schedules, programmes and academic affairs between colleges and departments.", "A department or college", "A college, deanship or academic unit", General, true, false, 20),
        T("FINANCIAL", "Financial correspondence", "مخاطبة مالية", "Payments, advances, budgets and other financial matters.", "Any unit", "The finance department", General, true, true, 30),
        T("INTER_UNIT", "Between departments", "مخاطبة بين الأقسام", "Coordination between two departments.", "A department", "Another department", General, true, false, 40),
        T("EMPLOYEE_REQUEST", "Employee request", "طلب موظف", "Leave, secondment, certificates and other personal requests of staff.", "A staff member", "Human resources, through the line manager", General, false, false, 50),
        T("STUDENT_REQUEST", "Student request", "طلب طالب", "Statements, grievances and section changes asked for by a student.", "A student", "Student affairs or the department", Student, false, false, 60, active: false),
        T("DECISION", "Decision", "قرار", "An official decision of an authorised official.", "An authorised official", "Everyone concerned", General, false, false, 70),
        T("CIRCULAR", "Circular", "تعميم", "A general notice to a group or to units.", "An official", "A group or several units", General, false, false, 80),
        T("INFO_REQUEST", "Information request or complaint", "طلب معلومات أو شكوى", "A request for information or a complaint to the competent unit.", "Any staff member", "The competent unit", General, false, false, 90),
    ];

    private static CorrespondenceTypeDef T(string code, string en, string ar, string purpose, string sender, string receiver, string permission,
        bool targetUnit, bool amount, int order, bool active = true) =>
        new(Guid.Empty, Guid.Empty, code, en, ar, purpose, sender, receiver, permission, targetUnit, amount, $"CORR_{code}", active, order);
}

public sealed record TypeReq(Guid TenantId, string? Code, string? NameEn, string? NameAr, string? Purpose, string? SenderDescription, string? ReceiverDescription,
    string? CreatePermission, bool? RequiresTargetUnit, bool? RequiresAmount, string? WorkflowCode, bool? IsActive, int? SortOrder);
