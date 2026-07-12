namespace DuelLinksAccess
{
    public readonly record struct DeckCountResult(
        bool IsExtraDeck, int Count);

    public static class DeckEditPolicy
    {
        public static bool UseFilteredCollection(bool filterExists)
        {
            return filterExists;
        }

        public static DeckCountResult ResolveAddedCount(
            int mainBefore,
            int extraBefore,
            int mainAfter,
            int extraAfter)
        {
            return extraAfter > extraBefore
                ? new DeckCountResult(true, extraAfter)
                : new DeckCountResult(false, mainAfter);
        }

        public static DeckCountResult ResolveRemovedCount(
            bool removedFromExtraDeck,
            int mainAfter,
            int extraAfter)
        {
            return removedFromExtraDeck
                ? new DeckCountResult(true, extraAfter)
                : new DeckCountResult(false, mainAfter);
        }
    }
}
