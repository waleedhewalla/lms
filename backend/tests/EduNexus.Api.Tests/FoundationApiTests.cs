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
    public async Task Correspondence_FullFlow_SubmitDecide_Notifies()
    {
        var perms = AllPerms.Concat(["correspondence:create", "correspondence:read", "correspondence:confidential",
            "approval:read", "approval:decide", "task:read", "task:update", "notification:read"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Corr Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        async Task<Guid> MkPerson(string name)
        {
            var r = await client.PostAsJsonAsync("/api/people",
                new { tenantId = tenant, type = "Employee", fullName = name, email = (string?)null });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return IdOf(await r.Content.ReadFromJsonAsync<JsonElement>());
        }
        var author = await MkPerson("Karim Author");
        var reviewer = await MkPerson("Rana Reviewer");

        // FR-COR-001: create (number reserved, draft)
        var create = await client.PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = tenant, type = "Internal", subject = "Budget memo", content = "Please review.",
            authorId = author, priority = "High", isConfidential = false,
            recipients = new[] { new { personId = reviewer.ToString(), displayName = "Rana", isExternal = false } },
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var corr = await create.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("CORR-", corr.GetProperty("number").GetString());
        Assert.Equal("Draft", corr.GetProperty("status").GetString());
        var corrId = corr.GetProperty("id").GetGuid();

        // submit → approval (accelerated, due +2d) + task + event
        var submit = await client.PostAsJsonAsync($"/api/correspondence/{corrId}/submit",
            new { tenantId = tenant, reviewerId = reviewer });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var approvalId = (await submit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetGuid();
        var resubmit = await client.PostAsJsonAsync($"/api/correspondence/{corrId}/submit",
            new { tenantId = tenant, reviewerId = reviewer });
        Assert.Equal(HttpStatusCode.Conflict, resubmit.StatusCode);

        var pending = await client.GetFromJsonAsync<JsonElement>($"/api/approvals?tenantId={tenant}&assigneeId={reviewer}&status=Pending");
        Assert.Equal(1, pending.GetArrayLength());
        Assert.Equal("Accelerated", pending[0].GetProperty("priority").GetString());
        var tasks = await client.GetFromJsonAsync<JsonElement>($"/api/tasks?tenantId={tenant}&assigneeId={reviewer}");
        Assert.Equal(1, tasks.GetArrayLength());

        // wrong decider → 403; right decider approves
        var stranger = await client.PostAsJsonAsync($"/api/approvals/{approvalId}/decide",
            new { tenantId = tenant, decidedBy = author, approve = true, comment = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        var decide = await client.PostAsJsonAsync($"/api/approvals/{approvalId}/decide",
            new { tenantId = tenant, decidedBy = reviewer, approve = true, comment = "Looks good" });
        Assert.Equal(HttpStatusCode.OK, decide.StatusCode);
        var again = await client.PostAsJsonAsync($"/api/approvals/{approvalId}/decide",
            new { tenantId = tenant, decidedBy = reviewer, approve = true, comment = (string?)null });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/correspondence?tenantId={tenant}");
        Assert.Equal("Approved", list.EnumerateArray().First(e => e.GetProperty("id").GetGuid() == corrId).GetProperty("status").GetString());

        // reviewer got an in-app notification via broker → consumer → DB (poll ≤ 25s)
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            var notifs = await client.GetFromJsonAsync<JsonElement>($"/api/notifications?tenantId={tenant}&personId={reviewer}");
            if (notifs.EnumerateArray().Any(n => n.GetProperty("title").GetString()!.Contains("Review requested")))
                return;
            await Task.Delay(500);
        }
        Assert.Fail("Reviewer notification not materialized within 25s");
    }

    [Fact]
    public async Task Correspondence_Confidential_Gated()
    {
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), ["tenant:create", "person:create", "correspondence:create", "correspondence:read", "correspondence:confidential"]));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Conf Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var plain = factory.CreateClient();
        Auth(plain, Mint(tenant, ["person:create", "correspondence:create", "correspondence:read"])); // no confidential perm
        var author = IdOf(await (await plain.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Sam Secret", email = (string?)null })).Content.ReadFromJsonAsync<JsonElement>());

        var denied = await plain.PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = tenant, type = "Internal", subject = "Secret", content = "Shh",
            authorId = author, priority = "Normal", isConfidential = true, recipients = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var elevated = factory.CreateClient();
        Auth(elevated, Mint(tenant, ["person:create", "correspondence:create", "correspondence:read", "correspondence:confidential"]));
        var ok = await elevated.PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = tenant, type = "Internal", subject = "Secret", content = "Shh",
            authorId = author, priority = "Normal", isConfidential = true, recipients = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);

        var visibleToPlain = await plain.GetFromJsonAsync<JsonElement>($"/api/correspondence?tenantId={tenant}");
        Assert.Equal(0, visibleToPlain.GetArrayLength());
        var visibleToElevated = await elevated.GetFromJsonAsync<JsonElement>($"/api/correspondence?tenantId={tenant}");
        Assert.Equal(1, visibleToElevated.GetArrayLength());
    }

    [Fact]
    public async Task Governance_FullChain_MeetingToAction_PolicyAck()
    {
        // BP-ACD-014: Agenda→Meeting→Discussion→Decision→Approval→Publication→Assignment→Action→Evidence→Verification→Closure
        var perms = AllPerms.Concat(["correspondence:create", "correspondence:read",
            "committee:create", "committee:read", "meeting:create", "meeting:read",
            "decision:create", "decision:read", "action:update",
            "policy:create", "policy:read", "policy:ack", "notification:read"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Gov Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        async Task<Guid> MkPerson(string name)
        {
            var r = await client.PostAsJsonAsync("/api/people",
                new { tenantId = tenant, type = "Employee", fullName = name, email = (string?)null });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return IdOf(await r.Content.ReadFromJsonAsync<JsonElement>());
        }
        var dean = await MkPerson("Dean Gov");
        var member = await MkPerson("Member Gov");

        var committee = IdOf(await (await client.PostAsJsonAsync("/api/committees",
            new { tenantId = tenant, code = "FC", name = "Faculty Council" })).Content.ReadFromJsonAsync<JsonElement>());
        foreach (var p in new[] { dean, member })
        {
            var m = await client.PostAsJsonAsync($"/api/committees/{committee}/members",
                new { tenantId = tenant, personId = p, role = "Member" });
            Assert.Equal(HttpStatusCode.Created, m.StatusCode);
        }

        var meeting = IdOf(await (await client.PostAsJsonAsync("/api/meetings", new
        {
            tenantId = tenant, committeeId = committee, title = "FC Session 1",
            startsAt = DateTimeOffset.UtcNow.AddDays(1),
            agenda = new[] { new { title = "Budget approval", description = "FY budget" } },
        })).Content.ReadFromJsonAsync<JsonElement>());

        var att = await client.PostAsJsonAsync($"/api/meetings/{meeting}/attendance",
            new { tenantId = tenant, personId = dean, status = "Present" });
        Assert.Equal(HttpStatusCode.OK, att.StatusCode);

        var conclude = await client.PostAsJsonAsync($"/api/meetings/{meeting}/conclude",
            new { tenantId = tenant, minutes = "Budget approved unanimously." });
        Assert.Equal(HttpStatusCode.OK, conclude.StatusCode);

        var decision = IdOf(await (await client.PostAsJsonAsync($"/api/meetings/{meeting}/decisions",
            new { tenantId = tenant, text = "Approve FY budget." })).Content.ReadFromJsonAsync<JsonElement>());

        var action = IdOf(await (await client.PostAsJsonAsync($"/api/decisions/{decision}/actions",
            new { tenantId = tenant, assigneeId = member, description = "Publish budget circular.", dueAt = (DateTimeOffset?)null })).Content.ReadFromJsonAsync<JsonElement>());

        var adv = await client.PostAsJsonAsync($"/api/decision-actions/{action}/advance",
            new { tenantId = tenant, status = "Done" });
        Assert.Equal(HttpStatusCode.OK, adv.StatusCode);

        // member notified of assignment (poll ≤ 25s)
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            var notifs = await client.GetFromJsonAsync<JsonElement>($"/api/notifications?tenantId={tenant}&personId={member}");
            if (notifs.EnumerateArray().Any(n => n.GetProperty("title").GetString() == "Action assigned"))
                break;
            await Task.Delay(500);
        }

        // policy publish → ack → pending empty
        var policy = IdOf(await (await client.PostAsJsonAsync("/api/policies",
            new { tenantId = tenant, code = "POL-001", title = "Attendance policy", content = "Be present." })).Content.ReadFromJsonAsync<JsonElement>());
        var pub = await client.PostAsJsonAsync($"/api/policies/{policy}/publish", new { tenantId = tenant });
        Assert.Equal(HttpStatusCode.OK, pub.StatusCode);
        var ack = await client.PostAsJsonAsync($"/api/policies/{policy}/acknowledge",
            new { tenantId = tenant, personId = member });
        Assert.Equal(HttpStatusCode.Created, ack.StatusCode);
        var dupAck = await client.PostAsJsonAsync($"/api/policies/{policy}/acknowledge",
            new { tenantId = tenant, personId = member });
        Assert.Equal(HttpStatusCode.Conflict, dupAck.StatusCode);
        var pending = await client.GetFromJsonAsync<JsonElement>($"/api/policies/{policy}/pending?tenantId={tenant}");
        Assert.DoesNotContain(pending.EnumerateArray(), p => p.GetProperty("id").GetGuid() == member);
        Assert.Contains(pending.EnumerateArray(), p => p.GetProperty("id").GetGuid() == dean);
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
