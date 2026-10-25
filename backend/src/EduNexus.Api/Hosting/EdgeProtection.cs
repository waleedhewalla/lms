using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using EduNexus.Api.Integrations;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

namespace EduNexus.Api.Hosting;

/// <summary>
/// Edge protections that do not depend on the ingress: trusted-proxy handling for X-Forwarded-*,
/// per-caller rate limiting, and an internal-only /metrics endpoint.
/// </summary>
public static class EdgeProtection
{
    /// <summary>Default trusted proxy ranges: loopback and private networks (ingress / sidecars live here).</summary>
    private static readonly string[] DefaultTrustedNetworks =
        ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "fc00::/7"];

    /// <summary>Expensive endpoints get their own, tighter budget.</summary>
    private static readonly string[] ExpensivePrefixes =
        ["/api/ai", "/api/search", "/api/auth/dev-token", "/api/intelligence"];

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

        var enabled = config.GetValue("RateLimiting:Enabled", true);
        var perMinute = config.GetValue("RateLimiting:PermitsPerMinute", 600);
        var expensivePerMinute = config.GetValue("RateLimiting:ExpensivePermitsPerMinute", 60);
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = (ctx, _) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString();
                return ValueTask.CompletedTask;
            };
            if (!enabled) return;
            o.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsExempt(ctx)
                    ? RateLimitPartition.GetNoLimiter("exempt")
                    : RateLimitPartition.GetFixedWindowLimiter(CallerKey(ctx), _ => Window(perMinute))),
                PartitionedRateLimiter.Create<HttpContext, string>(ctx => IsExpensive(ctx)
                    ? RateLimitPartition.GetFixedWindowLimiter("x:" + CallerKey(ctx), _ => Window(expensivePerMinute))
                    : RateLimitPartition.GetNoLimiter("cheap")));
        });
        return services;
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
        app.UseRateLimiter();
        return app;
    }

    /// <summary>In-process (no socket, e.g. tests) or loopback/private/link-local peers count as internal.</summary>
    public static bool IsInternal(IPAddress? ip) => ip is null || OutboundUrlGuard.IsBlocked(ip);

    private static FixedWindowRateLimiterOptions Window(int permits) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0,
        AutoReplenishment = true,
    };

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
