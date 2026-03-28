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
            Other
        }

        /// <summary>
        /// Currently active game screen.
        /// </summary>
        public static GameScreen CurrentScreen { get; private set; } = GameScreen.Unknown;

        /// <summary>
        /// Whether the game has fully loaded and managers are available.
        /// </summary>
        public static bool IsGameReady { get; private set; } = false;

        /// <summary>
        /// Name of the last focused ViewController (for debug logging).
        /// </summary>
        public static string LastViewControllerName { get; private set; } = "";

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
                // Skip known container names that don't represent actual screens
                string goName = null;
                if (!string.IsNullOrEmpty(dialogName)
                    && dialogName != "DialogManager"
                    && dialogName != "TutorialArrow")
                    goName = dialogName;
                else if (!string.IsNullOrEmpty(dialogBaseName)
                    && dialogBaseName != "DialogManager")
                    goName = dialogBaseName;
                else if (!string.IsNullOrEmpty(contentName))
                    goName = contentName;
                else if (!string.IsNullOrEmpty(baseName)
                    && baseName != "Header")
                    goName = baseName;

                if (string.IsNullOrEmpty(goName)) return;

                // Only process when the name actually changes
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
        /// Resets state. Call on scene changes.
        /// </summary>
        public static void Reset()
        {
            var oldScreen = CurrentScreen;
            CurrentScreen = GameScreen.Unknown;
            IsGameReady = false;
            LastViewControllerName = "";

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
            string locKey = GetScreenLocKey(newScreen);
            if (locKey != null)
            {
                bool suppressDuelDialog = newScreen == GameScreen.Dialog
                    && DuelEventAnnouncer.InDuel;
                string text = Loc.Get(locKey);
                if (!string.IsNullOrEmpty(text) && !suppressDuelDialog)
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

            if (goName.Contains("Shop") || goName.Contains("OpenPack"))
                return GameScreen.Shop;

            if (goName.Contains("CardDetail"))
                return GameScreen.CardDetail;

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
                _ => null
            };
        }
    }
}
