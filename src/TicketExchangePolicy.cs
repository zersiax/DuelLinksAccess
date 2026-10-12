namespace DuelLinksAccess
{
    public enum TicketExchangeAction
    {
        /// <summary>No confirm control is currently valid — do nothing.</summary>
        Reject,

        /// <summary>Invoke exchangeButton.onClick (card-trader-style modes).</summary>
        InvokeExchangeButton,

        /// <summary>Call DecideClicked() (ticket / dream-ticket / choice flow).</summary>
        ConfirmViaDecide,
    }

    public static class TicketExchangePolicy
    {
        /// <summary>
        /// Decides how — and whether — to submit a CardGetter confirmation.
        ///
        /// CardGetterViewController exposes two possible confirm controls:
        ///   - <c>decideButton</c>, driven by the game's <c>setDecideButton()</c>,
        ///     is the confirm for the ticket / dream-ticket / choice flow.
        ///     <c>exchangeButton</c> is null in those modes.
        ///   - <c>exchangeButton</c> is the confirm for card-trader-style modes.
        ///
        /// Whichever control the active mode uses, its <c>interactable</c> flag is
        /// the game's authoritative "a valid selection may be submitted now" gate.
        /// We must never submit while the active control is non-interactable:
        /// calling <c>DecideClicked()</c> with an empty or already-consumed
        /// selection makes the game hard-error to the title screen
        /// ("An error has occurred"). Blindly calling <c>DecideClicked()</c>
        /// whenever <c>exchangeButton</c> happened to be null was exactly that bug.
        /// </summary>
        public static TicketExchangeAction ChooseAction(
            bool decidePresent, bool decideInteractable,
            bool exchangePresent, bool exchangeInteractable)
        {
            // decideButton, when present, is the mode's real confirm gate.
            if (decidePresent)
                return decideInteractable
                    ? TicketExchangeAction.ConfirmViaDecide
                    : TicketExchangeAction.Reject;

            // Otherwise fall back to the card-trader exchange button.
            if (exchangePresent)
                return exchangeInteractable
                    ? TicketExchangeAction.InvokeExchangeButton
                    : TicketExchangeAction.Reject;

            return TicketExchangeAction.Reject;
        }

        /// <summary>
        /// Derives a clean user-facing screen title for CardGetter modes.
        /// </summary>
        public static string ResolveTitle(string rawTitle, string modeName)
        {
            if (!string.IsNullOrWhiteSpace(rawTitle))
            {
                string clean = rawTitle.Trim();
                if (clean.EndsWith("兑换") && clean.Length > 2)
                    return clean;
                if (!string.IsNullOrEmpty(clean))
                    return clean;
            }

            if (!string.IsNullOrEmpty(modeName))
            {
                if (modeName.IndexOf("RespectOrb", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "敬意宝珠兑换";
                if (modeName.IndexOf("BoxChip", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "BOX芯片兑换";
                if (modeName.IndexOf("ExChgKira", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "EX宝珠兑换";
                if (modeName.IndexOf("CardTrader", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "卡片商人兑换";
                if (modeName.IndexOf("CardTicket", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "兑换券兑换";
                if (modeName.IndexOf("CardExchange", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "卡片交换";
            }

            return "卡片兑换";
        }

        /// <summary>
        /// Derives a clean currency/medium name for CardGetter modes.
        /// </summary>
        public static string ResolveCurrencyName(string rawTitle, string modeName, string needItemPath = null)
        {
            if (!string.IsNullOrWhiteSpace(rawTitle))
            {
                string clean = rawTitle.Trim();
                if (clean.EndsWith("兑换") && clean.Length > 2)
                    return clean.Substring(0, clean.Length - 2);
                if (clean.Contains("敬意宝珠")) return "敬意宝珠";
                if (clean.Contains("EX宝珠")) return "EX宝珠";
                if (clean.Contains("宝珠")) return "宝珠";
                if (clean.Contains("BOX芯片") || clean.Contains("芯片")) return "BOX芯片";
                if (clean.Contains("兑换券")) return "兑换券";
                if (clean.Contains("水晶")) return "水晶";
                if (clean.Contains("金币")) return "金币";
                if (clean.Contains("宝石")) return "宝石";
            }

            if (!string.IsNullOrEmpty(modeName))
            {
                if (modeName.IndexOf("RespectOrb", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "敬意宝珠";
                if (modeName.IndexOf("BoxChip", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "BOX芯片";
                if (modeName.IndexOf("ExChgKira", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "EX宝珠";
                if (modeName.IndexOf("CardTrader", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "金币/宝珠";
                if (modeName.IndexOf("CardTicket", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "兑换券";
            }

            if (!string.IsNullOrEmpty(needItemPath))
            {
                if (needItemPath.IndexOf("respect", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "敬意宝珠";
                if (needItemPath.IndexOf("jewel", System.StringComparison.OrdinalIgnoreCase) >= 0 || needItemPath.IndexOf("orb", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "宝珠";
                if (needItemPath.IndexOf("chip", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "BOX芯片";
                if (needItemPath.IndexOf("ticket", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return "兑换券";
            }

            return "兑换券";
        }
    }
}
