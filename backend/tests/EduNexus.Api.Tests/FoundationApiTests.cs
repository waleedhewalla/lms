using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EduNexus.Api.Auth;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EduNexus.Api.Tests;

public sealed class FoundationApiTests(EduNexusFactory factory) : IClassFixture<EduNexusFactory>
{
    private static readonly string[] AllPerms =
    [
        "tenant:create", "tenant:read", "org:create", "org:read",
        "person:create", "person:read", "role:create", "role:assign", "role:read", "audit:read",
    ];

    private string Mint(Guid tenantId, IEnumerable<string>? perms = null)
    {
        var tokens = factory.Services.GetRequiredService<DevTokenService>();
        return tokens.Mint("tests", tenantId, perms ?? AllPerms);
    }

    private static void Auth(HttpClient c, string token) =>
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    private static string Uid(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static Guid IdOf(JsonElement e) => e.GetProperty("id").GetGuid();

    [Fact]
    public async Task Health_Ok_Anonymous()
    {
        var res = await factory.CreateClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Roles_Unauthenticated_401()
    {
        var res = await factory.CreateClient().GetAsync($"/api/roles?tenantId={Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Tenant_DuplicateSlug_409()
    {
        var client = factory.CreateClient();
        var tenantId = Guid.NewGuid();
        Auth(client, Mint(tenantId));
        var slug = Uid("t");
        var first = await client.PostAsJsonAsync("/api/tenants", new { slug, name = "Dup Uni" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var dup = await client.PostAsJsonAsync("/api/tenants", new { slug, name = "Dup Uni 2" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
    }

    [Fact]
    public async Task FullFlow_TenantToSoDToRevoke()
    {
        var client = factory.CreateClient();
        Auth(client, Mint(Guid.NewGuid())); // bootstrap: platform-level tenant creation

        // tenant → org → person
        var tenantRes = await client.PostAsJsonAsync("/api/tenants", new { slug = Uid("t"), name = "Flow Uni" });
        Assert.Equal(HttpStatusCode.Created, tenantRes.StatusCode);
        var tenant = IdOf(await tenantRes.Content.ReadFromJsonAsync<JsonElement>());

        client = factory.CreateClient();
        Auth(client, Mint(tenant)); // scoped: tenant claim must match payload tenant

        var orgRes = await client.PostAsJsonAsync("/api/organizations", new { tenantId = tenant, code = "ENG", name = "Engineering" });
        Assert.Equal(HttpStatusCode.Created, orgRes.StatusCode);

        var personRes = await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Amina Tester", email = "amina@test.local" });
        Assert.Equal(HttpStatusCode.Created, personRes.StatusCode);
        var person = IdOf(await personRes.Content.ReadFromJsonAsync<JsonElement>());

        // roles
        foreach (var (code, name) in new[] { ("finance.requester", "Requester"), ("finance.approver", "Approver") })
        {
            var r = await client.PostAsJsonAsync("/api/roles", new { tenantId = tenant, code, name, permissions = new[] { "x" } });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        }
        var dupRole = await client.PostAsJsonAsync("/api/roles", new { tenantId = tenant, code = "finance.requester", name = "Dup", permissions = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Conflict, dupRole.StatusCode);

        // assign requester, SoD blocks approver
        var a1 = await client.PostAsJsonAsync("/api/roles/assign", new { tenantId = tenant, personId = person, roleCode = "finance.requester" });
        Assert.Equal(HttpStatusCode.Created, a1.StatusCode);
        var sod = await client.PostAsJsonAsync("/api/roles/assign", new { tenantId = tenant, personId = person, roleCode = "finance.approver" });
        Assert.Equal(HttpStatusCode.Conflict, sod.StatusCode);

        // revoke requester → approver assignable (TC-AUTH-001 + revoke path)
        var revoke = await client.PostAsJsonAsync("/api/roles/revoke", new { tenantId = tenant, personId = person, roleCode = "finance.requester" });
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);
        var a2 = await client.PostAsJsonAsync("/api/roles/assign", new { tenantId = tenant, personId = person, roleCode = "finance.approver" });
        Assert.Equal(HttpStatusCode.Created, a2.StatusCode);

        // audit trail contains the assignment events
        var audit = await client.GetFromJsonAsync<JsonElement>($"/api/audit?tenantId={tenant}&limit=50");
        var actions = audit.EnumerateArray().Select(e => e.GetProperty("action").GetString()).ToHashSet();
        Assert.Contains("RoleAssigned", actions);
        Assert.Contains("RoleRevoked", actions);
    }

    [Fact]
    public async Task CrossTenant_Isolation_And_ClaimMismatch()
    {
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid())); // platform-level tenant creation

        // seed one role in tenant A
        var slug = Uid("t");
        var t = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants", new { slug, name = "Iso Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(t)); // scoped to tenant A
        var r = await client.PostAsJsonAsync("/api/roles", new { tenantId = t, code = "iso.role", name = "Iso", permissions = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);

        // fresh tenant B (token still claims A... mint B token) sees nothing
        var tenantB = Guid.NewGuid();
        var clientB = factory.CreateClient();
        Auth(clientB, Mint(tenantB));
        var rolesB = await clientB.GetFromJsonAsync<JsonElement>($"/api/roles?tenantId={tenantB}");
        Assert.Equal(0, rolesB.GetArrayLength());

        // A-claim token operating on B's tenant id → 403
        var forbidden = await client.PostAsJsonAsync("/api/roles", new { tenantId = tenantB, code = "x", name = "X", permissions = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task MissingPermission_403()
    {
        var client = factory.CreateClient();
        Auth(client, Mint(Guid.NewGuid(), ["role:read"])); // no role:create
        var res = await client.PostAsJsonAsync("/api/roles", new { tenantId = Guid.NewGuid(), code = "x", name = "X", permissions = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task AssignRole_WritesOutbox_And_RelayDispatches()
    {
        // Requires RabbitMQ on localhost:5673 (edunexus-rabbitmq). The test host
        // runs EventRelay, so a dispatched row proves broker publish end-to-end.
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid()));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Outbox Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant));
        var person = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Omar Relay", email = (string?)null })).Content.ReadFromJsonAsync<JsonElement>());
        var rc = await client.PostAsJsonAsync("/api/roles",
            new { tenantId = tenant, code = "relay.role", name = "Relay", permissions = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Created, rc.StatusCode);
        var assign = await client.PostAsJsonAsync("/api/roles/assign",
            new { tenantId = tenant, personId = person, roleCode = "relay.role" });
        Assert.Equal(HttpStatusCode.Created, assign.StatusCode);

        // outbox row exists atomically with the assignment
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var row = await db.OutboxEvents.FirstOrDefaultAsync(
                e => e.TenantId == tenant && e.EventType == "RoleAssigned");
            Assert.NotNull(row);
            Assert.Contains("relay.role", row.Payload);
        }

        // relay publishes within a few ticks (poll 2s) and stamps DispatchedAt
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var done = await db.OutboxEvents.AnyAsync(
                e => e.TenantId == tenant && e.EventType == "RoleAssigned" && e.DispatchedAt != null);
            if (done) return;
            await Task.Delay(500);
        }
        Assert.Fail("EventRelay did not dispatch RoleAssigned within 25s (is RabbitMQ up on :5673?)");
    }
}
