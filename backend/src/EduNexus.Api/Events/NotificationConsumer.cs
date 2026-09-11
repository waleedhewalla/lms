using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace EduNexus.Api.Events;

/// <summary>
/// Consumes domain events from `edunexus.events` and materializes in-app
/// notifications: queue `edunexus.notifications` bound to the routing keys below.
/// At-least-once: DB write first, ack after commit.
/// </summary>
public sealed class NotificationConsumer(
    IServiceScopeFactory scopes,
    IOptions<RabbitMqOptions> options,
    ILogger<NotificationConsumer> log) : BackgroundService
{
    private const string Queue = "edunexus.notifications";
    private static readonly string[] Keys =
        ["RoleAssigned", "RoleRevoked", "CorrespondenceSubmitted", "ApprovalDecided", "TaskBreached"];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var cfg = options.Value;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = cfg.Host, Port = cfg.Port,
                    UserName = cfg.Username, Password = cfg.Password,
                    AutomaticRecoveryEnabled = true,
                };
                await using var connection = await factory.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await channel.ExchangeDeclareAsync(cfg.Exchange, ExchangeType.Topic, durable: true, cancellationToken: stoppingToken);
                await channel.QueueDeclareAsync(Queue, durable: true, exclusive: false, autoDelete: false, cancellationToken: stoppingToken);
                foreach (var k in Keys)
                    await channel.QueueBindAsync(Queue, cfg.Exchange, k, cancellationToken: stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var msg = await channel.BasicGetAsync(Queue, autoAck: false, stoppingToken);
                    if (msg is null) { await Task.Delay(1000, stoppingToken); continue; }
                    try
                    {
                        await HandleAsync(msg, stoppingToken);
                        await channel.BasicAckAsync(msg.DeliveryTag, false, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Notification handling failed; requeueing");
                        await channel.BasicNackAsync(msg.DeliveryTag, false, true, stoppingToken);
                        await Task.Delay(2000, stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Notification consumer connection lost; reconnecting");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task HandleAsync(BasicGetResult msg, CancellationToken ct)
    {
        var tenantId = msg.BasicProperties.Headers is { } h && h.TryGetValue("tenant_id", out var v)
            ? Guid.Parse(Encoding.UTF8.GetString((byte[])v))
            : Guid.Empty;
        if (tenantId == Guid.Empty) return;
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(msg.Body.ToArray()));
        var root = doc.RootElement;
        var (personId, title, body) = msg.RoutingKey switch
        {
            "RoleAssigned" => (GetGuid(root, "personId"),
                $"Role assigned: {Get(root, "roleCode")}",
                $"You were assigned role {Get(root, "roleCode")} (scope {Get(root, "scope")})."),
            "RoleRevoked" => (GetGuid(root, "personId"),
                $"Role revoked: {Get(root, "roleCode")}",
                $"Your role {Get(root, "roleCode")} was revoked."),
            "CorrespondenceSubmitted" => (GetGuid(root, "reviewerId"),
                $"Review requested: {Get(root, "number")}",
                $"Correspondence {Get(root, "number")} awaits your review."),
            "ApprovalDecided" => (GetGuid(root, "decidedBy"),
                $"Approval {(root.TryGetProperty("approved", out var ap) && ap.GetBoolean() ? "approved" : "rejected")}",
                $"Approval {Get(root, "approvalId")} decided."),
            "TaskBreached" => (GetGuid(root, "assigneeId"),
                $"Task overdue: {Get(root, "title")}",
                $"Task {Get(root, "title")} breached its SLA."),
            _ => (Guid.Empty, "", ""),
        };
        if (personId == Guid.Empty || title == "") return;
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await TenantScope.BeginAsync(db, tenantId, ct);
        db.Notifications.Add(new Notification(Guid.NewGuid(), tenantId, personId, title, body,
            NotificationChannel.InApp, NotificationStatus.Sent, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static string Get(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ToString() : "?";

    private static Guid GetGuid(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && Guid.TryParse(v.GetString(), out var g) ? g : Guid.Empty;
}
