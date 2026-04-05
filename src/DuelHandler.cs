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
    ///   C — Re-read current card (verbose)
    ///   F — Field summary
    ///   P — Advance phase
    ///   S — Status (LP, phase, turn)
    ///   L — Toggle event log browsing
    ///   During log browsing:
    ///     Up/Down — Navigate entries (older/newer)
    ///     Tab — Re-read current entry
    ///     Escape or L — Close log
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

        #endregion

        #region Properties

        /// <summary>
        /// Whether the duel handler is currently active.
        /// True when a duel is in progress OR the screen is classified as Duel.
        /// </summary>
        public bool IsActive => DuelEventAnnouncer.InDuel
            || GameStateTracker.CurrentScreen == GameStateTracker.GameScreen.Duel;

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
                _eventLog.Clear();
                _fieldNav.Reset();
            }

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

            // Field navigation — most keys suppressed when a dialog overlay is active
            // so DialogHandler can handle duel dialogs (Yes/No, card selection).
            // Tab for zone cycling always works — it doesn't conflict with dialog keys.
            bool dialogActive = GameStateTracker.CurrentScreen
                == GameStateTracker.GameScreen.Dialog;

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
                || InputManager.TryConsumeKeyDown(KeyCode.L))
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
            // S = Status report (LP, phase, turn)
            if (InputManager.TryConsumeKeyDown(KeyCode.S))
            {
                string status = DuelEventAnnouncer.GetStatusText();
                ScreenReader.Say(status);
                return;
            }

            // L = Open event log
            if (InputManager.TryConsumeKeyDown(KeyCode.L))
            {
                _eventLog.StartBrowsing();
                return;
            }
        }

        private void OnDuelAnnouncement(string message)
        {
            _eventLog.Add(message);

            // Don't interrupt log browsing with live announcements
            if (_eventLog.IsBrowsing) return;

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
                try { text = dlg.dlgText?.text ?? ""; } catch { }

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
