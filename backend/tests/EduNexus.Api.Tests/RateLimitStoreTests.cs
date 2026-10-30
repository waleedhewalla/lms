using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;
using EduNexus.Api.Auth;
using EduNexus.Api.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;

namespace EduNexus.Api.Tests;

public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}

public class RateLimitStoreTests(EduNexusFactory factory) : IClassFixture<EduNexusFactory>
{
    // 12:00:10 UTC: inside a window, 50 s before the next one.
    private static ManualTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 30, 12, 0, 10, TimeSpan.Zero));

    [Fact]
    public async Task Local_Store_Counts_Per_Key_In_Wall_Clock_Minutes()
    {
        var clock = Clock();
        var store = new LocalRateLimitStore(clock);
        for (var i = 0; i < 3; i++) Assert.True((await store.HitAsync("a", 3, default)).Allowed);
        var over = await store.HitAsync("a", 3, default);
        Assert.False(over.Allowed);
        Assert.Equal(50, over.RetryAfterSeconds);                       // until 12:01:00
        Assert.True((await store.HitAsync("b", 3, default)).Allowed);   // another caller has its own budget

        clock.Advance(TimeSpan.FromSeconds(50));                        // 12:01:00: a new window
        Assert.True((await store.HitAsync("a", 3, default)).Allowed);
    }

    /// <summary>Counts calls and fails them all, like a Redis that is down.</summary>
    private sealed class DownStore : IRateLimitStore
    {
        public int Calls;
        public ValueTask<RateDecision> HitAsync(string key, int limit, CancellationToken ct)
        {
            Calls++;
            throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
        }
    }

    [Fact]
    public async Task Resilient_Store_Falls_Back_To_Local_Counting_And_Stops_Trying_For_A_While()
    {
        var clock = Clock();
        var down = new DownStore();
        var store = new ResilientRateLimitStore(down, new LocalRateLimitStore(clock), clock, NullLogger<ResilientRateLimitStore>.Instance);

        Assert.True((await store.HitAsync("a", 2, default)).Allowed);   // Redis failed: served locally, not refused
        Assert.Equal(1, down.Calls);
        Assert.True((await store.HitAsync("a", 2, default)).Allowed);
        Assert.False((await store.HitAsync("a", 2, default)).Allowed);   // the limit still holds, per instance
        Assert.Equal(1, down.Calls);                                     // breaker open: no call per request

        clock.Advance(ResilientRateLimitStore.BreakFor + TimeSpan.FromSeconds(1));
        await store.HitAsync("a", 2, default);
        Assert.Equal(2, down.Calls);                                     // tried again once the breaker closed
    }

    /// <summary>A Redis for the tests: EDUNEXUS_TEST_REDIS (host:port). CI always provides one; locally the test is a no-op without it.</summary>
    private static string? RedisAddress()
    {
        var address = Environment.GetEnvironmentVariable("EDUNEXUS_TEST_REDIS");
        if (string.IsNullOrWhiteSpace(address) && Environment.GetEnvironmentVariable("CI") == "true")
            Assert.Fail("EDUNEXUS_TEST_REDIS must be set in CI so the shared rate limit is really tested.");
        return string.IsNullOrWhiteSpace(address) ? null : address;
    }

    [Fact]
    public async Task Redis_Store_Shares_One_Budget_Between_Instances()
    {
        if (RedisAddress() is not { } address) return;
        var clock = Clock();
        var prefix = $"test:{Guid.NewGuid():N}";
        await using var redisA = await ConnectionMultiplexer.ConnectAsync(address);
        await using var redisB = await ConnectionMultiplexer.ConnectAsync(address);   // a second API replica
        var a = new RedisRateLimitStore(redisA, clock, prefix);
        var b = new RedisRateLimitStore(redisB, clock, prefix);

        for (var i = 0; i < 3; i++) Assert.True((await a.HitAsync("caller", 5, default)).Allowed);
        for (var i = 0; i < 2; i++) Assert.True((await b.HitAsync("caller", 5, default)).Allowed);
        Assert.False((await a.HitAsync("caller", 5, default)).Allowed);   // five in total across both, not five each
        var over = await b.HitAsync("caller", 5, default);
        Assert.False(over.Allowed);
        Assert.Equal(50, over.RetryAfterSeconds);
        Assert.True((await b.HitAsync("someone-else", 5, default)).Allowed);

        // Every counter expires: none is left behind without a TTL.
        var ttl = await redisA.GetDatabase().KeyTimeToLiveAsync($"{prefix}:{clock.GetUtcNow().ToUnixTimeSeconds() / 60}:caller");
        Assert.NotNull(ttl);
        Assert.InRange(ttl!.Value.TotalSeconds, 1, 120);

        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.True((await a.HitAsync("caller", 5, default)).Allowed);    // next window
    }

    // ---- through the real middleware ----

    private sealed class RecordingStore : IRateLimitStore
    {
        public readonly ConcurrentQueue<(string Key, int Limit)> Hits = new();
        public bool Deny;
        public ValueTask<RateDecision> HitAsync(string key, int limit, CancellationToken ct)
        {
            Hits.Enqueue((key, limit));
            return ValueTask.FromResult(Deny ? new RateDecision(false, 42) : new RateDecision(true, 0));
        }
    }

    private HttpClient As(WebApplicationFactory<Program> app, string subject, Guid tenant, params string[] perms)
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", app.Services.GetRequiredService<DevTokenService>().Mint(subject, tenant, perms));
        return c;
    }

    private WebApplicationFactory<Program> With(RecordingStore store, string? enabled = null) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("RateLimiting:PermitsPerMinute", "7");
        b.UseSetting("RateLimiting:ExpensivePermitsPerMinute", "3");
        if (enabled is not null) b.UseSetting("RateLimiting:Enabled", enabled);
        b.ConfigureServices(s => { s.RemoveAll<IRateLimitStore>(); s.AddSingleton<IRateLimitStore>(store); });
    });

    [Fact]
    public async Task Middleware_Counts_Callers_Expensive_Paths_And_Anonymous_Clients_And_Skips_Health()
    {
        var store = new RecordingStore();
        using var app = With(store);
        var tenant = Guid.NewGuid();
        var user = As(app, "s1", tenant, "search:read");

        await user.GetAsync("/api/auth/me");
        Assert.Equal([($"g:u:{tenant}:s1", 7)], store.Hits.ToArray());

        store.Hits.Clear();
        await user.GetAsync($"/api/search/suggest?tenantId={tenant}&q=ab");
        Assert.Equal([($"g:u:{tenant}:s1", 7), ($"x:u:{tenant}:s1", 3)], store.Hits.ToArray());   // general budget, then the expensive one

        store.Hits.Clear();
        await user.GetAsync("/health");
        await user.GetAsync("/metrics");
        Assert.Empty(store.Hits);

        await app.CreateClient().GetAsync("/api/auth/me");   // no token: counted by client address
        Assert.StartsWith("g:ip:", store.Hits.Single().Key);
    }

    [Fact]
    public async Task Middleware_Answers_429_With_The_Stores_RetryAfter_And_Can_Be_Switched_Off()
    {
        var store = new RecordingStore { Deny = true };
        using (var app = With(store))
        {
            var res = await As(app, "s2", Guid.NewGuid()).GetAsync("/api/auth/me");
            Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
            Assert.Equal("42", res.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
            Assert.Equal(HttpStatusCode.OK, (await app.CreateClient().GetAsync("/health")).StatusCode);   // never limited
        }

        var off = new RecordingStore { Deny = true };
        using (var app = With(off, enabled: "false"))
        {
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await As(app, "s3", Guid.NewGuid()).GetAsync("/api/auth/me")).StatusCode);
            Assert.Empty(off.Hits);
        }
    }

    [Fact]
    public async Task A_Configured_But_Unreachable_Redis_Never_Stops_Traffic_And_The_Limit_Still_Applies_Per_Instance()
    {
        using var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("RateLimiting:Redis:ConnectionString", "127.0.0.1:1");   // nothing listens here
            b.UseSetting("RateLimiting:PermitsPerMinute", "3");
        });
        var caller = As(app, "s4", Guid.NewGuid());
        var results = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++) results.Add((await caller.GetAsync("/api/auth/me")).StatusCode);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, results.Take(3));
        Assert.Equal(HttpStatusCode.TooManyRequests, results[3]);
    }
}
