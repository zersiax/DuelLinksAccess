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
        private HomeHandler _homeHandler;

        // Orphaned TutorialArrow tracking — for arrows on the dialog stack
        // when neither DuelHandler nor DialogHandler is handling them.
        // Tracked by GO instance id so consecutive arrows (same name, new
        // instance) each get their own chance. _orphanArrowHandled is set
        // after we successfully invoke a click path to prevent double-fire.
        private int _orphanArrowInstanceId = -1;
        private float _orphanArrowFirstSeen = -1f;
        private bool _orphanArrowHandled;
        private bool _orphanArrowAnnounced;
        // Fallback timeout: wait this long with no resolvable target before
        // invoking ipclick[] directly. Gives the game time to settle after a
        // dialog pop so we don't re-trigger a just-dismissed dialog.
        private const float OrphanArrowIpclickTimeout = 1.5f;

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
            _homeHandler = new HomeHandler();
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

            // F10 = Dump DLL_DuelGetCardNum for locates 0-25 (debug)
            if (DebugMode && InputManager.TryConsumeKeyDown(KeyCode.F10))
            {
                DuelFieldNavigator.DumpLocateCounts();
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

            // Speech watcher runs unconditionally: character intro lines fire
            // before DuelHandler.IsActive becomes true (duel HUD is up but
            // the DuelStart event hasn't fired yet), so we can't gate this
            // on the duel handler's active state.
            DuelSpeechWatcher.Update();

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

            // Home screen handler — dedicated navigation for the world map
            _homeHandler?.Update();
            if (_homeHandler?.IsActive == true) return;

            // Generic screen button handler — fallback for non-dialog, non-duel screens
            _screenButtonHandler?.Update();
            if (_screenButtonHandler?.IsActive == true) return;
        }

        /// <summary>
        /// Handles TutorialArrow overlays on the dialog stack when neither DuelHandler
        /// nor DialogHandler is active. Strategy:
        /// 1. Re-poll physicTarget/dispTarget each frame. If either becomes non-null,
        ///    click via that target (ClickArrowAtTarget).
        /// 2. If targets stay null for <see cref="OrphanArrowIpclickTimeout"/> seconds
        ///    and ipclick[] has handlers, invoke them directly (YgomButton → MonoBehaviour
        ///    → ExecuteEvents). This is the documented fallback for "pointing" arrows
        ///    whose UI handler is attached to a specific button (e.g. Missions footer).
        /// 3. User can press Enter/Space at any time to trigger the fallback manually.
        /// Instance-id tracking ensures each newly pushed arrow gets a fresh chance.
        /// Returns true only while we're actively processing an arrow — returning false
        /// lets ScreenButtonHandler still scan the underlying screen.
        /// </summary>
        private bool HandleOrphanedTutorialArrow()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return false;

                // Check dialog manager first (missions/home-screen arrows),
                // then content manager (in-screen tutorial overlays like
                // the shop or Duel Trials). Whichever has the arrow wins.
                var arrowVc = TryGetArrowOnTop(namedManager, "dialog",
                    out var topGo, out string mgrKind);
                if (arrowVc == null)
                {
                    arrowVc = TryGetArrowOnTop(namedManager, "content",
                        out topGo, out mgrKind);
                }

                if (arrowVc == null)
                {
                    ResetOrphanArrowState();
                    return false;
                }

                // Duel Trials protection: a TutorialArrowPart on content with
                // an HtjsonPage underneath is the quiz-banner navigation case.
                // Auto-clicking would select a random banner. The user is
                // supposed to navigate the banners via ScreenButtonHandler.
                if (mgrKind == "content" && HasHtjsonPageBeneath(namedManager))
                {
                    // Still track so we don't spam logs, but don't auto-click.
                    int protectedId = topGo.GetInstanceID();
                    if (protectedId != _orphanArrowInstanceId)
                    {
                        _orphanArrowInstanceId = protectedId;
                        _orphanArrowFirstSeen = UnityEngine.Time.time;
                        _orphanArrowHandled = true;  // disable all auto paths
                        _orphanArrowAnnounced = false;
                        DebugLogger.Log(LogCategory.Game, "Main",
                            $"Orphan TutorialArrow in content over HtjsonPage " +
                            $"(Duel Trials style), skipping auto-click");
                    }
                    return false;
                }

                string name = topGo.name;

                // New arrow instance (different GO than last frame) — reset state
                // so we give this arrow its own chance.
                int instanceId = topGo.GetInstanceID();
                if (instanceId != _orphanArrowInstanceId)
                {
                    _orphanArrowInstanceId = instanceId;
                    _orphanArrowFirstSeen = UnityEngine.Time.time;
                    _orphanArrowHandled = false;
                    _orphanArrowAnnounced = false;
                    DebugLogger.Log(LogCategory.Game, "Main",
                        $"Orphan TutorialArrow detected in {mgrKind}: {name}, " +
                        $"physicTarget={arrowVc.physicTarget?.name ?? "null"}, " +
                        $"dispTarget={arrowVc.dispTarget?.name ?? "null"}, " +
                        $"ipclick={arrowVc.ipclick?.Length ?? 0}");
                }

                // Re-poll targets every frame — the arrow may populate them
                // late, or we might catch a dispTarget even when physicTarget
                // is null (both are "often null" per game-api.md).
                var physicTarget = arrowVc.physicTarget;
                var dispTarget = arrowVc.dispTarget;

                // Announce once so the user knows an arrow is blocking.
                // Only announce for dialog-manager arrows when SBH has
                // nothing meaningful going on — otherwise the announcement
                // collides with SBH's item/text announcement.
                if (!_orphanArrowAnnounced)
                {
                    _orphanArrowAnnounced = true;
                    if (mgrKind == "dialog" && _screenButtonHandler?.IsActive != true)
                        ScreenReader.Say(Loc.Get("duel_tutorial_arrow_pointing"));
                }

                // Path 2: manual user trigger (dialog manager only).
                // Gated on:
                //   - !_orphanArrowHandled: each arrow instance gets at most
                //     one manual attempt — prevents the "stuck on stage
                //     mission popup" input-stealing loop where we kept
                //     re-firing ipclick on every Enter press.
                //   - SBH idle: when SBH has items or text mode, the user's
                //     Enter/Space belongs to SBH (navigate items, advance
                //     scenario text). We only claim keys when SBH has
                //     nothing else to do with them.
                if (mgrKind == "dialog"
                    && !_orphanArrowHandled
                    && _screenButtonHandler?.IsActive != true)
                {
                    bool userPressedAdvance =
                        InputManager.TryConsumeKeyDown(UnityEngine.KeyCode.Return)
                        || InputManager.TryConsumeKeyDown(UnityEngine.KeyCode.KeypadEnter)
                        || InputManager.TryConsumeKeyDown(UnityEngine.KeyCode.Space);

                    if (userPressedAdvance)
                    {
                        DebugLogger.Log(LogCategory.Game, "Main",
                            "Orphan TutorialArrow: user pressed advance key, " +
                            "invoking ipclick directly");
                        InvokeArrowIpclickDirect(arrowVc);
                        // Always mark handled — if the attempt failed, the
                        // user can press F9 (debug) or wait for a new arrow
                        // instance. This prevents infinite key stealing.
                        _orphanArrowHandled = true;
                        return true;
                    }
                }

                // Auto-click after timeout. Once per arrow instance. Unified
                // for both target-resolved and null-target cases — the
                // delay gives the game and any focus state time to settle
                // (Home→Shop arrow failed when we clicked immediately but
                // worked when F9 fired 11s later; same coords, same
                // function, only difference was timing/focus).
                if (!_orphanArrowHandled)
                {
                    float elapsed = UnityEngine.Time.time - _orphanArrowFirstSeen;
                    if (elapsed >= OrphanArrowIpclickTimeout)
                    {
                        if (physicTarget != null || dispTarget != null)
                        {
                            DebugLogger.Log(LogCategory.Game, "Main",
                                $"Orphan TutorialArrow target resolved after " +
                                $"{elapsed:F1}s " +
                                $"(physic={physicTarget?.name ?? "null"}, " +
                                $"disp={dispTarget?.name ?? "null"}), clicking");
                            ClickArrowAtTarget(arrowVc);
                            _orphanArrowHandled = true;
                            return mgrKind == "dialog";
                        }

                        var ipclick = arrowVc.ipclick;
                        if (ipclick != null && ipclick.Length > 0)
                        {
                            DebugLogger.Log(LogCategory.Game, "Main",
                                $"Orphan TutorialArrow: null targets after " +
                                $"{elapsed:F1}s, auto-invoking ipclick");
                            InvokeArrowIpclickDirect(arrowVc);
                            _orphanArrowHandled = true;
                            return mgrKind == "dialog";
                        }

                        // No usable target or ipclick — mark handled so we
                        // stop re-evaluating this instance.
                        DebugLogger.Log(LogCategory.Game, "Main",
                            "Orphan TutorialArrow: null targets and no " +
                            "ipclick handlers; cannot auto-advance");
                        _orphanArrowHandled = true;
                    }
                }

                // Announcing/waiting state — never block other handlers so
                // ScreenButtonHandler can still scan the underlying screen.
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
        /// Returns the TutorialArrowViewController on top of the named
        /// manager's stack, or null if the top isn't an arrow. Out params
        /// provide the GO (for instance tracking) and the manager name.
        /// </summary>
        private static Il2CppYgomGame.Menu.TutorialArrowViewController TryGetArrowOnTop(
            Il2CppSystem.Collections.Generic.Dictionary<string,
                Il2CppYgomSystem.UI.ViewControllerManager> namedManager,
            string key,
            out GameObject topGo,
            out string managerKind)
        {
            topGo = null;
            managerKind = null;
            if (!namedManager.TryGetValue(key, out var mgr) || mgr == null) return null;
            var topVc = mgr.GetStackTopViewController();
            if (topVc?.gameObject == null) return null;
            string name = topVc.gameObject.name;
            if (name != "TutorialArrow" && name != "TutorialArrowPart") return null;
            var vc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
            if (vc == null) return null;
            topGo = topVc.gameObject;
            managerKind = key;
            return vc;
        }

        /// <summary>
        /// Returns true if the content-manager stack has an HtjsonPage one
        /// level beneath the top VC. Used to detect Duel Trials quiz-banner
        /// screens where the user is expected to navigate manually rather
        /// than have us auto-click the arrow.
        /// </summary>
        private static bool HasHtjsonPageBeneath(
            Il2CppSystem.Collections.Generic.Dictionary<string,
                Il2CppYgomSystem.UI.ViewControllerManager> namedManager)
        {
            try
            {
                if (!namedManager.TryGetValue("content", out var contentMgr)) return false;
                if (contentMgr == null) return false;
                int count = contentMgr.GetStackCount();
                if (count < 2) return false;
                var belowVc = contentMgr.GetStackViewController(count - 2);
                if (belowVc?.gameObject == null) return false;
                return belowVc.gameObject.name == "HtjsonPage";
            }
            catch { return false; }
        }

        /// <summary>
        /// Resets orphan arrow tracking when no arrow is on the dialog stack.
        /// </summary>
        private void ResetOrphanArrowState()
        {
            _orphanArrowInstanceId = -1;
            _orphanArrowFirstSeen = -1f;
            _orphanArrowHandled = false;
            _orphanArrowAnnounced = false;
        }

        /// <summary>
        /// Invokes each handler in arrowVc.ipclick[] directly. Bypasses the
        /// arrow's IsCollider raycast check entirely — safe for UI buttons
        /// whose handler IS the specific target button (documented in
        /// game-api.md). Mirrors DialogHandler.ActivateViaTutorialArrow:
        /// prefers YgomButton.OnPointerClick, falls back to ExecuteEvents on
        /// the handler's MonoBehaviour GameObject.
        /// </summary>
        /// <returns>True if at least one handler was successfully dispatched.</returns>
        private static bool InvokeArrowIpclickDirect(
            Il2CppYgomGame.Menu.TutorialArrowViewController arrowVc)
        {
            try
            {
                var ipclick = arrowVc.ipclick;
                if (ipclick == null || ipclick.Length == 0)
                {
                    DebugLogger.Log(LogCategory.Game, "Main",
                        "InvokeArrowIpclickDirect: no ipclick handlers");
                    return false;
                }

                var eventData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current)
                {
                    position = new Vector2(Screen.width / 2f, Screen.height / 2f)
                };

                int dispatched = 0;
                for (int i = 0; i < ipclick.Length; i++)
                {
                    var handler = ipclick[i];
                    if (handler == null) continue;

                    try
                    {
                        // Prefer YgomButton cast — it's the game's button type
                        // and its OnPointerClick runs the full click pipeline.
                        var button = handler.TryCast<Il2CppYgomSystem.UI.YgomButton>();
                        if (button != null)
                        {
                            DebugLogger.Log(LogCategory.Game, "Main",
                                $"ipclick[{i}]: YgomButton on " +
                                $"{button.gameObject?.name ?? "?"}");
                            button.OnPointerClick(eventData);
                            dispatched++;
                            continue;
                        }

                        // Fall back to MonoBehaviour + ExecuteEvents for any
                        // other IPointerClickHandler implementation.
                        var mb = handler.TryCast<MonoBehaviour>();
                        if (mb?.gameObject != null)
                        {
                            DebugLogger.Log(LogCategory.Game, "Main",
                                $"ipclick[{i}]: {mb.GetType().Name} on " +
                                $"{mb.gameObject.name}");
                            UnityEngine.EventSystems.ExecuteEvents.Execute(
                                mb.gameObject, eventData,
                                UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                            dispatched++;
                            continue;
                        }

                        DebugLogger.Log(LogCategory.Game, "Main",
                            $"ipclick[{i}]: unknown handler type, skipping");
                    }
                    catch (System.Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Game, "Main",
                            $"ipclick[{i}] invoke error: {ex.Message}");
                    }
                }

                DebugLogger.Log(LogCategory.Game, "Main",
                    $"InvokeArrowIpclickDirect: dispatched {dispatched}/" +
                    $"{ipclick.Length} handler(s)");
                return dispatched > 0;
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "Main",
                    $"InvokeArrowIpclickDirect error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Clicks a TutorialArrow via arrowVc.OnPointerClick at the correct position.
        /// Prefers physicTarget, falls back to dispTarget (both "often null" per
        /// game-api.md). If both are null, uses screen center (click-to-continue).
        /// </summary>
        internal static void ClickArrowAtTarget(
            Il2CppYgomGame.Menu.TutorialArrowViewController arrowVc)
        {
            var eventData = new UnityEngine.EventSystems.PointerEventData(
                UnityEngine.EventSystems.EventSystem.current);

            // Prefer physicTarget (has collider for raycast); fall back to
            // dispTarget (display-only target). Either works for click routing.
            var clickTarget = arrowVc.physicTarget ?? arrowVc.dispTarget;
            string targetKind = arrowVc.physicTarget != null ? "physicTarget" : "dispTarget";

            if (clickTarget != null)
            {
                // Use the arrow's own targetCamera for coordinate conversion —
                // Camera.main is NOT the right camera for 3D world targets.
                var cam = arrowVc.targetCamera;
                if (cam == null) cam = Camera.main;

                if (cam != null)
                {
                    Vector3 screenPos = cam.WorldToScreenPoint(
                        clickTarget.transform.position);
                    eventData.position = new Vector2(screenPos.x, screenPos.y);
                    DebugLogger.Log(LogCategory.Game, "Main",
                        $"Clicking arrow at {targetKind} pos " +
                        $"({screenPos.x:F0}, {screenPos.y:F0}) via " +
                        $"{cam.name} for {clickTarget.name}");
                }
                else
                {
                    eventData.position = new Vector2(
                        Screen.width / 2f, Screen.height / 2f);
                    DebugLogger.Log(LogCategory.Game, "Main",
                        $"No camera for {targetKind}, using screen center");
                }
            }
            else
            {
                // No target at all — click-to-continue, screen center is fine
                eventData.position = new Vector2(
                    Screen.width / 2f, Screen.height / 2f);
                DebugLogger.Log(LogCategory.Game, "Main",
                    "No physicTarget or dispTarget, clicking arrow at screen center");
            }

            // RegistPointerCurrentRaycast populates the eventData with raycast
            // results that IsCollider needs — without it, OnPointerClick silently fails
            // even with the correct screen position.
            bool registered = false;
            bool hasTarget = arrowVc.physicTarget != null || arrowVc.dispTarget != null;
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
                return;
            }

            // Registration failed. For arrows with a real world/UI target
            // (physicTarget or dispTarget), hardware mouse at that position
            // is the only reliable path — ipclick direct invocation NREs
            // when the handler is SingleViewController (3D collider case).
            // For targetless arrows (click-to-continue), ipclick direct is
            // still the right fallback.
            if (hasTarget)
            {
                if (ClickViaHardwareMouse(eventData.position, "orphan arrow"))
                    return;
                // Hardware click couldn't find the game window — fall through
                // to ipclick as a last resort
                DebugLogger.Log(LogCategory.Game, "Main",
                    "Hardware mouse unavailable, falling back to ipclick direct");
            }

            // Targetless (or hardware-mouse unavailable) fallback:
            // call ipclick handlers directly (bypasses IsCollider check)
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
                try { arrowVc.OnPointerClick(eventData); } catch { }
            }
        }

        /// <summary>
        /// Simulates a real mouse click at the given Unity screen position by
        /// calling SetCursorPos + mouse_event. This is the only reliable way
        /// to click 3D world colliders (e.g. TutorialArrow pointing at
        /// Collider_Cardshop) — Unity's event system populates raycast data
        /// properly only for real OS-level clicks. Converts Unity coords
        /// (origin bottom-left, client-relative) to Windows screen coords.
        /// Returns false if the game window handle couldn't be found.
        /// </summary>
        internal static bool ClickViaHardwareMouse(Vector2 unityPos, string reason)
        {
            try
            {
                // Unity: (0,0)=bottom-left client. Windows: (0,0)=top-left screen.
                int clientX = (int)unityPos.x;
                int clientY = Screen.height - (int)unityPos.y;

                POINT pt;
                pt.X = clientX;
                pt.Y = clientY;
                var hwnd = FindWindow(null, "Yu-Gi-Oh! DUEL LINKS");
                if (hwnd == System.IntPtr.Zero) hwnd = GetActiveWindow();

                if (hwnd == System.IntPtr.Zero || !ClientToScreen(hwnd, ref pt))
                {
                    DebugLogger.Log(LogCategory.Game, "Main",
                        $"Hardware mouse ({reason}): no window handle or ClientToScreen failed");
                    return false;
                }

                // Force the game window to foreground before clicking. If a
                // screen reader (NVDA) or other app briefly stole focus,
                // SetCursorPos + mouse_event would otherwise route to the
                // wrong window. Windows may refuse this call when the
                // calling process doesn't own the foreground window; that's
                // fine — the click still works if the game is already focused.
                bool fg = SetForegroundWindow(hwnd);

                DebugLogger.Log(LogCategory.Game, "Main",
                    $"Hardware mouse ({reason}): click at screen ({pt.X}, {pt.Y}) " +
                    $"from Unity ({unityPos.x:F0}, {unityPos.y:F0}), " +
                    $"SetForegroundWindow={fg}");

                SetCursorPos(pt.X, pt.Y);
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, System.UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, System.UIntPtr.Zero);
                return true;
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "Main",
                    $"Hardware mouse ({reason}) error: {ex.Message}");
                return false;
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
        /// F9 (debug): Hardware mouse click at the current TutorialArrow's
        /// physicTarget. Checks both dialog and content managers — some
        /// arrows (shop tutorial, missions tutorial) are in dialog, others
        /// (post-shop tutorial overlay on ShopLineup) are in content.
        /// Uses ClickViaHardwareMouse — the only reliable click path for 3D
        /// world colliders since ipclick direct invocation NREs on
        /// SingleViewController handlers.
        /// </summary>
        private void SimulateClickAtArrowTarget()
        {
            try
            {
                MelonLogger.Msg("=== F9: Simulating click at arrow target ===");

                var arrowVc = FindCurrentArrowVc(out string mgrName);
                if (arrowVc == null) { ScreenReader.Say("No tutorial arrow active"); return; }

                MelonLogger.Msg($"F9: found TutorialArrow in {mgrName} manager");

                var physicTarget = arrowVc.physicTarget;
                var dispTarget = arrowVc.dispTarget;
                var clickTarget = physicTarget ?? dispTarget;
                if (clickTarget == null) { ScreenReader.Say("Arrow has no physics or display target"); return; }

                var cam = arrowVc.targetCamera;
                if (cam == null) cam = Camera.main;
                if (cam == null) { ScreenReader.Say("No camera"); return; }

                Vector3 screenPos = cam.WorldToScreenPoint(clickTarget.transform.position);
                MelonLogger.Msg($"Target ({(physicTarget != null ? "physicTarget" : "dispTarget")}={clickTarget.name}): " +
                    $"Unity ({screenPos.x:F0}, {screenPos.y:F0}), screen {Screen.width}x{Screen.height}");

                if (ClickViaHardwareMouse(new Vector2(screenPos.x, screenPos.y), "F9"))
                    ScreenReader.Say("Click simulated");
                else
                    ScreenReader.Say("Hardware click failed");
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"SimulateClick error: {ex.Message}");
                ScreenReader.Say($"Click failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Locates a TutorialArrowViewController on the top of either the
        /// dialog or content manager stack. Checks dialog first (the common
        /// case). Returns null if no arrow is active. Outputs the manager
        /// name where the arrow was found for diagnostics.
        /// </summary>
        private static Il2CppYgomGame.Menu.TutorialArrowViewController FindCurrentArrowVc(
            out string managerName)
        {
            managerName = null;
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return null;

                // Dialog manager first — missions/home-screen tutorial arrows live here
                if (namedManager.TryGetValue("dialog", out var dialogMgr))
                {
                    var top = dialogMgr?.GetStackTopViewController();
                    if (top?.gameObject != null &&
                        (top.gameObject.name == "TutorialArrow"
                         || top.gameObject.name == "TutorialArrowPart"))
                    {
                        var vc = top.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                        if (vc != null)
                        {
                            managerName = "dialog";
                            return vc;
                        }
                    }
                }

                // Content manager — in-screen tutorial overlays (shop tutorial,
                // Duel Trials quiz arrows, etc.)
                if (namedManager.TryGetValue("content", out var contentMgr))
                {
                    var top = contentMgr?.GetStackTopViewController();
                    if (top?.gameObject != null &&
                        (top.gameObject.name == "TutorialArrow"
                         || top.gameObject.name == "TutorialArrowPart"))
                    {
                        var vc = top.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                        if (vc != null)
                        {
                            managerName = "content";
                            return vc;
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "Main",
                    $"FindCurrentArrowVc error: {ex.Message}");
            }

            return null;
        }

        // Windows API for hardware mouse simulation
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern System.IntPtr FindWindow(string lpClassName, string lpWindowName);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern System.IntPtr GetActiveWindow();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(System.IntPtr hWnd);

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
