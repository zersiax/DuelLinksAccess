namespace DuelLinksAccess
{
    public static class ExtraDeckSourcePolicy
    {
        /// <summary>
        /// Decides whether the visual layer's DeckCardPlace stack is
        /// authoritative for the Extra Deck. CardPlace.isLoaded only says
        /// the place object finished loading — a loaded place can still
        /// carry an empty innerCards list while the registered-deck array
        /// has the real contents (observed in game 10.9.0, 2026-07-14 log:
        /// "RefreshZone MyExtra: found=0" for a populated extra deck). The
        /// stack is therefore only trusted when it actually holds cards;
        /// otherwise callers fall back to the registered-deck array.
        /// </summary>
        public static bool UseLiveStack(
            bool placeAvailable,
            bool placeLoaded,
            int cardCount)
        {
            return placeAvailable && placeLoaded && cardCount > 0;
        }

        public static bool CanRevealIdentity(
            int requestedPlayer, int localPlayer)
        {
            return (localPlayer == 0 || localPlayer == 1)
                && requestedPlayer == localPlayer;
        }
    }
}
