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
                _eventLog.Clear();
                _fieldNav.Reset();
            }

            // Log browsing mode takes priority for navigation keys
            if (_eventLog.IsBrowsing)
            {
                ProcessLogBrowsing();
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
                    else
                    {
                        ScreenReader.Say(Loc.Get("duel_tutorial_arrow"));
                    }
                }

                if (InputManager.TryConsumeKeyDown(KeyCode.Space))
                {
                    if (!_tutorialArrowDismissAttempted)
                    {
                        // First attempt — try to dismiss
                        _tutorialArrowDismissAttempted = true;
                        _tutorialArrowAnnounced = false;
                        DismissTutorialOverlay();
                        return;
                    }
                    // Already tried and failed — ignore further Space presses
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

            // Field navigation — most keys suppressed when a dialog overlay is active
            // so DialogHandler can handle duel dialogs (Yes/No, card selection).
            // Tab for zone cycling always works — it doesn't conflict with dialog keys.
            bool dialogActive = GameStateTracker.CurrentScreen
                == GameStateTracker.GameScreen.Dialog;

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

                var eventData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);
                eventData.position = new UnityEngine.Vector2(
                    UnityEngine.Screen.width / 2f, UnityEngine.Screen.height / 2f);

                DebugLogger.Log(LogCategory.Game, "DuelHandler",
                    "Calling TutorialArrowVC.OnPointerClick at screen center");
                arrowVc.OnPointerClick(eventData);
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelHandler",
                    $"DismissTutorialOverlay error: {ex.Message}");
            }
        }

        #endregion
    }
}
