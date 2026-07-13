using System.Collections.Generic;

namespace DuelLinksAccess
{
    /// <summary>Tracks monster battle position by runtime unique ID.</summary>
    public static class DuelPositionTracker
    {
        private static readonly Dictionary<int, bool> _isDefense = new();

        public static void Reset() => _isDefense.Clear();

        public static void SetAttack(int uniqueId)
        {
            if (uniqueId > 0) _isDefense[uniqueId] = false;
        }

        public static void SetDefense(int uniqueId)
        {
            if (uniqueId > 0) _isDefense[uniqueId] = true;
        }

        public static void Toggle(int uniqueId)
        {
            if (uniqueId <= 0) return;
            _isDefense[uniqueId] = !(
                _isDefense.TryGetValue(uniqueId, out bool current)
                && current);
        }

        public static bool? ApplyPositionChange(
            int uniqueId, bool? observedIsDefense)
        {
            if (uniqueId <= 0) return null;

            if (observedIsDefense.HasValue)
            {
                _isDefense[uniqueId] = observedIsDefense.Value;
                return observedIsDefense.Value;
            }

            if (!_isDefense.TryGetValue(uniqueId, out bool current))
                return null;

            bool changed = !current;
            _isDefense[uniqueId] = changed;
            return changed;
        }

        public static void Forget(int uniqueId)
        {
            if (uniqueId > 0) _isDefense.Remove(uniqueId);
        }

        public static bool? IsDefense(int uniqueId)
        {
            if (uniqueId <= 0) return null;
            return _isDefense.TryGetValue(uniqueId, out bool isDefense)
                ? (bool?)isDefense
                : null;
        }
    }
}
