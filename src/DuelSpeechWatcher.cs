using Il2CppYgomGame.Duel;
using UnityEngine;
using UnityEngine.UI;

namespace DuelLinksAccess
{
    /// <summary>
    /// Watches NearFaceText and FarFaceText for character speech changes during duels
    /// and announces them via the screen reader.
    /// </summary>
    internal static class DuelSpeechWatcher
    {
        private static string _lastNearText;
        private static string _lastFarText;

        /// <summary>
        /// Call every frame while a duel is active.
        /// </summary>
        internal static void Update()
        {
            try
            {
                var hud = DuelClient.instance?.duelHUD;
                if (hud == null) return;

                WatchNearText(hud);
                WatchFarText(hud);
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.State, "DuelSpeech", $"Update error: {ex.Message}");
            }
        }

        /// <summary>Resets tracked state. Call when a duel ends.</summary>
        internal static void Reset()
        {
            _lastNearText = null;
            _lastFarText = null;
        }

        private static void WatchNearText(DuelHUD hud)
        {
            var comp = hud.nearFaceText;
            if (comp == null || comp.gameObject == null || !comp.gameObject.activeInHierarchy)
            {
                _lastNearText = null;
                return;
            }

            var text = FindActiveText(comp.gameObject);
            if (text == null)
            {
                _lastNearText = null;
                return;
            }

            if (text == _lastNearText) return;

            _lastNearText = text;
            ScreenReader.Say(text);
            DebugLogger.Log(LogCategory.State, "DuelSpeech", $"Near: {text}");
        }

        private static void WatchFarText(DuelHUD hud)
        {
            var comp = hud.farFaceText;
            if (comp == null || comp.gameObject == null || !comp.gameObject.activeInHierarchy)
            {
                _lastFarText = null;
                return;
            }

            var text = FindActiveText(comp.gameObject);
            if (text == null)
            {
                _lastFarText = null;
                return;
            }

            if (text == _lastFarText) return;

            _lastFarText = text;
            ScreenReader.Say(text);
            DebugLogger.Log(LogCategory.State, "DuelSpeech", $"Far: {text}");
        }

        /// <summary>
        /// Returns the first non-empty text found in active child Text components.
        /// Only searches active children (includeInactive = false).
        /// </summary>
        private static string FindActiveText(GameObject root)
        {
            var texts = root.GetComponentsInChildren<Text>(false);
            if (texts == null) return null;

            foreach (var t in texts)
            {
                var content = t?.text?.Replace("\r", " ").Replace("\n", " ").Trim();
                if (!string.IsNullOrWhiteSpace(content))
                    return content;
            }

            return null;
        }
    }
}
