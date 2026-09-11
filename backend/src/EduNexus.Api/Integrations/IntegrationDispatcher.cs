using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;

namespace EduNexus.Api.Integrations;

/// <summary>
/// Outbound webhook fan-out: own queue `edunexus.integrations` bound to all keys.
/// For each message, POSTs to the tenant's active endpoints for that event type
/// (HMAC-signed) and records an IntegrationDelivery per attempt.
/// </summary>
public sealed class IntegrationDispatcher(
    IServiceScopeFactory scopes,
    IOptions<Api.Events.RabbitMqOptions> options,
    IHttpClientFactory httpFactory,
    ILogger<IntegrationDispatcher> log) : BackgroundService
{
    private const string Queue = "edunexus.integrations";

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
                await channel.QueueBindAsync(Queue, cfg.Exchange, "#", cancellationToken: stoppingToken);

                while (!stoppingToken.IsCancellationRequested)
                {
                    var msg = await channel.BasicGetAsync(Queue, autoAck: false, stoppingToken);
                    if (msg is null) { await Task.Delay(1000, stoppingToken); continue; }
                    try
                    {
                        await DispatchAsync(msg, stoppingToken);
                        await channel.BasicAckAsync(msg.DeliveryTag, false, stoppingToken);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Integration dispatch failed; requeueing");
                        await channel.BasicNackAsync(msg.DeliveryTag, false, true, stoppingToken);
                        await Task.Delay(2000, stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogWarning(ex, "Integration dispatcher connection lost; reconnecting");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task DispatchAsync(BasicGetResult msg, CancellationToken ct)
    {
        var tenantId = msg.BasicProperties.Headers is { } h && h.TryGetValue("tenant_id", out var v)
            ? Guid.Parse(Encoding.UTF8.GetString((byte[])v)) : Guid.Empty;
        if (tenantId == Guid.Empty) return;
        var body = Encoding.UTF8.GetString(msg.Body.ToArray());
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var http = httpFactory.CreateClient("integrations");
        await using var tx = await TenantScope.BeginAsync(db, tenantId, ct);
        var endpoints = await db.IntegrationEndpoints
            .Where(e => e.TenantId == tenantId && e.EventType == msg.RoutingKey && e.IsActive)
            .ToListAsync(ct);
        foreach (var ep in endpoints)
        {
            var at = DateTimeOffset.UtcNow;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, ep.TargetUrl)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                req.Headers.Add("X-EduNexus-Event", msg.RoutingKey);
                req.Headers.Add("X-EduNexus-Signature", WebhookSigner.Sign(ep.Secret, body));
                using var res = await http.SendAsync(req, ct);
                var ok = res.IsSuccessStatusCode;
                db.IntegrationDeliveries.Add(new IntegrationDelivery(Guid.NewGuid(), tenantId, ep.Id,
                    msg.RoutingKey, body, ok ? IntegrationDeliveryStatus.Delivered : IntegrationDeliveryStatus.Failed,
                    1, ok ? null : $"HTTP {(int)res.StatusCode}", at));
            }
            catch (Exception ex)
            {
                db.IntegrationDeliveries.Add(new IntegrationDelivery(Guid.NewGuid(), tenantId, ep.Id,
                    msg.RoutingKey, body, IntegrationDeliveryStatus.Failed, 1, ex.Message[..Math.Min(300, ex.Message.Length)], at));
            }
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
