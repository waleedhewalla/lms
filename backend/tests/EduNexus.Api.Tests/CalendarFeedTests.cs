using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EduNexus.Api.Auth;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EduNexus.Api.Tests;

/// <summary>Personal calendar subscription links: issued once, hashed at rest, personal content, and dead when revoked, expired or replaced.</summary>
public sealed class CalendarFeedTests(EduNexusFactory factory) : IClassFixture<EduNexusFactory>
{
    private HttpClient As(Guid tenant, Guid? person, params string[] perms)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.Services.GetRequiredService<DevTokenService>().Mint("tests", tenant, perms, personId: person));
        return c;
    }

    private async Task<T> InDb<T>(Func<AppDbContext, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private static Person NewPerson(Guid tenant, string name) => new(Guid.NewGuid(), tenant, PersonType.Employee, name, null, true, DateTimeOffset.UtcNow);

    private static async Task<string> IssueAsync(HttpClient c, Guid tenant)
    {
        var r = await c.PostAsJsonAsync("/api/calendar/feed-token", new { tenantId = tenant });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("path").GetString()!;
    }

    [Fact]
    public async Task Link_Is_Issued_Once_Stored_Hashed_And_Shows_Only_What_Concerns_The_Person()
    {
        var tenant = Guid.NewGuid();
        var (alice, bob) = (NewPerson(tenant, "Alice"), NewPerson(tenant, "Bob"));
        var committee = Guid.NewGuid();
        var soon = DateTimeOffset.UtcNow.AddDays(3);
        await InDb(async db =>
        {
            db.People.AddRange(alice, bob);
            db.CommitteeMembers.Add(new CommitteeMember(Guid.NewGuid(), tenant, committee, alice.Id, "Member", DateTimeOffset.UtcNow));
            db.Meetings.AddRange(new Meeting(Guid.NewGuid(), tenant, committee, "Senate meeting", soon, MeetingStatus.Scheduled, null),
                new Meeting(Guid.NewGuid(), tenant, Guid.NewGuid(), "Other committee meeting", soon, MeetingStatus.Scheduled, null));
            db.CalendarEvents.Add(new CalendarEvent(Guid.NewGuid(), tenant, "National Day holiday", CalendarEventKind.Holiday, soon, soon.AddDays(1), null, null, null, null, DateTimeOffset.UtcNow));
            db.WorkTasks.AddRange(new WorkTask(Guid.NewGuid(), tenant, "Alice's report", alice.Id, null, WorkTaskStatus.Open, soon, DateTimeOffset.UtcNow),
                new WorkTask(Guid.NewGuid(), tenant, "Bob's report", bob.Id, null, WorkTaskStatus.Open, soon, DateTimeOffset.UtcNow),
                new WorkTask(Guid.NewGuid(), tenant, "Alice's finished report", alice.Id, null, WorkTaskStatus.Done, soon, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            return 0;
        });
        var me = As(tenant, alice.Id, "calendar:read");

        Assert.False((await me.GetFromJsonAsync<JsonElement>($"/api/calendar/feed-token?tenantId={tenant}")).GetProperty("active").GetBoolean());
        var path = await IssueAsync(me, tenant);
        var token = path["/api/calendar/feed/".Length..^".ics".Length];
        Assert.Equal(75, token.Length);   // tenant id (32) + 32 random bytes as base64url (43)
        Assert.StartsWith(tenant.ToString("N"), token);

        // The status never repeats the link; the database holds only its hash.
        var status = await me.GetStringAsync($"/api/calendar/feed-token?tenantId={tenant}");
        Assert.DoesNotContain(token, status);
        var stored = await InDb(db => db.CalendarFeedTokens.AsNoTracking().SingleAsync(t => t.TenantId == tenant));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant(), stored.TokenHash);
        Assert.DoesNotContain(token, stored.TokenHash);

        // Anyone holding the link can read the feed without signing in; it is personal.
        var anonymous = factory.CreateClient();
        var res = await anonymous.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/calendar", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("private", res.Headers.CacheControl?.ToString());
        var ics = await res.Content.ReadAsStringAsync();
        Assert.Contains("X-WR-CALNAME:EduNexus", ics);
        Assert.Contains("National Day holiday", ics);
        Assert.Contains("Senate meeting", ics);
        Assert.Contains("Task due: Alice's report", ics);
        Assert.DoesNotContain("Other committee meeting", ics);
        Assert.DoesNotContain("Bob's report", ics);
        Assert.DoesNotContain("finished report", ics);
        Assert.NotNull((await InDb(db => db.CalendarFeedTokens.AsNoTracking().SingleAsync(t => t.TenantId == tenant))).LastUsedAt);
    }

    [Fact]
    public async Task Replacing_Revoking_Expiry_And_Bad_Links_All_Look_The_Same()
    {
        var tenant = Guid.NewGuid();
        var alice = NewPerson(tenant, "Alice");
        await InDb(async db => { db.People.Add(alice); await db.SaveChangesAsync(); return 0; });
        var me = As(tenant, alice.Id, "calendar:read");
        var anonymous = factory.CreateClient();

        var first = await IssueAsync(me, tenant);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(first)).StatusCode);
        var second = await IssueAsync(me, tenant);                       // replaces the first
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(first)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(second)).StatusCode);

        // Wrong secret, malformed, another tenant's prefix, and the link with one character changed.
        var secret = second["/api/calendar/feed/".Length..^".ics".Length][32..];
        foreach (var bad in new[]
        {
            $"/api/calendar/feed/{tenant:N}{new string('A', 43)}.ics",
            "/api/calendar/feed/not-a-token.ics",
            $"/api/calendar/feed/{Guid.NewGuid():N}{secret}.ics",
            second.Replace(".ics", "x.ics"),
        })
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(bad)).StatusCode);

        // Expiry: the link stops working although it was never revoked.
        await InDb(async db =>
        {
            var row = await db.CalendarFeedTokens.SingleAsync(t => t.TenantId == tenant && t.RevokedAt == null);
            db.Entry(row).CurrentValues.SetValues(row with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(second)).StatusCode);
        Assert.False((await me.GetFromJsonAsync<JsonElement>($"/api/calendar/feed-token?tenantId={tenant}")).GetProperty("active").GetBoolean());

        // Revocation, and a person who has left (inactive) cannot be read through an old link.
        var third = await IssueAsync(me, tenant);
        Assert.Equal(HttpStatusCode.NoContent, (await me.DeleteAsync($"/api/calendar/feed-token?tenantId={tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(third)).StatusCode);
        var fourth = await IssueAsync(me, tenant);
        await InDb(async db =>
        {
            var p = await db.People.SingleAsync(x => x.Id == alice.Id);
            db.Entry(p).CurrentValues.SetValues(p with { IsActive = false });
            await db.SaveChangesAsync();
            return 0;
        });
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(fourth)).StatusCode);

        // Issuing and revoking are audited, without the link.
        var events = await InDb(db => db.AuditEvents.AsNoTracking().Where(a => a.TenantId == tenant && a.Action.StartsWith("CalendarFeed")).Select(a => a.Action).ToListAsync());
        Assert.Contains("CalendarFeedIssued", events);
        Assert.Contains("CalendarFeedRevoked", events);
    }

    [Fact]
    public async Task Management_Needs_Permission_A_Linked_Person_And_The_Right_Tenant()
    {
        var tenant = Guid.NewGuid();
        var alice = NewPerson(tenant, "Alice");
        await InDb(async db => { db.People.Add(alice); await db.SaveChangesAsync(); return 0; });

        Assert.Equal(HttpStatusCode.Forbidden, (await As(tenant, alice.Id, "task:read").PostAsJsonAsync("/api/calendar/feed-token", new { tenantId = tenant })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Guid.NewGuid(), alice.Id, "calendar:read").PostAsJsonAsync("/api/calendar/feed-token", new { tenantId = tenant })).StatusCode);
        // A token that is not tied to any person has no calendar to share.
        Assert.Equal(HttpStatusCode.Conflict, (await As(tenant, null, "calendar:read").PostAsJsonAsync("/api/calendar/feed-token", new { tenantId = tenant })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/calendar/feed-token", new { tenantId = tenant })).StatusCode);
    }
}
