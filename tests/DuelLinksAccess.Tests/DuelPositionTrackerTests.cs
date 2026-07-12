using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class DuelPositionTrackerTests
{
    [Fact]
    public void Reset_ClearsTrackedPositions()
    {
        DuelPositionTracker.SetDefense(42);

        DuelPositionTracker.Reset();

        Assert.Null(DuelPositionTracker.IsDefense(42));
    }

    [Fact]
    public void SetAndToggle_UpdateKnownPosition()
    {
        DuelPositionTracker.SetAttack(42);
        Assert.False(DuelPositionTracker.IsDefense(42));

        DuelPositionTracker.Toggle(42);

        Assert.True(DuelPositionTracker.IsDefense(42));
    }

    [Fact]
    public void InvalidUniqueId_IsNeverTracked()
    {
        DuelPositionTracker.SetDefense(0);

        Assert.Null(DuelPositionTracker.IsDefense(0));
    }
}
