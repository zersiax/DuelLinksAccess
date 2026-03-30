using MelonLoader;
using UnityEngine;
using System.Collections;

// ============================================================================
// CRITICAL: Accessing Game Code
// ============================================================================
// Any access to game classes BEFORE full loading will crash!
//
// FORBIDDEN in OnInitializeMelon() or earlier:
//   - Game manager singletons (GameManager.i, AudioManager.instance, etc.)
//   - typeof(GameClass) in Harmony attributes
//
// ALLOWED only from OnSceneWasLoaded() / when CheckGameReady() is true.
//
// For crashes or silent failures:
//   See docs/technical-reference.md section "CRITICAL: Accessing Game Code"
// ============================================================================

[assembly: MelonInfo(typeof(DuelLinksAccess.Main), "DuelLinksAccess", "1.0.0", "DuelLinksAccess Team")]
[assembly: MelonGame("Konami Digital Entertainment Co., Ltd.", "Yu-Gi-Oh! DUEL LINKS")]

namespace DuelLinksAccess
{
    /// <summary>
    /// Main mod entry point. Coordinates all handlers and processes global hotkeys.
    /// Keep this class SMALL — put ALL feature logic in separate Handler classes.
    /// </summary>
    public class Main : MelonMod
    {
        #region Fields

        private bool _gameReady = false;
        private HarmonyLib.Harmony _harmony;

        /// <summary>
        /// Debug mode — when true, logs all screenreader output and detailed game state.
        /// Toggle with F12.
        /// </summary>
        public static bool DebugMode = false;

        // Handlers — one per feature/screen
        private DialogHandler _dialogHandler;
        private ScreenButtonHandler _screenButtonHandler;
        private DuelHandler _duelHandler;

        #endregion

        #region Lifecycle

        public override void OnInitializeMelon()
        {
            ScreenReader.Initialize();
            Loc.Initialize();
            ModConfig.Initialize();
            _harmony = new HarmonyLib.Harmony("com.duellinksaccess.mod");
            InitializeHandlers();
            MelonCoroutines.Start(AnnounceStartupDelayed());
        }

        private void InitializeHandlers()
        {
            // Subscribe to screen changes to update AccessStateManager context
            GameStateTracker.OnScreenChanged += OnScreenChanged;

            _dialogHandler = new DialogHandler();
            _screenButtonHandler = new ScreenButtonHandler();
            _duelHandler = new DuelHandler();
        }

        private void OnScreenChanged(GameStateTracker.GameScreen oldScreen,
            GameStateTracker.GameScreen newScreen)
        {
            var newContext = AccessStateManager.ContextFromScreen(newScreen);
            AccessStateManager.SetContext(newContext);
        }

        private IEnumerator AnnounceStartupDelayed()
        {
            // Short delay so screenreader is ready
            yield return new WaitForSeconds(1f);
            ScreenReader.Say(Loc.Get("mod_loaded"));
        }

        public override void OnUpdate()
        {
            // Input manager must update first — clears consumed keys
            InputManager.Update();

            // Settings menu takes priority
            if (ModConfig.IsMenuOpen)
            {
                ModConfig.Update();
                return;
            }

            // Global hotkeys work regardless of game ready state
            if (ProcessHotkeys()) return;

            // Wait for game to be ready before handler updates
            if (!CheckGameReady()) return;

            // Poll current screen — detects changes and fires events
            GameStateTracker.Update();

            // Update all handlers
            UpdateHandlers();
        }

        private bool CheckGameReady()
        {
            if (_gameReady) return true;

            if (GameStateTracker.CheckGameReady())
            {
                _gameReady = true;
                HarmonyPatches.Apply(_harmony);
            }

            return _gameReady;
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            MelonLogger.Msg($"Scene loaded: {sceneName}");
            DebugLogger.LogState($"Scene changed to: {sceneName}");
            _gameReady = false;
            GameStateTracker.Reset();
            AccessStateManager.ForceReset();
        }

        public override void OnApplicationQuit()
        {
            ScreenReader.Shutdown();
        }

        #endregion

        #region Hotkeys

        /// <summary>
        /// Processes global hotkeys. Returns true if a key was handled.
        /// Only dispatch to handlers here — don't put logic in Main!
        /// </summary>
        private bool ProcessHotkeys()
        {
            // F12 = Toggle debug mode
            if (InputManager.TryConsumeKeyDown(KeyCode.F12))
            {
                DebugMode = !DebugMode;
                var status = DebugMode ? "enabled" : "disabled";
                MelonLogger.Msg($"Debug mode {status}");
                ScreenReader.Say(Loc.Get("debug_mode", status));
                return true;
            }

            // F1 = Help
            if (InputManager.TryConsumeKeyDown(KeyCode.F1))
            {
                DebugLogger.LogInput("F1", "Help");
                AnnounceHelp();
                return true;
            }

            // F2 = Dump current state (debug)
            if (DebugMode && InputManager.TryConsumeKeyDown(KeyCode.F2))
            {
                GameStateTracker.DumpCurrentState();
                return true;
            }

            // F3 = Dump tutorial progress (debug)
            if (DebugMode && InputManager.TryConsumeKeyDown(KeyCode.F3))
            {
                DumpTutorialProgress();
                return true;
            }

            // F4 = Try to advance stuck tutorial (debug)
            if (DebugMode && InputManager.TryConsumeKeyDown(KeyCode.F4))
            {
                TryAdvanceTutorial();
                return true;
            }

            // Ctrl+R = Repeat last announcement
            if (Input.GetKey(KeyCode.LeftControl) && InputManager.TryConsumeKeyDown(KeyCode.R))
            {
                RepeatLastAnnouncement();
                return true;
            }

            // Ctrl+F11 = Mod Settings
            if (Input.GetKey(KeyCode.LeftControl) && InputManager.TryConsumeKeyDown(KeyCode.F11))
            {
                ModConfig.ToggleMenu();
                return true;
            }

            return false;
        }

