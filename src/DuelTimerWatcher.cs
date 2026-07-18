using UnityEngine;

namespace DuelLinksAccess
{
    /// <summary>
    /// Watches the game's PvP duel clock (DuelTimer on RunEffectWorker2D)
    /// and surfaces it to screen reader users:
    ///   - announces the inactivity (abandon) warning, which the game shows
    ///     as a purely visual overlay before auto-surrendering AFK players
    ///     (Engine.SendAbandonSurrender),
    ///   - announces low remaining-time thresholds while the local player's
    ///     clock is draining,
    ///   - logs the raw timer state once per second in debug mode so tester
    ///     logs show how the clock behaves around mod-driven actions
    ///     (2026-07-17 tester theory: time drains faster for mod users).
    /// Read-only: never writes timer fields or invokes mutating methods.
    /// </summary>
    internal static class DuelTimerWatcher
    {
        private const float PollInterval = 0.25f;
        private const float LogInterval = 1f;

        // Announced low-time marks, highest first. Reset when the input
        // window closes or the server refills the clock.
        private static readonly int[] Thresholds = { 60, 30, 10 };

        // Re-announce cadence while the abandon warning stays on screen.
        private const float AbandonReannounce = 5f;

        private static float _nextPoll;
        private static float _nextLog;
        private static float _prevRemain = -1f;
        private static bool _prevMyInput;
        private static bool _warnedAbandon;
        private static float _nextAbandonAnnounce;
        private static bool _warningVisible;
        private static int _announcedMask;
        private static bool _wasActive;

        /// <summary>Call every frame from Main.UpdateHandlers().</summary>
        internal static void Update()
        {
            // Key handling runs every frame against the cached warning state
            // — the throttled poll below would miss most key presses. Runs
            // before DuelHandler in Main, so while the warning is up Space
            // means "I'm still here" and outranks every other Space use.
            if (_warningVisible
                && InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                RespondToAbandonWarning();
            }

            if (Time.unscaledTime < _nextPoll) return;
            _nextPoll = Time.unscaledTime + PollInterval;

            Il2CppYgomGame.Duel.RunEffectWorker2D worker;
            try { worker = Il2CppYgomGame.Duel.DuelClient.instance?.worker2d; }
            catch { worker = null; }

            Il2CppYgomGame.Duel.DuelTimer timer = null;
            bool active = false;
            try
            {
                timer = worker?.duelTimer;
                active = timer != null && timer._Active_k__BackingField;
            }
            catch { }

            if (!active)
            {
                if (_wasActive) ResetSession();
                return;
            }

            if (!_wasActive)
            {
                _wasActive = true;
                DebugLogger.Log(LogCategory.Game, "DuelTimer",
                    "Duel clock active — watching");
            }

            float remain = 0f, remainTemp = 0f, oppRemain = 0f, maxTime = 0f;
            bool myInput = false, oppInput = false, locked = false;
            try
            {
                remain = timer.playerRemain;
                remainTemp = timer.playerRemainTemp;
                oppRemain = timer.opponentRemain;
                maxTime = timer.maxTime;
                myInput = timer.isPlayerInput;
                oppInput = timer.isOpponentInput;
                locked = timer._Lock_k__BackingField;
            }
            catch { return; }

            // ----- Inactivity (abandon) warning — visual-only in the game.
            // Announce on appear (leading with the overlay's own game-
            // localized text when readable), re-announce with a fresh
            // countdown while it stays up, confirm when it clears.
            bool abandonShown = false;
            try { abandonShown = worker.abandonWarning?.activeInHierarchy == true; }
            catch { }
            _warningVisible = abandonShown;

            if (abandonShown && Time.unscaledTime >= _nextAbandonAnnounce)
            {
                bool first = !_warnedAbandon;
                _warnedAbandon = true;
                _nextAbandonAnnounce = Time.unscaledTime + AbandonReannounce;

                int abandonRemain = -1;
                try { abandonRemain = timer.GetAbandonRemainTime(); } catch { }

                string overlayText = null;
                if (first)
                {
                    try
                    {
                        overlayText = LabelExtractor.GetChildText(
                            worker.abandonWarning);
                    }
                    catch { }
                }

                string msg = abandonRemain > 0
                    ? Loc.Get("duel_abandon_warning_timed", abandonRemain)
                    : Loc.Get("duel_abandon_warning");
                if (!string.IsNullOrWhiteSpace(overlayText))
                    msg = overlayText.Trim() + ". " + msg;
                ScreenReader.Say(msg);

                if (first)
                    MelonLoader.MelonLogger.Msg(
                        $"[DuelTimer] Abandon warning shown " +
                        $"(remain={abandonRemain}s, text='{overlayText}')");
            }
            else if (!abandonShown && _warnedAbandon)
            {
                _warnedAbandon = false;
                _nextAbandonAnnounce = 0f;
                ScreenReader.Say(Loc.Get("duel_abandon_cleared"));
                MelonLoader.MelonLogger.Msg("[DuelTimer] Abandon warning cleared");
            }

            // ----- Low-time thresholds, only while our clock is draining.
            // Server refill (remain jumps up) or a closed input window
            // resets the announced marks for the next window.
            if (!myInput || (remain > _prevRemain + 2f && _prevRemain >= 0f))
                _announcedMask = 0;

            if (myInput && _prevMyInput && _prevRemain >= 0f)
            {
                for (int i = 0; i < Thresholds.Length; i++)
                {
                    int t = Thresholds[i];
                    if ((_announcedMask & (1 << i)) != 0) continue;
                    if (_prevRemain > t && remain <= t && remain > 0f)
                    {
                        _announcedMask |= 1 << i;
                        ScreenReader.SayQueued(Loc.Get("duel_time_low", t));
                        break;
                    }
                }
            }

            _prevRemain = remain;
            _prevMyInput = myInput;

            // ----- Raw state log (debug mode) for the timekeeping spike.
            if (Time.unscaledTime >= _nextLog)
            {
                _nextLog = Time.unscaledTime + LogInterval;
                float abandonTimer = -1f;
                int abandonRemain = -1;
                bool needWarn = false;
                try { abandonTimer = timer.abandonTimer; } catch { }
                try { abandonRemain = timer.GetAbandonRemainTime(); } catch { }
                try { needWarn = timer.IsNeedAbandonWarning(); } catch { }
                DebugLogger.Log(LogCategory.Game, "DuelTimer",
                    $"my={remain:F1}/{remainTemp:F1} input={myInput} " +
                    $"opp={oppRemain:F1} oppInput={oppInput} max={maxTime:F0} " +
                    $"lock={locked} abandonTimer={abandonTimer:F1} " +
                    $"abandonRemain={abandonRemain} needWarn={needWarn}");
            }
        }

