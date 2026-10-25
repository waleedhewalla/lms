using EduNexus.Foundation;

namespace EduNexus.Foundation.Tests;

public class HijriDateTests
{
    // Reference values cross-checked against ICU's islamic-umalqura calendar (Node Intl).
    [Theory]
    [InlineData("2026-10-25T09:00:00Z", "1448-05-14")]
    [InlineData("2026-02-18T09:00:00Z", "1447-09-01")] // 1 Ramadan 1447
    [InlineData("2025-03-01T09:00:00Z", "1446-09-01")] // 1 Ramadan 1446
    [InlineData("2024-01-01T09:00:00Z", "1445-06-19")]
    [InlineData("2030-12-31T09:00:00Z", "1452-09-06")]
    public void Converts_To_Umm_Al_Qura(string instant, string expected) =>
        Assert.Equal(expected, HijriDate.Format(DateTimeOffset.Parse(instant)));

    [Fact]
    public void Uses_The_Local_Day_Not_The_Utc_Day()
    {
        // 22:30 UTC on 24 Oct is already 01:30 on 25 Oct in Riyadh.
        var instant = DateTimeOffset.Parse("2026-10-24T22:30:00Z");
        Assert.Equal("1448-05-14", HijriDate.Format(instant));
        Assert.Equal("1448-05-13", HijriDate.Format(instant, TimeSpan.Zero));
    }

    [Fact]
    public void Returns_Null_Outside_The_Supported_Range() =>
        Assert.Null(HijriDate.Format(new DateTimeOffset(1800, 1, 1, 0, 0, 0, TimeSpan.Zero)));
}
