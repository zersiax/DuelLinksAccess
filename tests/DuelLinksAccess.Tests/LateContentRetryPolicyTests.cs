using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class LateContentRetryPolicyTests
{
    [Theory]
    [InlineData(0, false, false, true)]
    [InlineData(0, true, false, false)]
    [InlineData(1, false, false, false)]
    [InlineData(0, false, true, false)]
    public void ShouldRetry_OnlyForEmptyNonTextContent(
        int itemCount,
        bool textMode,
        bool emptyTutorialArrow,
        bool expected)
    {
        Assert.Equal(expected, LateContentRetryPolicy.ShouldRetry(
            itemCount, textMode, emptyTutorialArrow));
    }

    [Theory]
    [InlineData(1, 2.0, 5.0, 2.0)]
    [InlineData(2, 2.0, 5.0, 2.0)]
    [InlineData(3, 2.0, 5.0, 5.0)]
    [InlineData(10, 1.0, 5.0, 5.0)]
    public void GetDelay_SlowsAfterInitialAttempts(
        int attempt,
        float initialDelay,
        float slowDelay,
        float expected)
    {
        Assert.Equal(expected, LateContentRetryPolicy.GetDelay(
            attempt, initialDelay, slowDelay));
    }
}
