using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class ExtraDeckSourcePolicyTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, false, false)]
    public void UseLiveStack_RequiresLoadedVisualStack(
        bool placeAvailable,
        bool placeLoaded,
        bool cardsAvailable,
        bool expected)
    {
        Assert.Equal(expected, ExtraDeckSourcePolicy.UseLiveStack(
            placeAvailable, placeLoaded, cardsAvailable));
    }
}
