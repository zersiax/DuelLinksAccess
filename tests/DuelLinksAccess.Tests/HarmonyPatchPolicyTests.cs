using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class HarmonyPatchPolicyTests
{
    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void RequiredPatchesApplied_RequiresEveryCorePatch(
        bool push,
        bool pop,
        bool runEffect,
        bool expected)
    {
        Assert.Equal(expected,
            HarmonyPatchPolicy.RequiredPatchesApplied(push, pop, runEffect));
    }

    [Fact]
    public void IsOwned_ReturnsTrueOnlyForMatchingOwner()
    {
        string[] owners = { "another.mod", "com.duellinksaccess.mod" };

        Assert.True(HarmonyPatchPolicy.IsOwned(
            owners, "com.duellinksaccess.mod"));
        Assert.False(HarmonyPatchPolicy.IsOwned(
            owners, "missing.mod"));
        Assert.False(HarmonyPatchPolicy.IsOwned(
            null, "com.duellinksaccess.mod"));
    }

    [Theory]
    [InlineData(false, 0, 0f, 0f, true)]
    [InlineData(false, 1, 1f, 2f, false)]
    [InlineData(false, 3, 10f, 0f, false)]
    [InlineData(true, 0, 10f, 0f, false)]
    public void ShouldAttempt_LimitsAndSpacesRetries(
        bool applied,
        int attempts,
        float now,
        float nextAttempt,
        bool expected)
    {
        Assert.Equal(expected, HarmonyPatchPolicy.ShouldAttempt(
            applied, attempts, now, nextAttempt, maxAttempts: 3));
    }
}
