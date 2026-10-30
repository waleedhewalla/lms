using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EduNexus.Api.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace EduNexus.Api.Tests;

/// <summary>Correspondence registers: numbering per direction and year, the register and its export, copies, and confidential letters.</summary>
public sealed class CorrespondenceRegisterTests(EduNexusFactory factory) : IClassFixture<EduNexusFactory>
{
    private static readonly string[] Setup = ["tenant:create", "tenant:read", "person:create", "person:read"];
    private static readonly string[] Clerk = ["correspondence:create", "correspondence:read", "person:read", "person:create"];
    private static readonly string[] Cleared = [.. Clerk, "correspondence:confidential"];

    private HttpClient As(Guid tenant, IEnumerable<string> perms)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.Services.GetRequiredService<DevTokenService>().Mint("tests", tenant, perms));
        return c;
    }

    private async Task<(Guid Tenant, Guid Author)> NewTenantAsync()
    {
        var boot = As(Guid.NewGuid(), Setup);
        var t = await boot.PostAsJsonAsync("/api/tenants", new { slug = $"t-{Guid.NewGuid():N}", name = "Register Uni" });
        var tenant = (await t.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var p = await As(tenant, Setup).PostAsJsonAsync("/api/people", new { tenantId = tenant, type = "Employee", fullName = "Clerk", email = (string?)null });
        Assert.Equal(HttpStatusCode.Created, p.StatusCode);
        return (tenant, (await p.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    /// <summary>GET that must succeed; a failure shows the response body instead of a JSON parse error.</summary>
    private static async Task<JsonElement> GetJsonAsync(HttpClient c, string url)
    {
        var res = await c.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{url} -> {(int)res.StatusCode}: {body}");
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static async Task<JsonElement> CreateAsync(HttpClient c, Guid tenant, Guid author, string type, string subject, bool confidential = false,
        object[]? recipients = null, Guid? parent = null)
    {
        var r = await c.PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = tenant, type, subject, content = "Body.", authorId = author, priority = "Normal", isConfidential = confidential,
            recipients = recipients ?? [], parentCorrespondenceId = parent,
        });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return await r.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Numbers_Run_Per_Direction_And_Year_And_The_Register_Lists_Them_With_Copies()
    {
        var (tenant, author) = await NewTenantAsync();
        var c = As(tenant, Clerk);
        var year = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).Year;   // the test host's default institution offset

        var in1 = await CreateAsync(c, tenant, author, "Incoming", "Ministry circular");
        var in2 = await CreateAsync(c, tenant, author, "Incoming", "Second circular");
        var out1 = await CreateAsync(c, tenant, author, "Outgoing", "Reply to the Ministry", recipients:
            [new { personId = (string?)null, displayName = "Ministry of Education", isExternal = true, kind = "To" },
             new { personId = (string?)null, displayName = "Legal Office", isExternal = false, kind = "Cc" }]);
        var int1 = await CreateAsync(c, tenant, author, "Internal", "Budget memo");

        // Each direction has its own register, restarting at 1 each year.
        Assert.Equal($"IN-{year}-000001", in1.GetProperty("number").GetString());
        Assert.Equal($"IN-{year}-000002", in2.GetProperty("number").GetString());
        Assert.Equal($"OUT-{year}-000001", out1.GetProperty("number").GetString());
        Assert.Equal($"INT-{year}-000001", int1.GetProperty("number").GetString());

        var all = await GetJsonAsync(c, $"/api/correspondence/register?tenantId={tenant}&year={year}");
        Assert.Equal(4, all.GetProperty("total").GetInt32());
        Assert.DoesNotContain("content", all.GetRawText(), StringComparison.OrdinalIgnoreCase);   // the register never carries the letter body

        var incoming = await GetJsonAsync(c, $"/api/correspondence/register?tenantId={tenant}&year={year}&type=Incoming");
        Assert.Equal(2, incoming.GetProperty("total").GetInt32());

        var found = await GetJsonAsync(c, $"/api/correspondence/register?tenantId={tenant}&q=ministry");
        Assert.Equal(2, found.GetProperty("total").GetInt32());   // the circular and the reply, matched on subject
        var literal = await GetJsonAsync(c, $"/api/correspondence/register?tenantId={tenant}&q=%25");
        Assert.Equal(0, literal.GetProperty("total").GetInt32());   // "%" is searched literally, not as a wildcard

        var reply = found.GetProperty("items").EnumerateArray().First(i => i.GetProperty("number").GetString()!.StartsWith("OUT-"));
        var recipients = reply.GetProperty("recipients").EnumerateArray().Select(r => (r.GetProperty("displayName").GetString(), r.GetProperty("kind").GetString())).ToList();
        Assert.Contains(("Ministry of Education", "To"), recipients);
        Assert.Contains(("Legal Office", "Cc"), recipients);

        // A year with no letters, a bad filter, and a bad recipient kind.
        var other = await GetJsonAsync(c, $"/api/correspondence/register?tenantId={tenant}&year={year - 1}");
        Assert.Equal(0, other.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/correspondence/register?tenantId={tenant}&type=Sideways")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = tenant, type = "Outgoing", subject = "x", content = "y", authorId = author, isConfidential = false,
            recipients = new[] { new { personId = (string?)null, displayName = "Z", isExternal = true, kind = "Bcc" } },
        })).StatusCode);

        // Paging.
        var page = await GetJsonAsync(c, $"/api/correspondence/register?tenantId={tenant}&skip=3&take=10");
        Assert.Equal(4, page.GetProperty("total").GetInt32());
        Assert.Single(page.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Export_Is_A_Spreadsheet_With_Both_Calendars_And_Needs_Permission()
    {
        var (tenant, author) = await NewTenantAsync();
        var c = As(tenant, Clerk);
        await CreateAsync(c, tenant, author, "Outgoing", "=HYPERLINK(\"x\")", recipients: [new { personId = (string?)null, displayName = "Dean, Faculty", isExternal = false, kind = "Cc" }]);

        var res = await c.GetAsync($"/api/correspondence/register/export?tenantId={tenant}&calendar=both");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);
        var csv = await res.Content.ReadAsStringAsync();
        Assert.Contains("number,type,subject,status,priority,confidential,created_at,created_at_hijri,to,cc", csv);
        Assert.Contains("'=HYPERLINK", csv);          // formula-leading cells are neutralised
        Assert.Contains("\"Dean, Faculty\"", csv);    // commas are quoted

        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/api/correspondence/register/export?tenantId={tenant}&calendar=lunar")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(tenant, ["person:read"]).GetAsync($"/api/correspondence/register/export?tenantId={tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Guid.NewGuid(), Clerk).GetAsync($"/api/correspondence/register?tenantId={tenant}")).StatusCode);   // another tenant's token
    }

    [Fact]
    public async Task Confidential_Letters_Are_Hidden_From_Every_Route_Unless_The_Caller_Is_Cleared()
    {
        var (tenant, author) = await NewTenantAsync();
        var cleared = As(tenant, Cleared);
        var clerk = As(tenant, Clerk);

        var secret = await CreateAsync(cleared, tenant, author, "Internal", "Disciplinary matter", confidential: true);
        var secretId = secret.GetProperty("id").GetGuid();
        var reply = await CreateAsync(cleared, tenant, author, "Internal", "Re: Disciplinary matter", confidential: true, parent: secretId);
        var open = await CreateAsync(clerk, tenant, author, "Internal", "Open memo");
        var slip = await cleared.PostAsJsonAsync($"/api/correspondence/{secretId}/routing-slips",
            new { tenantId = tenant, fromPersonId = author, toPersonId = author, actionRequired = "StudyAndAdvise", instructions = "Handle quietly." });
        Assert.Equal(HttpStatusCode.Created, slip.StatusCode);
        var slipId = (await slip.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Cleared: everything is there.
        var seen = await GetJsonAsync(cleared, $"/api/correspondence/register?tenantId={tenant}");
        Assert.Equal(3, seen.GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await cleared.GetAsync($"/api/correspondence/{secretId}/thread?tenantId={tenant}")).StatusCode);

        // Not cleared: the register omits them and every route answers as if they did not exist.
        var hidden = await GetJsonAsync(clerk, $"/api/correspondence/register?tenantId={tenant}");
        Assert.Equal(1, hidden.GetProperty("total").GetInt32());
        Assert.Equal(open.GetProperty("number").GetString(), hidden.GetProperty("items")[0].GetProperty("number").GetString());
        Assert.DoesNotContain("Disciplinary", await clerk.GetStringAsync($"/api/correspondence/register/export?tenantId={tenant}"));

        foreach (var path in new[]
        {
            $"/api/correspondence/{secretId}",
            $"/api/correspondence/{secretId}/thread?tenantId={tenant}",
            $"/api/correspondence/{reply.GetProperty("id").GetGuid()}/thread?tenantId={tenant}",
            $"/api/correspondence/{secretId}/routing-slips?tenantId={tenant}",
        })
        {
            var url = path.Contains("tenantId") ? path : $"{path}?tenantId={tenant}";
            Assert.Equal(HttpStatusCode.NotFound, (await clerk.GetAsync(url)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.PostAsJsonAsync($"/api/correspondence/{secretId}/routing-slips",
            new { tenantId = tenant, fromPersonId = author, actionRequired = "ForInformation", instructions = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.PostAsync($"/api/correspondence/{secretId}/routing-slips/{slipId}/complete?tenantId={tenant}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clerk.PostAsJsonAsync($"/api/correspondence/{secretId}/submit", new { tenantId = tenant, reviewerId = author })).StatusCode);
        // A reply cannot be attached to a letter the caller cannot see (and so cannot learn that it exists).
        var attach = await clerk.PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = tenant, type = "Internal", subject = "Probe", content = "x", authorId = author, isConfidential = false, parentCorrespondenceId = secretId,
        });
        Assert.Equal(HttpStatusCode.NotFound, attach.StatusCode);

        // Where an open reply sits under a confidential parent, the caller sees the reply but the chain stops at their clearance.
        var openReply = await CreateAsync(cleared, tenant, author, "Internal", "Open reply", parent: secretId);
        var thread = await (await clerk.GetAsync($"/api/correspondence/{openReply.GetProperty("id").GetGuid()}/thread?tenantId={tenant}")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(openReply.GetProperty("id").GetGuid(), thread.GetProperty("root").GetProperty("id").GetGuid());
        Assert.DoesNotContain("Disciplinary", thread.GetRawText());
    }
}
