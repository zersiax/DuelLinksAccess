using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class TicketExchangePolicyTests
{
    [Theory]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 0)]
    [InlineData(false, false, 2)]
    public void ChooseAction_RespectsExistingButtonGate(
        bool buttonPresent,
        bool buttonInteractable,
        int expected)
    {
        Assert.Equal(expected, (int)TicketExchangePolicy.ChooseAction(
            buttonPresent, buttonInteractable));
    }
}
