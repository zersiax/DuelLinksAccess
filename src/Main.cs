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
        private DeckEditHandler _deckEditHandler;
        private ShopHandler _shopHandler;
        private TicketExchangeHandler _ticketExchangeHandler;

        // Orphaned TutorialArrow tracking — for arrows on the dialog stack
        // when neither DuelHandler nor DialogHandler is handling them
        private bool _orphanArrowDismissAttempted;
        private bool _orphanArrowAnnounced;

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
            _deckEditHandler = new DeckEditHandler();
            _shopHandler = new ShopHandler();
            _ticketExchangeHandler = new TicketExchangeHandler();
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

            // F4 = Try to complete stuck Boot tutorial (debug)
            if (DebugMode && InputManager.TryConsumeKeyDown(KeyCode.F4))
            {
                TryCompleteTutorial();
                return true;
            }

            // F5 = Start tutorial duel directly (debug)
            if (DebugMode && InputManager.TryConsumeKeyDown(KeyCode.F5))
            {
                TryStartTutorialDuel();
                return true;
            }

            // F6 = ShowFirstTimer for Boot tutorial (debug)
            if (DebugMode && InputManager.TryConsumeKeyDown(KeyCode.F6))
            {
                TryShowFirstTimer();
                return true;
            }

            // F7 = Clear waitTarget and re-fetch (debug)
            if (DebugMode && InputManager.TryConsumeKeyDown(KeyCode.F7))
            {
                TryClearWaitTarget();
                return true;
            }

            // F8 = Account_create — reset/recreate account (debug, nuclear option)
            if (DebugMode && InputManager.TryConsumeKeyDown(KeyCode.F8))
            {
                TryCreateAccount();
                return true;
            }

            // F9 = Simulate real mouse click at TutorialArrow physicTarget (debug)
            if (DebugMode && InputManager.TryConsumeKeyDown(KeyCode.F9))
            {
                SimulateClickAtArrowTarget();
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
            // Process deferred announcements (e.g. phase change — engine needs
            // a frame to update before we can read the new phase)
            DuelEventAnnouncer.Update();

            // Duel handler runs first — announces events, provides log/status.
            // Key consumption via InputManager prevents conflicts with other handlers.
            _duelHandler?.Update();

            // Dialog handler — also handles duel dialogs (Yes/No, effect selection)
            _dialogHandler?.Update();
            if (_dialogHandler?.IsActive == true) return;

            // Safety net: TutorialArrow on dialog stack but screen is not Dialog/Duel.
            // Happens during ScenarioPlayerPart cutscenes — auto-dismiss click-to-continue.
            if (!(_duelHandler?.IsActive == true) && HandleOrphanedTutorialArrow()) return;

            // Deck editor handler — intercepts Deck screen when in DeckEdit2ViewController
            _deckEditHandler?.Update();
            if (_deckEditHandler?.IsActive == true) return;

            // Shop handler — intercepts Shop screen when ShopViewController2 is active
            _shopHandler?.Update();
            if (_shopHandler?.IsActive == true) return;

            // Ticket exchange handler — intercepts CardGetterViewController screens
            _ticketExchangeHandler?.Update();
            if (_ticketExchangeHandler?.IsActive == true) return;

            // Generic screen button handler — fallback for non-dialog, non-duel screens
            _screenButtonHandler?.Update();
            if (_screenButtonHandler?.IsActive == true) return;
        }

        /// <summary>
        /// Handles TutorialArrow overlays on the dialog stack when neither DuelHandler
        /// nor DialogHandler is active. Uses arrowVc.OnPointerClick with the correct
        /// position — physicTarget screen pos for pointing arrows, screen center for
        /// click-to-continue. First attempt auto-dismisses; if arrow persists it's a
        /// pointing arrow and Enter/Space retries.
        /// </summary>
        private bool HandleOrphanedTutorialArrow()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return false;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("dialog", out mgr)) return false;

                var topVc = mgr?.GetStackTopViewController();
                if (topVc?.gameObject == null)
                {
                    _orphanArrowDismissAttempted = false;
                    _orphanArrowAnnounced = false;
                    return false;
                }

                string name = topVc.gameObject.name;
                if (name != "TutorialArrow" && name != "TutorialArrowPart")
                {
                    _orphanArrowDismissAttempted = false;
                    _orphanArrowAnnounced = false;
                    return false;
                }

                var arrowVc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                if (arrowVc == null)
                {
                    _orphanArrowDismissAttempted = false;
                    _orphanArrowAnnounced = false;
                    return false;
                }

                // First encounter: try to auto-dismiss via OnPointerClick at correct position
                if (!_orphanArrowDismissAttempted)
                {
                    _orphanArrowDismissAttempted = true;
                    _orphanArrowAnnounced = false;

                    var target = arrowVc.physicTarget;
                    DebugLogger.Log(LogCategory.Game, "Main",
                        $"Auto-dismissing orphaned TutorialArrow: {name}, " +
                        $"physicTarget={target?.name ?? "null"}, " +
                        $"dispTarget={arrowVc.dispTarget?.name ?? "null"}, " +
                        $"ipclick={arrowVc.ipclick?.Length ?? 0}");

                    ClickArrowAtTarget(arrowVc);
                    return true;
                }

                // Arrow persists after dismiss attempt — it's a pointing arrow.
                // Don't block ScreenButtonHandler — let user navigate to the target
                // and press Enter. ScreenButtonHandler.ActivateViaTutorialArrow
                // will route the click through the arrow.
                if (!_orphanArrowAnnounced)
                {
                    _orphanArrowAnnounced = true;
                    ScreenReader.Say(Loc.Get("duel_tutorial_arrow_pointing"));
                }

                return false;
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "Main",
                    $"HandleOrphanedTutorialArrow error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clicks a TutorialArrow via arrowVc.OnPointerClick at the correct position.
        /// For pointing arrows (physicTarget != null): uses physicTarget's screen position
        /// so the arrow's IsCollider check passes and the click routes through to ipclick.
        /// For click-to-continue (physicTarget == null): uses screen center.
        /// </summary>
        internal static void ClickArrowAtTarget(
            Il2CppYgomGame.Menu.TutorialArrowViewController arrowVc)
        {
            var eventData = new UnityEngine.EventSystems.PointerEventData(
                UnityEngine.EventSystems.EventSystem.current);

            var physicTarget = arrowVc.physicTarget;
            if (physicTarget != null)
            {
                // Use the arrow's own targetCamera for coordinate conversion —
                // Camera.main is NOT the right camera for 3D world targets.
                var cam = arrowVc.targetCamera;
                if (cam == null) cam = Camera.main;

                if (cam != null)
                {
                    Vector3 screenPos = cam.WorldToScreenPoint(
                        physicTarget.transform.position);
                    eventData.position = new Vector2(screenPos.x, screenPos.y);
                    DebugLogger.Log(LogCategory.Game, "Main",
                        $"Clicking arrow at physicTarget pos " +
                        $"({screenPos.x:F0}, {screenPos.y:F0}) via " +
                        $"{cam.name} for {physicTarget.name}");
                }
                else
                {
                    eventData.position = new Vector2(
                        Screen.width / 2f, Screen.height / 2f);
                    DebugLogger.Log(LogCategory.Game, "Main",
                        "No camera for physicTarget, using screen center");
                }
            }
            else
            {
                // No physicTarget — click-to-continue, screen center is fine
                eventData.position = new Vector2(
                    Screen.width / 2f, Screen.height / 2f);
                DebugLogger.Log(LogCategory.Game, "Main",
                    "No physicTarget, clicking arrow at screen center");
            }

            // RegistPointerCurrentRaycast populates the eventData with raycast
            // results that IsCollider needs — without it, OnPointerClick silently fails
            // even with the correct screen position.
            bool registered = false;
            try
            {
                registered = arrowVc.RegistPointerCurrentRaycast(eventData);
                DebugLogger.Log(LogCategory.Game, "Main",
                    $"RegistPointerCurrentRaycast returned {registered}");
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "Main",
                    $"RegistPointerCurrentRaycast error: {ex.Message}");
            }

            if (registered)
            {
                arrowVc.OnPointerClick(eventData);
            }
            else
            {
                // Fallback: call ipclick handlers directly (bypasses IsCollider check)
                try
                {
                    var ipclick = arrowVc.ipclick;
                    if (ipclick != null && ipclick.Length > 0)
                    {
                        foreach (var handler in ipclick)
                        {
                            if (handler == null) continue;
                            handler.OnPointerClick(eventData);
                        }
                        DebugLogger.Log(LogCategory.Game, "Main",
                            $"Called {ipclick.Length} ipclick handler(s) directly");
                    }
                    else
                    {
                        // No ipclick handlers — try OnPointerClick anyway as last resort
                        arrowVc.OnPointerClick(eventData);
                    }
                }
                catch (System.Exception ex)
                {
                    DebugLogger.Log(LogCategory.Game, "Main",
                        $"ipclick fallback error: {ex.Message}");
                    arrowVc.OnPointerClick(eventData);
                }
            }
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

                // Probe ClientWork for tutorial-related data paths
                MelonLogger.Msg("--- ClientWork tutorial data ---");
                string[] pathsToProbe = {
                    "User.tutorial",
                    "User.tutorial_progress",
                    "User.tutorial_auto",
                    "UrlQueue.tutorial_auto",
                    "User.tutorial.8400",
                    "User.tutorial_progress.8400",
                    "tutorial",
                    "tutorial_auto",
                    "tutorial_progress",
                };
                foreach (string path in pathsToProbe)
                {
                    try
                    {
                        var val = Il2CppYgomSystem.Utility.ClientWork.getByJsonPath(path);
                        if (val != null)
                        {
                            string valStr = val.ToString();
                            MelonLogger.Msg($"  ClientWork[\"{path}\"] = {valStr}");
                            summary += $" CW[{path}]={valStr}.";
                        }
                    }
                    catch { }
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
        /// F5: Directly calls TutorialViewController.StartTutorialDuel() to trigger a tutorial duel.
        /// </summary>
        private void TryStartTutorialDuel()
        {
            try
            {
                MelonLogger.Msg("=== F5: Calling TutorialViewController.StartTutorialDuel() ===");
                Il2CppYgomGame.Menu.TutorialViewController.StartTutorialDuel();
                MelonLogger.Msg("StartTutorialDuel() called successfully");
                ScreenReader.Say("Tutorial duel started. Check screen.");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"StartTutorialDuel error: {ex.Message}");
                ScreenReader.Say($"Tutorial duel failed: {ex.Message}");
            }
        }

        /// <summary>
        /// F6: Calls TutorialUtil.ShowFirstTimer(Boot) to re-trigger the boot tutorial flow.
        /// </summary>
        private void TryShowFirstTimer()
        {
            try
            {
                MelonLogger.Msg("=== F6: Calling TutorialUtil.ShowFirstTimer(Boot) ===");
                var handle = Il2CppYgomGame.Utility.TutorialUtil.ShowFirstTimer(
                    Il2CppYgomGame.Utility.TutorialUtil.Type.Boot);
                MelonLogger.Msg($"ShowFirstTimer returned: {handle}");
                ScreenReader.Say("ShowFirstTimer called. Check screen.");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"ShowFirstTimer error: {ex.Message}");
                ScreenReader.Say($"ShowFirstTimer failed: {ex.Message}");
            }
        }

        /// <summary>
        /// F7: Clears TutorialManager.waitTarget and calls fetch() to nudge the tutorial system.
        /// </summary>
        private void TryClearWaitTarget()
        {
            try
            {
                string oldTarget = Il2CppYgomSystem.Utility.TutorialManager.waitTarget;
                MelonLogger.Msg($"=== F7: Clearing waitTarget (was \"{oldTarget}\") ===");

                Il2CppYgomSystem.Utility.TutorialManager.waitTarget = "";
                MelonLogger.Msg("waitTarget cleared");

                // Also try to find and call checkTarget on the instance
                var tmObj = UnityEngine.Object.FindObjectOfType<Il2CppYgomSystem.Utility.TutorialManager>();
                if (tmObj != null)
                {
                    tmObj.checkTarget();
                    MelonLogger.Msg("checkTarget() called");
                }

                Il2CppYgomSystem.Utility.TutorialManager.fetch();
                MelonLogger.Msg("fetch() called");

                ScreenReader.Say($"Wait target cleared (was {oldTarget}). Fetched.");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"ClearWaitTarget error: {ex.Message}");
                ScreenReader.Say($"Clear wait target failed: {ex.Message}");
            }
        }

        /// <summary>
        /// F9: Two-pronged approach to click through TutorialArrow:
        /// 1) Bypass arrow — call ipclick handlers directly with physicTarget position
        /// 2) Hardware mouse simulation via SetCursorPos + mouse_event
        /// </summary>
        private void SimulateClickAtArrowTarget()
        {
            try
            {
                MelonLogger.Msg("=== F9: Simulating click at arrow target ===");

                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) { ScreenReader.Say("No manager"); return; }

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("dialog", out mgr)) { ScreenReader.Say("No dialog manager"); return; }

                var topVc = mgr?.GetStackTopViewController();
                if (topVc?.gameObject == null) { ScreenReader.Say("No dialog VC"); return; }

                var arrowVc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                if (arrowVc == null) { ScreenReader.Say("Not a TutorialArrow"); return; }

                var physicTarget = arrowVc.physicTarget;
                if (physicTarget == null) { ScreenReader.Say("No physics target"); return; }

                var cam = arrowVc.targetCamera;
                if (cam == null) cam = Camera.main;
                if (cam == null) { ScreenReader.Say("No camera"); return; }

                Vector3 screenPos = cam.WorldToScreenPoint(physicTarget.transform.position);
                MelonLogger.Msg($"Target: Unity ({screenPos.x:F0}, {screenPos.y:F0}), screen {Screen.width}x{Screen.height}");

                // --- Approach 1: Direct ipclick invocation with correct position ---
                var ipclickHandlers = arrowVc.ipclick;
                if (ipclickHandlers != null && ipclickHandlers.Length > 0)
                {
                    MelonLogger.Msg($"Approach 1: Calling {ipclickHandlers.Length} ipclick handler(s) at target position");
                    var eventData = new UnityEngine.EventSystems.PointerEventData(
                        UnityEngine.EventSystems.EventSystem.current);
                    eventData.position = new Vector2(screenPos.x, screenPos.y);

                    for (int i = 0; i < ipclickHandlers.Length; i++)
                    {
                        var handler = ipclickHandlers[i];
                        if (handler == null) continue;

                        // Get the MonoBehaviour behind the interface
                        var mb = handler.TryCast<MonoBehaviour>();
                        string handlerName = mb != null ? mb.gameObject.name : "unknown";
                        MelonLogger.Msg($"  ipclick[{i}]: {handlerName}");

                        try
                        {
                            handler.OnPointerClick(eventData);
                            MelonLogger.Msg($"  ipclick[{i}] invoked OK");
                        }
                        catch (System.Exception ex)
                        {
                            MelonLogger.Msg($"  ipclick[{i}] error: {ex.Message}");
                        }
                    }
                }
                else
                {
                    MelonLogger.Msg("No ipclick handlers found");
                }

                // --- Approach 2: Hardware mouse simulation via SetCursorPos + mouse_event ---
                // Unity: (0,0)=bottom-left. Windows: (0,0)=top-left.
                int clientX = (int)screenPos.x;
                int clientY = Screen.height - (int)screenPos.y;

                // Convert client coords to screen coords
                POINT pt;
                pt.X = clientX;
                pt.Y = clientY;
                var hwnd = FindWindow(null, "Yu-Gi-Oh! DUEL LINKS");
                if (hwnd == System.IntPtr.Zero) hwnd = GetActiveWindow();

                if (hwnd != System.IntPtr.Zero && ClientToScreen(hwnd, ref pt))
                {
                    MelonLogger.Msg($"Approach 2: Hardware click at screen ({pt.X}, {pt.Y})");
                    SetCursorPos(pt.X, pt.Y);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, System.UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, System.UIntPtr.Zero);
                    MelonLogger.Msg("Hardware click sent");
                }
                else
                {
                    MelonLogger.Msg($"ClientToScreen failed or no window handle");
                }

                ScreenReader.Say("Click simulated");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"SimulateClick error: {ex.Message}");
                ScreenReader.Say($"Click failed: {ex.Message}");
            }
        }

        // Windows API for hardware mouse simulation
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern System.IntPtr FindWindow(string lpClassName, string lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern System.IntPtr GetActiveWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool ClientToScreen(System.IntPtr hWnd, ref POINT lpPoint);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, System.UIntPtr dwExtraInfo);

        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;

        /// <summary>
        /// F8: Calls Account_create() to attempt a fresh account on the server.
        /// Nuclear option when tutorial is irreparably stuck.
        /// </summary>
        private void TryCreateAccount()
        {
            try
            {
                MelonLogger.Msg("=== F8: Calling Account_create() ===");
                var handle = Il2CppYgomSystem.Network.API.Account_create();
                MelonLogger.Msg($"Account_create() returned: {handle}");
                ScreenReader.Say("Account create called. Restart the game.");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"Account_create error: {ex.Message}");
                ScreenReader.Say($"Account create failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Attempts to unstick the Boot tutorial by calling User_tutorial_dialog
        /// and manipulating TutorialManager state.
        /// </summary>
        private void TryCompleteTutorial()
        {
            try
            {
                MelonLogger.Msg("=== Attempting to complete tutorial ===");

                // Step 1: Check current state
                bool bootInProgress = Il2CppYgomGame.Utility.TutorialUtil
                    .IsTutorialProgress(Il2CppYgomGame.Utility.TutorialUtil.Type.Boot);
                string bootProgress = Il2CppYgomGame.Utility.TutorialUtil
                    .GetTutorialProgress(Il2CppYgomGame.Utility.TutorialUtil.Type.Boot);
                string waitTarget = Il2CppYgomSystem.Utility.TutorialManager.waitTarget;

                MelonLogger.Msg($"Boot tutorial: inProgress={bootInProgress}, progress=\"{bootProgress}\", waitTarget=\"{waitTarget}\"");

                // Step 2: Try User_tutorial_dialog for all tutorial types that are in progress
                var values = System.Enum.GetValues(
                    typeof(Il2CppYgomGame.Utility.TutorialUtil.Type));
                foreach (Il2CppYgomGame.Utility.TutorialUtil.Type t in values)
                {
                    try
                    {
                        if (Il2CppYgomGame.Utility.TutorialUtil.IsTutorialProgress(t))
                        {
                            int id = (int)t;
                            MelonLogger.Msg($"Calling User_tutorial_dialog({id}) for {t}...");
                            var handle = Il2CppYgomSystem.Network.API.User_tutorial_dialog(id);
                            MelonLogger.Msg($"  Returned handle: {handle}");
                        }
                    }
                    catch (System.Exception ex)
                    {
                        MelonLogger.Msg($"  User_tutorial_dialog error for {t}: {ex.Message}");
                    }
                }

                // Step 3: If waitTarget is set, try checkTarget to advance
                if (!string.IsNullOrEmpty(waitTarget))
                {
                    MelonLogger.Msg($"Calling TutorialManager.checkTarget() (waitTarget=\"{waitTarget}\")...");
                    var tmObj = UnityEngine.Object.FindObjectOfType<Il2CppYgomSystem.Utility.TutorialManager>();
                    if (tmObj != null)
                    {
                        tmObj.checkTarget();
                        MelonLogger.Msg("checkTarget() called on instance");
                    }
                    else
                    {
                        MelonLogger.Msg("TutorialManager instance not found");
                    }
                }

                // Step 4: Re-fetch tutorial data from server
                MelonLogger.Msg("Calling TutorialManager.fetch()...");
                Il2CppYgomSystem.Utility.TutorialManager.fetch();
                MelonLogger.Msg("fetch() called");

                ScreenReader.Say("Tutorial completion attempted. Press F3 to check state.");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"TryCompleteTutorial error: {ex.Message}");
                ScreenReader.Say("Tutorial completion failed. Check log.");
            }
        }

        #endregion
    }
}
