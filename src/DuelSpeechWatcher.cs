using UnityEngine;
using UnityEngine.UI;

namespace DuelLinksAccess
{
    /// <summary>
    /// Watches the duel HUD face-text objects for character speech changes and
    /// announces them via the screen reader. Covers both the player's character
    /// (nearFaceText) and the opponent's character (farFaceText).
    /// </summary>
    internal static class DuelSpeechWatcher
    {
        private static string _lastNearText;
        private static string _lastFarText;

        /// <summary>Call every frame from Main.UpdateHandlers().</summary>
        internal static void Update()
        {
            Il2CppYgomGame.Duel.DuelHUD hud;
            try { hud = Il2CppYgomGame.Duel.DuelClient.instance?.duelHUD; }
            catch { hud = null; }

            if (hud == null)
            {
                _lastNearText = null;
                _lastFarText = null;
                return;
            }

            CheckFaceText(hud.nearFaceText?.gameObject, ref _lastNearText);
            CheckFaceText(hud.farFaceText?.gameObject, ref _lastFarText);
        }

        private static void CheckFaceText(GameObject root, ref string lastText)
        {
            if (root == null || !root.activeInHierarchy)
            {
                lastText = null;
                return;
            }

            var text = FindActiveText(root);
            if (text == null)
            {
                lastText = null;
                return;
            }

            if (text == lastText) return;

            lastText = text;
            ScreenReader.Say(text);
            DebugLogger.Log(LogCategory.State, "DuelSpeech", text);
        }

        private static string FindActiveText(GameObject root)
        {
            var texts = root.GetComponentsInChildren<Text>(false);
            if (texts == null) return null;

            foreach (var t in texts)
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                var content = t.text?.Replace("\r", " ").Replace("\n", " ").Trim();
                if (!string.IsNullOrWhiteSpace(content))
                    return content;
            }

            return null;
        }
    }
}
