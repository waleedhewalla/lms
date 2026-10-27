using EduNexus.Foundation;
using Xunit;

namespace EduNexus.Foundation.Tests;

public class PolicyAttestationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);
    private static Policy P(int version, int? months) => new(Guid.NewGuid(), Guid.NewGuid(), "P", "T", "C", PolicyStatus.Published, version, AttestEveryMonths: months);
    private static PolicyAcknowledgement A(int version, DateTimeOffset at) => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), at, version);

    [Fact]
    public void New_Version_Requires_Reattestation()
    {
        Assert.True(PolicyAttestation.IsCurrent(A(2, Now.AddYears(-3)), P(2, null), Now));
        Assert.False(PolicyAttestation.IsCurrent(A(1, Now.AddDays(-1)), P(2, null), Now));
    }

    [Fact]
    public void Cycle_Expires_Attestations()
    {
        Assert.True(PolicyAttestation.IsCurrent(A(1, Now.AddMonths(-11)), P(1, 12), Now));
        Assert.False(PolicyAttestation.IsCurrent(A(1, Now.AddMonths(-13)), P(1, 12), Now));
        Assert.Equal(Now.AddMonths(1), PolicyAttestation.DueAgain(A(1, Now.AddMonths(-11)), P(1, 12)));
        Assert.Null(PolicyAttestation.DueAgain(A(1, Now), P(1, null)));
    }

    [Fact]
    public void Quiz_Score_Is_In_Question_Order_And_Unanswered_Is_Wrong()
    {
        var t = Guid.NewGuid(); var pid = Guid.NewGuid();
        var qs = new[]
        {
            new PolicyQuizQuestion(Guid.NewGuid(), t, pid, 2, "Q2", "a\nb", 1),
            new PolicyQuizQuestion(Guid.NewGuid(), t, pid, 1, "Q1", "a\nb\nc", 2),
            new PolicyQuizQuestion(Guid.NewGuid(), t, pid, 3, "Q3", "a\nb", 0),
        };
        Assert.Equal(100, PolicyAttestation.Score(qs, [2, 1, 0]));
        Assert.Equal(66, PolicyAttestation.Score(qs, [2, 1]));
        Assert.Equal(0, PolicyAttestation.Score(qs, null));
        Assert.Equal(100, PolicyAttestation.Score([], null));
        Assert.Equal(["a", "b", "c"], qs[1].Options);
    }
}
