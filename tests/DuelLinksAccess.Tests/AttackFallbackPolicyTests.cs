using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class AttackFallbackPolicyTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    public void ShouldRetry_RequiresFailedGestureAndActiveBattleInput(
        bool autoAttack, bool battleInputActive, bool expected)
    {
        Assert.Equal(expected, AttackFallbackPolicy.ShouldRetry(
            autoAttack, battleInputActive));
    }
}