        /// <summary>
        /// The "I'm still here" response: one real OS-level click aimed at
        /// the warning overlay itself. The overlay renders on top, so the
        /// click lands on it (its own button when it has one, else its
        /// center-screen catcher) and cannot reach the field beneath —
        /// safe even though the input window is open in this scenario.
        /// </summary>
        private static void RespondToAbandonWarning()
        {
            try
            {
                var warning = Il2CppYgomGame.Duel.DuelClient.instance?
                    .worker2d?.abandonWarning;
                var pos = new Vector2(
                    Screen.width * 0.5f, Screen.height * 0.5f);
                if (warning != null)
                {
                    var btn = warning
                        .GetComponentInChildren<UnityEngine.UI.Button>(true);
                    if (btn != null
                        && Main.TryGetUiScreenPos(btn.gameObject, out var btnPos))
                    {
                        pos = btnPos;
                    }
                }
                bool sent = Main.ClickViaHardwareMouse(pos, "abandon response");
                MelonLoader.MelonLogger.Msg(
                    $"[DuelTimer] Abandon response click sent={sent}");
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelTimer",
                    $"Abandon response failed: {ex.Message}");
            }
        }

        private static void ResetSession()
        {
            _wasActive = false;
            _warnedAbandon = false;
            _nextAbandonAnnounce = 0f;
            _warningVisible = false;
            _announcedMask = 0;
            _prevRemain = -1f;
            _prevMyInput = false;
            DebugLogger.Log(LogCategory.Game, "DuelTimer",
                "Duel clock inactive — watcher reset");
        }
    }
}
