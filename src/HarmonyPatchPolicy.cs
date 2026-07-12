using System.Collections.Generic;

namespace DuelLinksAccess
{
    public static class HarmonyPatchPolicy
    {
        public static bool RequiredPatchesApplied(
            bool push, bool pop, bool runEffect)
        {
            return push && pop && runEffect;
        }

        public static bool IsOwned(
            IEnumerable<string> owners, string ownerId)
        {
            if (owners == null) return false;
            foreach (string owner in owners)
            {
                if (owner == ownerId) return true;
            }
            return false;
        }

        public static bool ShouldAttempt(
            bool applied,
            int attempts,
            float now,
            float nextAttempt,
            int maxAttempts)
        {
            return !applied && attempts < maxAttempts && now >= nextAttempt;
        }
    }
}
