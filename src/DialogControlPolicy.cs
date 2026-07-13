namespace DuelLinksAccess
{
    public static class DialogControlPolicy
    {
        public static bool ShouldInclude(
            bool hasSelectable,
            bool interactable,
            bool nameLooksInteractive,
            bool selectedTab)
        {
            if (hasSelectable) return interactable || selectedTab;
            return nameLooksInteractive;
        }

        public static bool CanActivate(
            bool hasSelectable, bool interactable)
        {
            return !hasSelectable || interactable;
        }
    }
}
