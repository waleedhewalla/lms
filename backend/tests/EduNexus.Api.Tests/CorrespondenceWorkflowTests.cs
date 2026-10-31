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

/// <summary>The workflow of each correspondence type end to end: the main path, return, reject, resubmit, withdrawal, limits, visibility.</summary>
public sealed class CorrespondenceWorkflowTests(EduNexusFactory factory) : IClassFixture<EduNexusFactory>
{
    private static readonly string[] Setup = ["tenant:create", "tenant:read", "person:create", "person:read"];
    private static readonly string[] Writer = ["correspondence:create"];
    private static readonly string[] Approver = ["approval:decide", "approval:read"];
    private static readonly string[] Manager = ["workflow:manage", "correspondence:read"];
    private static readonly string[] Records = ["records:manage", "correspondence:read"];
    private static readonly string[] StudentOnly = ["correspondence:student-request"];

    private sealed record World(Guid Tenant, Guid Author, Guid Head, Guid Dean, Guid TargetHead, Guid TargetUnit, Guid College, Guid Sibling, Guid Finance, Guid Vp, Guid President, Guid Hr, Guid StudentAffairs, Guid AdminAffairs);

    private HttpClient As(Guid tenant, string[] perms, Guid? person = null)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            factory.Services.GetRequiredService<DevTokenService>().Mint("tests", tenant, perms, personId: person));
        return c;
    }

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> work) { using var s = factory.Services.CreateScope(); return await work(s.ServiceProvider.GetRequiredService<AppDbContext>()); }
    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    private async Task<World> BuildAsync(bool seed = true, bool withFinanceRoles = true, bool withAdminAffairs = true)
    {
        var tenant = (await Json(await As(Guid.NewGuid(), Setup).PostAsJsonAsync("/api/tenants", new { slug = $"w-{Guid.NewGuid():N}", name = "Flow Uni" }))).GetProperty("id").GetGuid();
        Guid P(string name) { var p = new Person(Guid.NewGuid(), tenant, PersonType.Employee, name, null, true, DateTimeOffset.UtcNow); Db(async db => { db.People.Add(p); await db.SaveChangesAsync(); return 0; }).GetAwaiter().GetResult(); return p.Id; }
        var (head, dean, targetHead, fin, vp, pres, hr, sa, aa) = (P("Head"), P("Dean"), P("TargetHead"), P("Finance"), P("Vp"), P("President"), P("Hr"), P("StudentAffairs"), P("AdminAffairs"));
        var college = OrganizationalUnit.Create(tenant, "COL", "College", null, dean);
        var dept = OrganizationalUnit.Create(tenant, "DEP", "Department", college.Id, head);
        var target = OrganizationalUnit.Create(tenant, "TGT", "Target", null, targetHead);
        var sibling = OrganizationalUnit.Create(tenant, "SIB", "Sibling department", college.Id, targetHead);
        var affairs = withAdminAffairs ? OrganizationalUnit.Create(tenant, "ADMIN_AFFAIRS", "Administrative affairs", college.Id, aa) : null;
        var author = new Person(Guid.NewGuid(), tenant, PersonType.Employee, "Author", null, true, DateTimeOffset.UtcNow, dept.Id);
        await Db(async db =>
        {
            db.OrganizationalUnits.AddRange(college, dept, target, sibling);
            if (affairs is not null) db.OrganizationalUnits.Add(affairs);
            db.People.Add(author);
            if (withFinanceRoles)
                foreach (var (code, who) in new[] { ("FINANCE_MANAGER", fin), ("VP_ADMIN_FINANCE", vp), ("PRESIDENT", pres), ("HR_MANAGER", hr), ("STUDENT_AFFAIRS", sa) })
                {
                    var role = Role.Create(tenant, code, code, ["approval:decide"]);
                    db.Roles.Add(role);
                    db.RoleAssignments.Add(new RoleAssignment(Guid.NewGuid(), tenant, who, role.Id, "tenant", null, DateTimeOffset.UtcNow));
                }
            await db.SaveChangesAsync();
            return 0;
        });
        if (seed) Assert.Equal(HttpStatusCode.OK, (await As(tenant, Manager).PostAsJsonAsync("/api/correspondence/types/seed-defaults", new { tenantId = tenant })).StatusCode);
        return new World(tenant, author.Id, head, dean, targetHead, target.Id, college.Id, sibling.Id, fin, vp, pres, hr, sa, aa);
    }

    private async Task<Guid> CreateAsync(World w, string typeCode, decimal? amount = null, Guid? unit = null, Guid? to = null, string priority = "Normal")
    {
        var res = await As(w.Tenant, Writer, w.Author).PostAsJsonAsync("/api/correspondence", new
        {
            tenantId = w.Tenant, type = "Internal", subject = "A letter", content = "Body", authorId = w.Author, priority, isConfidential = false,
            recipients = to is { } p ? new[] { new { personId = p.ToString(), displayName = "Recipient", isExternal = false, kind = "To" } } : [],
            typeCode, targetUnitId = unit, amount,
        });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await Json(res)).GetProperty("id").GetGuid();
    }

    private Task<HttpResponseMessage> SubmitAsync(World w, Guid id) => As(w.Tenant, Writer, w.Author).PostAsJsonAsync($"/api/correspondence/{id}/submit", new { tenantId = w.Tenant });

    private async Task<Approval> PendingAsync(Guid tenant, Guid letter) =>
        await Db(db => db.Approvals.Where(a => a.TenantId == tenant && a.EntityId == letter && a.Status == ApprovalStatus.Pending).SingleAsync());

    private Task<HttpResponseMessage> DecideAsync(World w, Approval a, bool approve, string? comment = null) =>
        As(w.Tenant, Approver, a.AssigneeId).PostAsJsonAsync($"/api/approvals/{a.Id}/decide", new { tenantId = w.Tenant, approve, comment });

    private Task<CorrespondenceStatus> StatusAsync(Guid letter) => Db(async db => (await db.Correspondences.AsNoTracking().SingleAsync(c => c.Id == letter)).Status);

    [Fact]
    public async Task Seeding_Also_Creates_The_Standard_Workflows_And_Keeps_Edited_Ones()
    {
        var w = await BuildAsync();
        var codes = await Db(db => db.WorkflowDefinitions.Where(d => d.TenantId == w.Tenant).Select(d => d.Code).ToListAsync());
        Assert.Equal(15, codes.Count(c => c.StartsWith("CORR_")));
        var again = await Json(await As(w.Tenant, Manager).PostAsJsonAsync("/api/correspondence/types/seed-defaults", new { tenantId = w.Tenant }));
        Assert.Empty(again.GetProperty("workflowsCreated").EnumerateArray());
    }

    [Fact]
    public async Task Admin_Memo_Goes_Line_Manager_Then_Recipient_Then_Closes_And_Tells_The_Author()
    {
        var w = await BuildAsync();
        var recipient = w.TargetHead;
        var id = await CreateAsync(w, "ADMIN_MEMO", to: recipient);
        Assert.Equal(HttpStatusCode.OK, (await SubmitAsync(w, id)).StatusCode);
        Assert.Equal(CorrespondenceStatus.Submitted, await StatusAsync(id));

        var first = await PendingAsync(w.Tenant, id);
        Assert.Equal(w.Head, first.AssigneeId);                                     // the head of the author's department
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w, first, true)).StatusCode);
        var second = await PendingAsync(w.Tenant, id);
        Assert.Equal(recipient, second.AssigneeId);                                 // then the person it is addressed to
        Assert.Equal(CorrespondenceStatus.Submitted, await StatusAsync(id));
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w, second, true)).StatusCode);

        // The last step belongs to the college's administrative affairs unit, which closes the file.
        Assert.Equal(CorrespondenceStatus.Submitted, await StatusAsync(id));
        var closing = await PendingAsync(w.Tenant, id);
        Assert.Equal(w.AdminAffairs, closing.AssigneeId);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w, closing, true)).StatusCode);
        Assert.Equal(CorrespondenceStatus.Closed, await StatusAsync(id));
        Assert.True(await Db(db => db.Notifications.AnyAsync(n => n.PersonId == w.Author && n.Title.StartsWith("Completed"))));

        var history = await Json(await As(w.Tenant, Writer, w.Author).GetAsync($"/api/correspondence/{id}/history?tenantId={w.Tenant}"));
        Assert.Equal(3, history.GetProperty("steps").GetArrayLength());
        Assert.Contains(history.GetProperty("events").EnumerateArray(), e => e.GetProperty("action").GetString() == "WorkflowCompleted");
        Assert.Contains(history.GetProperty("steps").EnumerateArray(), s => s.GetProperty("decidedByName").GetString() == "Head");
    }

    [Fact]
    public async Task A_Letter_To_The_Dean_Passes_The_Vice_Dean_When_There_Is_One_And_Skips_The_Step_When_There_Is_Not()
    {
        var w = await BuildAsync();
        // The college has no vice dean yet: the optional step is passed over and the dean decides.
        var first = await CreateAsync(w, "TO_DEAN", unit: w.College);
        Assert.Equal(HttpStatusCode.OK, (await SubmitAsync(w, first)).StatusCode);
        Assert.Equal(w.Head, (await PendingAsync(w.Tenant, first)).AssigneeId);                       // the sender's line manager
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w, await PendingAsync(w.Tenant, first), true)).StatusCode);
        Assert.Equal(w.Dean, (await PendingAsync(w.Tenant, first)).AssigneeId);                        // straight to the dean
        var skipped = await Db(db => db.AuditEvents.CountAsync(a => a.TenantId == w.Tenant && a.Action == "WorkflowStepSkipped" && a.Details!.Contains("optional step")));
        Assert.Equal(1, skipped);

        // With a vice dean, the vice dean comes first.
        var viceDean = w.Finance;                                                                      // any person will do for the test
        await Db(async db => { var u = await db.OrganizationalUnits.SingleAsync(x => x.Id == w.College); db.Entry(u).CurrentValues.SetValues(u with { DeputyPersonId = viceDean }); await db.SaveChangesAsync(); return 0; });
        var second = await CreateAsync(w, "TO_DEAN", unit: w.College);
        await SubmitAsync(w, second);
        await DecideAsync(w, await PendingAsync(w.Tenant, second), true);
        Assert.Equal(viceDean, (await PendingAsync(w.Tenant, second)).AssigneeId);
        await DecideAsync(w, await PendingAsync(w.Tenant, second), true);
        Assert.Equal(w.Dean, (await PendingAsync(w.Tenant, second)).AssigneeId);
    }

    [Fact]
    public async Task College_Administrations_And_Departments_Write_Head_To_Head_Inside_One_College()
    {
        var w = await BuildAsync();
        foreach (var code in new[] { "COLLEGE_ADMIN", "COLLEGE_DEPT" })
        {
            var id = await CreateAsync(w, code, unit: w.Sibling);
            Assert.Equal(HttpStatusCode.OK, (await SubmitAsync(w, id)).StatusCode);
            Assert.Equal(w.Head, (await PendingAsync(w.Tenant, id)).AssigneeId);
            Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w, await PendingAsync(w.Tenant, id), true)).StatusCode);
            Assert.Equal(w.TargetHead, (await PendingAsync(w.Tenant, id)).AssigneeId);
        }
        object Body(string code, Guid? unit) => new { tenantId = w.Tenant, type = "Internal", subject = "s", content = "c", authorId = w.Author, priority = "Normal", isConfidential = false, recipients = Array.Empty<object>(), typeCode = code, targetUnitId = unit };
        var author = As(w.Tenant, Writer, w.Author);
        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsJsonAsync("/api/correspondence", Body("TO_DEAN", null))).StatusCode);                  // needs the college
        // Another college is refused for the college types, with the reason; the general between-departments type is not limited.
        foreach (var code in new[] { "COLLEGE_ADMIN", "COLLEGE_DEPT", "TO_DEAN" })
        {
            var res = await author.PostAsJsonAsync("/api/correspondence", Body(code, w.TargetUnit));
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Contains("one college", (await Json(res)).GetProperty("error").GetString());
        }
        Assert.Equal(HttpStatusCode.Created, (await author.PostAsJsonAsync("/api/correspondence", Body("INTER_UNIT", w.TargetUnit))).StatusCode);
        // A sender with no department cannot be placed in a college.
        var noDept = await Db(async db => { var p = new Person(Guid.NewGuid(), w.Tenant, PersonType.Employee, "No Dept", null, true, DateTimeOffset.UtcNow); db.People.Add(p); await db.SaveChangesAsync(); return p.Id; });
        var res2 = await As(w.Tenant, Writer, noDept).PostAsJsonAsync("/api/correspondence", new { tenantId = w.Tenant, type = "Internal", subject = "s", content = "c", authorId = noDept, priority = "Normal", isConfidential = false, recipients = Array.Empty<object>(), typeCode = "COLLEGE_DEPT", targetUnitId = w.Sibling });
        Assert.Equal(HttpStatusCode.BadRequest, res2.StatusCode);
    }

    [Fact]
    public async Task A_Step_Whose_Approver_Is_The_Author_Is_Skipped()
    {
        var w = await BuildAsync();
        await Db(async db => { var u = await db.OrganizationalUnits.SingleAsync(x => x.TenantId == w.Tenant && x.Code == "DEP"); db.Entry(u).CurrentValues.SetValues(u with { LeaderPersonId = w.Author }); await db.SaveChangesAsync(); return 0; });
        var id = await CreateAsync(w, "ADMIN_MEMO", to: w.TargetHead);
        await SubmitAsync(w, id);
        Assert.Equal(w.TargetHead, (await PendingAsync(w.Tenant, id)).AssigneeId);   // the author is the head: no one approves their own letter
    }

    [Fact]
    public async Task A_Return_Needs_Words_Sends_The_Letter_Back_And_A_Resubmission_Starts_A_New_Round()
    {
        var w = await BuildAsync();
        var id = await CreateAsync(w, "ADMIN_MEMO", to: w.TargetHead);
        await SubmitAsync(w, id);
        var a = await PendingAsync(w.Tenant, id);
        var head = As(w.Tenant, Approver, w.Head);

        Assert.Equal(HttpStatusCode.BadRequest, (await head.PostAsJsonAsync($"/api/approvals/{a.Id}/request-changes", new { tenantId = w.Tenant, comment = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await head.PostAsJsonAsync($"/api/approvals/{a.Id}/request-changes", new { tenantId = w.Tenant, comment = "Add the budget line" })).StatusCode);
        Assert.Equal(CorrespondenceStatus.Returned, await StatusAsync(id));
        Assert.True(await Db(db => db.Notifications.AnyAsync(n => n.PersonId == w.Author && n.Body.Contains("Add the budget line"))));

        var author = As(w.Tenant, Writer, w.Author);
        Assert.Equal(HttpStatusCode.Conflict, (await author.PostAsJsonAsync($"/api/correspondence/{id}/submit", new { tenantId = w.Tenant })).StatusCode);   // only a draft is submitted
        var edit = await author.PatchAsJsonAsync($"/api/correspondence/{id}", new { tenantId = w.Tenant, content = "Body with the budget line" });
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await author.PostAsJsonAsync($"/api/correspondence/{id}/resubmit", new { tenantId = w.Tenant })).StatusCode);
        Assert.Equal(CorrespondenceStatus.Submitted, await StatusAsync(id));
        Assert.Equal(w.Head, (await PendingAsync(w.Tenant, id)).AssigneeId);          // back to the first step
        Assert.Equal(2, await Db(db => db.WorkflowInstances.CountAsync(i => i.EntityId == id)));
        Assert.Equal(HttpStatusCode.Conflict, (await author.PostAsJsonAsync($"/api/correspondence/{id}/resubmit", new { tenantId = w.Tenant })).StatusCode);   // no longer returned
    }

    [Fact]
    public async Task Only_The_Author_Edits_And_Only_While_Draft_Or_Returned()
    {
        var w = await BuildAsync();
        var id = await CreateAsync(w, "ADMIN_MEMO", to: w.TargetHead);
        var stranger = await Db(async db => { var p = new Person(Guid.NewGuid(), w.Tenant, PersonType.Employee, "Other", null, true, DateTimeOffset.UtcNow); db.People.Add(p); await db.SaveChangesAsync(); return p.Id; });
        Assert.Equal(HttpStatusCode.NotFound, (await As(w.Tenant, Writer, stranger).PatchAsJsonAsync($"/api/correspondence/{id}", new { tenantId = w.Tenant, subject = "Hijack" })).StatusCode);
        await SubmitAsync(w, id);
        Assert.Equal(HttpStatusCode.Conflict, (await As(w.Tenant, Writer, w.Author).PatchAsJsonAsync($"/api/correspondence/{id}", new { tenantId = w.Tenant, subject = "Too late" })).StatusCode);
    }

    [Fact]
    public async Task A_Rejection_Needs_A_Reason_Is_Final_And_Is_Reported_To_The_Author()
    {
        var w = await BuildAsync();
        var id = await CreateAsync(w, "ADMIN_MEMO", to: w.TargetHead);
        await SubmitAsync(w, id);
        var a = await PendingAsync(w.Tenant, id);
        Assert.Equal(HttpStatusCode.BadRequest, (await DecideAsync(w, a, false)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w, a, false, "Not in the budget")).StatusCode);
        Assert.Equal(CorrespondenceStatus.Rejected, await StatusAsync(id));
        Assert.True(await Db(db => db.Notifications.AnyAsync(n => n.PersonId == w.Author && n.Body.Contains("Not in the budget"))));
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(w, a, true)).StatusCode);                       // already decided
        Assert.Equal(HttpStatusCode.Conflict, (await As(w.Tenant, Writer, w.Author).PostAsJsonAsync($"/api/correspondence/{id}/resubmit", new { tenantId = w.Tenant })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(w.Tenant, Approver, w.Dean).PostAsJsonAsync($"/api/approvals/{a.Id}/decide", new { tenantId = w.Tenant, approve = true })).StatusCode);   // not the assignee
    }

    [Fact]
    public async Task Financial_Letters_Add_Approvers_As_The_Amount_Grows()
    {
        var w = await BuildAsync();
        async Task<List<Guid>> Chain(decimal amount)
        {
            var id = await CreateAsync(w, "FINANCIAL", amount, w.TargetUnit);
            Assert.Equal(HttpStatusCode.OK, (await SubmitAsync(w, id)).StatusCode);
            var seen = new List<Guid>();
            for (var i = 0; i < 8 && await StatusAsync(id) == CorrespondenceStatus.Submitted; i++)
            {
                var a = await PendingAsync(w.Tenant, id);
                seen.Add(a.AssigneeId);
                Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w, a, true)).StatusCode);
            }
            Assert.Equal(CorrespondenceStatus.Closed, await StatusAsync(id));
            return seen;
        }
        Assert.Equal([w.Head, w.Finance, w.TargetHead, w.AdminAffairs], await Chain(10_000m));
        Assert.Equal([w.Head, w.Finance, w.Vp, w.TargetHead, w.AdminAffairs], await Chain(100_000m));
        Assert.Equal([w.Head, w.Finance, w.Vp, w.President, w.TargetHead, w.AdminAffairs], await Chain(300_000m));
        // The limit is the university's to set: raise the first one and a letter of 100,000 no longer needs the VP.
        var def = await Db(db => db.WorkflowDefinitions.SingleAsync(d => d.TenantId == w.Tenant && d.Code == "CORR_FINANCIAL"));
        var edited = def.NodesJson.Replace("50000", "500000");
        var put = await As(w.Tenant, Manager).PostAsJsonAsync($"/api/workflows/definitions/{def.Id}/new-version", new { tenantId = w.Tenant, nodesJson = edited });
        Assert.Equal(2, (await Json(put)).GetProperty("version").GetInt32());
        Assert.Equal([w.Head, w.Finance, w.TargetHead, w.AdminAffairs], await Chain(100_000m));
    }

    [Fact]
    public async Task A_Missing_Approver_Is_Reported_And_Nothing_Is_Started()
    {
        var w = await BuildAsync(withFinanceRoles: false);
        var id = await CreateAsync(w, "FINANCIAL", 10_000m, w.TargetUnit);
        var res = await SubmitAsync(w, id);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);                       // the first step (line manager) exists
        var a = await PendingAsync(w.Tenant, id);
        var next = await DecideAsync(w, a, true);                               // the finance manager role is empty
        Assert.Equal(HttpStatusCode.UnprocessableEntity, next.StatusCode);
        Assert.Contains("FINANCE_MANAGER", (await Json(next)).GetProperty("error").GetString());
        Assert.Equal(ApprovalStatus.Pending, (await Db(db => db.Approvals.AsNoTracking().SingleAsync(x => x.Id == a.Id))).Status);   // the decision can be repeated later
        // A letter whose receiving unit has no head fails at submission, and stays a draft.
        var emptyUnit = OrganizationalUnit.Create(w.Tenant, "EMPTY", "No head");
        await Db(async db => { db.OrganizationalUnits.Add(emptyUnit); await db.SaveChangesAsync(); return 0; });
        var inter = await CreateAsync(w, "INTER_UNIT", unit: emptyUnit.Id);
        await Db(async db => { var u = await db.OrganizationalUnits.SingleAsync(x => x.TenantId == w.Tenant && x.Code == "DEP"); db.Entry(u).CurrentValues.SetValues(u with { LeaderPersonId = w.Author }); await db.SaveChangesAsync(); return 0; });
        var bad = await SubmitAsync(w, inter);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
        Assert.Equal(CorrespondenceStatus.Draft, await StatusAsync(inter));
    }

    [Fact]
    public async Task Withdrawing_Cancels_The_Pending_Step_And_Is_Limited_To_The_Author_And_Records()
    {
        var w = await BuildAsync();
        var id = await CreateAsync(w, "ADMIN_MEMO", to: w.TargetHead);
        await SubmitAsync(w, id);
        var a = await PendingAsync(w.Tenant, id);
        var author = As(w.Tenant, Writer, w.Author);

        Assert.Equal(HttpStatusCode.BadRequest, (await author.PostAsJsonAsync($"/api/correspondence/{id}/close", new { tenantId = w.Tenant, reason = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await As(w.Tenant, Writer, w.Dean).PostAsJsonAsync($"/api/correspondence/{id}/close", new { tenantId = w.Tenant, reason = "not mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await author.PostAsJsonAsync($"/api/correspondence/{id}/close", new { tenantId = w.Tenant, reason = "Sent by mistake" })).StatusCode);
        Assert.Equal(CorrespondenceStatus.Closed, await StatusAsync(id));
        Assert.Equal(ApprovalStatus.Cancelled, (await Db(db => db.Approvals.AsNoTracking().SingleAsync(x => x.Id == a.Id))).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await DecideAsync(w, a, true)).StatusCode);                          // the approver can no longer decide it
        Assert.Equal(HttpStatusCode.Conflict, (await author.PostAsJsonAsync($"/api/correspondence/{id}/close", new { tenantId = w.Tenant, reason = "again" })).StatusCode);

        var second = await CreateAsync(w, "ADMIN_MEMO", to: w.TargetHead);
        await SubmitAsync(w, second);
        var records = As(w.Tenant, Records, w.Dean);
        Assert.Equal(HttpStatusCode.OK, (await records.PostAsJsonAsync($"/api/correspondence/{second}/close", new { tenantId = w.Tenant, reason = "Duplicate" })).StatusCode);
        Assert.True(await Db(db => db.Notifications.AnyAsync(n => n.PersonId == w.Author && n.Body.Contains("Duplicate"))));
    }

    [Fact]
    public async Task A_Student_Sees_Only_Their_Own_Letters_And_Their_Request_Reaches_Student_Affairs()
    {
        var w = await BuildAsync();
        var sara = await Db(async db => { var p = new Person(Guid.NewGuid(), w.Tenant, PersonType.Student, "Sara", null, true, DateTimeOffset.UtcNow); db.People.Add(p); await db.SaveChangesAsync(); return p.Id; });
        var typeId = (await As(w.Tenant, Manager).GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={w.Tenant}&includeInactive=true"))!.Single(t => t.GetProperty("code").GetString() == "STUDENT_REQUEST").GetProperty("id").GetGuid();
        await As(w.Tenant, Manager).PatchAsJsonAsync($"/api/correspondence/types/{typeId}", new { tenantId = w.Tenant, isActive = true });
        var student = As(w.Tenant, StudentOnly, sara);
        var made = await student.PostAsJsonAsync("/api/correspondence", new { tenantId = w.Tenant, type = "Internal", subject = "Transcript", content = "Please", authorId = sara, priority = "Normal", isConfidential = false, recipients = Array.Empty<object>(), typeCode = "STUDENT_REQUEST" });
        var mine = (await Json(made)).GetProperty("id").GetGuid();
        var staffLetter = await CreateAsync(w, "ADMIN_MEMO", to: w.TargetHead);

        Assert.Equal(HttpStatusCode.OK, (await student.PostAsJsonAsync($"/api/correspondence/{mine}/submit", new { tenantId = w.Tenant })).StatusCode);
        Assert.Equal(w.StudentAffairs, (await PendingAsync(w.Tenant, mine)).AssigneeId);

        var list = await student.GetFromJsonAsync<JsonElement[]>($"/api/correspondence/mine?tenantId={w.Tenant}");
        Assert.Equal([mine], list!.Select(c => c.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync($"/api/correspondence?tenantId={w.Tenant}")).StatusCode);                    // no general read
        Assert.Equal(HttpStatusCode.NotFound, (await student.GetAsync($"/api/correspondence/{staffLetter}/history?tenantId={w.Tenant}")).StatusCode);   // not their letter
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync($"/api/correspondence/{mine}/history?tenantId={w.Tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync($"/api/correspondence/{mine}?tenantId={w.Tenant}")).StatusCode);                         // their own letter, in full
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync($"/api/correspondence/{staffLetter}?tenantId={w.Tenant}")).StatusCode);          // someone else's
        Assert.Equal(HttpStatusCode.OK, (await student.GetAsync($"/api/correspondence/units?tenantId={w.Tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(w.Tenant, ["person:read"]).GetAsync($"/api/correspondence/units?tenantId={w.Tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.PostAsJsonAsync($"/api/correspondence/{staffLetter}/submit", new { tenantId = w.Tenant })).StatusCode);   // not theirs to send
    }

    [Fact]
    public async Task Another_Tenant_Cannot_Read_Or_Change_A_Letter()
    {
        var w = await BuildAsync();
        var id = await CreateAsync(w, "ADMIN_MEMO", to: w.TargetHead);
        var outsider = As(Guid.NewGuid(), Writer.Concat(["correspondence:read"]).ToArray(), w.Author);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.GetAsync($"/api/correspondence/{id}/history?tenantId={w.Tenant}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await outsider.PostAsJsonAsync($"/api/correspondence/{id}/close", new { tenantId = w.Tenant, reason = "attack" })).StatusCode);
    }

    [Fact]
    public async Task A_Letter_Without_A_Type_Keeps_The_Single_Reviewer_Path()
    {
        var w = await BuildAsync(seed: false);
        var res = await As(w.Tenant, Writer, w.Author).PostAsJsonAsync("/api/correspondence", new { tenantId = w.Tenant, type = "Internal", subject = "Old style", content = "Body", authorId = w.Author, priority = "Normal", isConfidential = false, recipients = Array.Empty<object>() });
        var id = (await Json(res)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await SubmitAsync(w, id)).StatusCode);                                   // a reviewer is needed
        Assert.Equal(HttpStatusCode.OK, (await As(w.Tenant, Writer, w.Author).PostAsJsonAsync($"/api/correspondence/{id}/submit", new { tenantId = w.Tenant, reviewerId = w.Head })).StatusCode);
        var a = await PendingAsync(w.Tenant, id);
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w, a, true)).StatusCode);
        Assert.Equal(CorrespondenceStatus.Approved, await StatusAsync(id));                                               // as before: approved, not closed
        Assert.Equal(0, await Db(db => db.WorkflowInstances.CountAsync(i => i.EntityId == id)));
    }

    [Fact]
    public async Task New_Versions_Are_Validated_And_Need_The_Manage_Permission()
    {
        var w = await BuildAsync();
        var def = await Db(db => db.WorkflowDefinitions.SingleAsync(d => d.TenantId == w.Tenant && d.Code == "CORR_CIRCULAR"));
        Task<HttpResponseMessage> Put(string[] perms, string nodes) => As(w.Tenant, perms).PostAsJsonAsync($"/api/workflows/definitions/{def.Id}/new-version", new { tenantId = w.Tenant, nodesJson = nodes });
        Assert.Equal(HttpStatusCode.Forbidden, (await Put(Writer, def.NodesJson)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(Manager, "not json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(Manager, """[{"id":"s","type":"approval"}]""")).StatusCode);                                 // must start with start
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(Manager, """[{"id":"s","type":"start"},{"id":"x","type":"teleport"}]""")).StatusCode);        // unknown step type
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(Manager, """[{"id":"s","type":"start"},{"id":"a","type":"approval","when":{"field":"amount","op":"~","value":1}}]""")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put(Manager, """[{"id":"s","type":"start"},{"id":"a","type":"approval","slaDays":0}]""")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(Manager, def.NodesJson)).StatusCode);
    }

    [Fact]
    public async Task A_College_Without_An_Administrative_Affairs_Unit_Cannot_Close_A_Letter_And_Is_Told_So()
    {
        var w = await BuildAsync(withAdminAffairs: false);
        var id = await CreateAsync(w, "ADMIN_MEMO", to: w.TargetHead);
        await SubmitAsync(w, id);
        await DecideAsync(w, await PendingAsync(w.Tenant, id), true);
        var last = await PendingAsync(w.Tenant, id);
        var res = await DecideAsync(w, last, true);                                      // the recipient's approval would reach the closing step
        Assert.Equal(HttpStatusCode.UnprocessableEntity, res.StatusCode);
        Assert.Contains("administrative affairs", (await Json(res)).GetProperty("error").GetString());
        Assert.Equal(ApprovalStatus.Pending, (await Db(db => db.Approvals.AsNoTracking().SingleAsync(x => x.Id == last.Id))).Status);   // nothing was lost; repeat once the unit exists
        // Once the college has the unit, the same decision goes through.
        var aa = OrganizationalUnit.Create(w.Tenant, "ADMIN_AFFAIRS", "Administrative affairs", w.College, w.AdminAffairs);
        await Db(async db => { db.OrganizationalUnits.Add(aa); await db.SaveChangesAsync(); return 0; });
        Assert.Equal(HttpStatusCode.OK, (await DecideAsync(w, last, true)).StatusCode);
        Assert.Equal(w.AdminAffairs, (await PendingAsync(w.Tenant, id)).AssigneeId);
    }

    [Fact]
    public async Task Student_Services_Are_A_List_Of_Active_Types_And_Each_Ends_With_The_Colleges_Administrative_Affairs()
    {
        var w = await BuildAsync();
        var sara = await Db(async db => { var p = new Person(Guid.NewGuid(), w.Tenant, PersonType.Student, "Sara", null, true, DateTimeOffset.UtcNow, (await db.OrganizationalUnits.SingleAsync(u => u.TenantId == w.Tenant && u.Code == "DEP")).Id); db.People.Add(p); await db.SaveChangesAsync(); return p.Id; });
        var student = As(w.Tenant, StudentOnly, sara);
        var services = (await student.GetFromJsonAsync<JsonElement[]>($"/api/correspondence/types?tenantId={w.Tenant}"))!.Select(t => t.GetProperty("code").GetString()).ToList();
        Assert.Equal(["STUDENT_STATEMENT", "STUDENT_GRIEVANCE", "STUDENT_SECTION_CHANGE"], services);        // the dropdown: active services only (the generic type stays off)
        var made = await student.PostAsJsonAsync("/api/correspondence", new { tenantId = w.Tenant, type = "Internal", subject = "Statement", content = "Please", authorId = sara, priority = "Normal", isConfidential = false, recipients = Array.Empty<object>(), typeCode = "STUDENT_STATEMENT" });
        var id = (await Json(made)).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.OK, (await student.PostAsJsonAsync($"/api/correspondence/{id}/submit", new { tenantId = w.Tenant })).StatusCode);
        Assert.Equal(w.StudentAffairs, (await PendingAsync(w.Tenant, id)).AssigneeId);
        await DecideAsync(w, await PendingAsync(w.Tenant, id), true);
        Assert.Equal(w.AdminAffairs, (await PendingAsync(w.Tenant, id)).AssigneeId);                      // the college of the student's department closes it
        await DecideAsync(w, await PendingAsync(w.Tenant, id), true);
        Assert.Equal(CorrespondenceStatus.Closed, await StatusAsync(id));
    }

    [Fact]
    public async Task Financial_Limits_Are_Read_And_Set_Per_Level()
    {
        var w = await BuildAsync();
        var admin = As(w.Tenant, Manager);
        var read = await Json(await admin.GetAsync($"/api/correspondence/financial-limits?tenantId={w.Tenant}"));
        var levels = read.GetProperty("levels").EnumerateArray().ToDictionary(l => l.GetProperty("stepId").GetString()!, l => l.GetProperty("above"));
        Assert.Equal(JsonValueKind.Null, levels["line_manager"].ValueKind);                            // always signs
        Assert.Equal(50000m, levels["vp"].GetDecimal());
        Assert.Equal(250000m, levels["president"].GetDecimal());
        Task<HttpResponseMessage> Put(object[] limits) => admin.PutAsJsonAsync("/api/correspondence/financial-limits", new { tenantId = w.Tenant, limits });
        // Any level can get its own limit: here the head of the receiving unit signs only above 20,000, the VP above 80,000, the President above 400,000.
        Assert.Equal(HttpStatusCode.OK, (await Put([new { stepId = "finance_exec", above = 20000m }, new { stepId = "vp", above = 80000m }, new { stepId = "president", above = 400000m }])).StatusCode);
        var again = await Json(await admin.GetAsync($"/api/correspondence/financial-limits?tenantId={w.Tenant}"));
        Assert.Equal(read.GetProperty("version").GetInt32() + 1, again.GetProperty("version").GetInt32());   // every change is a new version
        Assert.Equal(80000m, again.GetProperty("levels").EnumerateArray().Single(l => l.GetProperty("stepId").GetString() == "vp").GetProperty("above").GetDecimal());
        // 30,000: the receiving head now signs, the VP does not.
        var id = await CreateAsync(w, "FINANCIAL", 30_000m, w.TargetUnit);
        await SubmitAsync(w, id);
        var seen = new List<Guid>();
        for (var i = 0; i < 8 && await StatusAsync(id) == CorrespondenceStatus.Submitted; i++) { var a = await PendingAsync(w.Tenant, id); seen.Add(a.AssigneeId); await DecideAsync(w, a, true); }
        Assert.Equal([w.Head, w.Finance, w.TargetHead, w.AdminAffairs], seen);
        // Rules: a limit can be cleared, unknown or non-approval steps and bad amounts are refused, and only managers set them.
        Assert.Equal(HttpStatusCode.OK, (await Put([new { stepId = "vp", above = (decimal?)null }])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put([new { stepId = "nope", above = 1m }])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put([new { stepId = "start", above = 1m }])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Put([new { stepId = "vp", above = -5m }])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(w.Tenant, Writer).PutAsJsonAsync("/api/correspondence/financial-limits", new { tenantId = w.Tenant, limits = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await As(Guid.NewGuid(), Manager).GetAsync($"/api/correspondence/financial-limits?tenantId={w.Tenant}")).StatusCode);
    }

    [Fact]
    public void Working_Days_Skip_The_Weekend_And_Holidays()
    {
        var cal = new WorkingCalendar(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, TimeSpan.FromHours(3));
        var wed = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.FromHours(3));
        var none = new HashSet<DateOnly>();
        Assert.Equal(new DateOnly(2026, 10, 29), DateOnly.FromDateTime(cal.AddWorkingDays(wed, 1, none).ToOffset(TimeSpan.FromHours(3)).DateTime));      // Thursday
        Assert.Equal(new DateOnly(2026, 11, 1), DateOnly.FromDateTime(cal.AddWorkingDays(wed, 2, none).ToOffset(TimeSpan.FromHours(3)).DateTime));       // Friday and Saturday skipped: Sunday
        Assert.Equal(new DateOnly(2026, 11, 2), DateOnly.FromDateTime(cal.AddWorkingDays(wed, 2, new HashSet<DateOnly> { new(2026, 11, 1) }).ToOffset(TimeSpan.FromHours(3)).DateTime));   // a holiday on Sunday: Monday
        Assert.Equal(new DateOnly(2026, 11, 4), DateOnly.FromDateTime(cal.AddWorkingDays(wed, 5, none).ToOffset(TimeSpan.FromHours(3)).DateTime));
        Assert.Equal(wed, cal.AddWorkingDays(wed, 0, none));
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Friday, DayOfWeek.Saturday }, WorkingCalendar.ParseWeekend("nonsense"));   // a typo never removes the weekend
        Assert.Equal(new HashSet<DayOfWeek> { DayOfWeek.Saturday, DayOfWeek.Sunday }, WorkingCalendar.ParseWeekend("saturday, Sunday"));
    }

    [Fact]
    public async Task Deadlines_Are_Counted_In_Working_Days_And_Skip_A_Holiday()
    {
        var w = await BuildAsync();
        var id0 = await CreateAsync(w, "ADMIN_MEMO", to: w.TargetHead);
        await SubmitAsync(w, id0);
        var plain = (await PendingAsync(w.Tenant, id0)).DueAt;
        var local = plain.ToOffset(TimeSpan.FromHours(3));
        Assert.DoesNotContain(local.DayOfWeek, new[] { DayOfWeek.Friday, DayOfWeek.Saturday });
        // Declare every day of the next fortnight a holiday: the same step now lands after them.
        var start = DateTimeOffset.UtcNow;
        await Db(async db =>
        {
            for (var d = 0; d < 14; d++)
                db.CalendarEvents.Add(new CalendarEvent(Guid.NewGuid(), w.Tenant, "Break", CalendarEventKind.Holiday, start.AddDays(d), start.AddDays(d).AddHours(1), null, null, null, null, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
            return 0;
        });
        var id1 = await CreateAsync(w, "ADMIN_MEMO", to: w.TargetHead);
        await SubmitAsync(w, id1);
        Assert.True((await PendingAsync(w.Tenant, id1)).DueAt > start.AddDays(14));
    }
}
