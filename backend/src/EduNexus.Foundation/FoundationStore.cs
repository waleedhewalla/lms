using System.Collections.Concurrent;

namespace EduNexus.Foundation;

/// <summary>
/// R1 in-memory store. Replace with EF Core + PostgreSQL RLS in R1-hardening.
/// Every collection is tenant-scoped by convention (see docs/06-data).
/// </summary>
public sealed class FoundationStore
{
    private readonly ConcurrentDictionary<string, Tenant> _tenantsBySlug = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, Tenant> _tenantsById = new();
    private readonly ConcurrentDictionary<Guid, Organization> _orgs = new();
    private readonly ConcurrentDictionary<Guid, Person> _people = new();
    private readonly ConcurrentDictionary<Guid, AuditEvent> _audit = new();

    // SoD: pairs of role codes that must never co-exist for one person (seed example).
    private static readonly HashSet<(string, string)> SodPairs = new()
    {
        ("finance.requester", "finance.approver"),
        ("hr.requester", "hr.approver"),
    };

    public IEnumerable<Tenant> Tenants => _tenantsById.Values;
    public IEnumerable<AuditEvent> AuditEvents => _audit.Values.OrderByDescending(a => a.At);

    public Tenant AddTenant(string slug, string name, Guid? actorId = null)
    {
        var t = Tenant.Create(slug, name);
        if (!_tenantsBySlug.TryAdd(t.Slug, t))
            throw new InvalidOperationException($"Tenant slug '{slug}' already exists.");
        _tenantsById[t.Id] = t;
        AddAudit(t.Id, "TenantCreated", nameof(Tenant), t.Id.ToString(), actorId, $"slug={t.Slug}");
        return t;
    }

    public Organization AddOrganization(Guid tenantId, string code, string name, Guid? actorId = null)
    {
        RequireTenant(tenantId);
        if (_orgs.Values.Any(o => o.TenantId == tenantId && o.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Organization code '{code}' already exists in tenant.");
        var o = Organization.Create(tenantId, code, name);
        _orgs[o.Id] = o;
        AddAudit(tenantId, "OrganizationCreated", nameof(Organization), o.Id.ToString(), actorId, code);
        return o;
    }

    public IEnumerable<Organization> ListOrganizations(Guid tenantId)
        => _orgs.Values.Where(o => o.TenantId == tenantId);

    public Person AddPerson(Guid tenantId, PersonType type, string fullName, string? email, Guid? actorId = null)
    {
        RequireTenant(tenantId);
        var p = Person.Create(tenantId, type, fullName, email);
        _people[p.Id] = p;
        AddAudit(tenantId, "PersonCreated", nameof(Person), p.Id.ToString(), actorId, fullName);
        return p;
    }

    public IEnumerable<Person> SearchPeople(Guid tenantId, string? q = null)
    {
        var all = _people.Values.Where(p => p.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(q))
            all = all.Where(p => p.FullName.Contains(q, StringComparison.OrdinalIgnoreCase)
                || (p.Email != null && p.Email.Contains(q, StringComparison.OrdinalIgnoreCase)));
        return all.OrderBy(p => p.FullName);
    }

    public IEnumerable<AuditEvent> ListAudit(Guid tenantId)
        => AuditEvents.Where(a => a.TenantId == tenantId);

    public static void CheckSod(IEnumerable<string> existingRoleCodes, string newRoleCode)
    {
        foreach (var e in existingRoleCodes)
            if (SodPairs.Contains((e, newRoleCode)) || SodPairs.Contains((newRoleCode, e)))
                throw new InvalidOperationException($"SoD violation: '{e}' conflicts with '{newRoleCode}'.");
    }

    private void RequireTenant(Guid tenantId)
    {
        if (!_tenantsById.ContainsKey(tenantId))
            throw new KeyNotFoundException("Tenant not found.");
    }

    private void AddAudit(Guid tenantId, string action, string entityType, string entityId, Guid? actorId, string? details)
        => _audit[Guid.NewGuid()] = new AuditEvent(Guid.NewGuid(), tenantId, action, entityType, entityId, actorId, DateTimeOffset.UtcNow, details);
}
