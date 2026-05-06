using System;
using MelonLoader;

namespace DuelLinksAccess
{
    /// <summary>
    /// Tracks which game screen is currently active by polling the ViewControllerManager
    /// each frame for the current top ViewController.
    ///
    /// NOTE: We poll instead of using Harmony on ViewController.OnFocusChanged because
    /// IL2CPP virtual method patching crashes during parameter marshalling.
    /// Polling is safe and reliable — runs once per frame with change detection.
    /// </summary>
    public static class GameStateTracker
    {
        /// <summary>
        /// Known game screens. Expand as new screens are identified.
        /// </summary>
        public enum GameScreen
        {
            Unknown,
            Title,
            Home,
            Duel,
            Deck,
            Shop,
            Dialog,
            CardDetail,
            Gate,
            Store,
            Notices,
            DuelTrials,
            Other
        }

        /// <summary>
        /// Currently active game screen.
        /// </summary>
        public static GameScreen CurrentScreen { get; private set; } = GameScreen.Unknown;

        /// <summary>
        /// When true, TutorialArrowPart is skipped in dialog name resolution.
        /// Set by DialogHandler after confirming the arrow has no content underneath.
        /// Reset when a new (non-arrow) dialog appears.
        /// </summary>
        public static bool SkipTutorialArrowPart { get; set; } = false;

        /// <summary>
        /// Whether the game has fully loaded and managers are available.
        /// </summary>
        public static bool IsGameReady { get; private set; } = false;

        /// <summary>
        /// Name of the last focused ViewController (for debug logging).
        /// </summary>
        public static string LastViewControllerName { get; private set; } = "";

        // Debounce: prevent rapid state changes from flash VCs (e.g. 16ms HtjsonDialog)
        private static string _pendingVcName = "";
        private static float _pendingStableTime = 0f;
        private static bool _pendingFromDialog = false;
        private const float DebounceDelay = 0.15f; // 150ms

        /// <summary>
        /// Fired when the active screen changes. Parameters: (oldScreen, newScreen).
        /// </summary>
        public static event Action<GameScreen, GameScreen> OnScreenChanged;

        /// <summary>
        /// Fired when the game becomes ready (managers available).
        /// </summary>
        public static event Action OnGameReady;

        /// <summary>
        /// Called each frame from Main.OnUpdate() after game is ready.
        /// Polls the current top ViewController and detects screen changes.
        /// </summary>
        public static void Update()
        {
            if (!IsGameReady) return;

            try
            {
                // Query the content manager for the current top view
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return;

                // Build composite name from all active managers for accurate detection.
                // Check multiple managers in priority order.
                string dialogName = GetTopViewName(namedManager, "dialog");
                string dialogBaseName = GetTopViewName(namedManager, "dialogbase");
                string contentName = GetTopViewName(namedManager, "content");
                string baseName = GetTopViewName(namedManager, "base");

                // Priority: dialog > dialogbase > content > base
                // Skip known container/overlay names that don't represent actual screens
                string goName = null;
                bool fromDialogManager = false;
                if (!string.IsNullOrEmpty(dialogName)
                    && dialogName != "DialogManager"
                    && dialogName != "TutorialArrow"
                    && !(SkipTutorialArrowPart && dialogName == "TutorialArrowPart"))
                {
                    goName = dialogName;
                    fromDialogManager = true;
                }
                else if (!string.IsNullOrEmpty(dialogBaseName)
                    && dialogBaseName != "DialogManager")
                {
                    goName = dialogBaseName;
                    fromDialogManager = true;
                }
                else if (!string.IsNullOrEmpty(contentName)
                    && contentName != "Standby")
                    goName = contentName;
                else if (!string.IsNullOrEmpty(baseName)
                    && baseName != "Header")
                    goName = baseName;

                if (string.IsNullOrEmpty(goName)) return;

                // Debounce: only process after the VC name has been stable for
                // DebounceDelay. This prevents flash VCs (e.g. 16ms HtjsonDialog
                // auto-dismiss) from causing state bounces that reset handlers.
                if (goName != _pendingVcName)
                {
                    _pendingVcName = goName;
                    _pendingStableTime = 0f;
                    _pendingFromDialog = fromDialogManager;
                    return;
                }

                _pendingStableTime += UnityEngine.Time.deltaTime;
                if (_pendingStableTime < DebounceDelay) return;

                // Stable long enough — now process the change
                if (goName == LastViewControllerName) return;

                LastViewControllerName = goName;
                DebugLogger.Log(LogCategory.State, "GameState",
                    $"VC focused: {goName}");

                // Log all active managers on change for debugging
                if (Main.DebugMode)
                {
                    if (!string.IsNullOrEmpty(dialogName))
                        MelonLogger.Msg($"  [dialog] = {dialogName}");
                    if (!string.IsNullOrEmpty(dialogBaseName))
                        MelonLogger.Msg($"  [dialogbase] = {dialogBaseName}");
                    if (!string.IsNullOrEmpty(contentName))
                        MelonLogger.Msg($"  [content] = {contentName}");
                    if (!string.IsNullOrEmpty(baseName))
                        MelonLogger.Msg($"  [base] = {baseName}");
                }

                var newScreen = ClassifyScreen(goName);

                // Any VC from the dialog/dialogbase manager that doesn't match
                // a known pattern should still be treated as Dialog
                // (e.g. Mission, reward screens, etc.)
                if (newScreen == GameScreen.Other && _pendingFromDialog)
                    newScreen = GameScreen.Dialog;

                // TutorialArrowPart in the content manager is a tutorial overlay
                // on a content page (e.g. Duel Trials), not a real dialog.
                // Let ScreenButtonHandler scan the page content underneath.
                if (newScreen == GameScreen.Dialog
                    && goName == "TutorialArrowPart" && !_pendingFromDialog)
                    newScreen = GameScreen.Other;

                SetScreen(newScreen);
            }
            catch
            {
                // IL2CPP access can throw — ignore silently
            }
        }

