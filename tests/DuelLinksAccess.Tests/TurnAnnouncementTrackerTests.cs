using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class TurnAnnouncementTrackerTests
{
    [Fact]
    public void ShouldAnnounce_SuppressesExactDuplicate()
    {
        var tracker = new TurnAnnouncementTracker();

        Assert.True(tracker.ShouldAnnounce(1, 0));
        Assert.False(tracker.ShouldAnnounce(1, 0));
    }

    [Fact]
    public void ShouldAnnounce_AllowsConsecutiveTurnsForSamePlayer()
    {
        var tracker = new TurnAnnouncementTracker();

        Assert.True(tracker.ShouldAnnounce(1, 0));
        Assert.True(tracker.ShouldAnnounce(2, 0));
    }

    [Fact]
    public void Reset_AllowsCurrentTurnAgain()
    {
        var tracker = new TurnAnnouncementTracker();
        tracker.ShouldAnnounce(1, 0);

        tracker.Reset();

        Assert.True(tracker.ShouldAnnounce(1, 0));
    }
}
