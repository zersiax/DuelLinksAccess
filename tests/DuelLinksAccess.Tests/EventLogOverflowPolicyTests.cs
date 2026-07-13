using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class EventLogOverflowPolicyTests
{
    [Theory]
    [InlineData(false, -1, -1)]
    [InlineData(true, 0, 0)]
    [InlineData(true, 1, 0)]
    [InlineData(true, 100, 99)]
    [InlineData(true, 199, 198)]
    public void AdjustBrowseIndex_PreservesFocusedEntryAfterHeadRemoval(
        bool browsing, int browseIndex, int expected)
    {
        Assert.Equal(expected, EventLogOverflowPolicy.AdjustBrowseIndex(
            browsing, browseIndex));
    }
}
