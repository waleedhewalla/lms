using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EduNexus.Api.Auth;
using EduNexus.Foundation;
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

    private string Mint(Guid tenantId, IEnumerable<string>? perms = null, Guid? personId = null)
    {
        var tokens = factory.Services.GetRequiredService<DevTokenService>();
        return tokens.Mint("tests", tenantId, perms ?? AllPerms, personId: personId);
    }

    /// <summary>A client whose token acts as <paramref name="personId"/> (person_id claim).</summary>
    private HttpClient ClientAs(Guid tenantId, IEnumerable<string> perms, Guid personId)
    {
        var c = factory.CreateClient();
        Auth(c, Mint(tenantId, perms, personId));
        return c;
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
    public async Task Endpoints_WithoutModulePermission_403()
    {
        var client = factory.CreateClient();
        var tenantId = Guid.NewGuid();
        Auth(client, Mint(tenantId)); // AllPerms only: no request/workflow/inbox/quality/... codes
        var qs = $"tenantId={tenantId}&personId={Guid.NewGuid()}&q=x";
        string[] gets =
        [
            "/api/requests", "/api/workflows/instances", "/api/inbox", "/api/my-work", "/api/search",
            "/api/quality/standards", "/api/strategy/plans", "/api/notification-templates",
            "/api/sla/breaches", "/api/document-workspaces",
        ];
        foreach (var path in gets)
        {
            var res = await client.GetAsync($"{path}?{qs}");
            Assert.True(res.StatusCode == HttpStatusCode.Forbidden, $"GET {path} → {(int)res.StatusCode}");
        }
        var create = await client.PostAsJsonAsync("/api/requests",
            new { tenantId, category = "IT", title = "No permission", formId = (Guid?)null, submitterId = Guid.NewGuid(), dataJson = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
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

        // the decider is the token's person, never the body: no person → 403, wrong person → 403,
        // claiming to be the reviewer from the author's token → 403; the reviewer's own token approves
        var anonymous = await client.PostAsJsonAsync($"/api/approvals/{approvalId}/decide",
            new { tenantId = tenant, decidedBy = reviewer, approve = true, comment = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
        var authorClient = ClientAs(tenant, perms, author);
        var stranger = await authorClient.PostAsJsonAsync($"/api/approvals/{approvalId}/decide",
            new { tenantId = tenant, decidedBy = author, approve = true, comment = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, stranger.StatusCode);
        var impersonation = await authorClient.PostAsJsonAsync($"/api/approvals/{approvalId}/decide",
            new { tenantId = tenant, decidedBy = reviewer, approve = true, comment = (string?)null });
        Assert.Equal(HttpStatusCode.Forbidden, impersonation.StatusCode);
        var reviewerClient = ClientAs(tenant, perms, reviewer);
        var decide = await reviewerClient.PostAsJsonAsync($"/api/approvals/{approvalId}/decide",
            new { tenantId = tenant, decidedBy = reviewer, approve = true, comment = "Looks good" });
        Assert.Equal(HttpStatusCode.OK, decide.StatusCode);
        var again = await reviewerClient.PostAsJsonAsync($"/api/approvals/{approvalId}/decide",
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
    public async Task Requests_PurchaseFlow_FormValidate_Submit_Approval()
    {
        // BBP killer-flow slice: dynamic purchase form → validated submission → numbered request → approval.
        var perms = AllPerms.Concat(["form:manage", "form:read", "request:create", "request:read"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Req Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));
        var requester = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Omar Buyer", email = (string?)null })).Content.ReadFromJsonAsync<JsonElement>());
        var head = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Huda Head", email = (string?)null })).Content.ReadFromJsonAsync<JsonElement>());

        var schema = """[{"key":"purpose","label":"Purpose","type":"Text","required":true},{"key":"amount","label":"Amount","type":"Currency","required":true},{"key":"urgent","label":"Urgent","type":"Checkbox","required":false},{"key":"justification","label":"Justification","type":"LongText","required":false,"visibleWhen":{"field":"urgent","equals":"True"}}]""";
        var form = await client.PostAsJsonAsync("/api/forms",
            new { tenantId = tenant, code = "PURCHASE", name = "Purchase Request", category = "Procurement", schemaJson = schema });
        Assert.Equal(HttpStatusCode.Created, form.StatusCode);
        var formId = IdOf(await form.Content.ReadFromJsonAsync<JsonElement>());
        var dupForm = await client.PostAsJsonAsync("/api/forms",
            new { tenantId = tenant, code = "PURCHASE", name = "Dup", category = "Procurement", schemaJson = "[]" });
        Assert.Equal(HttpStatusCode.Conflict, dupForm.StatusCode);
        var badSchema = await client.PostAsJsonAsync("/api/forms",
            new { tenantId = tenant, code = "BAD", name = "Bad", category = "Procurement", schemaJson = "{oops" });
        Assert.Equal(HttpStatusCode.BadRequest, badSchema.StatusCode);

        // invalid submission rejected (missing required amount)
        var bad = await client.PostAsJsonAsync("/api/requests", new
        {
            tenantId = tenant, category = "Procurement", title = "Laptops", submitterId = requester,
            formId, dataJson = """{"purpose":"Lab laptops"}""",
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // valid submission → numbered draft request
        var good = await client.PostAsJsonAsync("/api/requests", new
        {
            tenantId = tenant, category = "Procurement", title = "Laptops", submitterId = requester,
            formId, dataJson = """{"purpose":"Lab laptops","amount":15000,"urgent":false}""",
        });
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
        var req = await good.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("REQ-", req.GetProperty("number").GetString());
        Assert.Equal("Draft", req.GetProperty("status").GetString());
        var reqId = req.GetProperty("id").GetGuid();

        // submit with reviewer → approval + task
        var submit = await client.PostAsJsonAsync($"/api/requests/{reqId}/submit",
            new { tenantId = tenant, reviewerId = head });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var approvalId = (await submit.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetGuid();
        Assert.NotEqual(Guid.Empty, approvalId);
        var resubmit = await client.PostAsJsonAsync($"/api/requests/{reqId}/submit",
            new { tenantId = tenant, reviewerId = head });
        Assert.Equal(HttpStatusCode.Conflict, resubmit.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/requests?tenantId={tenant}&status=Submitted");
        Assert.Equal(1, list.GetArrayLength());
    }

    [Fact]
    public async Task Workflow_FacultyAdminRequest_Chain_To_Completion()
    {
        // BBP killer workflow: dept review → dept approval → dean approval → task → done.
        var perms = AllPerms.Concat(["request:create", "request:read", "workflow:manage", "workflow:read",
            "approval:read", "approval:decide", "task:read"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "WF Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        async Task<Guid> MkPerson(string name)
        {
            var r = await client.PostAsJsonAsync("/api/people",
                new { tenantId = tenant, type = "Employee", fullName = name, email = (string?)null });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return IdOf(await r.Content.ReadFromJsonAsync<JsonElement>());
        }
        var employee = await MkPerson("Omar Employee");
        var head = await MkPerson("Huda Head");
        var dean = await MkPerson("Dean Dani");

        var nodes = $$"""[{"id":"start","type":"start"},{"id":"dept","type":"approval","personId":"{{head}}","slaDays":3},{"id":"dean","type":"approval","personId":"{{dean}}","slaDays":2},{"id":"exec","type":"task","title":"Execute approved request","assigneeFrom":"submitter"},{"id":"end","type":"end"}]""";
        var def = await client.PostAsJsonAsync("/api/workflows/definitions",
            new { tenantId = tenant, code = "FAC-ADMIN", name = "Faculty Admin Request", nodesJson = nodes });
        Assert.Equal(HttpStatusCode.Created, def.StatusCode);
        var badDef = await client.PostAsJsonAsync("/api/workflows/definitions",
            new { tenantId = tenant, code = "BAD", name = "Bad", nodesJson = """[{"id":"x","type":"teleport"}]""" });
        Assert.Equal(HttpStatusCode.BadRequest, badDef.StatusCode);

        var req = await client.PostAsJsonAsync("/api/requests", new
        {
            tenantId = tenant, category = "Administrative", title = "Lab access", submitterId = employee,
            formId = (Guid?)null, dataJson = (string?)null,
        });
        Assert.Equal(HttpStatusCode.Created, req.StatusCode);
        var reqId = IdOf(await req.Content.ReadFromJsonAsync<JsonElement>());

        var submit = await client.PostAsJsonAsync($"/api/requests/{reqId}/submit",
            new { tenantId = tenant, reviewerId = (Guid?)null, workflowCode = "FAC-ADMIN" });
        Assert.Equal(HttpStatusCode.OK, submit.StatusCode);
        var submitted = await submit.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(Guid.Empty, submitted.GetProperty("instanceId").GetGuid());
        var approval1 = submitted.GetProperty("approvalId").GetGuid();
        Assert.NotEqual(Guid.Empty, approval1);

        // step 1: dept head approves → step 2 pending for dean
        var d1 = await ClientAs(tenant, perms, head).PostAsJsonAsync($"/api/approvals/{approval1}/decide",
            new { tenantId = tenant, decidedBy = head, approve = true, comment = (string?)null });
        Assert.Equal(HttpStatusCode.OK, d1.StatusCode);
        var pending = await client.GetFromJsonAsync<JsonElement>($"/api/approvals?tenantId={tenant}&assigneeId={dean}&status=Pending");
        Assert.Equal(1, pending.GetArrayLength());
        var approval2 = pending[0].GetProperty("id").GetGuid();

        // step 2: dean approves → task created → instance completed
        var d2 = await ClientAs(tenant, perms, dean).PostAsJsonAsync($"/api/approvals/{approval2}/decide",
            new { tenantId = tenant, decidedBy = dean, approve = true, comment = (string?)null });
        Assert.Equal(HttpStatusCode.OK, d2.StatusCode);
        var tasks = await client.GetFromJsonAsync<JsonElement>($"/api/tasks?tenantId={tenant}&assigneeId={employee}");
        Assert.True(tasks.GetArrayLength() >= 1);
        var instances = await client.GetFromJsonAsync<JsonElement>($"/api/workflows/instances?tenantId={tenant}&status=Completed");
        Assert.Equal(1, instances.GetArrayLength());

        // rejection path closes the instance
        var req2 = await client.PostAsJsonAsync("/api/requests", new
        {
            tenantId = tenant, category = "Administrative", title = "Rejected one", submitterId = employee,
            formId = (Guid?)null, dataJson = (string?)null,
        });
        var req2Id = IdOf(await req2.Content.ReadFromJsonAsync<JsonElement>());
        var sub2 = await client.PostAsJsonAsync($"/api/requests/{req2Id}/submit",
            new { tenantId = tenant, reviewerId = (Guid?)null, workflowCode = "FAC-ADMIN" });
        var rejApproval = (await sub2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetGuid();
        var rej = await ClientAs(tenant, perms, head).PostAsJsonAsync($"/api/approvals/{rejApproval}/decide",
            new { tenantId = tenant, decidedBy = head, approve = false, comment = "No" });
        Assert.Equal(HttpStatusCode.OK, rej.StatusCode);
        var rejected = await client.GetFromJsonAsync<JsonElement>($"/api/workflows/instances?tenantId={tenant}&status=Rejected");
        Assert.Equal(1, rejected.GetArrayLength());
    }

    [Fact]
    public async Task Communications_Directive_CreatesTasks_And_Inbox()
    {
        var perms = AllPerms.Concat(["communication:create", "communication:read", "inbox:read", "task:read"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Comm Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));
        async Task<Guid> MkPerson(string name)
        {
            var r = await client.PostAsJsonAsync("/api/people",
                new { tenantId = tenant, type = "Employee", fullName = name, email = (string?)null });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return IdOf(await r.Content.ReadFromJsonAsync<JsonElement>());
        }
        var author = await MkPerson("Dean Author");
        var head1 = await MkPerson("Head One");
        var head2 = await MkPerson("Head Two");

        // Announcement without action creates no tasks
        var ann = await client.PostAsJsonAsync("/api/communications", new
        {
            tenantId = tenant, kind = "Announcement", title = "Holiday notice", body = "Off on Friday", authorId = author,
            requiresAction = false, dueAt = (DateTimeOffset?)null, targetPersonIds = new[] { head1 }
        });
        Assert.Equal(HttpStatusCode.Created, ann.StatusCode);
        var annId = IdOf(await ann.Content.ReadFromJsonAsync<JsonElement>());
        var pubAnn = await client.PostAsJsonAsync($"/api/communications/{annId}/publish", new { tenantId = tenant });
        Assert.Equal(HttpStatusCode.OK, pubAnn.StatusCode);
        Assert.Equal(0, (await pubAnn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tasksCreated").GetInt32());

        // Directive with requiresAction fans out tasks to each target (killer workflow)
        var dir = await client.PostAsJsonAsync("/api/communications", new
        {
            tenantId = tenant, kind = "Directive", title = "Submit accreditation evidence", body = "Due 20 Sep",
            authorId = author, requiresAction = true, dueAt = DateTimeOffset.UtcNow.AddDays(7), targetPersonIds = new[] { head1, head2 }
        });
        Assert.Equal(HttpStatusCode.Created, dir.StatusCode);
        var dirId = IdOf(await dir.Content.ReadFromJsonAsync<JsonElement>());
        var pubDir = await client.PostAsJsonAsync($"/api/communications/{dirId}/publish", new { tenantId = tenant });
        Assert.Equal(HttpStatusCode.OK, pubDir.StatusCode);
        Assert.Equal(2, (await pubDir.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("tasksCreated").GetInt32());
        var dupPub = await client.PostAsJsonAsync($"/api/communications/{dirId}/publish", new { tenantId = tenant });
        Assert.Equal(HttpStatusCode.Conflict, dupPub.StatusCode);

        var tasksHead1 = await client.GetFromJsonAsync<JsonElement>($"/api/tasks?tenantId={tenant}&assigneeId={head1}");
        Assert.True(tasksHead1.GetArrayLength() >= 1);
        var inbox = await client.GetFromJsonAsync<JsonElement>($"/api/inbox?tenantId={tenant}&personId={head1}&filter=Task");
        Assert.True(inbox.GetProperty("total").GetInt32() >= 1);
        var myWork = await client.GetFromJsonAsync<JsonElement>($"/api/my-work?tenantId={tenant}&personId={head1}");
        Assert.True(myWork.GetProperty("counts").GetProperty("openTasks").GetInt32() >= 1);
    }

    [Fact]
    public async Task Notifications_Template_ChannelMatrix_And_Rendering()
    {
        // Planner unit coverage (no broker needed)
        Assert.Equal([NotificationChannel.InApp],
            EduNexus.Api.Events.NotificationPlanner.ChannelsFor(EduNexus.Foundation.NotificationPriority.FYI));
        Assert.Equal(3, EduNexus.Api.Events.NotificationPlanner.ChannelsFor(EduNexus.Foundation.NotificationPriority.Urgent).Length);
        Assert.Equal("Hello Omar, code CORR-1",
            EduNexus.Api.Events.NotificationPlanner.Render("Hello {name}, code {code}",
                new Dictionary<string, string> { ["NAME"] = "Omar", ["code"] = "CORR-1" }));

        // Template CRUD gating
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), ["tenant:create", "notification:manage", "notification:read"]));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Ntf Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, ["notification:manage", "notification:read"]));
        var tpl = await client.PostAsJsonAsync("/api/notification-templates", new
        {
            tenantId = tenant, code = "review-requested", channel = "Email",
            subject = "Review", bodyTemplate = "Hi {name}, review {number}."
        });
        Assert.Equal(HttpStatusCode.Created, tpl.StatusCode);
        var dup = await client.PostAsJsonAsync("/api/notification-templates", new
        {
            tenantId = tenant, code = "review-requested", channel = "Email",
            subject = "Review", bodyTemplate = "x"
        });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var bad = await client.PostAsJsonAsync("/api/notification-templates", new
        {
            tenantId = tenant, code = "x", channel = "Pigeon", subject = "", bodyTemplate = "x"
        });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var list = await client.GetFromJsonAsync<JsonElement>($"/api/notification-templates?tenantId={tenant}");
        Assert.Equal(1, list.GetArrayLength());
        var receipts = await client.GetFromJsonAsync<JsonElement>($"/api/notification-receipts?tenantId={tenant}");
        Assert.Equal(0, receipts.GetArrayLength());
    }

    [Fact]
    public async Task PeopleImport_DryRun_Then_Import()
    {
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), ["tenant:create", "person:create", "person:read"]));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Import Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, ["person:create", "person:read"]));
        var csv = "fullName,email,type\nAmina Import,amina@x.local,Employee\nBad Row\nKarim Import,,Student\n";
        var dry = await client.PostAsync($"/api/people/import?tenantId={tenant}&dryRun=true",
            new StringContent(csv, System.Text.Encoding.UTF8, "text/csv"));
        Assert.Equal(HttpStatusCode.OK, dry.StatusCode);
        var dryBody = await dry.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, dryBody.GetProperty("valid").GetInt32());
        Assert.Equal(1, dryBody.GetProperty("errors").GetArrayLength());
        var imp = await client.PostAsync($"/api/people/import?tenantId={tenant}&dryRun=false",
            new StringContent(csv, System.Text.Encoding.UTF8, "text/csv"));
        Assert.Equal(HttpStatusCode.OK, imp.StatusCode);
        var people = await client.GetFromJsonAsync<JsonElement>($"/api/people?tenantId={tenant}&q=Import");
        Assert.Equal(2, people.GetArrayLength());
    }

    [Fact]
    public async Task Intelligence_Documents_Search_Analytics_Quality_Strategy()
    {
        var perms = AllPerms.Concat(["correspondence:create", "document:create", "document:read",
            "search:read", "analytics:read", "quality:manage", "strategy:manage"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Intel Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        // documents: create → presigned upload URL (MinIO) → version → publish
        var doc = IdOf(await (await client.PostAsJsonAsync("/api/documents",
            new { tenantId = tenant, title = "Budget policy 2026" })).Content.ReadFromJsonAsync<JsonElement>());
        var urlRes = await client.PostAsJsonAsync($"/api/documents/{doc}/upload-url",
            new { tenantId = tenant, fileName = "budget.pdf" });
        Assert.Equal(HttpStatusCode.OK, urlRes.StatusCode);
        var upload = await urlRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("edunexus-docs", upload.GetProperty("putUrl").GetString());
        var ver = await client.PostAsJsonAsync($"/api/documents/{doc}/versions",
            new { tenantId = tenant, objectKey = upload.GetProperty("objectKey").GetString(), sizeBytes = 1234, sha256 = "abc", publish = true });
        Assert.Equal(HttpStatusCode.Created, ver.StatusCode);

        // correspondence for search + analytics signal
        var person = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Layla Intel", email = (string?)null })).Content.ReadFromJsonAsync<JsonElement>());
        var corr = await client.PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = tenant, type = "Internal", subject = "Budget policy review", content = "Review the budget policy.",
            authorId = person, priority = "Normal", isConfidential = false, recipients = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, corr.StatusCode);

        // unified search finds it in two entity kinds
        var search = await client.GetFromJsonAsync<JsonElement>($"/api/search?tenantId={tenant}&q=Budget");
        Assert.True(search.GetProperty("correspondence").GetArrayLength() >= 1);
        Assert.True(search.GetProperty("documents").GetArrayLength() >= 1);
        var tooShort = await client.GetAsync($"/api/search?tenantId={tenant}&q=x");
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        // analytics overview reflects the data
        var analytics = await client.GetFromJsonAsync<JsonElement>($"/api/analytics/overview?tenantId={tenant}");
        Assert.True(analytics.GetProperty("activePeople").GetInt32() >= 1);

        // accreditation: standard → criterion → evidence(link to correspondence) → finding → corrective action
        var std = IdOf(await (await client.PostAsJsonAsync("/api/quality/standards",
            new { tenantId = tenant, code = "STD-1", title = "Governance" })).Content.ReadFromJsonAsync<JsonElement>());
        var crit = IdOf(await (await client.PostAsJsonAsync("/api/quality/criteria",
            new { tenantId = tenant, standardId = std, code = "C1", text = "Decisions documented." })).Content.ReadFromJsonAsync<JsonElement>());
        var corrId = (await corr.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var ev = await client.PostAsJsonAsync("/api/quality/evidence",
            new { tenantId = tenant, criterionId = crit, entityType = "Correspondence", entityId = corrId, note = "Budget memo" });
        Assert.Equal(HttpStatusCode.Created, ev.StatusCode);
        var finding = IdOf(await (await client.PostAsJsonAsync("/api/quality/findings",
            new { tenantId = tenant, criterionId = crit, severity = "Minor", text = "Minutes missing." })).Content.ReadFromJsonAsync<JsonElement>());
        var ca = await client.PostAsJsonAsync("/api/quality/corrective-actions",
            new { tenantId = tenant, findingId = finding, assigneeId = person, description = "Attach minutes.", dueAt = (DateTimeOffset?)null });
        Assert.Equal(HttpStatusCode.Created, ca.StatusCode);

        // strategy: plan → objective → KPI → reading
        var plan = IdOf(await (await client.PostAsJsonAsync("/api/strategy/plans",
            new { tenantId = tenant, title = "Strategy 2030", yearFrom = 2026, yearTo = 2030 })).Content.ReadFromJsonAsync<JsonElement>());
        var obj = IdOf(await (await client.PostAsJsonAsync("/api/strategy/objectives",
            new { tenantId = tenant, planId = plan, code = "O1", text = "Digital-first." })).Content.ReadFromJsonAsync<JsonElement>());
        var kpi = IdOf(await (await client.PostAsJsonAsync("/api/strategy/kpis",
            new { tenantId = tenant, objectiveId = obj, name = "Online services %", target = 90.0, current = 40.0, unit = "%" })).Content.ReadFromJsonAsync<JsonElement>());
        var reading = await client.PostAsJsonAsync($"/api/strategy/kpis/{kpi}/reading",
            new { tenantId = tenant, current = 55.0 });
        Assert.Equal(HttpStatusCode.OK, reading.StatusCode);
        Assert.Equal(55.0, (await reading.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("current").GetDouble());
    }

    [Fact]
    public async Task Ai_AskEcho_LogsInteraction()
    {
        // Requires OpenSearch on 127.0.0.1:9201 (edunexus-opensearch).
        var perms = AllPerms.Concat(["correspondence:create", "ai:manage", "ai:ask", "ai:read"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "AI Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));
        var person = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Noor AI", email = (string?)null })).Content.ReadFromJsonAsync<JsonElement>());
        var corr = await client.PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = tenant, type = "Internal", subject = "Campus shuttle schedule", content = "Shuttle runs hourly.",
            authorId = person, priority = "Normal", isConfidential = false, recipients = Array.Empty<object>(),
        });
        Assert.Equal(HttpStatusCode.Created, corr.StatusCode);

        var index = await client.PostAsync($"/api/ai/index?tenantId={tenant}", null);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);

        var ask = await client.PostAsJsonAsync("/api/ai/ask",
            new { tenantId = tenant, question = "shuttle schedule", capability = (string?)null });
        Assert.Equal(HttpStatusCode.OK, ask.StatusCode);
        var answer = await ask.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("echo-extractive-v1", answer.GetProperty("model").GetString());
        Assert.Contains("shuttle", answer.GetProperty("answer").GetString(), StringComparison.OrdinalIgnoreCase);

        var interactions = await client.GetFromJsonAsync<JsonElement>($"/api/ai/interactions?tenantId={tenant}");
        Assert.Equal(1, interactions.GetArrayLength());
        Assert.Equal("ask", interactions[0].GetProperty("capability").GetString());
    }

    [Fact]
    public async Task Integrations_Webhook_FailedDelivery_Logged()
    {
        // Requires RabbitMQ on localhost:5673. Unreachable target → Failed delivery row (no poison).
        var perms = AllPerms.Concat(["integration:manage", "integration:read"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Hook Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));
        var person = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Hadi Hook", email = (string?)null })).Content.ReadFromJsonAsync<JsonElement>());
        var role = await client.PostAsJsonAsync("/api/roles",
            new { tenantId = tenant, code = "hook.role", name = "Hook", permissions = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Created, role.StatusCode);

        var reg = await client.PostAsJsonAsync("/api/integrations/endpoints",
            new { tenantId = tenant, eventType = "RoleAssigned", targetUrl = "http://127.0.0.1:9/hook", secret = "s3cr3t" });
        Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        var badUrl = await client.PostAsJsonAsync("/api/integrations/endpoints",
            new { tenantId = tenant, eventType = "RoleAssigned", targetUrl = "not-a-url", secret = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);

        var assign = await client.PostAsJsonAsync("/api/roles/assign",
            new { tenantId = tenant, personId = person, roleCode = "hook.role" });
        Assert.Equal(HttpStatusCode.Created, assign.StatusCode);

        // relay (2s) → dispatcher (1s) → failed POST → delivery row (poll ≤ 30s)
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var deliveries = await client.GetFromJsonAsync<JsonElement>($"/api/integrations/deliveries?tenantId={tenant}");
            if (deliveries.GetArrayLength() > 0)
            {
                Assert.Equal("Failed", deliveries[0].GetProperty("status").GetString());
                return;
            }
            await Task.Delay(500);
        }
        Assert.Fail("Integration delivery not recorded within 30s");
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

    [Fact]
    public async Task Depth_RequestChanges_Delegate_TaskEnrichment()
    {
        var perms = AllPerms.Concat(["request:create", "request:read", "approval:read", "approval:decide",
            "task:read", "task:update"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Depth Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));
        async Task<Guid> MkPerson(string name)
        {
            var r = await client.PostAsJsonAsync("/api/people",
                new { tenantId = tenant, type = "Employee", fullName = name, email = (string?)null });
            Assert.Equal(HttpStatusCode.Created, r.StatusCode);
            return IdOf(await r.Content.ReadFromJsonAsync<JsonElement>());
        }
        var submitter = await MkPerson("Depth Submitter");
        var reviewer = await MkPerson("Depth Reviewer");
        var deputy = await MkPerson("Depth Deputy");

        // request → submit with reviewer → request-changes returns it for revision
        var req = await client.PostAsJsonAsync("/api/requests", new
        {
            tenantId = tenant, category = "IT", title = "VPN access", submitterId = submitter,
            formId = (Guid?)null, dataJson = (string?)null,
        });
        var reqId = IdOf(await req.Content.ReadFromJsonAsync<JsonElement>());
        var sub = await client.PostAsJsonAsync($"/api/requests/{reqId}/submit",
            new { tenantId = tenant, reviewerId = reviewer, workflowCode = (string?)null });
        var approvalId = (await sub.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetGuid();
        var rc = await ClientAs(tenant, perms, reviewer).PostAsJsonAsync($"/api/approvals/{approvalId}/request-changes",
            new { tenantId = tenant, decidedBy = reviewer, comment = "Add business justification" });
        Assert.Equal(HttpStatusCode.OK, rc.StatusCode);
        var reqs = await client.GetFromJsonAsync<JsonElement>($"/api/requests?tenantId={tenant}&status=ChangesRequested");
        Assert.Equal(1, reqs.GetArrayLength());

        // delegate moves approval + task to the deputy
        var req2 = await client.PostAsJsonAsync("/api/requests", new
        {
            tenantId = tenant, category = "IT", title = "Monitor", submitterId = submitter,
            formId = (Guid?)null, dataJson = (string?)null,
        });
        var req2Id = IdOf(await req2.Content.ReadFromJsonAsync<JsonElement>());
        var sub2 = await client.PostAsJsonAsync($"/api/requests/{req2Id}/submit",
            new { tenantId = tenant, reviewerId = reviewer, workflowCode = (string?)null });
        var approval2 = (await sub2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetGuid();
        var del = await ClientAs(tenant, perms, reviewer).PostAsJsonAsync($"/api/approvals/{approval2}/delegate",
            new { tenantId = tenant, delegatedBy = reviewer, delegateTo = deputy });
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal(deputy, (await del.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("assigneeId").GetGuid());
        var depTasks = await client.GetFromJsonAsync<JsonElement>($"/api/tasks?tenantId={tenant}&assigneeId={deputy}");
        Assert.True(depTasks.GetArrayLength() >= 1);
        var taskId = depTasks[0].GetProperty("id").GetGuid();

        // task enrichment: patch → comment → evidence → complete
        var patch = await client.PatchAsJsonAsync($"/api/tasks/{taskId}",
            new { tenantId = tenant, description = "Install and configure", priority = "High", progress = 40 });
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var badProgress = await client.PatchAsJsonAsync($"/api/tasks/{taskId}",
            new { tenantId = tenant, progress = 150 });
        Assert.Equal(HttpStatusCode.BadRequest, badProgress.StatusCode);
        var comment = await client.PostAsJsonAsync($"/api/tasks/{taskId}/comments",
            new { tenantId = tenant, authorId = deputy, text = "Started installation" });
        Assert.Equal(HttpStatusCode.Created, comment.StatusCode);
        var ev = await client.PostAsJsonAsync($"/api/tasks/{taskId}/evidence",
            new { tenantId = tenant, uploadedBy = deputy, objectKey = "t/key.pdf", fileName = "key.pdf" });
        Assert.Equal(HttpStatusCode.Created, ev.StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}?tenantId={tenant}");
        Assert.Equal("Install and configure", detail.GetProperty("description").GetString());
        Assert.Equal(40, detail.GetProperty("progress").GetInt32());
        var comments = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/comments?tenantId={tenant}");
        Assert.Equal(1, comments.GetArrayLength());
        var evidences = await client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/evidence?tenantId={tenant}");
        Assert.Equal(1, evidences.GetArrayLength());
        var done = await client.PostAsJsonAsync($"/api/tasks/{taskId}/complete", new { tenantId = tenant });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }

    [Fact]
    public async Task Documents_Share_Classify_Retain()
    {
        var perms = AllPerms.Concat(["document:create", "document:read", "document:update", "document:share"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Doc Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));
        var author = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Doc Author", email = (string?)null })).Content.ReadFromJsonAsync<JsonElement>());

        // Create document
        var doc = await client.PostAsJsonAsync("/api/documents", new { tenantId = tenant, title = "Policy Draft" });
        Assert.Equal(HttpStatusCode.Created, doc.StatusCode);
        var docId = IdOf(await doc.Content.ReadFromJsonAsync<JsonElement>());
        var docDetail = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}?tenantId={tenant}");
        Assert.Equal("Internal", docDetail.GetProperty("classification").GetString());

        // Share document
        var person2 = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Reader", email = (string?)null })).Content.ReadFromJsonAsync<JsonElement>());
        var share = await client.PostAsJsonAsync($"/api/documents/{docId}/share", new { tenantId = tenant, personId = person2, expiresAt = DateTimeOffset.UtcNow.AddDays(7) });
        Assert.Equal(HttpStatusCode.Created, share.StatusCode);
        var shares = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/shares?tenantId={tenant}");
        Assert.Equal(1, shares.GetArrayLength());

        // Classify and set retention
        var update = await client.PatchAsJsonAsync($"/api/documents/{docId}?tenantId={tenant}", new { tenantId = tenant, classification = "Confidential", retainUntil = DateTimeOffset.UtcNow.AddYears(7) });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        var updated = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}?tenantId={tenant}");
        Assert.Equal("Confidential", updated.GetProperty("classification").GetString());
        Assert.True(updated.TryGetProperty("retainUntil", out _));

        // List shares
        var sharesList = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/shares?tenantId={tenant}");
        Assert.Equal(1, sharesList.GetArrayLength());
    }

    [Fact]
    public async Task Chatter_Comment_And_Follow_FullFlow()
    {
        var perms = AllPerms.Concat(["correspondence:create", "correspondence:read", "chatter:read", "chatter:write"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Chatter Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        var author = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Staff Member", email = (string?)null })).Content.ReadFromJsonAsync<JsonElement>());

        var corrRes = await client.PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = tenant,
            type = "Internal",
            subject = "Council Inquiry",
            content = "Please advise on senate quorum",
            authorId = author,
            priority = "Normal",
            isConfidential = false,
        });
        Assert.Equal(HttpStatusCode.Created, corrRes.StatusCode);
        var corrId = IdOf(await corrRes.Content.ReadFromJsonAsync<JsonElement>());

        // 1. Post internal note (staff-only)
        var internalNote = await client.PostAsJsonAsync("/api/chatter/comments", new
        {
            tenantId = tenant,
            entityType = "Correspondence",
            entityId = corrId,
            authorId = author,
            content = "Note for legal review: check bylaws section 4",
            isInternalOnly = true,
        });
        Assert.Equal(HttpStatusCode.Created, internalNote.StatusCode);

        // 2. Post public message
        var publicMsg = await client.PostAsJsonAsync("/api/chatter/comments", new
        {
            tenantId = tenant,
            entityType = "Correspondence",
            entityId = corrId,
            authorId = author,
            content = "Quorum requires 50% + 1 active voting members.",
            isInternalOnly = false,
        });
        Assert.Equal(HttpStatusCode.Created, publicMsg.StatusCode);

        // 3. List comments
        var comments = await client.GetFromJsonAsync<JsonElement>(
            $"/api/chatter/comments?tenantId={tenant}&entityType=Correspondence&entityId={corrId}");
        Assert.Equal(2, comments.GetArrayLength());
        Assert.True(comments[0].GetProperty("isInternalOnly").GetBoolean());
        Assert.False(comments[1].GetProperty("isInternalOnly").GetBoolean());

        // 4. Follow entity
        var followRes = await client.PostAsJsonAsync("/api/chatter/follow", new
        {
            tenantId = tenant,
            entityType = "Correspondence",
            entityId = corrId,
            personId = author,
        });
        Assert.Equal(HttpStatusCode.Created, followRes.StatusCode);

        var followers = await client.GetFromJsonAsync<JsonElement>(
            $"/api/chatter/followers?tenantId={tenant}&entityType=Correspondence&entityId={corrId}");
        Assert.Equal(1, followers.GetArrayLength());
        Assert.Equal(author, followers[0].GetProperty("personId").GetGuid());
    }

    [Fact]
    public async Task Activities_Schedule_Complete_And_MyWork_Flow()
    {
        var perms = AllPerms.Concat(["correspondence:create", "inbox:read", "activity:read", "activity:write"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Activity Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        var assignee = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Reviewing Dean", email = (string?)null })).Content.ReadFromJsonAsync<JsonElement>());
        var fakeEntityId = Guid.NewGuid();

        // 1. Schedule "Review" activity
        var act1Res = await client.PostAsJsonAsync("/api/activities", new
        {
            tenantId = tenant,
            entityType = "Request",
            entityId = fakeEntityId,
            type = "Review",
            assigneeId = assignee,
            summary = "Review accreditation syllabus submission",
            dueDate = DateTimeOffset.UtcNow.AddDays(2),
        });
        Assert.Equal(HttpStatusCode.Created, act1Res.StatusCode);
        var act1Id = IdOf(await act1Res.Content.ReadFromJsonAsync<JsonElement>());

        // 2. Schedule "Sign" activity
        var act2Res = await client.PostAsJsonAsync("/api/activities", new
        {
            tenantId = tenant,
            entityType = "Request",
            entityId = fakeEntityId,
            type = "Sign",
            assigneeId = assignee,
            summary = "Countersign faculty agreement",
            dueDate = DateTimeOffset.UtcNow.AddDays(4),
        });
        Assert.Equal(HttpStatusCode.Created, act2Res.StatusCode);

        // 3. Query My Activities
        var myActs = await client.GetFromJsonAsync<JsonElement>(
            $"/api/activities/my?tenantId={tenant}&personId={assignee}&completed=false");
        Assert.Equal(2, myActs.GetArrayLength());

        // 4. Complete first activity
        var completeRes = await client.PatchAsJsonAsync($"/api/activities/{act1Id}/complete", new { tenantId = tenant });
        Assert.Equal(HttpStatusCode.OK, completeRes.StatusCode);
        Assert.True((await completeRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isCompleted").GetBoolean());

        // 5. Verify MyWork counter reflects pending activities
        var myWorkRes = await client.GetFromJsonAsync<JsonElement>($"/api/my-work?tenantId={tenant}&personId={assignee}");
        Assert.Equal(1, myWorkRes.GetProperty("counts").GetProperty("pendingActivities").GetInt32());
    }

    [Fact]
    public async Task Hierarchy_Workflow_SubmitterHead_Resolution()
    {
        var perms = AllPerms.Concat(["request:create", "workflow:manage", "workflow:read", "approval:read", "approval:decide"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Hierarchy Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        // Create Department Head
        var deptHead = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Prof. Department Head", email = (string?)null, academicRank = "Professor" })).Content.ReadFromJsonAsync<JsonElement>());

        // Create Organizational Unit with deptHead as LeaderPersonId
        var unitRes = await client.PostAsJsonAsync("/api/organizational-units", new
        {
            tenantId = tenant,
            code = "CS-DEPT",
            name = "Computer Science Department",
            leaderPersonId = deptHead,
        });
        Assert.Equal(HttpStatusCode.Created, unitRes.StatusCode);
        var unitId = IdOf(await unitRes.Content.ReadFromJsonAsync<JsonElement>());

        // Create Faculty submitter assigned to this department
        var faculty = IdOf(await (await client.PostAsJsonAsync("/api/people",
            new { tenantId = tenant, type = "Employee", fullName = "Dr. Alice Faculty", email = (string?)null, departmentId = unitId, academicRank = "AssistantProfessor" })).Content.ReadFromJsonAsync<JsonElement>());

        // Create Workflow definition with assigneeFrom: "submitter_head"
        var nodesJson = """
        [
          { "id": "start", "type": "start" },
          { "id": "head_approval", "type": "approval", "assigneeFrom": "submitter_head", "slaDays": 3 },
          { "id": "end", "type": "end" }
        ]
        """;
        var wfRes = await client.PostAsJsonAsync("/api/workflows/definitions", new
        {
            tenantId = tenant,
            code = "FACULTY_RESEARCH_REQ",
            name = "Faculty Research Request",
            nodesJson,
        });
        Assert.Equal(HttpStatusCode.Created, wfRes.StatusCode);

        // Submit request triggering workflow
        var reqRes = await client.PostAsJsonAsync("/api/requests", new
        {
            tenantId = tenant,
            category = "Academic",
            title = "Conference Travel Grant",
            submitterId = faculty,
        });
        Assert.Equal(HttpStatusCode.Created, reqRes.StatusCode);
        var reqId = IdOf(await reqRes.Content.ReadFromJsonAsync<JsonElement>());

        var submitRes = await client.PostAsJsonAsync($"/api/requests/{reqId}/submit", new
        {
            tenantId = tenant,
            workflowCode = "FACULTY_RESEARCH_REQ",
        });
        Assert.Equal(HttpStatusCode.OK, submitRes.StatusCode);

        // Verify the approval was automatically assigned to the Department Head!
        var approvalId = (await submitRes.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("approvalId").GetGuid();
        var approvalDetail = await client.GetFromJsonAsync<JsonElement>($"/api/approvals/{approvalId}?tenantId={tenant}");
        Assert.Equal(deptHead, approvalDetail.GetProperty("assigneeId").GetGuid());
    }

    [Fact]
    public async Task Document_Workspaces_And_Tags_Flow()
    {
        var perms = AllPerms.Concat(["document:create", "document:read", "document:write", "document:manage"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Doc Uni 2" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        // 1. Create Workspace
        var wsRes = await client.PostAsJsonAsync("/api/document-workspaces", new
        {
            tenantId = tenant,
            code = "SENATE_PACKETS",
            name = "University Senate Packets",
            description = "Official council and senate packets",
        });
        Assert.Equal(HttpStatusCode.Created, wsRes.StatusCode);

        var wsList = await client.GetFromJsonAsync<JsonElement>($"/api/document-workspaces?tenantId={tenant}");
        Assert.True(wsList.GetArrayLength() >= 1);

        // 2. Create Document and Tag it
        var docRes = await client.PostAsJsonAsync("/api/documents", new { tenantId = tenant, title = "Senate Docket 2026-Q1" });
        var docId = IdOf(await docRes.Content.ReadFromJsonAsync<JsonElement>());

        var tagRes = await client.PostAsJsonAsync($"/api/documents/{docId}/tags", new
        {
            tenantId = tenant,
            category = "AcademicYear",
            value = "2026-2027",
        });
        Assert.Equal(HttpStatusCode.Created, tagRes.StatusCode);

        var tags = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/tags?tenantId={tenant}");
        Assert.Equal(1, tags.GetArrayLength());
        Assert.Equal("AcademicYear", tags[0].GetProperty("tagCategory").GetString());
        Assert.Equal("2026-2027", tags[0].GetProperty("tagValue").GetString());
    }

    [Fact]
    public async Task Convene_Meeting_Quorum_And_Voting_Flow()
    {
        var perms = AllPerms.Concat(["meeting:create", "meeting:read", "committee:create", "decision:create"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Senate Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        // Create 3 people for committee
        var p1 = IdOf(await (await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "President Alpha" })).Content.ReadFromJsonAsync<JsonElement>());
        var p2 = IdOf(await (await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Dean Beta" })).Content.ReadFromJsonAsync<JsonElement>());
        var p3 = IdOf(await (await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Dean Gamma" })).Content.ReadFromJsonAsync<JsonElement>());

        var comRes = await client.PostAsJsonAsync("/api/committees", new { tenantId = tenant, code = "SENATE", name = "Academic Senate" });
        var comId = IdOf(await comRes.Content.ReadFromJsonAsync<JsonElement>());
        await client.PostAsJsonAsync($"/api/committees/{comId}/members", new { tenantId = tenant, personId = p1, role = "Chair" });
        await client.PostAsJsonAsync($"/api/committees/{comId}/members", new { tenantId = tenant, personId = p2, role = "Member" });
        await client.PostAsJsonAsync($"/api/committees/{comId}/members", new { tenantId = tenant, personId = p3, role = "Member" });

        var meetingRes = await client.PostAsJsonAsync("/api/meetings", new
        {
            tenantId = tenant,
            committeeId = comId,
            title = "Academic Senate Session 42",
            startsAt = DateTimeOffset.UtcNow.AddDays(1),
            agenda = new[]
            {
                new { title = "Approval of New Curriculum", description = "CS 2026 update" }
            }
        });
        var meetingId = IdOf(await meetingRes.Content.ReadFromJsonAsync<JsonElement>());

        // Attendance: 2 present, 1 absent -> Quorum (2 of 3) achieved!
        await client.PostAsJsonAsync($"/api/meetings/{meetingId}/attendance", new { tenantId = tenant, personId = p1, status = "Present" });
        await client.PostAsJsonAsync($"/api/meetings/{meetingId}/attendance", new { tenantId = tenant, personId = p2, status = "Present" });
        await client.PostAsJsonAsync($"/api/meetings/{meetingId}/attendance", new { tenantId = tenant, personId = p3, status = "Absent" });

        var quorum = await client.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetingId}/quorum?tenantId={tenant}");
        Assert.Equal(3, quorum.GetProperty("totalMembers").GetInt32());
        Assert.Equal(2, quorum.GetProperty("presentCount").GetInt32());
        Assert.True(quorum.GetProperty("hasQuorum").GetBoolean());

        // Get agenda items to vote on
        var agendaList = await client.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetingId}?tenantId={tenant}");
        // Cast votes
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var agendaItem = await db.AgendaItems.FirstAsync(a => a.MeetingId == meetingId);

        var voteRes1 = await client.PostAsJsonAsync($"/api/meetings/{meetingId}/votes", new
        {
            tenantId = tenant,
            agendaItemId = agendaItem.Id,
            personId = p1,
            choice = "InFavor",
            remarks = "Endorsed with minor amendment"
        });
        Assert.Equal(HttpStatusCode.OK, voteRes1.StatusCode);

        var voteRes2 = await client.PostAsJsonAsync($"/api/meetings/{meetingId}/votes", new
        {
            tenantId = tenant,
            agendaItemId = agendaItem.Id,
            personId = p2,
            choice = "InFavor"
        });
        Assert.Equal(HttpStatusCode.OK, voteRes2.StatusCode);

        var votesSummary = await client.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetingId}/votes?tenantId={tenant}");
        Assert.Equal(2, votesSummary.GetProperty("votes").GetArrayLength());
        var tallies = votesSummary.GetProperty("tallies");
        Assert.Equal(1, tallies.GetArrayLength());
        Assert.Equal(2, tallies[0].GetProperty("inFavor").GetInt32());
        Assert.Equal(0, tallies[0].GetProperty("against").GetInt32());
    }

    [Fact]
    public async Task Odoo_Correspondence_Threading_And_RoutingSlips_Flow()
    {
        var perms = AllPerms.Concat(["correspondence:create", "correspondence:read"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Gov Ministry Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        var minister = IdOf(await (await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Ministry Liaison" })).Content.ReadFromJsonAsync<JsonElement>());
        var dean = IdOf(await (await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Dean of Science" })).Content.ReadFromJsonAsync<JsonElement>());

        // 1. Incoming Letter from Ministry
        var parentRes = await client.PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = tenant,
            type = "Incoming",
            subject = "Ministry Inquiry: Annual Lab Safety Compliance",
            content = "Please provide annual lab safety audit results.",
            authorId = minister,
            priority = "High",
            isConfidential = false
        });
        Assert.Equal(HttpStatusCode.Created, parentRes.StatusCode);
        var parentId = IdOf(await parentRes.Content.ReadFromJsonAsync<JsonElement>());

        // 2. Executive appends Routing Slip (Tashira)
        var slipRes = await client.PostAsJsonAsync($"/api/correspondence/{parentId}/routing-slips", new
        {
            tenantId = tenant,
            fromPersonId = minister,
            toPersonId = dean,
            actionRequired = "DraftOfficialReply",
            instructions = "Compile science college lab inspection reports and prepare response letter.",
            dueAt = DateTimeOffset.UtcNow.AddDays(5)
        });
        Assert.Equal(HttpStatusCode.Created, slipRes.StatusCode);
        var slipId = IdOf(await slipRes.Content.ReadFromJsonAsync<JsonElement>());

        var slips = await client.GetFromJsonAsync<JsonElement>($"/api/correspondence/{parentId}/routing-slips?tenantId={tenant}");
        Assert.Equal(1, slips.GetArrayLength());
        Assert.Equal("DraftOfficialReply", slips[0].GetProperty("actionRequired").GetString());

        // Complete the routing slip
        var completeRes = await client.PostAsJsonAsync($"/api/correspondence/{parentId}/routing-slips/{slipId}/complete?tenantId={tenant}", new { });
        Assert.Equal(HttpStatusCode.OK, completeRes.StatusCode);

        // 3. Create Outgoing Response Letter linked to parent
        var replyRes = await client.PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = tenant,
            type = "Outgoing",
            subject = "Response to Lab Safety Compliance Inquiry",
            content = "Enclosed are the certified inspection logs for 2026.",
            authorId = dean,
            priority = "Normal",
            isConfidential = false,
            parentCorrespondenceId = parentId
        });
        Assert.Equal(HttpStatusCode.Created, replyRes.StatusCode);
        var replyId = IdOf(await replyRes.Content.ReadFromJsonAsync<JsonElement>());

        // 4. Verify thread linkage
        var thread = await client.GetFromJsonAsync<JsonElement>($"/api/correspondence/{replyId}/thread?tenantId={tenant}");
        Assert.Equal(parentId, thread.GetProperty("root").GetProperty("id").GetGuid());
        Assert.Equal(replyId, thread.GetProperty("current").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Folderit_Document_Retention_Policy_And_Audit_Flow()
    {
        var perms = AllPerms.Concat(["document:create", "document:read", "document:update"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "ISO Compliance Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        var auditor = IdOf(await (await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Quality Lead" })).Content.ReadFromJsonAsync<JsonElement>());

        var docRes = await client.PostAsJsonAsync("/api/documents", new { tenantId = tenant, title = "ISO-9001 Quality Manual 2026" });
        var docId = IdOf(await docRes.Content.ReadFromJsonAsync<JsonElement>());

        // Set ISO Retention Policy
        var policyRes = await client.PostAsJsonAsync($"/api/documents/{docId}/retention-policy", new
        {
            tenantId = tenant,
            standard = "ISO 9001:2015",
            retentionPeriodMonths = 120, // 10 years
            dispositionAction = "PermanentPreservation",
            reviewIntervalMonths = 12,
            reviewedByPersonId = auditor,
            notes = "Accreditation master document, retained indefinitely under ISO clause 7.5"
        });
        Assert.Equal(HttpStatusCode.OK, policyRes.StatusCode);
        var policyJson = await policyRes.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ISO 9001:2015", policyJson.GetProperty("standard").GetString());
        Assert.Equal("PermanentPreservation", policyJson.GetProperty("dispositionAction").GetString());

        // Fetch policy
        var fetched = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/retention-policy?tenantId={tenant}");
        Assert.Equal("ISO 9001:2015", fetched.GetProperty("standard").GetString());
    }

    [Fact]
    public async Task Standing_Authority_Delegation_And_Workflow_Rerouting_Flow()
    {
        var perms = AllPerms.Concat(["workflow:manage", "workflow:create", "workflow:read", "request:create", "request:read", "approval:read"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Delegation Governance Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        // 1. Create Delegator (Dean) and Deputy (Vice Dean)
        var dean = IdOf(await (await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Dean Al-Mansoor" })).Content.ReadFromJsonAsync<JsonElement>());
        var deputy = IdOf(await (await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Vice Dean Al-Husseini" })).Content.ReadFromJsonAsync<JsonElement>());

        // 2. Set standing authority delegation
        var delRes = await client.PostAsJsonAsync($"/api/people/{dean}/delegations", new
        {
            tenantId = tenant,
            fromPersonId = dean,
            toPersonId = deputy,
            scope = "Workflow;Approval",
            expiresAt = DateTimeOffset.UtcNow.AddDays(14)
        });
        Assert.Equal(HttpStatusCode.Created, delRes.StatusCode);
        var delegationJson = await delRes.Content.ReadFromJsonAsync<JsonElement>();
        var delId = IdOf(delegationJson);
        Assert.Equal(deputy, delegationJson.GetProperty("toPersonId").GetGuid());

        // 3. Verify delegations list
        var delegations = await client.GetFromJsonAsync<JsonElement>($"/api/people/{dean}/delegations?tenantId={tenant}");
        Assert.Equal(1, delegations.GetArrayLength());

        // 4. Create a workflow definition where step 1 is assigned to Dean
        var nodes = new object[]
        {
            new { id = "start", type = "start" },
            new { id = "dean_approval", type = "approval", personId = dean.ToString(), slaDays = 3 },
            new { id = "end", type = "end" }
        };
        var wfRes = await client.PostAsJsonAsync("/api/workflows/definitions", new
        {
            tenantId = tenant,
            code = "EXP_APP_01",
            name = "Expense Approval Chain",
            nodesJson = JsonSerializer.Serialize(nodes)
        });
        Assert.Equal(HttpStatusCode.Created, wfRes.StatusCode);

        // 5. Submit a request targeting the workflow
        var reqRes = await client.PostAsJsonAsync("/api/requests", new
        {
            tenantId = tenant,
            category = "Academic",
            title = "International Conference Leave",
            submitterId = deputy
        });
        Assert.Equal(HttpStatusCode.Created, reqRes.StatusCode);
        var reqId = IdOf(await reqRes.Content.ReadFromJsonAsync<JsonElement>());

        var submitRes = await client.PostAsJsonAsync($"/api/requests/{reqId}/submit", new
        {
            tenantId = tenant,
            workflowCode = "EXP_APP_01"
        });
        Assert.Equal(HttpStatusCode.OK, submitRes.StatusCode);

        // 6. Verify that the generated approval was re-routed to Deputy because of standing delegation!
        var approvals = await client.GetFromJsonAsync<JsonElement>($"/api/approvals?tenantId={tenant}&assigneeId={deputy}");
        Assert.True(approvals.GetArrayLength() > 0);
        Assert.Equal(deputy, approvals[0].GetProperty("assigneeId").GetGuid());

        // 7. Revoke delegation
        var deleteRes = await client.DeleteAsync($"/api/people/{dean}/delegations/{delId}?tenantId={tenant}");
        Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);
    }

    [Fact]
    public async Task AzeusConvene_Board_Packet_Dossier_Compilation_Flow()
    {
        var perms = AllPerms.Concat(["committee:create", "committee:read", "meeting:create", "meeting:read", "decision:create", "decision:read"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Azeus Board Governance Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        // Create committee & 3 members
        var m1 = IdOf(await (await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Trustee 1" })).Content.ReadFromJsonAsync<JsonElement>());
        var m2 = IdOf(await (await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Trustee 2" })).Content.ReadFromJsonAsync<JsonElement>());
        var m3 = IdOf(await (await client.PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Trustee 3" })).Content.ReadFromJsonAsync<JsonElement>());

        var cRes = await client.PostAsJsonAsync("/api/committees", new { tenantId = tenant, code = "BOARD", name = "Board of Trustees" });
        var cId = IdOf(await cRes.Content.ReadFromJsonAsync<JsonElement>());
        await client.PostAsJsonAsync($"/api/committees/{cId}/members", new { tenantId = tenant, personId = m1, role = "Chair" });
        await client.PostAsJsonAsync($"/api/committees/{cId}/members", new { tenantId = tenant, personId = m2, role = "Member" });
        await client.PostAsJsonAsync($"/api/committees/{cId}/members", new { tenantId = tenant, personId = m3, role = "Member" });

        // Create meeting with agenda
        var meetRes = await client.PostAsJsonAsync("/api/meetings", new
        {
            tenantId = tenant,
            committeeId = cId,
            title = "Q3 Annual Governance Review",
            startsAt = DateTimeOffset.UtcNow,
            agenda = new[]
            {
                new { title = "Budget Ratification 2027", description = "Vote on university expansion budget" },
                new { title = "Honorary Degrees", description = "Review nominated candidates" }
            }
        });
        Assert.Equal(HttpStatusCode.Created, meetRes.StatusCode);
        var meetId = IdOf(await meetRes.Content.ReadFromJsonAsync<JsonElement>());

        // Attendance: 2 Present, 1 Absent -> Quorum achieved (2/3)
        await client.PostAsJsonAsync($"/api/meetings/{meetId}/attendance", new { tenantId = tenant, personId = m1, status = "Present" });
        await client.PostAsJsonAsync($"/api/meetings/{meetId}/attendance", new { tenantId = tenant, personId = m2, status = "Present" });
        await client.PostAsJsonAsync($"/api/meetings/{meetId}/attendance", new { tenantId = tenant, personId = m3, status = "Absent" });

        // Get agenda item id from meeting
        var meetObj = await client.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetId}?tenantId={tenant}");
        var agendaList = meetObj.GetProperty("agenda");
        var item1Id = agendaList[0].GetProperty("id").GetGuid();

        // Cast votes
        await client.PostAsJsonAsync($"/api/meetings/{meetId}/votes", new { tenantId = tenant, agendaItemId = item1Id, personId = m1, choice = "InFavor", remarks = "Approved as budgeted" });
        await client.PostAsJsonAsync($"/api/meetings/{meetId}/votes", new { tenantId = tenant, agendaItemId = item1Id, personId = m2, choice = "InFavor" });

        // Conclude meeting
        await client.PostAsJsonAsync($"/api/meetings/{meetId}/conclude", new { tenantId = tenant, minutes = "Certified Board Minutes for Q3 Session." });

        // Fetch Convene Board Packet Dossier
        var packet = await client.GetFromJsonAsync<JsonElement>($"/api/meetings/{meetId}/packet?tenantId={tenant}");
        Assert.True(packet.GetProperty("governance").GetProperty("hasQuorum").GetBoolean());
        Assert.Equal(2, packet.GetProperty("governance").GetProperty("presentCount").GetInt32());
        Assert.Equal(3, packet.GetProperty("governance").GetProperty("totalMembers").GetInt32());
        Assert.Equal(2, packet.GetProperty("agenda").GetArrayLength());
        Assert.True(packet.GetProperty("certification").GetProperty("isConcluded").GetBoolean());
        Assert.Equal("Certified Board Minutes for Q3 Session.", packet.GetProperty("certification").GetProperty("minutes").GetString());

        var tallies = packet.GetProperty("votingTallies");
        Assert.Equal(1, tallies.GetArrayLength());
        Assert.Equal(2, tallies[0].GetProperty("inFavor").GetInt32());
    }

    [Fact]
    public async Task Odoo_Document_Action_Rule_AutoRetention_Trigger_Flow()
    {
        var perms = AllPerms.Concat(["document:create", "document:read", "document:update"]).ToArray();
        var bootstrap = factory.CreateClient();
        Auth(bootstrap, Mint(Guid.NewGuid(), perms));
        var tenant = IdOf(await (await bootstrap.PostAsJsonAsync("/api/tenants",
            new { slug = $"t-{Guid.NewGuid():N}", name = "Automated DMS Uni" })).Content.ReadFromJsonAsync<JsonElement>());
        var client = factory.CreateClient();
        Auth(client, Mint(tenant, perms));

        // 1. Create Automated Document Action Rule
        var config = JsonSerializer.Serialize(new
        {
            standard = "ISO-14001:2015",
            retentionMonths = 84,
            reviewIntervalMonths = 12,
            disposition = "PermanentPreservation"
        });

        var ruleRes = await client.PostAsJsonAsync("/api/documents/action-rules", new
        {
            tenantId = tenant,
            triggerCategory = "Tag",
            triggerValue = "EnvironmentalCompliance",
            actionType = "AutoRetention",
            targetValue = config
        });
        Assert.Equal(HttpStatusCode.Created, ruleRes.StatusCode);
        var ruleId = IdOf(await ruleRes.Content.ReadFromJsonAsync<JsonElement>());

        // 2. Fetch action rules
        var rules = await client.GetFromJsonAsync<JsonElement>($"/api/documents/action-rules?tenantId={tenant}");
        Assert.Equal(1, rules.GetArrayLength());

        // 3. Create document
        var docRes = await client.PostAsJsonAsync("/api/documents", new { tenantId = tenant, title = "2026 Campus Carbon Emission Audit" });
        var docId = IdOf(await docRes.Content.ReadFromJsonAsync<JsonElement>());

        // 4. Add matching tag to trigger the action rule
        var tagRes = await client.PostAsJsonAsync($"/api/documents/{docId}/tags", new
        {
            tenantId = tenant,
            category = "Tag",
            value = "EnvironmentalCompliance"
        });
        Assert.Equal(HttpStatusCode.Created, tagRes.StatusCode);

        // 5. Verify the retention policy was automatically created by the rule!
        var policy = await client.GetFromJsonAsync<JsonElement>($"/api/documents/{docId}/retention-policy?tenantId={tenant}");
        Assert.Equal("ISO-14001:2015", policy.GetProperty("standard").GetString());
        Assert.Equal("PermanentPreservation", policy.GetProperty("dispositionAction").GetString());
        Assert.Equal(84, policy.GetProperty("retentionPeriodMonths").GetInt32());

        // 6. Clean up rule
        var delRuleRes = await client.DeleteAsync($"/api/documents/action-rules/{ruleId}?tenantId={tenant}");
        Assert.Equal(HttpStatusCode.NoContent, delRuleRes.StatusCode);
    }
}

