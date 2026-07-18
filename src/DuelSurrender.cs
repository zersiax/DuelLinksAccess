using UnityEngine;

namespace DuelLinksAccess
{
    /// <summary>
    /// Ctrl+Q surrender for duels. The visual surrender button lives in the
    /// duel menu overlay (DuelMenu), which SR users can't reach; this wires
    /// its OnSurrender() handler to a key.
    ///
    /// OnSurrender() opens the game's own "give up?" YES/NO dialog
    /// (confirmed 2026-07-18), which DialogHandler reads and which defaults
    /// focus to NO — so a single deliberate Ctrl+Q is safe: the real
    /// confirmation is that dialog, exactly as for a sighted player who taps
    /// the menu's surrender button. No mod-side confirm needed.
    ///
    /// Gated on DuelMenu.Instance.Surrenderable — the game's own flag, only
    /// true inside a duel where surrender is currently permitted.
    /// </summary>
    internal static class DuelSurrender
    {
        /// <summary>
        /// Handles one Ctrl+Q press. Call from Main.ProcessHotkeys after the
        /// key is consumed.
        /// </summary>
        internal static void OnHotkey()
        {
            Il2CppYgomGame.Duel.DuelMenu menu = null;
            try { menu = Il2CppYgomGame.Duel.DuelMenu.Instance; }
            catch { menu = null; }

            bool surrenderable = false;
            if (menu != null)
            {
                try { surrenderable = menu.Surrenderable; }
                catch { surrenderable = false; }
            }

            if (menu == null || !surrenderable)
            {
                // Deliberate combo pressed but surrender isn't allowed
                // (not in a duel, or a point where the game forbids it).
                ScreenReader.Say(Loc.Get("surrender_unavailable"));
                return;
            }

            try
            {
                // Opens the game's own YES/NO confirm dialog; DialogHandler
                // announces it and the user commits with YES.
                menu.OnSurrender();
                ScreenReader.Say(Loc.Get("surrender_opening"));
                DebugLogger.Log(LogCategory.Game, "Surrender",
                    "DuelMenu.OnSurrender() invoked — game confirm dialog expected");
            }
            catch (System.Exception ex)
            {
                ScreenReader.Say(Loc.Get("surrender_error"));
                DebugLogger.Log(LogCategory.Game, "Surrender",
                    $"OnSurrender failed: {ex.Message}");
            }
        }
    }
}