        /// <summary>
        /// Gets the GameObject name of the top ViewController in a named manager.
        /// Returns null if not found or on error.
        /// </summary>
        private static string GetTopViewName(
            Il2CppSystem.Collections.Generic.Dictionary<string,
                Il2CppYgomSystem.UI.ViewControllerManager> managers,
            string managerName)
        {
            try
            {
                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!managers.TryGetValue(managerName, out mgr)) return null;
                if (mgr == null) return null;

                var topVc = mgr.GetStackTopViewController();
                if (topVc == null) return null;

                var go = topVc.gameObject;
                if (go == null) return null;

                return go.name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Dumps current state of all named managers to the log and screen reader.
        /// Called by F2 in debug mode.
        /// </summary>
        public static void DumpCurrentState()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null)
                {
                    ScreenReader.Say("No managers available");
                    return;
                }

                MelonLogger.Msg("=== GameStateTracker dump ===");
                MelonLogger.Msg($"CurrentScreen: {CurrentScreen}, LastVC: {LastViewControllerName}");

                string summary = $"Screen: {CurrentScreen}. ";
                foreach (var entry in namedManager)
                {
                    string topName = "(empty)";
                    try
                    {
                        var topVc = entry.Value?.GetStackTopViewController();
                        if (topVc?.gameObject != null)
                            topName = topVc.gameObject.name;
                    }
                    catch { }
                    MelonLogger.Msg($"  {entry.Key} = {topName}");
                    if (topName != "(empty)")
                        summary += $"{entry.Key}: {topName}. ";
                }

