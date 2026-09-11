namespace EduNexus.Foundation.Tests;

public class FoundationTests
{
    [Fact]
    public void TC_TEN_001_CreateTenant_AuditAndEvent()
    {
        var store = new FoundationStore();
        var t = store.AddTenant("kaust", "KAUST");
        Assert.Equal("kaust", t.Slug);
        Assert.Contains(store.ListAudit(t.Id), a => a.Action == "TenantCreated");
    }

    [Fact]
    public void DuplicateSlug_Conflict()
    {
        var store = new FoundationStore();
        store.AddTenant("kaust", "KAUST");
        Assert.Throws<InvalidOperationException>(() => store.AddTenant("kaust", "Dup"));
    }

    [Fact]
    public void TC_AUTH_001_SoD_Blocked()
    {
        Assert.Throws<InvalidOperationException>(() =>
            FoundationStore.CheckSod(new[] { "finance.requester" }, "finance.approver"));
    }
}
