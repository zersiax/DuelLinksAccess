using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using Il2CppYgomGame.Deck;

namespace DuelLinksAccess
{
    /// <summary>
    /// Keyboard-driven deck editor accessibility handler.
    /// Provides navigation through deck contents and card collection,
    /// card detail reading, and programmatic add/remove (bypassing drag gestures).
    /// </summary>
    public class DeckEditHandler
    {
        #region Types

        private enum Zone { MainDeck, ExtraDeck, Collection }

        #endregion

        #region Fields

        private DeckEdit2ViewController _vc;
        private Zone _currentZone = Zone.MainDeck;
        private int _focusIndex;
        private bool _wasActive;
        private string _lastVcGoName = "";

        // Managed copies of card lists (refreshed on zone switch / add / remove)
        private readonly List<int> _mainDeckMrks = new();
        private readonly List<int> _extraDeckMrks = new();
        private readonly List<int> _collectionMrks = new();

        // Cooldown to prevent rapid-fire operations
        private float _operationCooldown;
        private const float OperationCooldownTime = 0.3f;

        // Delayed initial scan (deckInfo may not be populated immediately)
        private float _initialScanDelay;
        private bool _initialScanDone;
        private int _initialScanAttempts;

        #endregion

        #region Properties

        /// <summary>
        /// Whether the handler is actively managing the deck editor screen.
        /// </summary>
        public bool IsActive { get; private set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Called each frame from Main.UpdateHandlers().
        /// </summary>
        public void Update()
        {
            // Tick cooldown
            if (_operationCooldown > 0f)
                _operationCooldown -= Time.deltaTime;

            // Only activate on Deck screens
            if (GameStateTracker.CurrentScreen != GameStateTracker.GameScreen.Deck)
            {
                if (_wasActive) Deactivate();
                return;
            }

            // Try to find the DeckEdit2ViewController
            var vc = TryGetDeckEditVC();
            if (vc == null)
            {
                if (_wasActive) Deactivate();
                return;
            }

            // Detect VC change (re-entered editor or different deck)
            string goName = vc.gameObject?.name ?? "";
            if (!_wasActive || goName != _lastVcGoName)
            {
                Activate(vc, goName);
            }

            // Wait for delayed initial scan
            if (!_initialScanDone)
            {
                _initialScanDelay -= Time.deltaTime;
                if (_initialScanDelay <= 0f)
                    DoInitialScan();
                return;
            }

            ProcessInput();
        }

        #endregion

        #region Lifecycle

        private void Activate(DeckEdit2ViewController vc, string goName)
        {
            _vc = vc;
            _lastVcGoName = goName;
            _wasActive = true;
            IsActive = true;
            _currentZone = Zone.MainDeck;
            _focusIndex = 0;
            _initialScanDone = false;
            _initialScanDelay = 0.5f;
            _initialScanAttempts = 0;

            DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                $"Activated, waiting for initial scan...");
        }

        private void DoInitialScan()
        {
            _initialScanAttempts++;
            RefreshAllLists();

            int mainCount = _mainDeckMrks.Count;
            int extraCount = _extraDeckMrks.Count;
            int collectionCount = _collectionMrks.Count;

            DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                $"Scan attempt {_initialScanAttempts}: main={mainCount}, extra={extraCount}, collection={collectionCount}");

            // If all lists are empty and we haven't retried too many times, try again
            if (mainCount == 0 && extraCount == 0 && collectionCount == 0
                && _initialScanAttempts < 6)
            {
                _initialScanDelay = 0.5f; // Retry in 0.5s
                return;
            }

            _initialScanDone = true;
            ScreenReader.Say(Loc.Get("deck_edit_entered", mainCount, extraCount, collectionCount));
        }

        private void Deactivate()
        {
            _wasActive = false;
            IsActive = false;
            _vc = null;
            _mainDeckMrks.Clear();
            _extraDeckMrks.Clear();
            _collectionMrks.Clear();

            DebugLogger.Log(LogCategory.Handler, "DeckEdit", "Deactivated");
        }

        #endregion

        #region VC Detection

