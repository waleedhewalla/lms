using EduNexus.Foundation;
using Xunit;

namespace EduNexus.Foundation.Tests;

public class AccreditationTests
{
    [Theory]
    [InlineData(0, 0, 0, CriterionStatus.NotMet)]
    [InlineData(0, 1, 0, CriterionStatus.NotMet)]
    [InlineData(2, 1, 0, CriterionStatus.Partial)]
    [InlineData(2, 0, 1, CriterionStatus.Partial)]
    [InlineData(1, 0, 0, CriterionStatus.Met)]
    public void Assess_Criterion(int evidence, int major, int minor, CriterionStatus expected) =>
        Assert.Equal(expected, Accreditation.Assess(evidence, major, minor));

    [Fact]
    public void Readiness_Counts_Partial_As_Half() =>
        Assert.Equal(50, Accreditation.ReadinessPercent([CriterionStatus.Met, CriterionStatus.Partial, CriterionStatus.NotMet, CriterionStatus.Partial]));

    [Fact]
    public void Readiness_Of_Nothing_Is_Zero() => Assert.Equal(0, Accreditation.ReadinessPercent([]));

    private static Kpi K(double current, double target, double? baseline = null, bool lower = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "k", target, current, "%", baseline, lower);

    [Theory]
    [InlineData(90, 90, null, false, KpiStatus.Achieved)]
    [InlineData(80, 100, null, false, KpiStatus.OnTrack)]
    [InlineData(60, 100, 20.0, false, KpiStatus.AtRisk)]   // (60-20)/(100-20) = 0.5
    [InlineData(30, 100, 20.0, false, KpiStatus.OffTrack)]
    [InlineData(12, 10, 20.0, true, KpiStatus.OnTrack)]    // dropout 20 → 12, target 10: 0.8
    [InlineData(9, 10, 20.0, true, KpiStatus.Achieved)]
    [InlineData(25, 10, 20.0, true, KpiStatus.OffTrack)]   // moved the wrong way
    public void Kpi_Status_Is_Direction_Aware(double current, double target, double? baseline, bool lower, KpiStatus expected) =>
        Assert.Equal(expected, KpiProgress.Status(K(current, target, baseline, lower)));

    [Fact]
    public void Kpi_Fraction_Undefined_When_Target_Equals_Baseline() => Assert.Null(KpiProgress.Fraction(K(5, 10, 10)));

    [Fact]
    public void Library_Has_Unique_Codes_And_Criteria()
    {
        var codes = AccreditationLibrary.Frameworks.SelectMany(f => f.Standards).Select(s => s.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.All(AccreditationLibrary.Frameworks.SelectMany(f => f.Standards), s => Assert.NotEmpty(s.Criteria));
        Assert.Equal(7, AccreditationLibrary.Find("abet-eac")!.Standards.Single(s => s.Code == "ABET-3").Criteria.Count);
        Assert.Null(AccreditationLibrary.Find("nope"));
    }
}

public class CsvTextTests
{
    [Fact]
    public void Parses_Quotes_Escapes_And_Line_Endings()
    {
        var rows = CsvText.Parse("﻿a,b\r\n\"x, y\",\"say \"\"hi\"\"\"\n\nlast,\"multi\nline\"");
        Assert.Equal(3, rows.Count);
        Assert.Equal(["a", "b"], rows[0]);
        Assert.Equal(["x, y", "say \"hi\""], rows[1]);
        Assert.Equal(["last", "multi\nline"], rows[2]);
    }
}
