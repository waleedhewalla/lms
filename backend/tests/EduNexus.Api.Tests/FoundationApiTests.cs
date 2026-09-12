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
}
