using UnityEngine;

namespace DuelLinksAccess
{
    /// <summary>
    /// Main handler for duel accessibility. Coordinates event announcements,
    /// event log browsing, field navigation, and card actions.
    ///
    /// Key bindings during duel:
    ///   Tab / Shift+Tab — Cycle field zones
    ///   Left / Right — Navigate cards within zone
    ///   Enter — Open actions for selected card
    ///   V — Re-read current card (verbose)
    ///   F — Field summary
    ///   P — Advance phase
    ///   I — Status (LP, phase, turn)
    ///   J — Toggle event log browsing
    ///   Space — Retry automatic draw during Draw Phase
    ///   During log browsing:
    ///     Up/Down — Navigate entries (older/newer)
    ///     Tab — Re-read current entry
    ///     Escape or J — Close log
    ///   During action menu:
    ///     Up/Down — Navigate commands
    ///     Enter — Execute command
    ///     Escape — Cancel
    ///
    /// Field navigation is suppressed when a dialog overlay is active
    /// so DialogHandler can process duel dialogs (Yes/No, effect selection).
    /// </summary>
    public class DuelHandler
    {
        #region Fields

        private readonly DuelEventLog _eventLog = new();
        private readonly DuelFieldNavigator _fieldNav = new();
        private readonly AutomaticDrawController _automaticDraw = new();
        private bool _wasActive;
        private bool _tutorialArrowAnnounced;
        private bool _tutorialArrowDismissAttempted;

        // Post-duel result screen: DuelEndMessage.OnNextButton() is the OK click
        private bool _duelResultScanned;
        private Il2Cpp.DuelEndMessage _duelEndMessage;

        // Duel yes/no dialog (DuelCommonDialog) — tribute summon confirmation, etc.
        private bool _yesNoDialogAnnounced;
        private string _lastYesNoText = "";
        private float _yesNoCooldown; // Grace period after OnButton — game doesn't clear text immediately

        // Battle position dialog — ATK or DEF position choice
        private bool _bpDialogAnnounced;

        // Select-effect dialog (SelectEffectDialog) — the tabbed picker shown when
        // a card has multiple effects to choose from. Each effect is a tab; the OK
        // button confirms the selected one. Not a VC/Htjson-stack dialog — it's a
        // MonoBehaviour on the duel worker (worker2d.selEffDialog), same as yes/no.
        private bool _selEffAnnounced;
        private int _selEffIndex;
        private float _selEffCooldown; // Grace period after confirm/cancel

        #endregion

        #region Properties

        /// <summary>
        /// Whether the duel handler is currently active.
        /// True when a duel is in progress OR the screen is classified as Duel.
        /// </summary>
        public bool IsActive => DuelEventAnnouncer.InDuel
            || GameStateTracker.CurrentScreen == GameScreen.Duel;

        #endregion

        #region Constructor

