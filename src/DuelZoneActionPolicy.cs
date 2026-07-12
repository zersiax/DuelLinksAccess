namespace DuelLinksAccess
{
    public static class DuelZoneActionPolicy
    {
        public static bool IsLocalMonster(
            bool mainMonsterZone,
            bool sharedExtraMonsterZone,
            int owner,
            int localPlayer)
        {
            return mainMonsterZone
                || (sharedExtraMonsterZone && owner == localPlayer);
        }
    }
}
