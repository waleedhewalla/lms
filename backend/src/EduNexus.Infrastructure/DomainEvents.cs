using EduNexus.Foundation;
using System.Text.Json;

namespace EduNexus.Infrastructure;

/// <summary>
/// Writes audit + outbox rows in the caller's transaction: consumers (RabbitMQ
/// relay, projectors) only ever see committed domain changes.
/// </summary>
public static class DomainEvents
{
    public static void Record(
        AppDbContext db,
        Guid tenantId,
        string eventType,
        string action,
        string entityType,
        string entityId,
        object? payload = null,
        Guid? actorId = null,
        string? details = null)
    {
        var at = DateTimeOffset.UtcNow;
        db.AuditEvents.Add(new AuditEvent(Guid.NewGuid(), tenantId, action, entityType, entityId, actorId, at, details));
        db.OutboxEvents.Add(new OutboxEvent(
            Guid.NewGuid(),
            tenantId,
            eventType,
            JsonSerializer.Serialize(payload ?? new { action, entityType, entityId, tenantId, at }),
            at,
            null));
    }
}