        public DuelHandler()
        {
            DuelEventAnnouncer.OnAnnouncement += OnDuelAnnouncement;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Called every frame by Main.Update().
        /// Processes duel-specific key bindings and manages event log.
        /// </summary>
        public void Update()
        {
            if (!IsActive)
            {
                if (_wasActive)
                {
                    _wasActive = false;
                    if (_eventLog.IsBrowsing)
                        _eventLog.StopBrowsing();
                    _eventLog.Clear();
                    _fieldNav.Reset();
                    _automaticDraw.Reset();
                    _tutorialArrowAnnounced = false;
                    _tutorialArrowDismissAttempted = false;
                    DuelEventAnnouncer.Reset();
                }
                return;
            }

            if (!_wasActive)
            {
                _wasActive = true;
                _tutorialArrowAnnounced = false;
                _tutorialArrowDismissAttempted = false;
                _duelResultScanned = false;
                _duelEndMessage = null;
                _yesNoDialogAnnounced = false;
                _lastYesNoText = "";
                _yesNoCooldown = 0f;
                _selEffAnnounced = false;
                _selEffIndex = 0;
                _selEffCooldown = 0f;
                _eventLog.Clear();
                _fieldNav.Reset();
                _automaticDraw.Reset();
            }

            UpdateAutomaticDraw();
            CompleteAutomaticDrawPresentation();

            // Log browsing mode takes priority for navigation keys
            if (_eventLog.IsBrowsing)
            {
                ProcessLogBrowsing();
                return;
            }

            // Post-duel result screen takes priority — find DuelEndMessage and
            // call OnNextButton() directly (bypasses TutorialArrow entirely).
            if (DuelEventAnnouncer.DuelEnded)
            {
                HandleDuelResult();
                return;
            }

            // TutorialArrow overlay: two types:
            //   "click-to-continue" — dismissed by clicking anywhere (OnPointerClick)
            //   "pointing" — stays on screen, points at a game element to interact with
            // We detect "pointing" arrows by checking if a dismiss attempt failed.
            if (IsTutorialArrowActive())
            {
                if (!_tutorialArrowAnnounced)
                {
                    _tutorialArrowAnnounced = true;
                    if (_tutorialArrowDismissAttempted)
                    {
                        // Already tried to dismiss once — this is a "pointing" arrow.
                        // Don't re-announce. Let the user interact with the field.
                        ScreenReader.Say(Loc.Get("duel_tutorial_arrow_pointing"));
                    }
                    // "Click-to-continue" arrows are auto-dismissed below —
                    // no need to announce, it just adds noise
                }

                if (!_tutorialArrowDismissAttempted)
                {
                    // Auto-dismiss click-to-continue arrows silently
                    _tutorialArrowDismissAttempted = true;
                    _tutorialArrowAnnounced = false;
                    DismissTutorialOverlay();
                    return;
                }

                // Pointing arrow — Space re-attempts dismiss
                if (InputManager.TryConsumeKeyDown(KeyCode.Space))
                {
                    DismissTutorialOverlay();
                    return;
                }
            }
            else
            {
                // Arrow gone — reset tracking
                if (_tutorialArrowDismissAttempted || _tutorialArrowAnnounced)
                {
                    _tutorialArrowDismissAttempted = false;
                    _tutorialArrowAnnounced = false;
                }
            }

            // Duel yes/no dialog (DuelCommonDialog) — tribute summon confirmation, etc.
            // This is NOT a standard VC/Htjson dialog, it's a MonoBehaviour inside the duel.
            if (HandleDuelYesNoDialog()) return;

            // Battle position dialog — ATK or DEF position choice during normal summon.
            if (HandleBattlePositionDialog()) return;

            // Multi-effect picker (SelectEffectDialog) — choose which of a card's
            // effects to activate. Tabbed dialog with its own OK/confirm button.
            if (HandleSelectEffectDialog()) return;

            // Field navigation — most keys suppressed when a dialog overlay is active
            // so DialogHandler can handle duel dialogs (Yes/No, card selection).
            // Tab for zone cycling always works — it doesn't conflict with dialog keys.
            bool dialogActive = GameStateTracker.CurrentScreen
                == GameScreen.Dialog;

            // EmotionalList works regardless of dialog state — both when already
            // active and when first detected. RunList often fires alongside RunDialog,
            // so detection must happen before the dialogActive gate.
            if (_fieldNav.InEmotionalList || _fieldNav.CheckForEmotionalList())
            {
                _fieldNav.ProcessInput();
                return;
            }

            if (!dialogActive)
            {
                // Card selection (tribute/material) takes full priority
                if (_fieldNav.InCardSelect)
                {
                    _fieldNav.ProcessInput();
                    return;
                }

                // Action menu and target selection take full priority when open
                if (_fieldNav.InActionMenu || _fieldNav.InTargetMode)
                {
                    _fieldNav.ProcessInput();
                    return;
                }

                // Field nav handles Tab, Left/Right, Enter, C, F, P, Escape
                if (_fieldNav.ProcessInput()) return;
            }
            else
            {
                if (_fieldNav.InActionMenu)
                    _fieldNav.CancelActionMenu();

                // Allow non-conflicting keys (Tab, P, F, C) even during duel dialogs.
                // Dialog keys (Up/Down/Enter/Escape) stay with DialogHandler.
                if (DuelEventAnnouncer.InDuel)
                    _fieldNav.ProcessNonConflictingInput();
            }

            ProcessDuelKeys();
        }

        #endregion

        #region Private Methods

        private void ProcessLogBrowsing()
        {
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.UpArrow))
            {
                _eventLog.BrowseOlder();
                return;
            }

            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.DownArrow))
            {
                _eventLog.BrowseNewer();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.J))
            {
                _eventLog.StopBrowsing();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                _eventLog.ReadCurrent();
                return;
            }
        }

        private void ProcessDuelKeys()
        {
            if (IsAutomaticDrawEligible()
                && InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                try
                {
                    _automaticDraw.Retry(true, true, true,
                        () => DispatchDrawCommand("Space retry"));
                }
                catch (System.Exception ex)
                {
                    DebugLogger.Log(LogCategory.Game, "DuelDraw",
                        $"Space retry failed: {ex.Message}");
                }
                return;
            }

            // I = Status report (LP, phase, turn) — was S, now S is spell zone hotkey
            if (InputManager.TryConsumeKeyDown(KeyCode.I))
            {
                string status = DuelEventAnnouncer.GetStatusText();
                ScreenReader.Say(status);
                return;
            }

            // J = Open event log — was L, now L is LP hotkey
            if (InputManager.TryConsumeKeyDown(KeyCode.J))
            {
                _eventLog.StartBrowsing();
                return;
            }

            // Space (no prompt claimed it above, engine input window CLOSED)
            // = animation/resolution playback is running. Sighted players
            // tap the screen to fast-forward these while the PvP clock
            // drains; keyboard users had no equivalent (2026-07-17 tester
            // report). Send one real OS-level click at screen center — the
            // skip checks are native touch polls, so only a hardware click
            // registers. With the input window closed a tap cannot take
            // game actions (TapCard no-ops there, 2026-07-16 spell log),
            // so a mistimed press is a no-op. Repeat presses = repeat taps.
            if (InputManager.TryConsumeKeyDown(KeyCode.Space)
                && DuelEventAnnouncer.InDuel
                && !DuelEventAnnouncer.DuelEnded
                && DuelState.InputType
                    == Il2CppYgomGame.Duel.Engine.MenuActType.Null)
            {
                var center = new Vector2(
                    Screen.width * 0.5f, Screen.height * 0.5f);
                bool sent = Main.ClickViaHardwareMouse(center, "anim skip");
                DebugLogger.Log(LogCategory.Game, "DuelHandler",
                    $"Space animation-skip click sent={sent}");
                return;
            }
        }

        private void UpdateAutomaticDraw()
        {
            bool duelActive = DuelEventAnnouncer.InDuel
                && !DuelEventAnnouncer.DuelEnded;
            bool localTurn = duelActive
                && DuelState.CurrentTurnPlayer == DuelState.MyPlayerNum();
            bool drawPhase = DuelState.InputType
                == Il2CppYgomGame.Duel.Engine.MenuActType.DrawPhase;

            try
            {
                _automaticDraw.Update(duelActive, localTurn, drawPhase,
                    Time.unscaledTime,
                    () => DispatchDrawCommand("fallback"));
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelDraw",
                    $"Automatic draw failed: {ex.Message}");
            }
        }

        private static bool IsAutomaticDrawEligible()
        {
            return DuelEventAnnouncer.InDuel
                && !DuelEventAnnouncer.DuelEnded
                && DuelState.CurrentTurnPlayer == DuelState.MyPlayerNum()
                && DuelState.InputType
                    == Il2CppYgomGame.Duel.Engine.MenuActType.DrawPhase;
        }

        private static void DispatchDrawCommand(string source)
        {
            int player = DuelState.MyPlayerNum();
            DebugLogger.Log(LogCategory.Game, "DuelDraw",
                $"Dispatching {source} draw command for player {player}");
            Il2CppYgomGame.Duel.Engine.DLL_DuelComDoCommand(
                player,
                15,
                0,
                (int)Il2CppYgomGame.Duel.Engine.CommandType.Draw);
        }

        private void CompleteAutomaticDrawPresentation()
        {
            Il2CppYgomGame.Duel.DrawOperationMultiDraw operation = null;
            bool ready = false;

            try
            {
                operation = Il2CppYgomGame.Duel.DuelClient.instance?
                    .worker3d?.drawOperation?
                    .TryCast<Il2CppYgomGame.Duel.DrawOperationMultiDraw>();
                ready = operation != null
                    && operation.step
                        == Il2CppYgomGame.Duel.DrawOperationMultiDraw.Step.Touchable
                    && (operation.phase
                            == Il2CppYgomGame.Duel.DrawOperationMultiDraw.TouchPhase.Neutral
                        || operation.phase
                            == Il2CppYgomGame.Duel.DrawOperationMultiDraw.TouchPhase.Touching)
                    && operation.deckPlace != null;
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelDraw",
                    $"Draw operation probe failed: {ex.Message}");
            }

            try
            {
                _automaticDraw.CompleteGesture(ready, Time.unscaledTime,
                    () => SwipeDrawCard(operation));
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelDraw",
                    $"Automatic draw gesture failed: {ex.Message}");
            }

            bool detailReady = operation != null
                && operation.step
                    == Il2CppYgomGame.Duel.DrawOperationMultiDraw.Step.Touchable
                && operation.phase
                    == Il2CppYgomGame.Duel.DrawOperationMultiDraw.TouchPhase.WaitDetail;
            try
            {
                _automaticDraw.CompleteDetail(detailReady, () =>
                {
                    operation.time = Il2CppYgomGame.Duel.DrawOperationMultiDraw
                        .waitDetailPhaseTimeLimit;
                    DebugLogger.Log(LogCategory.Game, "DuelDraw",
                        "Advanced automatic draw card detail");
                });
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelDraw",
                    $"Automatic draw detail advance failed: {ex.Message}");
            }
        }

        private static void SwipeDrawCard(
            Il2CppYgomGame.Duel.DrawOperationMultiDraw operation)
        {
            var start = new Vector2(
                Screen.width * 0.85f, Screen.height * 0.25f);

            var end = new Vector2(Screen.width / 2f, Screen.height / 2f);

            var eventData = new UnityEngine.EventSystems.PointerEventData(
                UnityEngine.EventSystems.EventSystem.current)
            {
                button = UnityEngine.EventSystems.PointerEventData.InputButton.Left,
                pointerId = -1,
                position = start,
                pressPosition = start
            };

            operation.OnTapDownDrawCard(eventData);
            eventData.delta = end - eventData.position;
            eventData.position = end;
            operation.OnTapUpDrawCard(eventData);

            DebugLogger.Log(LogCategory.Game, "DuelDraw",
                $"Swiped draw card from ({start.x:F0},{start.y:F0}) " +
                $"to ({end.x:F0},{end.y:F0})");
        }

        private void OnDuelAnnouncement(string message, bool queued)
        {
            _eventLog.Add(message);

            // Don't interrupt log browsing with live announcements
            if (_eventLog.IsBrowsing) return;

            if (queued)
                ScreenReader.SayQueued(message);
            else
                ScreenReader.Say(message);
        }

        /// <summary>
        /// Checks if TutorialArrow is the top VC on the dialog stack.
        /// </summary>
        private static bool IsTutorialArrowActive()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return false;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("dialog", out mgr)) return false;

                var topVc = mgr?.GetStackTopViewController();
                if (topVc?.gameObject == null) return false;

                return topVc.gameObject.name == "TutorialArrow";
            }
            catch { return false; }
        }

        /// <summary>
        /// Dismisses the TutorialArrow by calling OnPointerClick directly on
        /// the TutorialArrowViewController. Uses screen center as click position.
        /// </summary>
        private static void DismissTutorialOverlay()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("dialog", out mgr)) return;

                var topVc = mgr?.GetStackTopViewController();
                if (topVc?.gameObject == null) return;

                var arrowVc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                if (arrowVc == null)
                {
                    DebugLogger.Log(LogCategory.Game, "DuelHandler",
                        "TutorialArrow cast failed, falling back to ExecuteEvents");
                    var fallbackData = new UnityEngine.EventSystems.PointerEventData(
                        UnityEngine.EventSystems.EventSystem.current);
                    UnityEngine.EventSystems.ExecuteEvents.Execute(
                        topVc.gameObject, fallbackData,
                        UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                    return;
                }

                // Use physicTarget position for pointing arrows, screen center otherwise
                var eventData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);

                var physicTarget = arrowVc.physicTarget;
                if (physicTarget != null)
                {
                    var cam = arrowVc.targetCamera;
                    if (cam == null) cam = Camera.main;

                    if (cam != null)
                    {
                        Vector3 sp = cam.WorldToScreenPoint(
                            physicTarget.transform.position);
                        eventData.position = new Vector2(sp.x, sp.y);
                        DebugLogger.Log(LogCategory.Game, "DuelHandler",
                            $"Clicking arrow at physicTarget ({sp.x:F0}, {sp.y:F0}) via {cam.name}");
                    }
                    else
                    {
                        eventData.position = new Vector2(
                            Screen.width / 2f, Screen.height / 2f);
                        DebugLogger.Log(LogCategory.Game, "DuelHandler",
                            "No camera for physicTarget, using screen center");
                    }
                }
                else
                {
                    eventData.position = new Vector2(
                        Screen.width / 2f, Screen.height / 2f);
                    DebugLogger.Log(LogCategory.Game, "DuelHandler",
                        "Clicking arrow at screen center (no physicTarget)");
                }

                arrowVc.OnPointerClick(eventData);
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelHandler",
                    $"DismissTutorialOverlay error: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks for and handles the duel yes/no dialog (DuelCommonDialog).
        /// This dialog appears for tribute summon confirmation, effect activation prompts, etc.
        /// It's a MonoBehaviour on the duel HUD, not a standard VC-based dialog.
        /// Returns true if a yes/no dialog is active and input was consumed.
        /// </summary>
        private bool HandleDuelYesNoDialog()
        {
            // Use IsActive (InDuel OR screen==Duel) instead of just InDuel,
            // because resumed duels never fire DuelStart so InDuel stays false.
            if (!IsActive) return false;

            // After calling OnButton, the game doesn't clear dlgText immediately.
            // Skip checks during the cooldown so the user can proceed (e.g., select
            // tribute materials) without the stale text blocking input.
            if (_yesNoCooldown > 0f)
            {
                _yesNoCooldown -= Time.deltaTime;
                return false;
            }

            try
            {
                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                var worker = client?.worker2d;
                var dlg = worker?.yesnoDialog;

                if (dlg == null || dlg.content == null || !dlg.content.activeSelf)
                {
                    // Dialog closed — reset tracking
                    if (_yesNoDialogAnnounced)
                    {
                        _yesNoDialogAnnounced = false;
                        _lastYesNoText = "";
                    }
                    return false;
                }

                // The game keeps DuelCommonDialog content active during the entire duel
                // but only populates dlgText when a real yes/no prompt is showing.
                // Treat empty text as inactive to avoid intercepting other dialogs' keys.
                string text = "";
                try
                {
                    text = SpeechTextFormatter.StripRichText(
                        dlg.dlgText?.text ?? "");
                }
                catch { }

                if (string.IsNullOrEmpty(text))
                {
                    if (_yesNoDialogAnnounced)
                    {
                        _yesNoDialogAnnounced = false;
                        _lastYesNoText = "";
                    }
                    return false;
                }

                // If the text is the same as what we already responded to, the game
                // hasn't cleared it yet — don't re-activate. Only activate on NEW text.
                if (text == _lastYesNoText && !_yesNoDialogAnnounced)
                    return false;

                // Dialog is active with real text — read if not yet announced
                if (!_yesNoDialogAnnounced)
                {
                    _yesNoDialogAnnounced = true;
                    _lastYesNoText = text;
                    ScreenReader.Say(Loc.Get("duel_yesno_prompt", text));

                    DebugLogger.Log(LogCategory.Game, "DuelHandler",
                        $"DuelCommonDialog active: text='{text}'");
                }

                // Enter/Space = Yes
                if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                    || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter)
                    || InputManager.TryConsumeKeyDown(KeyCode.Space))
                {
                    DebugLogger.Log(LogCategory.Game, "DuelHandler",
                        "DuelCommonDialog: calling OnButton(0) (Yes)");
                    dlg.OnButton(0);
                    ScreenReader.Say(Loc.Get("duel_yes"));
                    _yesNoDialogAnnounced = false;
                    _yesNoCooldown = 0.5f;
                    return true;
                }

                // Escape = No
                if (InputManager.TryConsumeKeyDown(KeyCode.Escape))
                {
                    DebugLogger.Log(LogCategory.Game, "DuelHandler",
                        "DuelCommonDialog: calling OnButton(1) (No)");
                    dlg.OnButton(1);
                    ScreenReader.Say(Loc.Get("duel_no"));
                    _yesNoDialogAnnounced = false;
                    _yesNoCooldown = 0.5f;
                    return true;
                }

                // Consume other keys while dialog is active
                return true;
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelHandler",
                    $"DuelYesNo error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Handles the battle position dialog — choose ATK or DEF position during summon.
        /// The game shows BattlePositionDialog (via worker2d.bpDialog) when summoning a
        /// monster that can be placed in either position.
        /// Enter/1 = ATK position, 2 = DEF position.
        /// </summary>
        private bool HandleBattlePositionDialog()
        {
            if (!IsActive) return false;

            try
            {
                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                var worker = client?.worker2d;
                var bpDlg = worker?.bpDialog;

                if (bpDlg == null) { _bpDialogAnnounced = false; return false; }

                // Check if dialog's content is active AND has a valid card
                // (cardId > 0 distinguishes an active prompt from a dormant component)
                var content = bpDlg.content;
                if (content == null || !content.activeInHierarchy)
                {
                    _bpDialogAnnounced = false;
                    return false;
                }

                int cardId = 0;
                try { cardId = bpDlg.cardId; } catch { }
                if (cardId <= 0)
                {
                    _bpDialogAnnounced = false;
                    return false;
                }

                // Announce the dialog
                if (!_bpDialogAnnounced)
                {
                    _bpDialogAnnounced = true;
                    ScreenReader.Say(Loc.Get("duel_battle_position"));
                }

                // Enter or 1 = ATK position
                if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                    || InputManager.TryConsumeKeyDown(KeyCode.Alpha1))
                {
                    bpDlg.OnClickCard(1); // left=1 = left card = ATK
                    bpDlg.OnConfirm();
                    ScreenReader.Say(Loc.Get("duel_atk_position"));
                    _bpDialogAnnounced = false;
                    return true;
                }

                // 2 = DEF position
                if (InputManager.TryConsumeKeyDown(KeyCode.Alpha2))
                {
                    bpDlg.OnClickCard(0); // left=0 = right card = DEF
                    bpDlg.OnConfirm();
                    ScreenReader.Say(Loc.Get("duel_def_position"));
                    _bpDialogAnnounced = false;
                    return true;
                }

                // Escape = back/cancel
                if (InputManager.TryConsumeKeyDown(KeyCode.Escape))
                {
                    bpDlg.OnBack();
                    _bpDialogAnnounced = false;
                    return true;
                }

                // Consume keys while dialog is active
                return true;
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelHandler",
                    $"BattlePosition error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Handles the multi-effect picker (SelectEffectDialog) — shown when a card
        /// has more than one effect and the player must choose which to activate
        /// (e.g. a monster with two optional effects). It's a tabbed MonoBehaviour
        /// on worker2d.selEffDialog, not a VC/Htjson-stack dialog: each effect is a
        /// tab under tabParent, effectText shows the selected effect's description,
        /// and okButton confirms. Up/Down move between effects (firing each tab's
        /// own toggle so the game updates selectedIdx + effectText), Enter/Space
        /// confirm via OnClickOk, Escape cancels via OnClickCancel. Returns true
        /// when the dialog is active and input was consumed.
        /// </summary>
        private bool HandleSelectEffectDialog()
        {
            if (!IsActive) return false;

            // Grace period after confirm/cancel — the game doesn't tear the
            // dialog down instantly, so skip checks briefly to avoid re-entry.
            if (_selEffCooldown > 0f)
            {
                _selEffCooldown -= Time.deltaTime;
                return false;
            }

            try
            {
                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                var worker = client?.worker2d;
                var dlg = worker?.selEffDialog;

                var content = dlg?.content;
                if (dlg == null || content == null || !content.activeInHierarchy)
                {
                    if (_selEffAnnounced)
                    {
                        _selEffAnnounced = false;
                        _selEffIndex = 0;
                    }
                    return false;
                }

                var tabs = GetEffectTabs(dlg);
                int count = tabs.Count;

                // First detection — announce and dump the structure so the first
                // live encounter either works or reveals the exact tab layout.
                if (!_selEffAnnounced)
                {
                    _selEffAnnounced = true;
                    int sel = SafeSelectedIdx(dlg);
                    _selEffIndex = (count > 0 && sel >= 0 && sel < count) ? sel : 0;

                    DumpSelectEffectState(dlg, tabs);

                    string info = StripText(dlg.infoText);
                    string eff = StripText(dlg.effectText);
                    if (count > 1)
                        ScreenReader.Say(Loc.Get("duel_effect_intro",
                            info, _selEffIndex + 1, count, eff));
                    else
                        ScreenReader.Say(Loc.Get("duel_effect_single", info, eff));
                }

                // Up = previous effect
                if (count > 1
                    && InputManager.TryConsumeKeyDownOrRepeat(KeyCode.UpArrow))
                {
                    _selEffIndex = (_selEffIndex - 1 + count) % count;
                    SelectEffectTab(tabs, _selEffIndex);
                    AnnounceEffect(dlg, tabs);
                    return true;
                }

                // Down = next effect
                if (count > 1
                    && InputManager.TryConsumeKeyDownOrRepeat(KeyCode.DownArrow))
                {
                    _selEffIndex = (_selEffIndex + 1) % count;
                    SelectEffectTab(tabs, _selEffIndex);
                    AnnounceEffect(dlg, tabs);
                    return true;
                }

                // Enter/Space = confirm the selected effect
                if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                    || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter)
                    || InputManager.TryConsumeKeyDown(KeyCode.Space))
                {
                    DebugLogger.Log(LogCategory.Game, "DuelHandler",
                        $"SelectEffectDialog: OnClickOk() selectedIdx={SafeSelectedIdx(dlg)}");
                    dlg.OnClickOk();
                    ScreenReader.Say(Loc.Get("duel_effect_confirmed"));
                    _selEffAnnounced = false;
                    _selEffIndex = 0;
                    _selEffCooldown = 0.5f;
                    return true;
                }

                // Escape = cancel (game ignores it when the choice is mandatory)
                if (InputManager.TryConsumeKeyDown(KeyCode.Escape))
                {
                    DebugLogger.Log(LogCategory.Game, "DuelHandler",
                        "SelectEffectDialog: OnClickCancel()");
                    dlg.OnClickCancel();
                    ScreenReader.Say(Loc.Get("duel_effect_cancelled"));
                    _selEffAnnounced = false;
                    _selEffIndex = 0;
                    _selEffCooldown = 0.5f;
                    return true;
                }

                // V = re-read the current effect
                if (InputManager.TryConsumeKeyDown(KeyCode.V))
                {
                    AnnounceEffect(dlg, tabs);
                    return true;
                }

                // Consume other keys while the dialog is up.
                return true;
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelHandler",
                    $"SelectEffectDialog error: {ex.Message}");
                return false;
            }
        }

        /// <summary>Announces the currently focused effect (number + description).</summary>
        private void AnnounceEffect(Il2CppYgomGame.Duel.SelectEffectDialog dlg,
            System.Collections.Generic.List<GameObject> tabs)
        {
            string eff = StripText(dlg.effectText);
            // Fall back to the tab's own label when effectText is empty.
            if (string.IsNullOrWhiteSpace(eff)
                && _selEffIndex >= 0 && _selEffIndex < tabs.Count)
                eff = LabelExtractor.GetLabel(tabs[_selEffIndex]);
            ScreenReader.Say(Loc.Get("duel_effect_item",
                _selEffIndex + 1, tabs.Count, eff));
        }

        /// <summary>
        /// Collects the effect tabs — active direct children of tabParent that
        /// carry an interactive control. Order matches on-screen tab order.
        /// </summary>
        private static System.Collections.Generic.List<GameObject> GetEffectTabs(
            Il2CppYgomGame.Duel.SelectEffectDialog dlg)
        {
            var list = new System.Collections.Generic.List<GameObject>();
            try
            {
                var parent = dlg.tabParent;
                if (parent == null) return list;
                int n = parent.childCount;
                for (int i = 0; i < n; i++)
                {
                    var child = parent.GetChild(i);
                    if (child == null) continue;
                    var go = child.gameObject;
                    if (go == null || !go.activeSelf) continue;
                    if (HasInteractive(go)) list.Add(go);
                }
            }
            catch { }
            return list;
        }

        /// <summary>True if the GO (or a descendant) has a Toggle or Button.</summary>
        private static bool HasInteractive(GameObject go)
        {
            try { if (go.GetComponentInChildren<UnityEngine.UI.Toggle>(true) != null) return true; } catch { }
            try { if (go.GetComponentInChildren<UnityEngine.UI.Button>(true) != null) return true; } catch { }
            return false;
        }

        /// <summary>
        /// Selects effect tab <paramref name="index"/> by firing its own control,
        /// so the game runs its native selection path (updates selectedIdx +
        /// effectText). Toggles are preferred (tabs are usually radio toggles);
        /// a Button click is the fallback.
        /// </summary>
        private static void SelectEffectTab(
            System.Collections.Generic.List<GameObject> tabs, int index)
        {
            if (index < 0 || index >= tabs.Count) return;
            var go = tabs[index];
            if (go == null) return;
            try
            {
                var toggle = go.GetComponentInChildren<UnityEngine.UI.Toggle>(true);
                if (toggle != null) { toggle.isOn = true; return; }

                var btn = go.GetComponentInChildren<UnityEngine.UI.Button>(true);
                if (btn != null) { btn.onClick.Invoke(); return; }

                DebugLogger.Log(LogCategory.Game, "DuelHandler",
                    $"SelectEffectTab: no interactive on tab {index} ('{go.name}')");
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelHandler",
                    $"SelectEffectTab error: {ex.Message}");
            }
        }

        /// <summary>Reads dlg.selectedIdx defensively (-1 on failure).</summary>
        private static int SafeSelectedIdx(Il2CppYgomGame.Duel.SelectEffectDialog dlg)
        {
            try { return dlg.selectedIdx; } catch { return -1; }
        }

        /// <summary>Strips rich text from a Unity Text, empty string on failure.</summary>
        private static string StripText(UnityEngine.UI.Text t)
        {
            try { return SpeechTextFormatter.StripRichText(t?.text ?? ""); }
            catch { return ""; }
        }

        /// <summary>
        /// Diagnostic dump of the SelectEffectDialog structure on first detection.
        /// Debug-log only (no TTS). Reveals the tab layout so the handler can be
        /// corrected if the tab→effect mapping turns out different in-game.
        /// </summary>
        private static void DumpSelectEffectState(
            Il2CppYgomGame.Duel.SelectEffectDialog dlg,
            System.Collections.Generic.List<GameObject> tabs)
        {
            try
            {
                string info = StripText(dlg.infoText);
                string eff = StripText(dlg.effectText);
                int sel = SafeSelectedIdx(dlg);
                int cancel = -1; try { cancel = dlg.cancelIdx; } catch { }
                bool ok1 = false, ok2 = false;
                try { ok1 = dlg.okButton != null && dlg.okButton.interactable; } catch { }
                try { ok2 = dlg.okButton2 != null && dlg.okButton2.interactable; } catch { }
                int tabParentChildren = -1;
                try { tabParentChildren = dlg.tabParent != null ? dlg.tabParent.childCount : -1; } catch { }

                DebugLogger.Log(LogCategory.Game, "DuelHandler",
                    $"[SelEff] info='{info}' effect='{eff}' selectedIdx={sel} "
                    + $"cancelIdx={cancel} tabParentChildren={tabParentChildren} "
                    + $"tabs={tabs.Count} ok1i={ok1} ok2i={ok2}");

                for (int i = 0; i < tabs.Count; i++)
                {
                    var go = tabs[i];
                    string label = "?";
                    try { label = LabelExtractor.GetLabel(go); } catch { }
                    bool hasToggle = false, hasButton = false;
                    try { hasToggle = go.GetComponentInChildren<UnityEngine.UI.Toggle>(true) != null; } catch { }
                    try { hasButton = go.GetComponentInChildren<UnityEngine.UI.Button>(true) != null; } catch { }
                    DebugLogger.Log(LogCategory.Game, "DuelHandler",
                        $"[SelEff]   tab[{i}] name='{go.name}' label='{label}' "
                        + $"toggle={hasToggle} button={hasButton}");
                }
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelHandler",
                    $"[SelEff] dump error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handles the post-duel result screen (YOU WIN/LOSE with OK button).
        /// Finds the DuelEndMessage MonoBehaviour and calls OnNextButton() directly —
        /// this is the exact method the game's OK button invokes. It sets
        /// isNextButtonClicked=true, which TaskHUDDuelEnd.WaitWinLoseStep watches for.
        /// </summary>
        private void HandleDuelResult()
        {
            if (!_duelResultScanned)
            {
                _duelResultScanned = true;
                _duelEndMessage = null;

                try
                {
                    _duelEndMessage = UnityEngine.Object.FindObjectOfType<Il2Cpp.DuelEndMessage>();
                    if (_duelEndMessage != null)
                    {
                        var result = _duelEndMessage.resultType;
                        string resultText = result switch
                        {
                            Il2CppYgomGame.Duel.Engine.ResultType.Win => Loc.Get("duel_result_win"),
                            Il2CppYgomGame.Duel.Engine.ResultType.Lose => Loc.Get("duel_result_lose"),
                            Il2CppYgomGame.Duel.Engine.ResultType.Draw => Loc.Get("duel_result_draw"),
                            _ => result.ToString()
                        };
                        ScreenReader.Say(Loc.Get("duel_result_screen", resultText));
                        DebugLogger.Log(LogCategory.Game, "DuelResult",
                            $"DuelEndMessage found, result={result}, nextClicked={_duelEndMessage.isNextButtonClicked}");
                    }
                    else
                    {
                        // DuelEndMessage not yet created — re-scan next frame
                        _duelResultScanned = false;
                    }
                }
                catch (System.Exception ex)
                {
                    DebugLogger.Log(LogCategory.Game, "DuelResult", $"Scan error: {ex.Message}");
                    _duelResultScanned = false;
                }
            }

            if (_duelEndMessage == null) return;

            // Enter/Space = click OK (call OnNextButton directly)
            if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter)
                || InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                try
                {
                    DebugLogger.Log(LogCategory.Game, "DuelResult",
                        "Calling DuelEndMessage.OnNextButton()");
                    _duelEndMessage.OnNextButton();
                    ScreenReader.Say("OK");
                    _duelEndMessage = null;
                }
                catch (System.Exception ex)
                {
                    DebugLogger.Log(LogCategory.Game, "DuelResult",
                        $"OnNextButton error: {ex.Message}");
                }
            }
        }

        #endregion
    }
}
