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

        public static bool CanRevealIdentity(
            int requestedPlayer, int localPlayer)
        {
            return (localPlayer == 0 || localPlayer == 1)
                && requestedPlayer == localPlayer;
        }
    }
}
