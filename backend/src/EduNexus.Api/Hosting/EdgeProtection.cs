using System.Net;
using System.Security.Claims;
using EduNexus.Api.Integrations;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace EduNexus.Api.Hosting;

/// <summary>
/// Edge protections that do not depend on the ingress: trusted-proxy handling for X-Forwarded-*,
/// per-caller rate limiting (counted in Redis when configured, so replicas share one budget), and an internal-only
/// /metrics endpoint.
/// </summary>
public static class EdgeProtection
{
    /// <summary>Default trusted proxy ranges: loopback and private networks (ingress / sidecars live here).</summary>
    private static readonly string[] DefaultTrustedNetworks =
        ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"];

    /// <summary>Expensive endpoints get their own, tighter budget.</summary>
    private static readonly string[] ExpensivePrefixes =
        ["/api/ai", "/api/search", "/api/auth/dev-token", "/api/intelligence"];

    /// <summary>Limits per caller per minute: the general budget, and the tighter one for expensive endpoints.</summary>
    public sealed record RateLimitSettings(bool Enabled, int PerMinute, int ExpensivePerMinute);

    public static IServiceCollection AddEdgeProtection(this IServiceCollection services, IConfiguration config)
    {
        // X-Forwarded-For/Proto are honoured only when the direct peer is a trusted proxy, so a client
        // talking to the API directly cannot spoof its IP (which would defeat per-IP limits and /metrics).
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownNetworks.Clear();
            o.KnownProxies.Clear();
            var networks = config.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() is { Length: > 0 } configured
                ? configured : DefaultTrustedNetworks;
            foreach (var cidr in networks)
            {
                var parts = cidr.Split('/');
                o.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse(parts[0]), int.Parse(parts[1])));
            }
        });

        services.AddSingleton(new RateLimitSettings(
            config.GetValue("RateLimiting:Enabled", true),
            config.GetValue("RateLimiting:PermitsPerMinute", 600),
            config.GetValue("RateLimiting:ExpensivePermitsPerMinute", 60)));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<LocalRateLimitStore>();

        // Redis is optional: without a connection string every instance counts for itself (the limit is then per
        // instance), exactly as before. With one, replicas share the count, and Redis trouble falls back to local.
        var redisConnection = config["RateLimiting:Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(redisConnection))
            services.AddSingleton<IRateLimitStore>(sp => sp.GetRequiredService<LocalRateLimitStore>());
        else
        {
            services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(RedisOptions(redisConnection, sp.GetService<Tls.PrivateCa>())));
            services.AddSingleton<IRateLimitStore>(sp => new ResilientRateLimitStore(
                new RedisRateLimitStore(sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<TimeProvider>()),
                sp.GetRequiredService<LocalRateLimitStore>(), sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ResilientRateLimitStore>>()));
        }
        return services;
    }

    /// <summary>
    /// Redis options for the limiter: never block startup or a request on Redis. Timeouts are short unless the connection
    /// string sets them, and the client keeps retrying the connection in the background.
    /// </summary>
    internal static ConfigurationOptions RedisOptions(string connectionString, Tls.PrivateCa? ca = null)
    {
        var o = ConfigurationOptions.Parse(connectionString);
        // "ssl=true" in the connection string turns TLS on; the site CA (if configured) is what the server must chain to.
        if (o.Ssl && ca is not null)
            o.CertificateValidation += (_, cert, chain, errors) => ca.Validate(cert, chain, errors, null);
        o.AbortOnConnectFail = false;
        if (!connectionString.Contains("connectTimeout", StringComparison.OrdinalIgnoreCase)) o.ConnectTimeout = 500;
        if (!connectionString.Contains("syncTimeout", StringComparison.OrdinalIgnoreCase)) o.SyncTimeout = 150;
        if (!connectionString.Contains("asyncTimeout", StringComparison.OrdinalIgnoreCase)) o.AsyncTimeout = 150;
        return o;
    }

    /// <summary>Call after UseForwardedHeaders and UseAuthentication so the caller key is the real user/IP.</summary>
    public static WebApplication UseEdgeProtection(this WebApplication app)
    {
        var allowExternalMetrics = app.Configuration.GetValue("Metrics:AllowExternal", false);
        app.Use(async (ctx, next) =>
        {
            // /metrics exposes internals: only callers on an internal network (Prometheus in-cluster) may read it.
            if (!allowExternalMetrics && ctx.Request.Path.StartsWithSegments("/metrics") && !IsInternal(ctx.Connection.RemoteIpAddress))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });
        var settings = app.Services.GetRequiredService<RateLimitSettings>();
        if (settings.Enabled)
        {
            app.Services.GetRequiredService<IRateLimitStore>();   // connect to Redis now, not on the first request
            app.Use(async (ctx, next) =>
            {
                if (IsExempt(ctx)) { await next(); return; }
                var store = ctx.RequestServices.GetRequiredService<IRateLimitStore>();
                var key = CallerKey(ctx);
                // Every request counts against the general budget; expensive ones against their own as well.
                var decision = await store.HitAsync("g:" + key, settings.PerMinute, ctx.RequestAborted);
                if (decision.Allowed && IsExpensive(ctx))
                    decision = await store.HitAsync("x:" + key, settings.ExpensivePerMinute, ctx.RequestAborted);
                if (!decision.Allowed)
                {
                    ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    ctx.Response.Headers.RetryAfter = decision.RetryAfterSeconds.ToString();
                    return;
                }
                await next();
            });
        }
        return app;
    }

    /// <summary>In-process (no socket, e.g. tests) or loopback/private/link-local peers count as internal.</summary>
    public static bool IsInternal(IPAddress? ip) => ip is null || OutboundUrlGuard.IsBlocked(ip);

    private static bool IsExempt(HttpContext ctx) =>
        ctx.Request.Path.StartsWithSegments("/health") || ctx.Request.Path.StartsWithSegments("/metrics");

    private static bool IsExpensive(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        return ExpensivePrefixes.Any(p => path.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            || (path.StartsWith("/api/reports/", StringComparison.OrdinalIgnoreCase) && path.EndsWith("/export", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Authenticated callers are limited per user and tenant; anonymous callers per client IP.</summary>
    private static string CallerKey(HttpContext ctx)
    {
        var sub = ctx.User.FindFirstValue("sub") ?? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        return sub is not null
            ? $"u:{ctx.User.FindFirstValue("tenant_id")}:{sub}"
            : $"ip:{ctx.Connection.RemoteIpAddress}";
    }
}
