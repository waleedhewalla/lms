using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EduNexus.Api.Auth;
using EduNexus.Api.Connectors;
using EduNexus.Foundation;
using EduNexus.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EduNexus.Api.Tests;

public sealed class ConnectorEndpointTests(EduNexusFactory factory) : IClassFixture<EduNexusFactory>
{
    private static readonly string[] Admin = ["integration:manage", "integration:read"];
    private static readonly string[] ReadOnly = ["integration:read"];

    // A tiny seam so tests can use either this class's factory or a variant with other settings.
    private sealed class WebApplicationFactoryLike(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> inner)
    {
        public HttpClient Client(Guid tenant, string[] perms)
        {
            var c = inner.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", inner.Services.GetRequiredService<DevTokenService>().Mint("tests", tenant, perms));
            return c;
        }
        public T Service<T>() where T : notnull => inner.Services.GetRequiredService<T>();
        public async Task<T> Db<T>(Func<AppDbContext, Task<T>> work) { using var s = inner.Services.CreateScope(); return await work(s.ServiceProvider.GetRequiredService<AppDbContext>()); }
    }

    private WebApplicationFactoryLike Main => new(factory);

    private static CatalogCourse C(string id, string title, bool active = true, int? n = null) =>
        new(id, $"CODE-{id}", title, "Fall 2026", null, null, n, ["Dr. Layla Haddad"], active);

    private static async Task<Guid> Create(HttpClient c, Guid tenant, string system = "Moodle", string name = "Campus Moodle", string? credential = "token-123", string? url = "https://moodle.example.edu")
    {
        var r = await c.PostAsJsonAsync("/api/connectors", new { tenantId = tenant, system, name, baseUrl = url, credential });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    [Fact]
    public async Task The_Credential_Is_Stored_Sealed_And_Never_Returned_Anywhere()
    {
        var tenant = Guid.NewGuid();
        var admin = Main.Client(tenant, Admin);
        var r = await admin.PostAsJsonAsync("/api/connectors", new { tenantId = tenant, system = "Moodle", name = "Campus Moodle", baseUrl = "https://moodle.example.edu/", credential = "very-secret-token", options = new { } });
        var created = await r.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.DoesNotContain("very-secret-token", created);
        Assert.DoesNotContain("credentialSealed", created, StringComparison.OrdinalIgnoreCase);
        var id = JsonSerializer.Deserialize<JsonElement>(created).GetProperty("id").GetGuid();

        var list = await admin.GetStringAsync($"/api/connectors?tenantId={tenant}");
        Assert.DoesNotContain("very-secret-token", list);
        Assert.Contains("\"hasCredential\":true", list);
        Assert.Contains("\"baseUrl\":\"https://moodle.example.edu\"", list);   // trailing slash dropped

        var row = await Main.Db(db => db.ConnectorConfigs.AsNoTracking().SingleAsync(x => x.Id == id));
        Assert.DoesNotContain("very-secret-token", row.CredentialSealed);
        var box = Main.Service<SecretBox>();
        Assert.Equal("very-secret-token", box.Open(row.CredentialSealed!, $"{tenant:N}:{id:N}"));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => box.Open(row.CredentialSealed!, $"{Guid.NewGuid():N}:{id:N}"));
    }

