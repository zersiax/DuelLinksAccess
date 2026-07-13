namespace DuelLinksAccess
{
    public static class LateContentRetryPolicy
    {
        public static bool ShouldRetry(
            int itemCount, bool textMode, bool emptyTutorialArrow)
        {
            return itemCount == 0 && !textMode && !emptyTutorialArrow;
        }

        public static float GetDelay(
            int attempt, float initialDelay, float slowDelay)
        {
            return attempt < 3 ? initialDelay : slowDelay;
        }
    }
}
