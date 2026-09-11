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
        ["RoleAssigned", "RoleRevoked", "CorrespondenceSubmitted", "ApprovalDecided", "TaskBreached",
         "MeetingScheduled", "DecisionPublished", "ActionAssigned", "PolicyPublished"];

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
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var tx = await TenantScope.BeginAsync(db, tenantId, ct);
        var now = DateTimeOffset.UtcNow;
        void Notify(Guid personId, string title, string body) =>
            db.Notifications.Add(new Notification(Guid.NewGuid(), tenantId, personId, title, body,
                NotificationChannel.InApp, NotificationStatus.Sent, now));

        switch (msg.RoutingKey)
        {
            case "RoleAssigned":
                NotifyReq(root, "personId", $"Role assigned: {Get(root, "roleCode")}",
                    $"You were assigned role {Get(root, "roleCode")} (scope {Get(root, "scope")}).", Notify);
                break;
            case "RoleRevoked":
                NotifyReq(root, "personId", $"Role revoked: {Get(root, "roleCode")}",
                    $"Your role {Get(root, "roleCode")} was revoked.", Notify);
                break;
            case "CorrespondenceSubmitted":
                NotifyReq(root, "reviewerId", $"Review requested: {Get(root, "number")}",
                    $"Correspondence {Get(root, "number")} awaits your review.", Notify);
                break;
            case "ApprovalDecided":
                NotifyReq(root, "decidedBy",
                    $"Approval {(root.TryGetProperty("approved", out var ap) && ap.GetBoolean() ? "approved" : "rejected")}",
                    $"Approval {Get(root, "approvalId")} decided.", Notify);
                break;
            case "TaskBreached":
                NotifyReq(root, "assigneeId", $"Task overdue: {Get(root, "title")}",
                    $"Task {Get(root, "title")} breached its SLA.", Notify);
                break;
            case "MeetingScheduled":
                foreach (var pid in await MemberIdsAsync(db, tenantId, GetGuid(root, "committeeId"), ct))
                    Notify(pid, $"Meeting scheduled: {Get(root, "title")}", $"Meeting {Get(root, "title")} scheduled.");
                break;
            case "DecisionPublished":
                foreach (var pid in await MemberIdsAsync(db, tenantId, GetGuid(root, "committeeId"), ct))
                    Notify(pid, "Decision published", Get(root, "text"));
                break;
            case "ActionAssigned":
                NotifyReq(root, "assigneeId", "Action assigned", Get(root, "description"), Notify);
                break;
            case "PolicyPublished":
                var people = await db.People.Where(p => p.TenantId == tenantId && p.IsActive).Select(p => p.Id).ToListAsync(ct);
                foreach (var pid in people)
                    Notify(pid, $"New policy: {Get(root, "code")}", $"Policy {Get(root, "title")} requires acknowledgement.");
                break;
            default:
                return;
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private static void NotifyReq(JsonElement root, string field, string title, string body, Action<Guid, string, string> notify)
    {
        var pid = GetGuid(root, field);
        if (pid != Guid.Empty && title != "") notify(pid, title, body);
    }

    private static async Task<List<Guid>> MemberIdsAsync(AppDbContext db, Guid tenantId, Guid committeeId, CancellationToken ct) =>
        committeeId == Guid.Empty
            ? []
            : await db.CommitteeMembers.Where(m => m.TenantId == tenantId && m.CommitteeId == committeeId)
                .Select(m => m.PersonId).ToListAsync(ct);

    private static string Get(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ToString() : "?";

    private static Guid GetGuid(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && Guid.TryParse(v.GetString(), out var g) ? g : Guid.Empty;
}
