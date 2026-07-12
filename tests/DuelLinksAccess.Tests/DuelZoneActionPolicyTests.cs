using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class DuelZoneActionPolicyTests
{
    [Theory]
    [InlineData(true, false, 1, 0, true)]
    [InlineData(false, true, 0, 0, true)]
    [InlineData(false, true, 1, 0, false)]
    [InlineData(false, false, 0, 0, false)]
    public void IsLocalMonster_ResolvesSharedExtraMonsterOwner(
        bool mainMonsterZone,
        bool sharedExtraMonsterZone,
        int owner,
        int localPlayer,
        bool expected)
    {
        Assert.Equal(expected, DuelZoneActionPolicy.IsLocalMonster(
            mainMonsterZone,
            sharedExtraMonsterZone,
            owner,
            localPlayer));
    }
}
