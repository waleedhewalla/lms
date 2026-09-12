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

    [Fact]
    public void FormValidation_Required_And_Types()
    {
        const string schema = """[{"key":"purpose","label":"Purpose","type":"Text","required":true},{"key":"amount","label":"Amount","type":"Currency","required":true},{"key":"kind","label":"Kind","type":"Dropdown","required":false}]""";
        Assert.Empty(FormValidation.Validate(schema, """{"purpose":"x","amount":5}"""));
        var errs = FormValidation.Validate(schema, """{"purpose":"x"}""");
        Assert.Contains(errs, e => e.Contains("amount"));
        errs = FormValidation.Validate(schema, """{"purpose":"x","amount":"lots"}""");
        Assert.Contains(errs, e => e.Contains("amount"));
    }

    [Fact]
    public void FormValidation_Options_And_Conditional()
    {
        const string schema = """[{"key":"kind","label":"Kind","type":"Dropdown","required":true,"options":["standard","other"]},{"key":"why","label":"Why","type":"Text","required":true,"visibleWhen":{"field":"kind","equals":"other"}}]""";
        Assert.Empty(FormValidation.Validate(schema, """{"kind":"standard"}"""));
        var errs = FormValidation.Validate(schema, """{"kind":"weird"}""");
        Assert.Contains(errs, e => e.Contains("kind"));
        errs = FormValidation.Validate(schema, """{"kind":"other"}""");
        Assert.Contains(errs, e => e.Contains("why"));
    }

    [Fact]
    public void FormValidation_BadJson()
    {
        Assert.NotEmpty(FormValidation.Validate("{oops", "{}"));
        Assert.NotEmpty(FormValidation.Validate("[]", "not-json"));
    }
}