        /// <summary>
        /// Attempts to find DeckEdit2ViewController as the top content VC.
        /// Returns null if the current screen is DeckSelect or another Deck-type VC.
        /// </summary>
        private DeckEdit2ViewController TryGetDeckEditVC()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return null;

                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedMgr.TryGetValue("content", out contentMgr) || contentMgr == null)
                    return null;

                var topVc = contentMgr.GetStackTopViewController();
                if (topVc == null) return null;

                return topVc.TryCast<DeckEdit2ViewController>();
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"TryGetDeckEditVC error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Data Access

        /// <summary>
        /// Refreshes all three card lists from the VC's live data.
        /// </summary>
        private void RefreshAllLists()
        {
            RefreshDeckLists();
            RefreshCollectionList();
        }

        private void RefreshDeckLists()
        {
            _mainDeckMrks.Clear();
            _extraDeckMrks.Clear();

            try
            {
                var deckInfo = _vc?.deckInfo;
                if (deckInfo == null) return;

                var mainDeck = deckInfo.mainDeck;
                if (mainDeck != null)
                {
                    for (int i = 0; i < mainDeck.Count; i++)
                    {
                        var card = mainDeck[i];
                        _mainDeckMrks.Add(card.Mrk);
                    }
                }

                var extraDeck = deckInfo.extraDeck;
                if (extraDeck != null)
                {
                    for (int i = 0; i < extraDeck.Count; i++)
                    {
                        var card = extraDeck[i];
                        _extraDeckMrks.Add(card.Mrk);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"RefreshDeckLists error: {ex.Message}");
            }
        }

        private void RefreshCollectionList()
        {
            _collectionMrks.Clear();

            try
            {
                if (_vc == null) return;

                // Use trunkFiltered first (respects game's current filter/sort)
                var filtered = _vc.trunkFiltered;
                if (filtered != null && filtered.Count > 0)
                {
                    for (int i = 0; i < filtered.Count; i++)
                        _collectionMrks.Add(filtered[i]);
                    return;
                }

                // Fall back to trunkSorted
                var sorted = _vc.trunkSorted;
                if (sorted != null && sorted.Count > 0)
                {
                    for (int i = 0; i < sorted.Count; i++)
                        _collectionMrks.Add(sorted[i]);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"RefreshCollectionList error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the card list for the current zone.
        /// </summary>
        private List<int> GetCurrentList()
        {
            return _currentZone switch
            {
                Zone.MainDeck => _mainDeckMrks,
                Zone.ExtraDeck => _extraDeckMrks,
                Zone.Collection => _collectionMrks,
                _ => _mainDeckMrks
            };
        }

        #endregion

        #region Input Processing

        private void ProcessInput()
        {
            // Tab / Shift+Tab — switch zone
            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    SwitchZonePrev();
                else
                    SwitchZoneNext();
                return;
            }

            // Left / Right — navigate cards
            if (InputManager.TryConsumeKeyDown(KeyCode.LeftArrow))
            {
                NavigateBy(-1);
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.RightArrow))
            {
                NavigateBy(1);
                return;
            }

            // Up / Down — page jump (10 cards)
            if (InputManager.TryConsumeKeyDown(KeyCode.UpArrow))
            {
                NavigateBy(-10);
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.DownArrow))
            {
                NavigateBy(10);
                return;
            }

            // Home / End — jump to start / end
            if (InputManager.TryConsumeKeyDown(KeyCode.Home))
            {
                var list = GetCurrentList();
                if (list.Count > 0)
                {
                    _focusIndex = 0;
                    AnnounceCurrentCard();
                }
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.End))
            {
                var list = GetCurrentList();
                if (list.Count > 0)
                {
                    _focusIndex = list.Count - 1;
                    AnnounceCurrentCard();
                }
                return;
            }

            // Enter — add (collection) or remove (deck)
            if (InputManager.TryConsumeKeyDown(KeyCode.Return))
            {
                if (_currentZone == Zone.Collection)
                    AddCurrentCard();
                else
                    RemoveCurrentCard();
                return;
            }

            // Delete — remove card from deck
            if (InputManager.TryConsumeKeyDown(KeyCode.Delete))
            {
                if (_currentZone != Zone.Collection)
                    RemoveCurrentCard();
                return;
            }

            // C — verbose card reading
            if (InputManager.TryConsumeKeyDown(KeyCode.C))
            {
                AnnounceCurrentCard(verbose: true);
                return;
            }

            // I — deck stats
            if (InputManager.TryConsumeKeyDown(KeyCode.I))
            {
                AnnounceDeckStats();
                return;
            }

            // Ctrl+S — save deck. MUST be checked before bare S, otherwise
            // TryConsumeKeyDown(KeyCode.S) for the skill announcement consumes
            // the key first and the Ctrl+S handler never sees it.
            bool ctrlHeld = Input.GetKey(KeyCode.LeftControl)
                || Input.GetKey(KeyCode.RightControl);
            if (ctrlHeld && InputManager.TryConsumeKeyDown(KeyCode.S))
            {
                SaveDeck();
                return;
            }

            // S — announce current skill (only when Ctrl is NOT held)
            if (!ctrlHeld && InputManager.TryConsumeKeyDown(KeyCode.S))
            {
                AnnounceCurrentSkill();
                return;
            }

            // K — open skill selection
            if (InputManager.TryConsumeKeyDown(KeyCode.K))
            {
                OpenSkillSelection();
                return;
            }

            // U — set the currently-open deck as the active/main deck.
            // Routes through DeckEdit2ViewController.confirmSet() (the same
            // entry point the in-editor "Use this deck" button calls) so the
            // game's normal confirmation dialog fires; DialogHandler picks
            // that up and the user confirms with Enter as usual.
            if (InputManager.TryConsumeKeyDown(KeyCode.U))
            {
                SetUseDeck();
                return;
            }

            // Escape — go back
            if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.Backspace))
            {
                GoBack();
                return;
            }
        }

        #endregion

        #region Navigation

        private void SwitchZoneNext()
        {
            _currentZone = _currentZone switch
            {
                Zone.MainDeck => Zone.ExtraDeck,
                Zone.ExtraDeck => Zone.Collection,
                Zone.Collection => Zone.MainDeck,
                _ => Zone.MainDeck
            };
            OnZoneChanged();
        }

        private void SwitchZonePrev()
        {
            _currentZone = _currentZone switch
            {
                Zone.MainDeck => Zone.Collection,
                Zone.ExtraDeck => Zone.MainDeck,
                Zone.Collection => Zone.ExtraDeck,
                _ => Zone.MainDeck
            };
            OnZoneChanged();
        }

        private void OnZoneChanged()
        {
            // Refresh the relevant list to catch any changes
            if (_currentZone == Zone.Collection)
                RefreshCollectionList();
            else
                RefreshDeckLists();

            _focusIndex = 0;
            var list = GetCurrentList();

            string zoneName = GetZoneName();
            ScreenReader.Say(Loc.Get("deck_zone", zoneName, list.Count));

            if (list.Count > 0)
                AnnounceCurrentCard(queued: true);

            DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                $"Zone: {_currentZone}, {list.Count} cards");
        }

        private void NavigateBy(int delta)
        {
            var list = GetCurrentList();
            if (list.Count == 0)
            {
                ScreenReader.Say(Loc.Get("deck_zone_empty"));
                return;
            }

            _focusIndex += delta;

            // Clamp to valid range
            if (_focusIndex < 0) _focusIndex = 0;
            if (_focusIndex >= list.Count) _focusIndex = list.Count - 1;

            AnnounceCurrentCard();
        }

        #endregion

        #region Card Operations

        private void AddCurrentCard()
        {
            if (_operationCooldown > 0f) return;

            var list = GetCurrentList();
            if (list.Count == 0 || _focusIndex < 0 || _focusIndex >= list.Count) return;

            int mrk = list[_focusIndex];
            string name = GetCardName(mrk);

            try
            {
                // Check if card can be added — gives specific rejection reason
                if (!_vc.IsCardAddible(mrk, -1))
                {
                    string reason = GetAddFailReason(mrk);
                    ScreenReader.Say(Loc.Get("deck_card_not_addible_reason", name, reason));
                    return;
                }

                // Use synchronous addToDeck (not the coroutine version)
                bool result = _vc.addToDeck(mrk, -1L);

                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"addToDeck({mrk}) = {result}");

                if (result)
                {
                    RefreshAllLists();
                    int count = _mainDeckMrks.Count;
                    ScreenReader.Say(Loc.Get("deck_card_added_count", name, count));
                }
                else
                {
                    ScreenReader.Say(Loc.Get("deck_card_not_addible", name));
                }

                _operationCooldown = OperationCooldownTime;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"AddCurrentCard error: {ex.Message}");
                ScreenReader.Say(Loc.Get("deck_operation_error"));
            }
        }

        private void RemoveCurrentCard()
        {
            if (_operationCooldown > 0f) return;

            var list = GetCurrentList();
            if (list.Count == 0 || _focusIndex < 0 || _focusIndex >= list.Count) return;

            int mrk = list[_focusIndex];
            string name = GetCardName(mrk);

            try
            {
                // Use synchronous delFromDeck (not the coroutine version)
                bool result = _vc.delFromDeck(mrk, -1L);

                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"delFromDeck({mrk}) = {result}");

                if (result)
                {
                    RefreshDeckLists();
                    ClampFocusIndex();
                    int count = _mainDeckMrks.Count;
                    ScreenReader.Say(Loc.Get("deck_card_removed_count", name, count));
                }
                else
                {
                    ScreenReader.Say(Loc.Get("deck_operation_error"));
                }

                _operationCooldown = OperationCooldownTime;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"RemoveCurrentCard error: {ex.Message}");
                ScreenReader.Say(Loc.Get("deck_operation_error"));
            }
        }

        /// <summary>
        /// Tries to determine why a card can't be added to the deck.
        /// </summary>
        private string GetAddFailReason(int mrk)
        {
            try
            {
                var deckInfo = _vc?.deckInfo;
                if (deckInfo == null) return Loc.Get("deck_reason_unknown");

                // Check deck full
                int mainCount = _mainDeckMrks.Count;
                int mainMax = DeckInfo.MainDeckMaxNum(false);
                if (mainCount >= mainMax)
                    return Loc.Get("deck_reason_full", mainMax);

                // Check same card limit (max 3 copies)
                int inDeck = _vc.GetNumForDeck(mrk, 0);
                if (inDeck >= 3)
                    return Loc.Get("deck_reason_limit", inDeck);

                // Check if player doesn't have more copies
                if (!_vc.IsRemainInTrunk(mrk))
                    return Loc.Get("deck_reason_no_copies");
            }
            catch { }

            return Loc.Get("deck_reason_unknown");
        }

        private void ClampFocusIndex()
        {
            var list = GetCurrentList();
            if (_focusIndex >= list.Count && list.Count > 0)
                _focusIndex = list.Count - 1;
        }

        private void AnnounceCurrentSkill()
        {
            try
            {
                var header = _vc?.deckHeader;
                if (header == null)
                {
                    ScreenReader.Say(Loc.Get("deck_no_skill"));
                    return;
                }

                var skillText = header.skillName;
                string name = skillText?.text;

                if (string.IsNullOrEmpty(name))
                    ScreenReader.Say(Loc.Get("deck_no_skill"));
                else
                    ScreenReader.Say(Loc.Get("deck_skill", name));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"AnnounceCurrentSkill error: {ex.Message}");
                ScreenReader.Say(Loc.Get("deck_no_skill"));
            }
        }

        private void OpenSkillSelection()
        {
            try
            {
                var header = _vc?.deckHeader;
                var skillBtn = header?.skillButton;
                if (skillBtn != null)
                {
                    skillBtn.onClick.Invoke();
                    DebugLogger.Log(LogCategory.Handler, "DeckEdit", "Clicked skill button");
                }
                else
                {
                    DebugLogger.Log(LogCategory.Handler, "DeckEdit", "Skill button not found");
                    ScreenReader.Say(Loc.Get("deck_operation_error"));
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"OpenSkillSelection error: {ex.Message}");
                ScreenReader.Say(Loc.Get("deck_operation_error"));
            }
        }

        private void SaveDeck()
        {
            try
            {
                _vc.saveCommon(DeckEdit2ViewController.SAVEFOR.Save);
                ScreenReader.Say(Loc.Get("deck_saved"));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"Save error: {ex.Message}");
                ScreenReader.Say(Loc.Get("deck_operation_error"));
            }
        }

        /// <summary>
        /// Marks the currently-open deck as the player's active/main deck.
        /// Calls DeckEdit2ViewController.confirmSet() — the public entry point
        /// the in-editor "Use this deck" button calls. Server-side, this ends
        /// up at API.User_set_use_deck(charaId, deckId). The game raises a
        /// confirmation dialog before committing, so the user lands on a
        /// standard yes/no dialog that DialogHandler already drives.
        /// </summary>
        private void SetUseDeck()
        {
            if (_vc == null)
            {
                ScreenReader.Say(Loc.Get("deck_operation_error"));
                return;
            }

            try
            {
                _vc.confirmSet();
                ScreenReader.Say(Loc.Get("deck_use_deck_pressed"));
            }
            catch (NullReferenceException)
            {
                // Empirical: confirmSet throws NullReferenceException inside the
                // game's own code when the currently-open deck is ALREADY the
                // active one — its "previous active deck" reference is null
                // because there's nothing to swap. Treat as a soft "no-op" and
                // announce that, rather than the generic "Operation failed".
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    "SetUseDeck: confirmSet NRE — deck is already active");
                ScreenReader.Say(Loc.Get("deck_already_active"));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"SetUseDeck error: {ex.Message}");
                ScreenReader.Say(Loc.Get("deck_operation_error"));
            }
        }

        private void GoBack()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null)
                {
                    DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                        "GoBack: namedManager == null");
                    return;
                }

                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedMgr.TryGetValue("content", out contentMgr) || contentMgr == null)
                {
                    DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                        "GoBack: no 'content' manager");
                    return;
                }

                var topVc = contentMgr.GetStackTopViewController();
                string topName = topVc?.gameObject?.name ?? "(null)";

                // _vc.Mode is informative — different MODEs (Structure, DeckLocked,
                // ViewContribute, etc.) use the same DeckEdit2ViewController GO
                // but behave differently, including refusing SendBack in some cases.
                string modeStr = "(no _vc)";
                try
                {
                    if (_vc != null)
                        modeStr = _vc.Mode.ToString();
                }
                catch { modeStr = "(Mode threw)"; }

                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"GoBack: topVc={topName}, _vc.Mode={modeStr}, calling SendBack()");

                if (topVc == null)
                {
                    DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                        "GoBack: topVc == null, nothing to send back");
                    return;
                }

                topVc.SendBack();

                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    "GoBack: SendBack() returned");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"GoBack error: {ex.Message}");
            }
        }

        #endregion

        #region Announcements

        private void AnnounceCurrentCard(bool verbose = false, bool queued = false)
        {
            var list = GetCurrentList();
            if (list.Count == 0)
            {
                ScreenReader.Say(Loc.Get("deck_zone_empty"));
                return;
            }

            if (_focusIndex < 0 || _focusIndex >= list.Count)
                _focusIndex = 0;

            int mrk = list[_focusIndex];
            int pos = _focusIndex + 1;
            int total = list.Count;

            string cardText = verbose ? FormatCardVerbose(mrk) : FormatCardCompact(mrk);
            string announcement = Loc.Get("deck_card_position", pos, total, cardText);

            if (queued)
                ScreenReader.SayQueued(announcement);
            else
                ScreenReader.Say(announcement);
        }

        private void AnnounceDeckStats()
        {
            RefreshDeckLists();

            int mainMax = 30;
            int extraMax = 5;
            try
            {
                mainMax = DeckInfo.MainDeckMaxNum(false);
                extraMax = DeckInfo.ExtraDeckMaxNum(false);
            }
            catch { }

            ScreenReader.Say(Loc.Get("deck_stats",
                _mainDeckMrks.Count, mainMax,
                _extraDeckMrks.Count, extraMax));
        }

        #endregion

        #region Card Formatting

        /// <summary>
        /// Compact card announcement via shared CardFormatter.
        /// </summary>
        private string FormatCardCompact(int mrk) => CardFormatter.FormatCompact(mrk);

        /// <summary>
        /// Verbose card announcement: compact info + description + deck/owned counts.
        /// </summary>
        private string FormatCardVerbose(int mrk)
        {
            string verbose = CardFormatter.FormatVerbose(mrk);

            // In collection zone, append deck ownership info
            if (_currentZone == Zone.Collection && _vc != null)
            {
                try
                {
                    int inDeck = _vc.GetNumForDeck(mrk, 0);
                    verbose += ". " + Loc.Get("deck_card_in_deck", inDeck);
                }
                catch { }
            }

            return verbose;
        }

        private string GetCardName(int mrk) => CardFormatter.GetName(mrk);

        private string GetZoneName()
        {
            return _currentZone switch
            {
                Zone.MainDeck => Loc.Get("deck_zone_main_name"),
                Zone.ExtraDeck => Loc.Get("deck_zone_extra_name"),
                Zone.Collection => Loc.Get("deck_zone_collection_name"),
                _ => "Unknown"
            };
        }

        #endregion
    }
}
