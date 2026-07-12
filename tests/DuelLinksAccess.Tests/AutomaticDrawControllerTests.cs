using System;
using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class AutomaticDrawControllerTests
{
    [Fact]
    public void Update_DispatchesOnceDuringContinuousEligibleDrawPhase()
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;

        bool first = controller.Update(true, true, true, () => dispatches++);
        bool second = controller.Update(true, true, true, () => dispatches++);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, dispatches);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Update_DoesNotDispatchWhenStateIsIneligible(
        bool duelActive,
        bool localTurn,
        bool drawPhase)
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;

        bool dispatched = controller.Update(
            duelActive,
            localTurn,
            drawPhase,
            () => dispatches++);

        Assert.False(dispatched);
        Assert.Equal(0, dispatches);
    }

    [Fact]
    public void Update_DispatchesAgainAfterLeavingDrawPhase()
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;

        controller.Update(true, true, true, () => dispatches++);
        controller.Update(true, true, false, () => dispatches++);
        bool nextDraw = controller.Update(true, true, true, () => dispatches++);

        Assert.True(nextDraw);
        Assert.Equal(2, dispatches);
    }

    [Fact]
    public void Retry_DispatchesAgainDuringEligibleDrawPhase()
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;

        controller.Update(true, true, true, () => dispatches++);
        bool retried = controller.Retry(true, true, true, () => dispatches++);

        Assert.True(retried);
        Assert.Equal(2, dispatches);
    }

    [Fact]
    public void FailedDispatch_CanBeAttemptedAgain()
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;

        Assert.Throws<InvalidOperationException>(() =>
            controller.Update(true, true, true, () =>
            {
                dispatches++;
                throw new InvalidOperationException("not ready");
            }));

        bool retry = controller.Update(true, true, true, () => dispatches++);

        Assert.True(retry);
        Assert.Equal(2, dispatches);
    }

    [Fact]
    public void SuccessfulDispatch_ArmsOneGestureCompletion()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, () => { });
        bool first = controller.CompleteGesture(true, () => completions++);
        bool second = controller.CompleteGesture(true, () => completions++);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void SuccessfulDispatch_ArmsOneDetailCompletion()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, () => { });
        bool first = controller.CompleteDetail(true, () => completions++);
        bool second = controller.CompleteDetail(true, () => completions++);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void DetailCompletion_WaitsUntilVisualOperationIsReady()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, () => { });
        bool early = controller.CompleteDetail(false, () => completions++);
        bool ready = controller.CompleteDetail(true, () => completions++);

        Assert.False(early);
        Assert.True(ready);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void GestureCompletion_WaitsUntilVisualOperationIsReady()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, () => { });
        bool early = controller.CompleteGesture(false, () => completions++);
        bool ready = controller.CompleteGesture(true, () => completions++);

        Assert.False(early);
        Assert.True(ready);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void FailedGestureCompletion_CanBeAttemptedAgain()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, () => { });
        Assert.Throws<InvalidOperationException>(() =>
            controller.CompleteGesture(true, () =>
            {
                completions++;
                throw new InvalidOperationException("not ready");
            }));

        bool retry = controller.CompleteGesture(true, () => completions++);

        Assert.True(retry);
        Assert.Equal(2, completions);
    }

    [Fact]
    public void Reset_ClearsPendingPresentationCompletions()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, () => { });
        controller.Reset();
        bool gestureCompleted = controller.CompleteGesture(
            true, () => completions++);
        bool detailCompleted = controller.CompleteDetail(
            true, () => completions++);

        Assert.False(gestureCompleted);
        Assert.False(detailCompleted);
        Assert.Equal(0, completions);
    }

    [Fact]
    public void LeavingDrawPhase_PreservesPendingGestureCompletion()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, () => { });
        controller.Update(true, true, false, () => { });
        bool completed = controller.CompleteGesture(true, () => completions++);

        Assert.True(completed);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void LeavingLocalTurn_ClearsPendingGestureCompletion()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, () => { });
        controller.Update(true, false, false, () => { });
        bool gestureCompleted = controller.CompleteGesture(
            true, () => completions++);
        bool detailCompleted = controller.CompleteDetail(
            true, () => completions++);

        Assert.False(gestureCompleted);
        Assert.False(detailCompleted);
        Assert.Equal(0, completions);
    }
}
