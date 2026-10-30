using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EduNexus.Api.Auth;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EduNexus.Api.Tests;

/// <summary>The catalogue of correspondence types: seeding, who sees and manages it, and what a typed letter must satisfy.</summary>
public sealed class CorrespondenceTypeTests(EduNexusFactory factory) : IClassFixture<EduNexusFactory>
{
    private static readonly string[] Setup = ["tenant:create", "tenant:read", "person:create", "person:read"];
    private static readonly string[] Staff = ["correspondence:create", "correspondence:read"];
    private static readonly string[] Manager = ["workflow:manage", "correspondence:read"];
    private static readonly string[] StudentOnly = ["correspondence:student-request"];

    private HttpClient As(Guid tenant, string[] perms, Guid? person = null)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.Services.GetRequiredService<DevTokenService>().Mint("tests", tenant, perms, personId: person));
        return c;
    }

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> work) { using var s = factory.Services.CreateScope(); return await work(s.ServiceProvider.GetRequiredService<AppDbContext>()); }

    private async Task<Guid> NewTenantAsync()
    {
        var t = await As(Guid.NewGuid(), Setup).PostAsJsonAsync("/api/tenants", new { slug = $"t-{Guid.NewGuid():N}", name = "Types Uni" });
        return (await t.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> NewPersonAsync(Guid tenant, PersonType type = PersonType.Employee)
    {
        var p = new Person(Guid.NewGuid(), tenant, type, type == PersonType.Student ? "Sara Student" : "Layla Staff", null, true, DateTimeOffset.UtcNow);
        await Db(async db => { db.People.Add(p); await db.SaveChangesAsync(); return 0; });
        return p.Id;
    }

    private async Task<Guid> NewUnitAsync(Guid tenant, string code)
    {
        var u = OrganizationalUnit.Create(tenant, code, $"Unit {code}");
        await Db(async db => { db.OrganizationalUnits.Add(u); await db.SaveChangesAsync(); return 0; });
        return u.Id;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private static object Letter(Guid tenant, Guid author, string? typeCode, Guid? unit = null, decimal? amount = null, bool confidential = false) => new
    {
        tenantId = tenant, type = "Internal", subject = "Subject", content = "Body", authorId = author, priority = "Normal", isConfidential = confidential,
        recipients = Array.Empty<object>(), typeCode, targetUnitId = unit, amount,
    };

    [Fact]
    public async Task Seeding_Creates_The_Nine_Standard_Types_Once_And_Keeps_Edits()
    {
        var tenant = await NewTenantAsync();
        var admin = As(tenant, Manager);

        var first = await Json(await admin.PostAsJsonAsync("/api/correspondence/types/seed-defaults", new { tenantId = tenant }));
        Assert.Equal(9, first.GetProperty("created").GetArrayLength());
        var types = await admin.GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={tenant}&includeInactive=true");
        Assert.Equal(9, types!.Length);
        Assert.Equal(["ADMIN_MEMO", "ACADEMIC", "FINANCIAL", "INTER_UNIT", "EMPLOYEE_REQUEST", "STUDENT_REQUEST", "DECISION", "CIRCULAR", "INFO_REQUEST"],
            types.Select(t => t.GetProperty("code").GetString()));
        Assert.All(types, t => { Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("nameAr").GetString())); Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("purpose").GetString())); });

        // The student request exists but is off until students can see only their own letters; the financial type needs an amount.
        var student = types.Single(t => t.GetProperty("code").GetString() == "STUDENT_REQUEST");
        Assert.False(student.GetProperty("isActive").GetBoolean());
        Assert.Equal("correspondence:student-request", student.GetProperty("createPermission").GetString());
        var financial = types.Single(t => t.GetProperty("code").GetString() == "FINANCIAL");
        Assert.True(financial.GetProperty("requiresAmount").GetBoolean());
        Assert.True(financial.GetProperty("requiresTargetUnit").GetBoolean());
        Assert.Equal("CORR_FINANCIAL", financial.GetProperty("workflowCode").GetString());

        // An edit survives a second seeding, which then creates nothing.
        var memo = types.Single(t => t.GetProperty("code").GetString() == "ADMIN_MEMO").GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/correspondence/types/{memo}", new { tenantId = tenant, nameAr = "مذكرة داخلية" })).StatusCode);
        var again = await Json(await admin.PostAsJsonAsync("/api/correspondence/types/seed-defaults", new { tenantId = tenant }));
        Assert.Equal(0, again.GetProperty("created").GetArrayLength());
        var renamed = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={tenant}"))!.Single(t => t.GetProperty("code").GetString() == "ADMIN_MEMO");
        Assert.Equal("مذكرة داخلية", renamed.GetProperty("nameAr").GetString());
        Assert.Equal("Administrative memo", renamed.GetProperty("nameEn").GetString());   // fields not sent are unchanged

        Assert.Equal(HttpStatusCode.Forbidden, (await As(tenant, Staff).PostAsJsonAsync("/api/correspondence/types/seed-defaults", new { tenantId = tenant })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Guid.NewGuid(), Manager).PostAsJsonAsync("/api/correspondence/types/seed-defaults", new { tenantId = tenant })).StatusCode);
    }

    [Fact]
    public async Task Who_Sees_Which_Types_Follows_The_Permission_Each_Type_Names()
    {
        var tenant = await NewTenantAsync();
        var admin = As(tenant, Manager);
        await admin.PostAsJsonAsync("/api/correspondence/types/seed-defaults", new { tenantId = tenant });

        var readerView = await As(tenant, Staff).GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={tenant}");
        Assert.Equal(8, readerView!.Length);                                                // the inactive student type is hidden
        var asked = await As(tenant, Staff).GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={tenant}&includeInactive=true");
        Assert.Equal(8, asked!.Length);                                                     // only a manager may ask for the inactive ones

        var studentClient = As(tenant, StudentOnly);
        Assert.Empty((await studentClient.GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={tenant}"))!);   // allowed to ask, but nothing is switched on for a student yet

        var student = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={tenant}&includeInactive=true"))!.Single(t => t.GetProperty("code").GetString() == "STUDENT_REQUEST");
        await admin.PatchAsJsonAsync($"/api/correspondence/types/{student.GetProperty("id").GetGuid()}", new { tenantId = tenant, isActive = true });
        var studentView = await studentClient.GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={tenant}");
        Assert.Equal(["STUDENT_REQUEST"], studentView!.Select(t => t.GetProperty("code").GetString()));

        Assert.Equal(HttpStatusCode.Forbidden, (await As(tenant, ["person:read"]).GetAsync($"/api/correspondence/types?tenantId={tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Guid.NewGuid(), Staff).GetAsync($"/api/correspondence/types?tenantId={tenant}")).StatusCode);
    }

    [Fact]
    public async Task A_University_Can_Add_And_Edit_Its_Own_Types_Within_Limits()
    {
        var tenant = await NewTenantAsync();
        var admin = As(tenant, Manager);
        object Body(string? code = "RESEARCH_GRANT", string? permission = "correspondence:create") => new
        {
            tenantId = tenant, code, nameEn = "Research grant", nameAr = "منحة بحثية", purpose = "Grant applications", senderDescription = "A researcher",
            receiverDescription = "The research office", createPermission = permission, requiresAmount = true, workflowCode = "CORR_RESEARCH_GRANT",
        };

        var created = await admin.PostAsJsonAsync("/api/correspondence/types", Body());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await Json(created)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/correspondence/types", Body("research_grant"))).StatusCode);   // codes are case-insensitive
        foreach (var bad in new[] { "1BAD", "has space", "x", new string('A', 41) })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/correspondence/types", Body(bad))).StatusCode);
        foreach (var perm in new[] { "correspondence:read", "correspondence:confidential", "person:create", "made:up" })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/correspondence/types", Body("OTHER", perm))).StatusCode);   // a type cannot hand out other permissions
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/correspondence/types", new { tenantId = tenant, code = "NONAME", nameEn = "x" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync($"/api/correspondence/types/{id}", new { tenantId = tenant, isActive = false, workflowCode = "" })).StatusCode);
        var stored = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={tenant}&includeInactive=true"))!.Single();
        Assert.False(stored.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, stored.GetProperty("workflowCode").ValueKind);        // an empty value clears the workflow
        Assert.Equal("RESEARCH_GRANT", stored.GetProperty("code").GetString());                // the code never changes
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/correspondence/types/{id}", new { tenantId = tenant, createPermission = "person:create" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PatchAsJsonAsync($"/api/correspondence/types/{Guid.NewGuid()}", new { tenantId = tenant, isActive = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(tenant, Staff).PostAsJsonAsync("/api/correspondence/types", Body("NOPE"))).StatusCode);
    }

    [Fact]
    public async Task A_Typed_Letter_Must_Satisfy_Its_Type_And_An_Untyped_One_Works_As_Before()
    {
        var tenant = await NewTenantAsync();
        var me = await NewPersonAsync(tenant);
        var finance = await NewUnitAsync(tenant, "FIN");
        await As(tenant, Manager).PostAsJsonAsync("/api/correspondence/types/seed-defaults", new { tenantId = tenant });
        var staff = As(tenant, Staff, me);
        Task<HttpResponseMessage> Post(object body) => staff.PostAsJsonAsync("/api/correspondence", body);

        // Untyped: unchanged (no type, no new fields).
        var plain = await Json(await Post(Letter(tenant, me, null)));
        Assert.Equal(JsonValueKind.Null, plain.GetProperty("typeCode").ValueKind);
        Assert.StartsWith("INT-", plain.GetProperty("number").GetString());

        // A simple type is recorded on the letter (the code is case-insensitive); the number still follows direction and year.
        var memo = await Json(await Post(Letter(tenant, me, "admin_memo")));
        Assert.Equal("ADMIN_MEMO", memo.GetProperty("typeCode").GetString());

        // The financial type needs the receiving unit and an amount.
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Letter(tenant, me, "FINANCIAL", amount: 500m))).StatusCode);          // no unit
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Letter(tenant, me, "FINANCIAL", unit: finance))).StatusCode);          // no amount
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Letter(tenant, me, "FINANCIAL", unit: finance, amount: 0m))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Letter(tenant, me, "FINANCIAL", unit: Guid.NewGuid(), amount: 500m))).StatusCode);   // a unit that is not there
        var ok = await Json(await Post(Letter(tenant, me, "FINANCIAL", unit: finance, amount: 12500.50m)));
        Assert.Equal(12500.50m, ok.GetProperty("amount").GetDecimal());
        Assert.Equal(finance, ok.GetProperty("targetUnitId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Letter(tenant, me, "ADMIN_MEMO", amount: -1m))).StatusCode);           // limits apply to every letter
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Letter(tenant, me, "ADMIN_MEMO", amount: 2_000_000_000m))).StatusCode);

        // Unknown and switched-off types are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Letter(tenant, me, "NO_SUCH_TYPE"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Letter(tenant, me, "STUDENT_REQUEST"))).StatusCode);                   // switched off
        var inactiveId = (await As(tenant, Manager).GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={tenant}&includeInactive=true"))!.Single(t => t.GetProperty("code").GetString() == "CIRCULAR").GetProperty("id").GetGuid();
        await As(tenant, Manager).PatchAsJsonAsync($"/api/correspondence/types/{inactiveId}", new { tenantId = tenant, isActive = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(Letter(tenant, me, "CIRCULAR"))).StatusCode);

        // A typed letter needs permission too: someone with no correspondence permission cannot start one.
        Assert.Equal(HttpStatusCode.Forbidden, (await As(tenant, ["person:read"], me).PostAsJsonAsync("/api/correspondence", Letter(tenant, me, "ADMIN_MEMO"))).StatusCode);

        // The register can be filtered by type and shows it, and the export has the column.
        var byType = await staff.GetFromJsonAsync<JsonElement>($"/api/correspondence/register?tenantId={tenant}&typeCode=financial");
        Assert.Equal(1, byType.GetProperty("total").GetInt32());
        Assert.Equal("FINANCIAL", byType.GetProperty("items")[0].GetProperty("typeCode").GetString());
        var csv = await staff.GetStringAsync($"/api/correspondence/register/export?tenantId={tenant}");
        Assert.StartsWith("number,type,subject,status,priority,confidential,created_at,to,cc,type_code", csv.TrimStart('\uFEFF'));   // the new column comes last
    }

    [Fact]
    public async Task A_Student_Can_Start_Only_A_Student_Request_And_Only_As_Themselves()
    {
        var tenant = await NewTenantAsync();
        var sara = await NewPersonAsync(tenant, PersonType.Student);
        var other = await NewPersonAsync(tenant, PersonType.Student);
        var admin = As(tenant, Manager);
        await admin.PostAsJsonAsync("/api/correspondence/types/seed-defaults", new { tenantId = tenant });
        var typeId = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={tenant}&includeInactive=true"))!.Single(t => t.GetProperty("code").GetString() == "STUDENT_REQUEST").GetProperty("id").GetGuid();
        var student = As(tenant, StudentOnly, sara);

        Assert.Equal(HttpStatusCode.BadRequest, (await student.PostAsJsonAsync("/api/correspondence", Letter(tenant, sara, "STUDENT_REQUEST"))).StatusCode);   // still switched off
        await admin.PatchAsJsonAsync($"/api/correspondence/types/{typeId}", new { tenantId = tenant, isActive = true });

        var made = await student.PostAsJsonAsync("/api/correspondence", Letter(tenant, sara, "STUDENT_REQUEST"));
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        Assert.Equal("STUDENT_REQUEST", (await Json(made)).GetProperty("typeCode").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await student.PostAsJsonAsync("/api/correspondence", Letter(tenant, other, "STUDENT_REQUEST"))).StatusCode);   // not on someone else's behalf
        Assert.Equal(HttpStatusCode.Forbidden, (await student.PostAsJsonAsync("/api/correspondence", Letter(tenant, sara, "ADMIN_MEMO"))).StatusCode);
        var staffMember = await NewPersonAsync(tenant);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(tenant, Staff, staffMember).PostAsJsonAsync("/api/correspondence", Letter(tenant, staffMember, "STUDENT_REQUEST"))).StatusCode);   // staff do not hold the student permission
        Assert.Equal(HttpStatusCode.Forbidden, (await student.PostAsJsonAsync("/api/correspondence", Letter(tenant, sara, null))).StatusCode);                  // an untyped letter needs the general permission
        Assert.Equal(HttpStatusCode.Forbidden, (await student.PostAsJsonAsync("/api/correspondence", Letter(tenant, sara, "STUDENT_REQUEST", confidential: true))).StatusCode);
        // A token that is not tied to any person cannot start one either.
        Assert.Equal(HttpStatusCode.Forbidden, (await As(tenant, StudentOnly).PostAsJsonAsync("/api/correspondence", Letter(tenant, sara, "STUDENT_REQUEST"))).StatusCode);
    }
}
