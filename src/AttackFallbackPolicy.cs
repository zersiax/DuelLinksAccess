namespace DuelLinksAccess
{
    public static class AttackFallbackPolicy
    {
        public static bool ShouldRetry(
            bool autoAttack, bool battleInputActive)
        {
            return !autoAttack && battleInputActive;
        }
    }
}
