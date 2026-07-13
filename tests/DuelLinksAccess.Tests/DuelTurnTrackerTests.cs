using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class DuelTurnTrackerTests
{
    [Fact]
    public void ObservePhase_InitializesOpeningTurn()
    {
        var tracker = new DuelTurnTracker();

        tracker.ObservePhase(0);

        Assert.Equal(0, tracker.CurrentPlayer);
        Assert.Equal(1, tracker.TurnNumber);
    }

    [Fact]
    public void ObserveTurnChange_AfterOpeningPhaseStartsTurnTwo()
    {
        var tracker = new DuelTurnTracker();
        tracker.ObservePhase(1);

        tracker.ObserveTurnChange(0);

        Assert.Equal(0, tracker.CurrentPlayer);
        Assert.Equal(2, tracker.TurnNumber);
    }

    [Fact]
    public void ObserveTurnChange_InitializesResumedTurnWhenNoPhaseWasSeen()
    {
        var tracker = new DuelTurnTracker();

        tracker.ObserveTurnChange(1);

        Assert.Equal(1, tracker.CurrentPlayer);
        Assert.Equal(1, tracker.TurnNumber);
    }

    [Fact]
    public void InvalidPlayer_DoesNotChangeTurnState()
    {
        var tracker = new DuelTurnTracker();

        tracker.ObservePhase(-1);
        tracker.ObserveTurnChange(2);

        Assert.Equal(-1, tracker.CurrentPlayer);
        Assert.Equal(0, tracker.TurnNumber);
    }
}
