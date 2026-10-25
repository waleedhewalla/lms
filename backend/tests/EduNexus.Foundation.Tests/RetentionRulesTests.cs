using EduNexus.Foundation;

namespace EduNexus.Foundation.Tests;

public class RetentionRulesTests
{
    private static readonly DateTimeOffset Created = new(2015, 3, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Eligible_After_The_Retention_Period() =>
        Assert.Equal(new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero), RetentionRules.EligibleAt(Created, 10, Disposition.Destroy));

    [Fact]
    public void Retain_Until_Extends_But_Never_Shortens()
    {
        var later = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(later, RetentionRules.EligibleAt(Created, 10, Disposition.Destroy, later));
        var earlier = new DateTimeOffset(2016, 1, 1, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(Created.AddYears(10), RetentionRules.EligibleAt(Created, 10, Disposition.Destroy, earlier));
    }

    [Fact]
    public void Permanent_Records_Are_Never_Eligible() =>
        Assert.Null(RetentionRules.EligibleAt(Created, null, Disposition.Permanent));

    [Theory]
    [InlineData(CorrespondenceStatus.Approved, true)]
    [InlineData(CorrespondenceStatus.Archived, true)]
    [InlineData(CorrespondenceStatus.Rejected, true)]
    [InlineData(CorrespondenceStatus.Draft, false)]
    [InlineData(CorrespondenceStatus.InReview, false)]
    public void Only_Closed_Correspondence_Can_Be_Disposed(CorrespondenceStatus status, bool closed) =>
        Assert.Equal(closed, RetentionRules.IsClosed(status));

    [Theory]
    [InlineData("", "n", "Document", 5, Disposition.Destroy)]
    [InlineData("C", "n", "Meeting", 5, Disposition.Destroy)]
    [InlineData("C", "n", "Document", 5, Disposition.Permanent)]
    [InlineData("C", "n", "Document", null, Disposition.Destroy)]
    [InlineData("C", "n", "Document", 0, Disposition.TransferToArchive)]
    public void Rejects_Invalid_Schedules(string code, string name, string type, int? years, Disposition d) =>
        Assert.NotNull(RetentionRules.Validate(code, name, type, years, d));

    [Fact]
    public void Accepts_Valid_Schedules()
    {
        Assert.Null(RetentionRules.Validate("ADM-01", "Routine letters", "Correspondence", 5, Disposition.Destroy));
        Assert.Null(RetentionRules.Validate("GOV-01", "Board resolutions", "Document", null, Disposition.Permanent));
    }
}
