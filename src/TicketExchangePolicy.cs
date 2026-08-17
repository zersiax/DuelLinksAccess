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

            // No confirm control available: nothing safe to trigger.
            return TicketExchangeAction.Reject;
        }
    }
}
