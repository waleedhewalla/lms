using EduNexus.Foundation;
using Xunit;

namespace EduNexus.Foundation.Tests;

public class BoardRulesTests
{
    [Theory]
    [InlineData(3, null, 2)]
    [InlineData(4, null, 3)]   // a majority, not half
    [InlineData(9, 66.67, 6)]  // two-thirds
    [InlineData(3, 66.67, 2)]
    [InlineData(10, 50.0, 5)]  // "at least half"
    [InlineData(5, 100.0, 5)]
    [InlineData(0, null, 1)]
    public void Quorum(int members, double? percent, int expected) => Assert.Equal(expected, BoardRules.QuorumRequired(members, percent));

    [Theory]
    [InlineData(0, 0, 0, true, ItemOutcome.NoVotes)]
    [InlineData(3, 1, 0, false, ItemOutcome.NoQuorum)]
    [InlineData(2, 1, 5, true, ItemOutcome.Carried)]   // abstentions do not count against
    [InlineData(1, 2, 0, true, ItemOutcome.Failed)]
    [InlineData(2, 2, 1, true, ItemOutcome.Tied)]
    public void Item_Outcome(int f, int a, int ab, bool quorate, ItemOutcome expected) => Assert.Equal(expected, BoardRules.Outcome(f, a, ab, quorate));

    [Theory]
    [InlineData(5, 3, 0, false, false, ResolutionStatus.Passed)]   // majority of all 5 reached early
    [InlineData(5, 2, 0, false, false, ResolutionStatus.Open)]
    [InlineData(5, 2, 3, false, false, ResolutionStatus.Failed)]   // cannot reach 3
    [InlineData(5, 2, 0, false, true, ResolutionStatus.Expired)]
    [InlineData(3, 2, 0, true, false, ResolutionStatus.Open)]      // unanimity still possible
    [InlineData(3, 2, 1, true, false, ResolutionStatus.Failed)]
    [InlineData(3, 3, 0, true, false, ResolutionStatus.Passed)]
    [InlineData(0, 0, 0, false, true, ResolutionStatus.Expired)]
    public void Written_Resolution(int eligible, int f, int other, bool unanimous, bool closed, ResolutionStatus expected) =>
        Assert.Equal(expected, BoardRules.Decide(eligible, f, other, unanimous, closed));

    [Fact]
    public void Ended_Terms_Do_Not_Count()
    {
        var now = DateTimeOffset.UtcNow;
        var m = new CommitteeMember(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Member", now.AddYears(-1), now.AddDays(-1));
        Assert.False(BoardRules.IsCurrent(m, now));
        Assert.True(BoardRules.IsCurrent(m with { TermEndsAt = null }, now));
    }
}
