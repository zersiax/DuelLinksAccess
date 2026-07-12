namespace DuelLinksAccess
{
    public static class ExtraDeckSourcePolicy
    {
        public static bool UseLiveStack(
            bool placeAvailable,
            bool placeLoaded,
            bool cardsAvailable)
        {
            return placeAvailable && placeLoaded && cardsAvailable;
        }
    }
}
