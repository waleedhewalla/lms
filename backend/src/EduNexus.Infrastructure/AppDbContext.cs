using EduNexus.Foundation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using System.Text.Json;

namespace EduNexus.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Campus> Campuses => Set<Campus>();
    public DbSet<OrganizationalUnit> OrganizationalUnits => Set<OrganizationalUnit>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RoleAssignment> RoleAssignments => Set<RoleAssignment>();
    public DbSet<AuthorityDelegation> AuthorityDelegations => Set<AuthorityDelegation>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();

    public async Task SetTenantAsync(Guid tenantId, CancellationToken ct = default)
    {
        // Per-session GUC consumed by RLS policies (see migration SQL).
        await Database.ExecuteSqlRawAsync(
            "SELECT set_config('app.current_tenant', {0}, false)", new[] { tenantId.ToString() }, ct);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        var permsConverter = new ValueConverter<IReadOnlyList<string>, string>(
            v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
            v => JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>());

        b.Entity<Tenant>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(63).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });
        b.Entity<Organization>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.Code).HasMaxLength(50).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        });
        b.Entity<Campus>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });
        b.Entity<OrganizationalUnit>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
        });
        b.Entity<Person>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.Email);
            e.Property(x => x.FullName).HasMaxLength(250).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        });
        b.Entity<Role>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Code }).IsUnique();
            e.Property(x => x.Permissions).HasConversion(permsConverter).HasColumnType("jsonb");
        });
        b.Entity<RoleAssignment>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.PersonId, x.RoleId }).IsUnique();
        });
        b.Entity<AuthorityDelegation>(e => e.HasKey(x => x.Id));
        b.Entity<AuditEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.At });
            e.HasIndex(x => new { x.EntityType, x.EntityId });
        });
        b.Entity<OutboxEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.DispatchedAt);
            e.HasIndex(x => new { x.TenantId, x.OccurredAt });
            e.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            e.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        });
    }
}