        #endregion

        #region Handler Updates

        private void UpdateHandlers()
        {
            // Duel handler runs first — announces events, provides log/status.
            // Key consumption via InputManager prevents conflicts with other handlers.
            _duelHandler?.Update();

            // Dialog handler — also handles duel dialogs (Yes/No, effect selection)
            _dialogHandler?.Update();
            if (_dialogHandler?.IsActive == true) return;

            // Generic screen button handler — fallback for non-dialog, non-duel screens
            _screenButtonHandler?.Update();
            if (_screenButtonHandler?.IsActive == true) return;
        }

        #endregion

        #region Help

        private void AnnounceHelp()
        {
            string help = Loc.Get("help_text");
            ScreenReader.Say(help);
        }

        private void RepeatLastAnnouncement()
        {
            string last = ScreenReader.LastAnnouncement;
            if (string.IsNullOrEmpty(last))
            {
                ScreenReader.Say(Loc.Get("no_repeat"));
            }
            else
            {
                ScreenReader.Say(last);
            }
        }

        /// <summary>
        /// Dumps tutorial progress for all known tutorial types.
        /// Called by F3 in debug mode to diagnose progression blocks.
        /// </summary>
        private void DumpTutorialProgress()
        {
            try
            {
                MelonLogger.Msg("=== Tutorial Progress Dump ===");
                string summary = "Tutorials in progress: ";
                int inProgressCount = 0;

                var values = System.Enum.GetValues(
                    typeof(Il2CppYgomGame.Utility.TutorialUtil.Type));

                foreach (Il2CppYgomGame.Utility.TutorialUtil.Type t in values)
                {
                    try
                    {
                        bool inProgress = Il2CppYgomGame.Utility.TutorialUtil
                            .IsTutorialProgress(t);
                        string progress = Il2CppYgomGame.Utility.TutorialUtil
                            .GetTutorialProgress(t);

                        if (inProgress || !string.IsNullOrEmpty(progress))
                        {
                            MelonLogger.Msg(
                                $"  {t} ({(int)t}): inProgress={inProgress}, progress=\"{progress}\"");
                            if (inProgress)
                            {
                                summary += $"{t}, ";
                                inProgressCount++;
                            }
                        }
                    }
                    catch { }
                }

                if (inProgressCount == 0)
                    summary = "No tutorials in progress.";
                else
                    summary = summary.TrimEnd(',', ' ') + ".";

                // Also dump TutorialManager state
                try
                {
                    string waitTarget = Il2CppYgomSystem.Utility.TutorialManager.waitTarget;
                    string queueName = Il2CppYgomSystem.Utility.TutorialManager.QUEUE_NAME;
                    string queuePath = Il2CppYgomSystem.Utility.TutorialManager.QUEUE_PATH;
                    string tutParam = Il2CppYgomSystem.Utility.TutorialManager.TUTORIAL_PARAM;

                    MelonLogger.Msg($"TutorialManager.waitTarget: \"{waitTarget}\"");
                    MelonLogger.Msg($"TutorialManager.QUEUE_NAME: \"{queueName}\"");
                    MelonLogger.Msg($"TutorialManager.QUEUE_PATH: \"{queuePath}\"");
                    MelonLogger.Msg($"TutorialManager.TUTORIAL_PARAM: \"{tutParam}\"");

                    if (!string.IsNullOrEmpty(waitTarget))
                        summary += $" Waiting for: {waitTarget}.";
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Msg($"TutorialManager read error: {ex.Message}");
                }

                MelonLogger.Msg($"Summary: {summary}");
                ScreenReader.Say(summary);
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"Tutorial dump error: {ex.Message}");
                ScreenReader.Say("Tutorial dump failed");
            }
        }

        /// <summary>
        /// Tries multiple approaches to advance a stuck tutorial.
        /// Called by F4 in debug mode.
        /// </summary>
        private void TryAdvanceTutorial()
        {
            MelonLogger.Msg("=== Attempting to advance tutorial ===");

            // Approach 1: Re-fetch tutorial data from server
            try
            {
                MelonLogger.Msg("Calling TutorialManager.fetch()...");
                Il2CppYgomSystem.Utility.TutorialManager.fetch();
                MelonLogger.Msg("TutorialManager.fetch() called successfully");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"fetch() failed: {ex.Message}");
            }

            // Approach 2: Try ShowFirstTimer(Boot) to re-trigger boot tutorial
            try
            {
                MelonLogger.Msg("Calling TutorialUtil.ShowFirstTimer(Boot)...");
                var handle = Il2CppYgomGame.Utility.TutorialUtil.ShowFirstTimer(
                    Il2CppYgomGame.Utility.TutorialUtil.Type.Boot);
                MelonLogger.Msg($"ShowFirstTimer returned handle: {handle != null}");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"ShowFirstTimer() failed: {ex.Message}");
            }

            // Approach 3: Try StartTutorialDuel (might re-enter the tutorial flow)
            try
            {
                MelonLogger.Msg("Calling TutorialViewController.StartTutorialDuel()...");
                Il2CppYgomGame.Menu.TutorialViewController.StartTutorialDuel();
                MelonLogger.Msg("StartTutorialDuel() called successfully");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"StartTutorialDuel() failed: {ex.Message}");
            }

            ScreenReader.Say("Tutorial advance attempted. Check log for details.");
        }

        #endregion
    }
}
