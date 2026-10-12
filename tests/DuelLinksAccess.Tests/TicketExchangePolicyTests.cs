using Xunit;

namespace DuelLinksAccess.Tests;

public sealed class TicketExchangePolicyTests
{
    [Theory]
    // decideButton present: it is the mode's real gate and wins outright.
    [InlineData(true, true, false, false, TicketExchangeAction.ConfirmViaDecide)]
    [InlineData(true, false, false, false, TicketExchangeAction.Reject)]
    // decideButton present-but-disabled is rejected even if exchangeButton
    // would be interactable — never confirm against the wrong control.
    [InlineData(true, false, true, true, TicketExchangeAction.Reject)]
    // decideButton absent: fall back to exchangeButton's own gate.
    [InlineData(false, false, true, true, TicketExchangeAction.InvokeExchangeButton)]
    [InlineData(false, false, true, false, TicketExchangeAction.Reject)]
    // Neither control available: refuse rather than blindly calling
    // DecideClicked() (the old bug that hard-errored the game).
    [InlineData(false, false, false, false, TicketExchangeAction.Reject)]
    public void ChooseAction_GatesOnActiveConfirmButton(
        bool decidePresent,
        bool decideInteractable,
        bool exchangePresent,
        bool exchangeInteractable,
        TicketExchangeAction expected)
    {
        Assert.Equal(expected, TicketExchangePolicy.ChooseAction(
            decidePresent, decideInteractable,
            exchangePresent, exchangeInteractable));
    }

    [Theory]
    [InlineData("敬意宝珠兑换", "RespectOrb", "敬意宝珠兑换")]
    [InlineData("EX宝珠兑换", "ExChgKira2", "EX宝珠兑换")]
    [InlineData("BOX芯片兑换", "BoxChip", "BOX芯片兑换")]
    [InlineData("", "RespectOrb", "敬意宝珠兑换")]
    [InlineData(null, "BoxChip", "BOX芯片兑换")]
    [InlineData(null, "CardTicket", "兑换券兑换")]
    [InlineData(null, "CardExchange", "卡片交换")]
    [InlineData(null, "UnknownMode", "卡片兑换")]
    public void ResolveTitle_ReturnsExpectedTitle(string rawTitle, string modeName, string expected)
    {
        Assert.Equal(expected, TicketExchangePolicy.ResolveTitle(rawTitle, modeName));
    }

    [Theory]
    [InlineData("敬意宝珠兑换", "RespectOrb", null, "敬意宝珠")]
    [InlineData("EX宝珠兑换", "ExChgKira2", null, "EX宝珠")]
    [InlineData("BOX芯片兑换", "BoxChip", null, "BOX芯片")]
    [InlineData("兑换券兑换", "CardTicket", null, "兑换券")]
    [InlineData(null, "RespectOrb", null, "敬意宝珠")]
    [InlineData(null, "BoxChip", null, "BOX芯片")]
    [InlineData(null, "ExChgKira2", null, "EX宝珠")]
    [InlineData(null, "CardTicket", null, "兑换券")]
    [InlineData(null, null, "Images/Item/RespectOrb", "敬意宝珠")]
    [InlineData(null, null, "Images/Item/ExJewel", "宝珠")]
    [InlineData(null, null, "Images/Item/BoxChip", "BOX芯片")]
    [InlineData(null, null, null, "兑换券")]
    public void ResolveCurrencyName_ReturnsExpectedCurrency(string rawTitle, string modeName, string needPath, string expected)
    {
        Assert.Equal(expected, TicketExchangePolicy.ResolveCurrencyName(rawTitle, modeName, needPath));
    }
}

