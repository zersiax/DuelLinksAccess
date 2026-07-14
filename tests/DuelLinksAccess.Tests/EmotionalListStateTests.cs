using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class EmotionalListStateTests
{
    [Fact]
    public void Reset_ClearsEverySessionField()
    {
        var state = new EmotionalListState
        {
            IsActive = true,
            Index = 4,
            Count = 9,
            IsHandled = true,
            HandledUntil = 12.5f,
            ViewOnly = true,
        };

        state.Reset();

        Assert.False(state.IsActive);
        Assert.Equal(0, state.Index);
        Assert.Equal(0, state.Count);
        Assert.False(state.IsHandled);
        Assert.Equal(0f, state.HandledUntil);
        Assert.False(state.ViewOnly);
    }

    [Fact]
    public void MarkHandled_SetsDeadlineFromCurrentTime()
    {
        var state = new EmotionalListState();

        state.MarkHandled(now: 10f, timeout: 0.4f);

        Assert.True(state.IsHandled);
        Assert.Equal(10.4f, state.HandledUntil);
    }
}
