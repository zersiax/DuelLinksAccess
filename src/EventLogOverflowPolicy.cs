namespace DuelLinksAccess
{
    public static class EventLogOverflowPolicy
    {
        public static int AdjustBrowseIndex(
            bool browsing, int browseIndex)
        {
            if (!browsing || browseIndex < 0) return browseIndex;
            return browseIndex == 0 ? 0 : browseIndex - 1;
        }
    }
}
