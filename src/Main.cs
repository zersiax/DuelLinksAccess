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

[assembly: MelonInfo(typeof(DuelLinksAccess.Main), "DuelLinksAccess", "1.1.5", "DuelLinksAccess Team")]
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
        private CardTraderHandler _cardTraderHandler;

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

        // Tutorial-state change-detection cache (debug instrumentation).
        // Logged whenever the boot tutorial's IsTutorialProgress flag or
        // TutorialManager.waitTarget changes — gives us a timeline of
        // gate-state transitions to correlate with click events.
        private bool _tutLastInProgress = false;
        private string _tutLastWaitTarget = "";
        private bool _tutStateInitialized = false;

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
            _cardTraderHandler = new CardTraderHandler();
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

            // F11 (no Ctrl) = Activate the current TutorialArrow target.
            // The dependable, always-available activation path. Fires both
            // ipclick and a hardware mouse click — the F9 working-pair from
            // game-api.md:543. Available outside debug mode.
            if (InputManager.TryConsumeKeyDown(KeyCode.F11))
            {
                ActivateTutorialArrowTarget();
                return true;
            }

            return false;
        }

        #endregion

        #region Handler Updates

        private void UpdateHandlers()
        {
            // Tutorial-state instrumentation (debug only). Logs the boot
            // tutorial inProgress + waitTarget state when EITHER changes,
            // so we can correlate state transitions with click events from
            // the next test session log. Throttled so we don't spam.
            DumpTutorialStateOnChange();

            // Process deferred announcements (e.g. phase change — engine needs
            // a frame to update before we can read the new phase)
            DuelEventAnnouncer.Update();

            // Character speech bubbles — runs unconditionally because intro lines
            // fire before DuelHandler becomes active (HUD is up, DuelStart not yet fired)
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

            // Card Trader handler — intercepts CardTraderViewController2 screens
            _cardTraderHandler?.Update();
            if (_cardTraderHandler?.IsActive == true) return;

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

                // Walk every named manager — game versions/tutorial steps
                // sometimes push arrows into managers we didn't anticipate
                // (the recurring shop-tutorial breakage). The shared helper
                // also backs F11 and ScreenButtonHandler so all sites see
                // the same set of arrows.
                if (!GameStateTracker.TryFindArrowAcrossManagers(
                        namedManager,
                        out var arrowVc,
                        out var topGo,
                        out string mgrKind))
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

                // Announce once per arrow instance so the user knows an arrow
                // is active and how to advance it. Shape C (3D world Collider)
                // always announces immediately. Shape B (UISelectablePointer)
                // delays 1.5s to avoid being cut off by the home-screen
                // re-scan speech (SBH rescan takes ~1.2s). Shape A (click-to-
                // continue) auto-dismisses quickly so no announcement needed.
                if (!_orphanArrowAnnounced)
                {
                    var announceShape = ClassifyArrowShape(arrowVc);
                    bool sbhIdle = _screenButtonHandler?.IsActive != true;
                    // UISelectablePointer always uses its own delayed path —
                    // exclude it from the sbhIdle fast branch to avoid a race
                    // where SBH going idle (screen=Dialog) fires the generic
                    // message before the home-screen speech finishes.
                    if (announceShape == ArrowShape.WorldColliderPointer
                        || (sbhIdle && announceShape != ArrowShape.UISelectablePointer))
                    {
                        _orphanArrowAnnounced = true;
                        ScreenReader.Say(Loc.Get("duel_tutorial_arrow_pointing"));
                    }
                    else if (announceShape == ArrowShape.UISelectablePointer)
                    {
                        // Wait until home-screen speech settles before announcing.
                        float elapsed = UnityEngine.Time.time - _orphanArrowFirstSeen;
                        if (elapsed >= 1.5f)
                        {
                            _orphanArrowAnnounced = true;
                            // Prefer the SBH item label (live, post-dedup) over the
                            // ipclick GO label (may be a static parent like "ステージ").
                            var ipclickGo = GetIpclickGO(arrowVc);
                            string liveLabel = _screenButtonHandler?.GetLabelForDescendant(ipclickGo);
                            bool sbhActiveNoTarget = _screenButtonHandler?.IsActive == true
                                && string.IsNullOrEmpty(liveLabel);
                            string msg;
                            if (sbhActiveNoTarget)
                            {
                                // SBH active but no item matches the target — user is on a
                                // different screen (e.g. Home VC after series change).
                                string fallback = GetIpclickTargetLabel(arrowVc);
                                msg = !string.IsNullOrEmpty(fallback)
                                    ? Loc.Get("tutorial_arrow_back_named", fallback)
                                    : Loc.Get("tutorial_arrow_back");
                            }
                            else
                            {
                                string label = !string.IsNullOrEmpty(liveLabel)
                                    ? liveLabel : GetIpclickTargetLabel(arrowVc);
                                msg = !string.IsNullOrEmpty(label)
                                    ? Loc.Get("tutorial_arrow_target_named", label)
                                    : Loc.Get("duel_tutorial_arrow_pointing");
                            }
                            ScreenReader.SayQueued(msg);
                        }
                    }
                }

                // Path 2: manual user trigger (any manager).
                // Gated on:
                //   - !_orphanArrowHandled: each arrow instance gets at most
                //     one manual attempt — prevents the "stuck on stage
                //     mission popup" input-stealing loop where we kept
                //     re-firing ipclick on every Enter press.
                //   - SBH idle: when SBH has items or text mode, the user's
                //     Enter/Space belongs to SBH (navigate items, advance
                //     scenario text). We only claim keys when SBH has
                //     nothing else to do with them.
                if (!_orphanArrowHandled
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

                // Auto-click after timeout, shape-aware. Different arrow
                // shapes need different policies — overfitting one auto-click
                // for all of them is the source of the recurring bug pattern.
                //   Shape A (click-to-continue): auto-dismiss like before.
                //   Shape B (UI-Selectable pointer): user-driven only.
                //   Shape C (3D world Collider pointer, e.g. shop on Home):
                //     user-driven only — OnPointerClick on world colliders is
                //     documented to silently fail (game-api.md:543), so the
                //     user must hit Enter on the navigated item or F11.
                if (!_orphanArrowHandled)
                {
                    float elapsed = UnityEngine.Time.time - _orphanArrowFirstSeen;
                    if (elapsed >= OrphanArrowIpclickTimeout)
                    {
                        ArrowShape shape = ClassifyArrowShape(arrowVc);
                        switch (shape)
                        {
                            case ArrowShape.ClickToContinue:
                                if (physicTarget != null || dispTarget != null)
                                {
                                    DebugLogger.Log(LogCategory.Game, "Main",
                                        $"Orphan TutorialArrow (ClickToContinue) " +
                                        $"target resolved after {elapsed:F1}s " +
                                        $"(physic={physicTarget?.name ?? "null"}, " +
                                        $"disp={dispTarget?.name ?? "null"}), clicking");
                                    ClickArrowAtTarget(arrowVc);
                                }
                                else
                                {
                                    DebugLogger.Log(LogCategory.Game, "Main",
                                        $"Orphan TutorialArrow (ClickToContinue): " +
                                        $"null targets after {elapsed:F1}s, " +
                                        $"clicking screen center to dismiss");
                                    ClickArrowAtTarget(arrowVc);
                                }
                                _orphanArrowHandled = true;
                                return true;

                            case ArrowShape.UISelectablePointer:
                            case ArrowShape.WorldColliderPointer:
                                DebugLogger.Log(LogCategory.Game, "Main",
                                    $"Orphan TutorialArrow ({shape}): no " +
                                    $"auto-click; user must press Enter on " +
                                    $"the navigated item or F11");
                                _orphanArrowHandled = true;
                                break;
                        }
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

        internal enum ArrowShape
        {
            // physicTarget is null. Click anywhere dismisses; cutscene narrative
            // text. Safe to auto-dismiss after a delay.
            ClickToContinue,
            // physicTarget is a UI Selectable / Graphic. The user navigates to
            // it via SBH/DialogHandler and presses Enter; SBH's
            // ActivateViaTutorialArrow routes through ipclick. No auto-click.
            UISelectablePointer,
            // physicTarget is a 3D world Collider (e.g. Collider_Cardshop on
            // Home). targetCamera is non-null and not Camera.main.
            // OnPointerClick fails (game-api.md:543); the working pair is
            // ipclick + hardware mouse. No auto-click — user uses Enter on the
            // navigated item or F11.
            WorldColliderPointer
        }

        /// <summary>
        /// Classifies a TutorialArrow into one of three shapes that need
        /// different activation policies. See ArrowShape doc-comments.
        /// </summary>
        internal static ArrowShape ClassifyArrowShape(
            Il2CppYgomGame.Menu.TutorialArrowViewController arrowVc)
        {
            try
            {
                var physicTarget = arrowVc.physicTarget;
                if (physicTarget == null)
                {
                    // A null-physicTarget arrow with an ipclick handler is
                    // pointing at a specific UI button (e.g. Stage-7 Vagrant
                    // shop tutorial: ipclick = Trainer Purchase button; GX
                    // Series-unlock: ipclick = SeriesButton). Auto-firing the
                    // ipclick reopens the very screen the user just closed,
                    // creating an inescapable loop. Treat as UISelectablePointer
                    // so the user navigates to the button and presses Enter.
                    // Genuine cutscene "click anywhere" arrows have no ipclick.
                    int ipclickCount = arrowVc.ipclick?.Length ?? 0;
                    if (ipclickCount > 0) return ArrowShape.UISelectablePointer;
                    return ArrowShape.ClickToContinue;
                }

                // 3D world target: arrow's own targetCamera is something
                // other than Camera.main (typically "SingleCamera" or another
                // scene-specific camera) AND the target has no UI Graphic.
                // This is the signal documented in game-api.md:753 for the
                // shop-on-Home case (Collider_Cardshop rendered by SingleCamera).
                bool hasUiGraphic = physicTarget.GetComponent<UnityEngine.UI.Graphic>() != null;
                var targetCam = arrowVc.targetCamera;
                bool worldCamera = targetCam != null && targetCam != Camera.main;

                if (worldCamera && !hasUiGraphic) return ArrowShape.WorldColliderPointer;

                return ArrowShape.UISelectablePointer;
            }
            catch
            {
                // If we can't tell, treat as UI pointer (the safer default —
                // no auto-click, user-driven).
                return ArrowShape.UISelectablePointer;
            }
        }

        /// <summary>Returns the GameObject of ipclick[0], or null.</summary>
        private static GameObject GetIpclickGO(
            Il2CppYgomGame.Menu.TutorialArrowViewController arrowVc)
        {
            try
            {
                var ipclick = arrowVc?.ipclick;
                if (ipclick == null || ipclick.Length == 0) return null;
                return ipclick[0]?.TryCast<MonoBehaviour>()?.gameObject;
            }
            catch { return null; }
        }

        /// <summary>
        /// Returns the display label of ipclick[0]'s target GO via LabelExtractor, or null.
        /// Falls back when SBH has no live label for the target.
        /// </summary>
        private static string GetIpclickTargetLabel(
            Il2CppYgomGame.Menu.TutorialArrowViewController arrowVc)
        {
            try
            {
                var go = GetIpclickGO(arrowVc);
                if (go == null) return null;
                string label = LabelExtractor.GetLabel(go);
                if (!string.IsNullOrEmpty(label) && !LabelExtractor.IsPlaceholderText(label))
                    return label;
            }
            catch { }
            return null;
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
        internal static bool InvokeArrowIpclickDirect(
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

                // Position eventData at the physicTarget's screen position via
                // the arrow's own targetCamera (game-api.md:543/:756). The
                // ipclick handler — typically SingleViewController — inspects
                // eventData.position / pointerCurrentRaycast to decide what
                // was clicked. Screen center hits the wrong thing for 3D
                // world targets like the Gate or Shop. Falls back to screen
                // center for click-to-continue arrows with no physicTarget.
                Vector2 eventPos = new Vector2(
                    Screen.width / 2f, Screen.height / 2f);
                var arrowPhysicTarget = arrowVc.physicTarget;
                if (arrowPhysicTarget != null)
                {
                    var arrowCam = arrowVc.targetCamera;
                    if (arrowCam == null) arrowCam = Camera.main;
                    if (arrowCam != null)
                    {
                        Vector3 worldScreen = arrowCam.WorldToScreenPoint(
                            arrowPhysicTarget.transform.position);
                        eventPos = new Vector2(worldScreen.x, worldScreen.y);
                        DebugLogger.Log(LogCategory.Game, "Main",
                            $"InvokeArrowIpclickDirect: eventData.position " +
                            $"= ({eventPos.x:F0}, {eventPos.y:F0}) " +
                            $"from {arrowPhysicTarget.name} via {arrowCam.name}");
                    }
                }

                var eventData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current)
                {
                    position = eventPos
                };

                // DO NOT call arrowVc.RegistPointerCurrentRaycast(eventData)
                // here. Empirical: pre-alpha2 (a5044b5) advanced the Shop
                // tutorial WITHOUT it; v1.1.0 with this call did NOT advance
                // the gate even with otherwise-identical ipclick + hardware
                // mouse. game-api.md:756 documents that the call returns
                // false for the home-screen Cardshop arrow — and the
                // half-populated raycast data appears to poison the click
                // for the SingleViewController handler. The arrow's own
                // OnPointerClick path needs raycast data, but the direct
                // ipclick handler path does not.

                int dispatched = 0;
                for (int i = 0; i < ipclick.Length; i++)
                {
                    var handler = ipclick[i];
                    if (handler == null) continue;

                    try
                    {
                        // Call OnPointerClick directly through the IL2CPP-
                        // generated IPointerClickHandler interface thunk. This
                        // is the ONLY path that actually reaches the boot-
                        // tutorial gate-release trigger — confirmed by
                        // empirical test against the pre-alpha2 a5044b5 build,
                        // where this exact direct call advanced the Shop step.
                        // ExecuteEvents.Execute on the handler's GO does NOT
                        // work in IL2CPP — Unity's component-lookup path
                        // misses the handler. Log the GO name for diagnostics.
                        var mb = handler.TryCast<MonoBehaviour>();
                        string handlerName = mb != null && mb.gameObject != null
                            ? mb.gameObject.name : "unknown";
                        DebugLogger.Log(LogCategory.Game, "Main",
                            $"ipclick[{i}]: invoking OnPointerClick on {handlerName}");
                        handler.OnPointerClick(eventData);
                        dispatched++;
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
                    ScreenReader.Say(Loc.Get("hardware_click_no_window"));
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

        /// <summary>
        /// Logs the boot tutorial state (inProgress + waitTarget) any time
        /// either changes. Cheap per-frame query that only emits a log line
        /// on transition. Combined with the Harmony patches on
        /// API.User_tutorial_dialog / TutorialManager.fetch / .Notificator
        /// this gives a full timeline of tutorial gate-state transitions
        /// alongside the calls that drove them — the missing oracle for
        /// "what actually advances the boot tutorial Shop step?"
        /// </summary>
        private void DumpTutorialStateOnChange()
        {
            try
            {
                if (!GameStateTracker.IsGameReady) return;

                bool ip = Il2CppYgomGame.Utility.TutorialUtil.IsTutorialProgress(
                    Il2CppYgomGame.Utility.TutorialUtil.Type.Boot);
                string wt = Il2CppYgomSystem.Utility.TutorialManager.waitTarget ?? "";

                if (!_tutStateInitialized)
                {
                    _tutLastInProgress = ip;
                    _tutLastWaitTarget = wt;
                    _tutStateInitialized = true;
                    DebugLogger.Log(LogCategory.Game, "Tutorial",
                        $"Initial state: boot inProgress={ip}, waitTarget=\"{wt}\"");
                    return;
                }

                if (ip != _tutLastInProgress || wt != _tutLastWaitTarget)
                {
                    DebugLogger.Log(LogCategory.Game, "Tutorial",
                        $"State change: boot inProgress " +
                        $"{_tutLastInProgress}->{ip}, " +
                        $"waitTarget \"{_tutLastWaitTarget}\"->\"{wt}\"");
                    _tutLastInProgress = ip;
                    _tutLastWaitTarget = wt;
                }
            }
            catch
            {
                // Swallow — instrumentation must never break the mod
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
        /// Locates the current TutorialArrowViewController by walking every
        /// named manager in priority order (dialog, dialogbase, content,
        /// overlay, sub, subcontent, then anything else). Returns null if no
        /// arrow is active. Outputs the manager name where the arrow was
        /// found for diagnostics.
        /// </summary>
        private static Il2CppYgomGame.Menu.TutorialArrowViewController FindCurrentArrowVc(
            out string managerName)
        {
            managerName = null;
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return null;

                if (GameStateTracker.TryFindArrowAcrossManagers(
                        namedManager, out var arrowVc, out _, out string mgrKey))
                {
                    managerName = mgrKey;
                    return arrowVc;
                }
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "Main",
                    $"FindCurrentArrowVc error: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// F11 (always available, not debug-gated): activate the current
        /// TutorialArrow target via the working pair from game-api.md:543 —
        /// ipclick handlers AND a hardware mouse click together. The
        /// dependable user-driven advancement key for arrows where the normal
        /// SBH path doesn't reach (3D world colliders, overlay-only arrows).
        /// </summary>
        private void ActivateTutorialArrowTarget()
        {
            try
            {
                MelonLogger.Msg("=== F11: Activate arrow target ===");

                var arrowVc = FindCurrentArrowVc(out string mgrName);
                if (arrowVc == null)
                {
                    ScreenReader.Say(Loc.Get("tutorial_arrow_no_arrow"));
                    return;
                }

                MelonLogger.Msg($"F11: found TutorialArrow in {mgrName} manager");

                // Step 1: invoke ipclick handlers directly. This is what
                // satisfies the tutorial gate for most pointer arrows even
                // when the visible click "fails" (RegistPointerCurrentRaycast
                // returning false).
                bool dispatched = InvokeArrowIpclickDirect(arrowVc);

                // Step 2: hardware mouse click at the target's screen
                // position. Some arrows need both — ipclick wakes the gate,
                // the hardware click satisfies any focus/raycast checks.
                bool clickedHardware = false;
                var physicTarget = arrowVc.physicTarget;
                var dispTarget = arrowVc.dispTarget;
                var clickTarget = physicTarget ?? dispTarget;
                if (clickTarget != null)
                {
                    var cam = arrowVc.targetCamera;
                    if (cam == null) cam = Camera.main;
                    if (cam != null)
                    {
                        Vector3 screenPos = cam.WorldToScreenPoint(
                            clickTarget.transform.position);
                        MelonLogger.Msg($"F11 target ({clickTarget.name}): " +
                            $"Unity ({screenPos.x:F0}, {screenPos.y:F0}), " +
                            $"screen {Screen.width}x{Screen.height}");
                        clickedHardware = ClickViaHardwareMouse(
                            new Vector2(screenPos.x, screenPos.y), "F11");
                    }
                }

                if (dispatched || clickedHardware)
                    ScreenReader.Say(Loc.Get("tutorial_arrow_activated"));
                else
                    ScreenReader.Say(Loc.Get("tutorial_arrow_no_target"));
            }
            catch (System.Exception ex)
            {
                MelonLogger.Msg($"ActivateTutorialArrowTarget error: {ex.Message}");
                ScreenReader.Say($"Activation failed: {ex.Message}");
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