                ScreenReader.Say(summary);
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"DumpCurrentState error: {ex.Message}");
            }
        }

        // Priority order for finding orphan TutorialArrows. Dialog first because
        // most arrows live there; content next for in-screen overlays (Duel Trials,
        // shop tutorial); then the rest as a catch-all so we don't go blind when
        // a new game version pushes an arrow somewhere unexpected.
        private static readonly string[] ArrowManagerPriority = new[]
        {
            "dialog", "dialogbase", "content", "overlay", "sub", "subcontent"
        };

        /// <summary>
        /// Walks every named manager looking for a TutorialArrow / TutorialArrowPart
        /// on top of its stack. Used by Main.HandleOrphanedTutorialArrow, the F11
        /// activation path, and ScreenButtonHandler's arrow routing wrappers so
        /// every site sees arrows in the same set of managers.
        ///
        /// Returns true with the arrow VC, its GameObject (for instance tracking),
        /// and the manager key it was found in. Walks the priority list first,
        /// then any remaining managers in dictionary order.
        /// </summary>
        public static bool TryFindArrowAcrossManagers(
            Il2CppSystem.Collections.Generic.Dictionary<string,
                Il2CppYgomSystem.UI.ViewControllerManager> namedManager,
            out Il2CppYgomGame.Menu.TutorialArrowViewController arrowVc,
            out UnityEngine.GameObject topGo,
            out string mgrKey)
        {
            arrowVc = null;
            topGo = null;
            mgrKey = null;
            if (namedManager == null) return false;

            foreach (string key in ArrowManagerPriority)
            {
                if (TryGetArrowFromManager(namedManager, key, out arrowVc, out topGo))
                {
                    mgrKey = key;
                    return true;
                }
            }

            foreach (var entry in namedManager)
            {
                bool alreadyTried = false;
                for (int i = 0; i < ArrowManagerPriority.Length; i++)
                {
                    if (ArrowManagerPriority[i] == entry.Key) { alreadyTried = true; break; }
                }
                if (alreadyTried) continue;

                if (TryGetArrowFromManager(namedManager, entry.Key, out arrowVc, out topGo))
                {
                    mgrKey = entry.Key;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetArrowFromManager(
            Il2CppSystem.Collections.Generic.Dictionary<string,
                Il2CppYgomSystem.UI.ViewControllerManager> namedManager,
            string key,
            out Il2CppYgomGame.Menu.TutorialArrowViewController arrowVc,
            out UnityEngine.GameObject topGo)
        {
            arrowVc = null;
            topGo = null;
            try
            {
                if (!namedManager.TryGetValue(key, out var mgr) || mgr == null) return false;
                var topVc = mgr.GetStackTopViewController();
                if (topVc?.gameObject == null) return false;
                string name = topVc.gameObject.name;
                if (name != "TutorialArrow" && name != "TutorialArrowPart") return false;
                var vc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                if (vc == null) return false;
                arrowVc = vc;
                topGo = topVc.gameObject;
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// Called by Harmony patch on ViewControllerManager.PushChildViewController.
        /// Logs navigation for debugging.
        /// </summary>
        public static void OnViewPushed(string prefabPath)
        {
            DebugLogger.Log(LogCategory.State, "GameState", $"View pushed: {prefabPath}");
        }

        /// <summary>
        /// Called by Harmony patch on ViewControllerManager.PopChildViewController.
        /// Logs navigation for debugging.
        /// </summary>
        public static void OnViewPopped()
        {
            DebugLogger.Log(LogCategory.State, "GameState", "View popped");
        }

        /// <summary>
        /// Checks if the game is ready by looking for ViewControllerManager.namedManager.
        /// Called each frame from Main until game is ready.
        /// </summary>
        public static bool CheckGameReady()
        {
            if (IsGameReady) return true;

            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager != null && namedManager.Count > 0)
                {
                    IsGameReady = true;
                    MelonLogger.Msg("Game ready — ViewControllerManager available");
                    MelonLogger.Msg($"namedManager has {namedManager.Count} entries");

                    // Dump all named manager keys so we know what to query
                    try
                    {
                        foreach (var entry in namedManager)
                        {
                            string key = entry.Key;
                            var mgr = entry.Value;
                            string topName = "(no top VC)";
                            try
                            {
                                var topVc = mgr?.GetStackTopViewController();
                                if (topVc?.gameObject != null)
                                    topName = topVc.gameObject.name;
                            }
                            catch { }
                            MelonLogger.Msg($"  Manager: \"{key}\" -> top: {topName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Msg($"  Error dumping managers: {ex.Message}");
                    }

                    OnGameReady?.Invoke();
                    return true;
                }
            }
            catch
            {
                // Not ready yet — managers not loaded
            }

            return false;
        }

        /// <summary>
        /// Forces the debounce to re-evaluate on the next frame.
        /// Call when skip conditions change (e.g. SkipTutorialArrowPart toggled).
        /// </summary>
        public static void ForceReevaluate()
        {
            _pendingVcName = "";
            _pendingStableTime = 0f;
        }

        /// <summary>
        /// Resets state. Call on scene changes.
        /// </summary>
        public static void Reset()
        {
            var oldScreen = CurrentScreen;
            CurrentScreen = GameScreen.Unknown;
            IsGameReady = false;
            LastViewControllerName = "";
            _pendingVcName = "";
            _pendingStableTime = 0f;
            SkipTutorialArrowPart = false;

            if (oldScreen != GameScreen.Unknown)
            {
                DebugLogger.Log(LogCategory.State, "GameState", "Reset");
                OnScreenChanged?.Invoke(oldScreen, GameScreen.Unknown);
            }
        }

        /// <summary>
        /// Sets the current screen and fires event if changed.
        /// </summary>
        private static void SetScreen(GameScreen newScreen)
        {
            if (newScreen == CurrentScreen) return;

            var oldScreen = CurrentScreen;
            CurrentScreen = newScreen;

            DebugLogger.Log(LogCategory.State, "GameState",
                $"Screen: {oldScreen} -> {newScreen}");

            // Announce screen change to user
            // Skip empty strings (e.g. Duel — handled by DuelEventAnnouncer)
            // Skip Dialog announcements during a duel (DialogHandler reads the text)
            // Skip Dialog when it's a TutorialArrowPart overlay — DialogHandler
            // will scan and dismiss it; the real dialog (or home screen) announces itself.
            string locKey = GetScreenLocKey(newScreen);
            if (locKey != null)
            {
                bool suppressDuelDialog = newScreen == GameScreen.Dialog
                    && DuelEventAnnouncer.InDuel;
                bool suppressTutorialArrow = newScreen == GameScreen.Dialog
                    && LastViewControllerName == "TutorialArrowPart";
                string text = Loc.Get(locKey);
                if (!string.IsNullOrEmpty(text) && !suppressDuelDialog && !suppressTutorialArrow)
                {
                    ScreenReader.Say(text);
                }
            }

            OnScreenChanged?.Invoke(oldScreen, newScreen);
        }

        /// <summary>
        /// Maps a GameObject name to a GameScreen.
        /// GameObject names typically match the ViewController class name or prefab path.
        /// Enable debug mode (F12) to discover new names and add them here.
        /// </summary>
        private static GameScreen ClassifyScreen(string goName)
        {
            if (goName.Contains("Home") || goName == "Single")
                return GameScreen.Home;

            if (goName.Contains("Title"))
                return GameScreen.Title;

            // Only match the main DuelClient VC as Duel — NOT duel sub-dialogs.
            // DuelCommonDialog, SelectEffectDialog, etc. fall through to Dialog below.
            if (goName == "DuelClient")
                return GameScreen.Duel;

            if (goName.Contains("Deck"))
                return GameScreen.Deck;

            if (goName.Contains("Shop"))
                return GameScreen.Shop;

            if (goName.Contains("CardDetail"))
                return GameScreen.CardDetail;

            if (goName.Contains("Gate"))
                return GameScreen.Gate;

            if (goName == "Store")
                return GameScreen.Store;

            // HtjsonPage is the game's web-like content viewer used for login bonuses,
            // notices, reward lists, and other server-driven content pages.
            // Standby is the daily login bonus / standby screen.
            if (goName == "HtjsonPage" || goName == "Standby")
                return GameScreen.Notices;

            if (goName.Contains("School") || goName.Contains("DuelQuest"))
                return GameScreen.DuelTrials;

            if (goName.Contains("Dialog") || goName.Contains("Confirm")
                || goName.Contains("AgeVerification") || goName.Contains("Tutorial"))
                return GameScreen.Dialog;

            return GameScreen.Other;
        }

        /// <summary>
        /// Gets the Loc key for announcing a screen change, or null for screens
        /// that shouldn't be announced (like Other or Unknown).
        /// </summary>
        private static string GetScreenLocKey(GameScreen screen)
        {
            return screen switch
            {
                GameScreen.Home => "screen_home",
                GameScreen.Title => "screen_title",
                GameScreen.Duel => null, // Handled by DuelEventAnnouncer
                GameScreen.Deck => "screen_deck",
                GameScreen.Shop => "screen_shop",
                GameScreen.Dialog => "screen_dialog",
                GameScreen.CardDetail => "screen_card_detail",
                GameScreen.Gate => "screen_gate",
                GameScreen.Store => "screen_store",
                GameScreen.Notices => "screen_notices",
                GameScreen.DuelTrials => "screen_duel_trials",
                _ => null
            };
        }
    }
}
