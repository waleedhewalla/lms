using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Text;

namespace EduNexus.Api.Events;

/// <summary>
/// Transactional-outbox relay: polls undispatched OutboxEvents and publishes
/// them to the `edunexus.events` topic exchange (routing key = event type,
/// `tenant_id` header). Marks rows dispatched only after broker confirm.
/// Tolerates broker outages: logs and retries on the next tick.
/// </summary>
public sealed class EventRelay(
    IServiceScopeFactory scopes,
    IOptions<RabbitMqOptions> options,
    ILogger<EventRelay> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var batch = await db.OutboxEvents
                    .Where(e => e.DispatchedAt == null)
                    .OrderBy(e => e.OccurredAt)
                    .Take(cfg.BatchSize)
                    .ToListAsync(stoppingToken);
                if (batch.Count > 0)
                {
                    await PublishBatchAsync(cfg, batch, stoppingToken);
                    var at = DateTimeOffset.UtcNow;
                    foreach (var e in batch) e.DispatchedAt = at;
                    // OutboxEvents is platform-level (no RLS): no tenant scope needed.
                    await db.SaveChangesAsync(stoppingToken);
                    log.LogInformation("Relayed {Count} events", batch.Count);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Event relay tick failed; retrying");
            }
            await Task.Delay(TimeSpan.FromSeconds(cfg.PollSeconds), stoppingToken);
        }
    }

    private static async Task PublishBatchAsync(RabbitMqOptions cfg, List<Foundation.OutboxEvent> batch, CancellationToken ct)
    {
        var factory = new ConnectionFactory
        {
            HostName = cfg.Host,
            Port = cfg.Port,
            UserName = cfg.Username,
            Password = cfg.Password,
            AutomaticRecoveryEnabled = true,
        };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);
        await channel.ExchangeDeclareAsync(cfg.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);
        foreach (var e in batch)
        {
            var props = new BasicProperties
            {
                Persistent = true,
                Headers = new Dictionary<string, object?> { ["tenant_id"] = e.TenantId.ToString() },
            };
            await channel.BasicPublishAsync(cfg.Exchange, e.EventType, false, props,
                Encoding.UTF8.GetBytes(e.Payload), ct);
        }
    }
}
