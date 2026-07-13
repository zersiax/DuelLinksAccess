namespace DuelLinksAccess
{
    public enum TicketExchangeAction
    {
        Reject,
        InvokeButton,
        UseLegacyFallback,
    }

    public static class TicketExchangePolicy
    {
        public static TicketExchangeAction ChooseAction(
            bool buttonPresent, bool buttonInteractable)
        {
            if (!buttonPresent)
                return TicketExchangeAction.UseLegacyFallback;
            return buttonInteractable
                ? TicketExchangeAction.InvokeButton
                : TicketExchangeAction.Reject;
        }
    }
}
