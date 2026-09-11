using System.Text.RegularExpressions;

namespace EduNexus.Foundation;

public sealed record Tenant(Guid Id, string Slug, string Name, bool IsActive, DateTimeOffset CreatedAt)
{
    private static readonly Regex SlugRx = new("^[a-z0-9-]{3,63}$", RegexOptions.Compiled);

    public static Tenant Create(string slug, string name)
    {
        if (string.IsNullOrWhiteSpace(slug) || !SlugRx.IsMatch(slug))
            throw new ArgumentException("Slug must match ^[a-z0-9-]{3,63}$.", nameof(slug));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));
        return new Tenant(Guid.NewGuid(), slug.Trim().ToLowerInvariant(), name.Trim(), true, DateTimeOffset.UtcNow);
    }
}

public sealed record Organization(Guid Id, Guid TenantId, string Code, string Name, DateTimeOffset CreatedAt)
{
    public static Organization Create(Guid tenantId, string code, string name)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));
        return new Organization(Guid.NewGuid(), tenantId, code.Trim(), name.Trim(), DateTimeOffset.UtcNow);
    }
}

public sealed record Campus(Guid Id, Guid OrganizationId, Guid TenantId, string Code, string Name);

public sealed record OrganizationalUnit(Guid Id, Guid TenantId, Guid? ParentId, string Code, string Name, bool IsActive)
{
    public static OrganizationalUnit Create(Guid tenantId, string code, string name, Guid? parentId = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code required.", nameof(code));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required.", nameof(name));
        if (parentId == Guid.Empty) throw new ArgumentException("Invalid parent.", nameof(parentId));
        return new OrganizationalUnit(Guid.NewGuid(), tenantId, parentId, code.Trim(), name.Trim(), true);
    }
}

public enum PersonType { Employee, Student, Other }

public sealed record Person(Guid Id, Guid TenantId, PersonType Type, string FullName, string? Email, bool IsActive, DateTimeOffset CreatedAt)
{
    public static Person Create(Guid tenantId, PersonType type, string fullName, string? email = null)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(fullName)) throw new ArgumentException("Full name required.", nameof(fullName));
        return new Person(Guid.NewGuid(), tenantId, type, fullName.Trim(), email?.Trim(), true, DateTimeOffset.UtcNow);
    }
}

public sealed record Role(Guid Id, Guid TenantId, string Code, string Name, IReadOnlyList<string> Permissions)
{
    public static Role Create(Guid tenantId, string code, string name, IEnumerable<string> permissions)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant required.", nameof(tenantId));
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code required.", nameof(code));
        var perms = permissions?.Distinct().ToList() ?? new List<string>();
        return new Role(Guid.NewGuid(), tenantId, code.Trim(), name.Trim(), perms);
    }
}

public sealed record RoleAssignment(Guid Id, Guid TenantId, Guid PersonId, Guid RoleId, string Scope, DateTimeOffset? ExpiresAt, DateTimeOffset CreatedAt);

public sealed record AuthorityDelegation(Guid Id, Guid TenantId, Guid FromPersonId, Guid ToPersonId, string Scope, DateTimeOffset ExpiresAt);

public sealed record AuditEvent(Guid Id, Guid TenantId, string Action, string EntityType, string EntityId, Guid? ActorId, DateTimeOffset At, string? Details);

/// <summary>
/// Transactional outbox: written atomically with domain changes, relayed to
/// RabbitMQ by EventRelay. Platform-level table (no RLS) so the relay can read
/// all tenants' events; consumers isolate by the tenant_id header/claim.
/// Mutable class (not a record): the relay stamps DispatchedAt after publish.
/// </summary>
public sealed class OutboxEvent
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }

    public OutboxEvent() { }

    public OutboxEvent(Guid id, Guid tenantId, string eventType, string payload, DateTimeOffset occurredAt, DateTimeOffset? dispatchedAt)
    {
        Id = id;
        TenantId = tenantId;
        EventType = eventType;
        Payload = payload;
        OccurredAt = occurredAt;
        DispatchedAt = dispatchedAt;
    }
}