    [Fact]
    public async Task Creation_Is_Validated_Authorised_And_Safe_Against_Internal_Addresses()
    {
        var tenant = Guid.NewGuid();
        var admin = Main.Client(tenant, Admin);
        async Task<HttpStatusCode> Post(object body) => (await admin.PostAsJsonAsync("/api/connectors", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { tenantId = tenant, system = "Blackboard", name = "x", baseUrl = "https://a.example.edu", credential = "t" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { tenantId = tenant, system = "Moodle", name = " ", baseUrl = "https://a.example.edu", credential = "t" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { tenantId = tenant, system = "Moodle", name = "n", baseUrl = (string?)null, credential = "t" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { tenantId = tenant, system = "Moodle", name = "n", baseUrl = "https://a.example.edu", credential = "" }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { tenantId = tenant, system = "Moodle", name = "n", baseUrl = "https://a.example.edu", credential = "t", options = new { token = "x" } }));
        // The address is checked like a webhook target: no loopback, private or cloud-metadata addresses.
        foreach (var bad in new[] { "http://169.254.169.254/latest", "http://localhost:8080", "http://10.0.0.5", "ftp://a.example.edu", "https://user:pw@a.example.edu" })
            Assert.Equal(HttpStatusCode.BadRequest, await Post(new { tenantId = tenant, system = "Moodle", name = "n", baseUrl = bad, credential = "t" }));
        // A Banner auth address is checked too.
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { tenantId = tenant, system = "Banner", name = "n", baseUrl = "https://a.example.edu", credential = "t", options = new { ethosAuthUrl = "http://169.254.169.254/auth" } }));

        Assert.Equal(HttpStatusCode.Created, await Post(new { tenantId = tenant, system = "Canvas", name = "Canvas", baseUrl = "https://c.example.edu", credential = "t", options = new { accountId = "7" } }));
        Assert.Equal(HttpStatusCode.Conflict, await Post(new { tenantId = tenant, system = "Moodle", name = "Canvas", baseUrl = "https://a.example.edu", credential = "t" }));

        Assert.Equal(HttpStatusCode.Forbidden, (await Main.Client(tenant, ReadOnly).PostAsJsonAsync("/api/connectors", new { tenantId = tenant, system = "Csv", name = "f" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Main.Client(Guid.NewGuid(), Admin).PostAsJsonAsync("/api/connectors", new { tenantId = tenant, system = "Csv", name = "f" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Main.Client(tenant, ReadOnly).GetAsync($"/api/connectors?tenantId={tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Main.Client(tenant, ["person:read"]).GetAsync($"/api/connectors?tenantId={tenant}")).StatusCode);
    }

    [Fact]
    public async Task Sync_Creates_Updates_And_Deactivates_And_Hands_The_Client_The_Opened_Credential()
    {
        var tenant = Guid.NewGuid();
        var admin = Main.Client(tenant, Admin);
        var id = await Create(admin, tenant, credential: "token-123");
        factory.Catalogs.Reset();
        factory.Catalogs.Courses = [C("1", "Algebra", n: 30), C("2", "Biology", n: 25), C("3", "Chemistry")];

        var first = await admin.PostAsJsonAsync($"/api/connectors/{id}/sync", new { tenantId = tenant });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var sum = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((3, 0, 0, 3), (sum.GetProperty("created").GetInt32(), sum.GetProperty("updated").GetInt32(), sum.GetProperty("deactivated").GetInt32(), sum.GetProperty("total").GetInt32()));
        Assert.Equal("token-123", factory.Catalogs.LastCredential);
        Assert.Equal(ConnectorSystem.Moodle, factory.Catalogs.LastSystem);
        Assert.Equal("https://moodle.example.edu", factory.Catalogs.LastBaseUrl);

        // Second read: Algebra changed (enrolment), Biology unchanged, Chemistry gone, a new Drama.
        factory.Catalogs.Courses = [C("1", "Algebra", n: 31), C("2", "Biology", n: 25), C("4", "Drama")];
        var second = await (await admin.PostAsJsonAsync($"/api/connectors/{id}/sync", new { tenantId = tenant })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((1, 1, 1, 3), (second.GetProperty("created").GetInt32(), second.GetProperty("updated").GetInt32(), second.GetProperty("deactivated").GetInt32(), second.GetProperty("total").GetInt32()));

        var all = await admin.GetFromJsonAsync<JsonElement>($"/api/connectors/courses?tenantId={tenant}&connectorId={id}");
        Assert.Equal(4, all.GetProperty("total").GetInt32());          // Chemistry is kept, as inactive
        var active = await admin.GetFromJsonAsync<JsonElement>($"/api/connectors/courses?tenantId={tenant}&active=true");
        Assert.Equal(3, active.GetProperty("total").GetInt32());
        var algebra = all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("externalId").GetString() == "1");
        Assert.Equal(31, algebra.GetProperty("enrolledCount").GetInt32());
        Assert.Equal("Dr. Layla Haddad", algebra.GetProperty("instructorNames").GetString());
        var found = await admin.GetFromJsonAsync<JsonElement>($"/api/connectors/courses?tenantId={tenant}&q=drama");
        Assert.Equal(1, found.GetProperty("total").GetInt32());
        Assert.Equal(0, (await admin.GetFromJsonAsync<JsonElement>($"/api/connectors/courses?tenantId={tenant}&q=%25")).GetProperty("total").GetInt32());   // % is literal

        var view = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/connectors?tenantId={tenant}"))!.Single();
        Assert.Equal("Succeeded", view.GetProperty("lastSyncStatus").GetString());
        Assert.Equal(3, view.GetProperty("courses").GetInt32());
        var audit = await Main.Db(db => db.AuditEvents.AsNoTracking().Where(a => a.TenantId == tenant && a.Action == "ConnectorSynced").CountAsync());
        Assert.Equal(2, audit);
    }

    [Fact]
    public async Task A_Failing_System_Is_Reported_As_502_With_A_Message_And_Keeps_The_Last_Good_Catalogue()
    {
        var tenant = Guid.NewGuid();
        var admin = Main.Client(tenant, Admin);
        var id = await Create(admin, tenant, "Canvas", "Canvas", "tok", "https://canvas.example.edu");
        factory.Catalogs.Reset();
        factory.Catalogs.Courses = [C("1", "Algebra")];
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/connectors/{id}/sync", new { tenantId = tenant })).StatusCode);

        factory.Catalogs.FailWith = "Canvas: the server refused the credential (401).";
        var bad = await admin.PostAsJsonAsync($"/api/connectors/{id}/sync", new { tenantId = tenant });
        Assert.Equal(HttpStatusCode.BadGateway, bad.StatusCode);
        Assert.Contains("refused the credential", await bad.Content.ReadAsStringAsync());

        var view = (await admin.GetFromJsonAsync<JsonElement[]>($"/api/connectors?tenantId={tenant}"))!.Single();
        Assert.Equal("Failed", view.GetProperty("lastSyncStatus").GetString());
        Assert.Contains("refused the credential", view.GetProperty("lastSyncMessage").GetString());
        Assert.Equal(1, view.GetProperty("courses").GetInt32());               // the earlier catalogue is untouched
        Assert.Equal(1, (await admin.GetFromJsonAsync<JsonElement>($"/api/connectors/courses?tenantId={tenant}&active=true")).GetProperty("total").GetInt32());
        factory.Catalogs.Reset();
    }

    [Fact]
    public async Task A_Sync_That_Is_Already_Running_Is_Not_Started_Twice_But_A_Stale_One_Does_Not_Block()
    {
        var tenant = Guid.NewGuid();
        var admin = Main.Client(tenant, Admin);
        var id = await Create(admin, tenant);
        factory.Catalogs.Reset();
        async Task SetStatus(DateTimeOffset at) => await Main.Db(async db =>
        {
            var c = await db.ConnectorConfigs.SingleAsync(x => x.Id == id);
            db.Entry(c).CurrentValues.SetValues(c with { LastSyncStatus = "Running", LastSyncAt = at });
            await db.SaveChangesAsync(); return 0;
        });
        await SetStatus(DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/connectors/{id}/sync", new { tenantId = tenant })).StatusCode);
        await SetStatus(DateTimeOffset.UtcNow.AddMinutes(-30));                // a crashed run does not block forever
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/api/connectors/{id}/sync", new { tenantId = tenant })).StatusCode);
    }

    [Fact]
    public async Task A_File_Connector_Takes_A_Csv_All_Or_Nothing_And_Live_Ones_Do_Not()
    {
        var tenant = Guid.NewGuid();
        var admin = Main.Client(tenant, Admin);
        var file = await Create(admin, tenant, "Csv", "Banner export", null, null);
        var live = await Create(admin, tenant, "Moodle", "Live", "t", "https://m.example.edu");

        var good = await admin.PostAsJsonAsync($"/api/connectors/{file}/import", new { tenantId = tenant, csv = "code,title,enrolled\nENG101,English,40\nMAT101,Maths,35\n" });
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal(2, (await good.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("created").GetInt32());

        var bad = await admin.PostAsJsonAsync($"/api/connectors/{file}/import", new { tenantId = tenant, csv = "code,title,enrolled\nENG101,English Updated,40\nMAT101,,x\n" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Row 3", await bad.Content.ReadAsStringAsync());
        var titles = (await admin.GetFromJsonAsync<JsonElement>($"/api/connectors/courses?tenantId={tenant}&connectorId={file}")).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToList();
        Assert.Contains("English", titles);                                    // nothing was half-applied
        Assert.DoesNotContain("English Updated", titles);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/connectors/{live}/import", new { tenantId = tenant, csv = "code,title\nA,B\n" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync($"/api/connectors/{file}/sync", new { tenantId = tenant })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync($"/api/connectors/{Guid.NewGuid()}/import", new { tenantId = tenant, csv = "code,title\nA,B\n" })).StatusCode);
    }

    [Fact]
    public async Task Deleting_A_Connector_Removes_Its_Courses_And_Tenants_Cannot_See_Each_Others()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var adminA = Main.Client(a, Admin);
        var id = await Create(adminA, a, "Csv", "Export", null, null);
        await adminA.PostAsJsonAsync($"/api/connectors/{id}/import", new { tenantId = a, csv = "code,title\nA1,Alpha\n" });

        var adminB = Main.Client(b, Admin);
        Assert.Equal(0, (await adminB.GetFromJsonAsync<JsonElement>($"/api/connectors/courses?tenantId={b}")).GetProperty("total").GetInt32());
        Assert.Empty((await adminB.GetFromJsonAsync<JsonElement[]>($"/api/connectors?tenantId={b}"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.DeleteAsync($"/api/connectors/{id}?tenantId={b}")).StatusCode);   // another tenant's id is simply unknown
        Assert.Equal(HttpStatusCode.Forbidden, (await adminB.GetAsync($"/api/connectors/courses?tenantId={a}")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await adminA.DeleteAsync($"/api/connectors/{id}?tenantId={a}")).StatusCode);
        Assert.Equal(0, await Main.Db(db => db.ExternalCourses.CountAsync(c => c.TenantId == a)));
        Assert.Equal(0, await Main.Db(db => db.ConnectorConfigs.CountAsync(c => c.TenantId == a)));
    }

    [Fact]
    public async Task Switched_Off_Every_Route_Says_So_And_Without_A_Key_A_Credential_Is_Not_Accepted()
    {
        var tenant = Guid.NewGuid();
        using var off = factory.WithWebHostBuilder(b => b.UseSetting("Connectors:Enabled", "false"));
        var client = new WebApplicationFactoryLike(off).Client(tenant, Admin);
        foreach (var r in new[]
        {
            await client.GetAsync($"/api/connectors?tenantId={tenant}"),
            await client.PostAsJsonAsync("/api/connectors", new { tenantId = tenant, system = "Csv", name = "f" }),
            await client.GetAsync($"/api/connectors/courses?tenantId={tenant}"),
            await client.PostAsJsonAsync($"/api/connectors/{Guid.NewGuid()}/sync", new { tenantId = tenant }),
        })
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        using var noKey = factory.WithWebHostBuilder(b => b.UseSetting("Connectors:SecretKey", ""));
        var keyless = new WebApplicationFactoryLike(noKey).Client(tenant, Admin);
        var live = await keyless.PostAsJsonAsync("/api/connectors", new { tenantId = tenant, system = "Moodle", name = "m", baseUrl = "https://m.example.edu", credential = "t" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, live.StatusCode);
        Assert.Contains("Connectors:SecretKey", await live.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Created, (await keyless.PostAsJsonAsync("/api/connectors", new { tenantId = tenant, system = "Csv", name = "f" })).StatusCode);   // a file needs no secret
    }
}
