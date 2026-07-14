using System;
using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class AutomaticDrawControllerTests
{
    private const float T0 = 100f;
    private const float AfterGestureWait =
        T0 + AutomaticDrawController.GestureWaitSeconds + 0.1f;

    [Fact]
    public void Update_ArmsGestureWithoutDispatchingCommand()
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;
        int completions = 0;

        bool fallback = controller.Update(true, true, true, T0, () => dispatches++);
        bool swiped = controller.CompleteGesture(true, T0 + 0.5f, () => completions++);

        Assert.False(fallback);
        Assert.Equal(0, dispatches);
        Assert.True(swiped);
        Assert.Equal(1, completions);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Update_DoesNotArmWhenStateIsIneligible(
        bool duelActive,
        bool localTurn,
        bool drawPhase)
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(duelActive, localTurn, drawPhase, T0, () => { });
        bool swiped = controller.CompleteGesture(true, T0, () => completions++);

        Assert.False(swiped);
        Assert.Equal(0, completions);
    }

    [Fact]
    public void Update_DispatchesFallbackWhenPromptNeverBecomesTouchable()
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;

        controller.Update(true, true, true, T0, () => dispatches++);
        bool early = controller.Update(true, true, true, T0 + 1f, () => dispatches++);
        bool late = controller.Update(true, true, true, AfterGestureWait, () => dispatches++);

        Assert.False(early);
        Assert.True(late);
        Assert.Equal(1, dispatches);
    }

    [Fact]
    public void Update_DispatchesFallbackWhenGestureDidNotAdvanceEngine()
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;

        controller.Update(true, true, true, T0, () => dispatches++);
        controller.CompleteGesture(true, T0 + 0.5f, () => { });

        float beforeDeadline = T0 + 0.5f
            + AutomaticDrawController.FallbackAfterGestureSeconds - 0.1f;
        float afterDeadline = T0 + 0.5f
            + AutomaticDrawController.FallbackAfterGestureSeconds + 0.1f;
        bool early = controller.Update(true, true, true, beforeDeadline, () => dispatches++);
        bool late = controller.Update(true, true, true, afterDeadline, () => dispatches++);

        Assert.False(early);
        Assert.True(late);
        Assert.Equal(1, dispatches);
    }

    [Fact]
    public void Update_DispatchesFallbackAtMostOncePerDrawPhase()
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;

        controller.Update(true, true, true, T0, () => dispatches++);
        controller.Update(true, true, true, AfterGestureWait, () => dispatches++);
        bool again = controller.Update(
            true, true, true, AfterGestureWait + 5f, () => dispatches++);

        Assert.False(again);
        Assert.Equal(1, dispatches);
    }

    [Fact]
    public void FallbackDispatch_CancelsPendingGesture()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        controller.Update(true, true, true, AfterGestureWait, () => { });
        bool swiped = controller.CompleteGesture(
            true, AfterGestureWait, () => completions++);

        Assert.False(swiped);
        Assert.Equal(0, completions);
    }

    [Fact]
    public void LeavingDrawPhase_CancelsPendingGesture()
    {
        // The draw resolved without our swipe — a late swipe on the
        // lingering prompt would replay the draw presentation.
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        controller.Update(true, true, false, T0 + 1f, () => { });
        bool swiped = controller.CompleteGesture(true, T0 + 1f, () => completions++);

        Assert.False(swiped);
        Assert.Equal(0, completions);
    }

    [Fact]
    public void LeavingDrawPhase_PreservesPendingDetailCompletion()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        controller.CompleteGesture(true, T0 + 0.5f, () => { });
        controller.Update(true, true, false, T0 + 1f, () => { });
        bool completed = controller.CompleteDetail(true, () => completions++);

        Assert.True(completed);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void ReenteringDrawPhase_ArmsANewGesture()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        controller.CompleteGesture(true, T0 + 0.5f, () => completions++);
        controller.Update(true, true, false, T0 + 1f, () => { });
        controller.Update(true, true, true, T0 + 30f, () => { });
        bool swiped = controller.CompleteGesture(true, T0 + 30.5f, () => completions++);

        Assert.True(swiped);
        Assert.Equal(2, completions);
    }

    [Fact]
    public void Retry_DispatchesImmediatelyDuringEligibleDrawPhase()
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;

        controller.Update(true, true, true, T0, () => { });
        bool retried = controller.Retry(true, true, true, () => dispatches++);

        Assert.True(retried);
        Assert.Equal(1, dispatches);
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Retry_DoesNotDispatchWhenStateIsIneligible(
        bool duelActive,
        bool localTurn,
        bool drawPhase)
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;

        bool retried = controller.Retry(
            duelActive, localTurn, drawPhase, () => dispatches++);

        Assert.False(retried);
        Assert.Equal(0, dispatches);
    }

    [Fact]
    public void Retry_CancelsPendingGesture()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        controller.Retry(true, true, true, () => { });
        bool swiped = controller.CompleteGesture(true, T0 + 1f, () => completions++);

        Assert.False(swiped);
        Assert.Equal(0, completions);
    }

    [Fact]
    public void FailedFallbackDispatch_CanBeAttemptedAgain()
    {
        var controller = new AutomaticDrawController();
        int dispatches = 0;

        controller.Update(true, true, true, T0, () => { });
        Assert.Throws<InvalidOperationException>(() =>
            controller.Update(true, true, true, AfterGestureWait, () =>
            {
                dispatches++;
                throw new InvalidOperationException("not ready");
            }));

        bool retry = controller.Update(
            true, true, true, AfterGestureWait + 1f, () => dispatches++);

        Assert.True(retry);
        Assert.Equal(2, dispatches);
    }

    [Fact]
    public void GestureCompletion_WaitsUntilVisualOperationIsReady()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        bool early = controller.CompleteGesture(false, T0, () => completions++);
        bool ready = controller.CompleteGesture(true, T0 + 0.5f, () => completions++);

        Assert.False(early);
        Assert.True(ready);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void GestureCompletion_RunsAtMostOnce()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        bool first = controller.CompleteGesture(true, T0 + 0.5f, () => completions++);
        bool second = controller.CompleteGesture(true, T0 + 0.6f, () => completions++);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void FailedGestureCompletion_CanBeAttemptedAgain()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        Assert.Throws<InvalidOperationException>(() =>
            controller.CompleteGesture(true, T0 + 0.5f, () =>
            {
                completions++;
                throw new InvalidOperationException("not ready");
            }));

        bool retry = controller.CompleteGesture(true, T0 + 0.6f, () => completions++);

        Assert.True(retry);
        Assert.Equal(2, completions);
    }

    [Fact]
    public void DetailCompletion_WaitsUntilVisualOperationIsReady()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        bool early = controller.CompleteDetail(false, () => completions++);
        bool ready = controller.CompleteDetail(true, () => completions++);

        Assert.False(early);
        Assert.True(ready);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void DetailCompletion_RunsAtMostOnce()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        bool first = controller.CompleteDetail(true, () => completions++);
        bool second = controller.CompleteDetail(true, () => completions++);

        Assert.True(first);
        Assert.False(second);
        Assert.Equal(1, completions);
    }

    [Fact]
    public void Reset_ClearsPendingPresentationCompletions()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        controller.Reset();
        bool gestureCompleted = controller.CompleteGesture(
            true, T0 + 0.5f, () => completions++);
        bool detailCompleted = controller.CompleteDetail(
            true, () => completions++);

        Assert.False(gestureCompleted);
        Assert.False(detailCompleted);
        Assert.Equal(0, completions);
    }

    [Fact]
    public void LeavingLocalTurn_ClearsAllPendingCompletions()
    {
        var controller = new AutomaticDrawController();
        int completions = 0;

        controller.Update(true, true, true, T0, () => { });
        controller.Update(true, false, false, T0 + 1f, () => { });
        bool gestureCompleted = controller.CompleteGesture(
            true, T0 + 1f, () => completions++);
        bool detailCompleted = controller.CompleteDetail(
            true, () => completions++);

        Assert.False(gestureCompleted);
        Assert.False(detailCompleted);
        Assert.Equal(0, completions);
    }
}
