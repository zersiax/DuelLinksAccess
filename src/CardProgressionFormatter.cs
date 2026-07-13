namespace DuelLinksAccess
{
    public static class CardProgressionFormatter
    {
        public static string Format(int level, int rank, int link)
        {
            if (link > 0) return $"{Loc.Get("deck_link")} {link}";
            if (rank > 0) return $"{Loc.Get("deck_rank")} {rank}";
            return $"{Loc.Get("deck_level")} {level}";
        }

        public static string FormatCombatStats(int attack, int defense, int link)
        {
            if (link > 0) return $"ATK {attack}";
            return $"ATK {attack} DEF {defense}";
        }
    }
}
