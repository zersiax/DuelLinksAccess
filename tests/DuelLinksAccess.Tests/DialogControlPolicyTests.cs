using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class DialogControlPolicyTests
{
    [Theory]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, false, true, true, true)]
    [InlineData(true, false, true, false, false)]
    [InlineData(false, false, true, false, true)]
    [InlineData(false, false, false, false, false)]
    public void ShouldInclude_OnlyKeepsUsableControlsAndSelectedTabs(
        bool hasSelectable,
        bool interactable,
        bool nameLooksInteractive,
        bool selectedTab,
        bool expected)
    {
        Assert.Equal(expected, DialogControlPolicy.ShouldInclude(
            hasSelectable,
            interactable,
            nameLooksInteractive,
            selectedTab));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void CanActivate_RejectsDisabledSelectable(
        bool hasSelectable, bool interactable, bool expected)
    {
        Assert.Equal(expected, DialogControlPolicy.CanActivate(
            hasSelectable, interactable));
    }
}
