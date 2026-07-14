using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class ExtraDeckSourcePolicyTests
{
    [Theory]
    [InlineData(true, true, 6, true)]
    [InlineData(true, true, 1, true)]
    [InlineData(true, false, 6, false)]
    [InlineData(false, true, 6, false)]
    // A loaded place with an empty innerCards list is NOT authoritative:
    // game 10.9.0 reports isLoaded=true while the registered-deck array
    // still holds the real extra deck (2026-07-14 regression).
    [InlineData(true, true, 0, false)]
    public void UseLiveStack_RequiresPopulatedVisualStack(
        bool placeAvailable,
        bool placeLoaded,
        int cardCount,
        bool expected)
    {
        Assert.Equal(expected, ExtraDeckSourcePolicy.UseLiveStack(
            placeAvailable, placeLoaded, cardCount));
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
