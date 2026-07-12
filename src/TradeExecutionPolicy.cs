namespace DuelLinksAccess
{
    public static class TradeExecutionPolicy
    {
        public static bool CanClick(
            int expectedItemId,
            int actualItemId,
            bool buttonActive,
            bool buttonInteractable)
        {
            return expectedItemId > 0
                && actualItemId == expectedItemId
                && buttonActive
                && buttonInteractable;
        }
    }
}
