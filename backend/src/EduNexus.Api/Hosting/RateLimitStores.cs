using System.Collections.Concurrent;
using StackExchange.Redis;

namespace EduNexus.Api.Hosting;

/// <summary>The outcome of counting one request against a budget.</summary>
public readonly record struct RateDecision(bool Allowed, int RetryAfterSeconds);

/// <summary>
/// Counts requests per key in one-minute windows aligned to the wall clock (so every replica agrees on where a window
/// starts). Implementations: in memory (one instance), Redis (shared by all replicas), and a wrapper that uses Redis
/// but never lets a Redis failure stop traffic.
/// </summary>
public interface IRateLimitStore
{
    ValueTask<RateDecision> HitAsync(string key, int limit, CancellationToken ct);
}

internal static class RateWindow
{
    public static long MinuteOf(TimeProvider clock) => clock.GetUtcNow().ToUnixTimeSeconds() / 60;

    /// <summary>Seconds until the current window ends (at least 1: Retry-After must not be 0).</summary>
    public static int SecondsLeft(TimeProvider clock) => (int)Math.Max(1, (MinuteOf(clock) + 1) * 60 - clock.GetUtcNow().ToUnixTimeSeconds());
}

/// <summary>Per-process counts: exact for one API instance, and the fallback when Redis is not configured or is down.</summary>
public sealed class LocalRateLimitStore(TimeProvider clock) : IRateLimitStore
{
    private sealed class Window { public long Minute = -1; public int Count; }

    private readonly ConcurrentDictionary<string, Window> windows = new();
    private long lastSweepMinute;

    public ValueTask<RateDecision> HitAsync(string key, int limit, CancellationToken ct)
    {
        var minute = RateWindow.MinuteOf(clock);
        Sweep(minute);
        var w = windows.GetOrAdd(key, _ => new Window());
        lock (w)
        {
            if (w.Minute != minute) { w.Minute = minute; w.Count = 0; }
            if (w.Count >= limit) return ValueTask.FromResult(new RateDecision(false, RateWindow.SecondsLeft(clock)));
            w.Count++;
            return ValueTask.FromResult(new RateDecision(true, 0));
        }
    }

    /// <summary>Once a minute, forget callers that have not been seen in the current or previous window.</summary>
    private void Sweep(long minute)
    {
        var last = Interlocked.Read(ref lastSweepMinute);
        if (last == minute || Interlocked.CompareExchange(ref lastSweepMinute, minute, last) != last) return;
        foreach (var (key, w) in windows)
            if (w.Minute < minute - 1) windows.TryRemove(key, out _);
    }
}

/// <summary>One counter per key per minute in Redis, so all API replicas draw on the same budget.</summary>
public sealed class RedisRateLimitStore(IConnectionMultiplexer redis, TimeProvider clock, string prefix = "edunexus:rl") : IRateLimitStore
{
    // INCR and EXPIRE together, so a counter can never be left without an expiry.
    private static readonly LuaScript Hit = LuaScript.Prepare(
        "local c = redis.call('INCR', @key) if c == 1 then redis.call('EXPIRE', @key, @ttl) end return c");

    /// <summary>A Redis call must not hold a request for long: past this, the caller falls back to local counting.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromMilliseconds(150);

    public async ValueTask<RateDecision> HitAsync(string key, int limit, CancellationToken ct)
    {
        var redisKey = (RedisKey)$"{prefix}:{RateWindow.MinuteOf(clock)}:{key}";
        var count = (long)await redis.GetDatabase().ScriptEvaluateAsync(Hit, new { key = redisKey, ttl = 120 }).WaitAsync(CallTimeout, ct);
        return count > limit ? new RateDecision(false, RateWindow.SecondsLeft(clock)) : new RateDecision(true, 0);
    }
}

/// <summary>
/// Uses the shared store, and the local one while the shared one is failing. Availability beats precision: if Redis
/// is down the limit simply becomes per instance again (what it was before Redis), and requests are never refused
/// because of it. After a failure the shared store is left alone for <see cref="BreakFor"/> so a dead Redis costs
/// one timeout, not one per request.
/// </summary>
public sealed class ResilientRateLimitStore(IRateLimitStore shared, IRateLimitStore local, TimeProvider clock, ILogger<ResilientRateLimitStore> log) : IRateLimitStore
{
    public static readonly TimeSpan BreakFor = TimeSpan.FromSeconds(10);

    private long openUntilTicks;   // clock ticks (UTC) until which the shared store is skipped

    public async ValueTask<RateDecision> HitAsync(string key, int limit, CancellationToken ct)
    {
        if (clock.GetUtcNow().UtcTicks < Interlocked.Read(ref openUntilTicks)) return await local.HitAsync(key, limit, ct);
        try
        {
            return await shared.HitAsync(key, limit, ct);
        }
        catch (Exception ex) when ((ex is RedisException or TimeoutException) && !ct.IsCancellationRequested)
        {
            // Only the request that trips the breaker logs; the rest fall back quietly until it closes.
            var until = clock.GetUtcNow().UtcTicks + BreakFor.Ticks;
            if (Interlocked.Exchange(ref openUntilTicks, until) < until - BreakFor.Ticks)
                log.LogWarning(ex, "Shared rate-limit store failed; counting per instance for {Seconds} s", (int)BreakFor.TotalSeconds);
            return await local.HitAsync(key, limit, ct);
        }
    }
}
