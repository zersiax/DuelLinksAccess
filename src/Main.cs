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

        #endregion
    }
}
