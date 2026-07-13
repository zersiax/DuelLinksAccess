using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class TradeExecutionPolicyTests
{
    [Theory]
    [InlineData(42, 42, true, true, true)]
    [InlineData(42, 43, true, true, false)]
    [InlineData(42, -1, true, true, false)]
    [InlineData(42, 42, false, true, false)]
    [InlineData(42, 42, true, false, false)]
    public void CanClick_RequiresMatchingSelectionAndUsableButton(
        int expectedItemId,
        int actualItemId,
        bool buttonActive,
        bool buttonInteractable,
        bool expected)
    {
        Assert.Equal(expected, TradeExecutionPolicy.CanClick(
            expectedItemId,
            actualItemId,
            buttonActive,
            buttonInteractable));
    }
}
