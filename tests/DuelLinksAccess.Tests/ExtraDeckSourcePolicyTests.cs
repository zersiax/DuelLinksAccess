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

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(1, 1, true)]
    [InlineData(1, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(0, -1, false)]
    public void CanRevealIdentity_OnlyAllowsKnownLocalPlayer(
        int requestedPlayer, int localPlayer, bool expected)
    {
        Assert.Equal(expected, ExtraDeckSourcePolicy.CanRevealIdentity(
            requestedPlayer, localPlayer));
    }
}
