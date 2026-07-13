using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class DuelPositionTrackerTests
{
    public DuelPositionTrackerTests()
    {
        DuelPositionTracker.Reset();
    }

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
    public void ApplyPositionChange_UsesObservedLivePosition()
    {
        bool? result = DuelPositionTracker.ApplyPositionChange(
            42, observedIsDefense: false);

        Assert.False(result);
        Assert.False(DuelPositionTracker.IsDefense(42));
    }

    [Fact]
    public void ApplyPositionChange_TogglesKnownPositionWithoutObservation()
    {
        DuelPositionTracker.SetDefense(42);

        bool? result = DuelPositionTracker.ApplyPositionChange(
            42, observedIsDefense: null);

        Assert.False(result);
    }

    [Fact]
    public void ApplyPositionChange_DoesNotGuessUnknownPosition()
    {
        bool? result = DuelPositionTracker.ApplyPositionChange(
            42, observedIsDefense: null);

        Assert.Null(result);
        Assert.Null(DuelPositionTracker.IsDefense(42));
    }

    [Fact]
    public void InvalidUniqueId_IsNeverTracked()
    {
        DuelPositionTracker.SetDefense(0);

        Assert.Null(DuelPositionTracker.IsDefense(0));
    }
}
