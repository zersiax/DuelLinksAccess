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
            if (GameStateTracker.CurrentScreen != GameScreen.Deck)
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
            LogTrunkDiagnostics();
            ScreenReader.Say(Loc.Get("deck_edit_entered", mainCount, extraCount, collectionCount));
            AnnounceDeckOwner();
        }

        // Temporary diagnostic (2026-08-13): a card traded from the Card Trader
        // (Beast Gear Buggy Dog, mrk 15420) was confirmed received by the game's
        // own "Receive Rewards" modal, yet did not appear in the deck editor's
        // 181-card collection view. Our collection uses trunkFiltered (the game's
        // filtered view) when present, so this logs the full trunk (trunkSorted)
        // vs the filtered view counts and whether the traded card is in either.
        // That tells "our filtered view hides owned cards" (inSorted=true,
        // inFiltered=false) from "the card isn't in the client trunk at all —
        // stale or not added" (inSorted=false). Remove once resolved.
        private const int TrunkProbeMrk = 15420;
        // Known real card that is NOT owned (Disturbance Strategy). If it's in
        // trunkSorted, trunkSorted is the all-cards master list; if not, it's an
        // owned-only list. Disambiguates what "sorted=10537" means.
        private const int KnownRealCardMrk = 5546;

        private void LogTrunkDiagnostics()
        {
            try
            {
                if (_vc == null) return;
                var filtered = _vc.trunkFiltered;
                var sorted = _vc.trunkSorted;
                int fc = filtered?.Count ?? -1;
                int sc = sorted?.Count ?? -1;

                bool inFiltered = false, inSorted = false, knownInSorted = false;
                if (filtered != null)
                    for (int i = 0; i < filtered.Count; i++)
                        if (filtered[i] == TrunkProbeMrk) { inFiltered = true; break; }
                if (sorted != null)
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        if (sorted[i] == TrunkProbeMrk) inSorted = true;
                        if (sorted[i] == KnownRealCardMrk) knownInSorted = true;
                    }

                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"[trunk] filtered={fc} sorted={sc} "
                    + $"usingFiltered={DeckEditPolicy.UseFilteredCollection(filtered != null)} | "
                    + $"probe mrk={TrunkProbeMrk} inFiltered={inFiltered} inSorted={inSorted}");

                // Identify what the probe id actually is and whether the game
                // thinks the card is owned. Content.GetName tells us if 15420 is
                // even a card mrk (does it resolve to "Beast Gear Buggy Dog"?);
                // CardPoss is the authoritative owned-count for that mrk. The
                // known-real-but-unowned control (5546 Disturbance Strategy) in
                // trunkSorted proves whether trunkSorted is the all-cards master
                // (control present) or an owned-only list (control absent).
                string probeName = "?";
                try { probeName = Il2CppYgomGame.Card.Content.Instance?.GetName(TrunkProbeMrk) ?? "(null)"; }
                catch (Exception e) { probeName = "err:" + e.Message; }

                int probePoss = -1;
                try { probePoss = Il2CppYgomGame.Single.CardTraderInfoBase.CardPoss(TrunkProbeMrk, 0); }
                catch { }

                // trunkData.GetNum(mrk) is the raw inventory count for this card,
                // ignoring rarity (deck-legality is only a display filter on top),
                // so it's authoritative for "do you own it" even for a card the
                // deck pool excludes. Closes the CardPoss(mrk,0) single-kirarity
                // caveat.
                int trunkNum = -1;
                try { trunkNum = _vc.trunkData != null ? _vc.trunkData.GetNum(TrunkProbeMrk) : -1; }
                catch { }
                bool remain = false;
                try { remain = _vc.IsRemainInTrunk(TrunkProbeMrk); } catch { }

                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"[trunk] probe mrk={TrunkProbeMrk} name='{probeName}' CardPoss={probePoss} "
                    + $"trunkData.GetNum={trunkNum} IsRemainInTrunk={remain} | "
                    + $"control mrk={KnownRealCardMrk} inSorted={knownInSorted}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit", $"[trunk] error: {ex.Message}");
            }
        }

        /// <summary>
        /// Announces which character owns the deck being edited. Deck slots
        /// are character-bound: saving a deck whose owner differs from the
        /// active character makes the game offer a character SWITCH
        /// (2026-07-18 tester report — a Slifer deck built in a Joey slot
        /// while playing Yami Yugi), so the owner must be audible.
        /// </summary>
        private void AnnounceDeckOwner()
        {
            try
            {
                string chara = LabelExtractor.ResolveCharaName(
                    _vc?.currentChara ?? 0);
                if (string.IsNullOrWhiteSpace(chara)) return;
                ScreenReader.SayQueued(Loc.Get("deck_edit_owner", chara));
            }
            catch { }
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
                if (DeckEditPolicy.UseFilteredCollection(filtered != null))
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
            if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter))
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

            // V — verbose card reading (matches DuelFieldNavigator)
            if (InputManager.TryConsumeKeyDown(KeyCode.V))
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

            // A — open deck accessories (card sleeve, game mat, ace card)
            if (InputManager.TryConsumeKeyDown(KeyCode.A))
            {
                OpenAccessories();
                return;
            }

            // B — open auto deck build
            if (InputManager.TryConsumeKeyDown(KeyCode.B))
            {
                OpenAutoBuild();
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
                int mainBefore = _mainDeckMrks.Count;
                int extraBefore = _extraDeckMrks.Count;

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
                    var changed = DeckEditPolicy.ResolveAddedCount(
                        mainBefore,
                        extraBefore,
                        _mainDeckMrks.Count,
                        _extraDeckMrks.Count);
                    string zoneName = changed.IsExtraDeck
                        ? Loc.Get("deck_zone_extra_name")
                        : Loc.Get("deck_zone_main_name");
                    ScreenReader.Say(Loc.Get(
                        "deck_card_added_zone_count",
                        name,
                        zoneName,
                        changed.Count));
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
            bool removedFromExtraDeck = _currentZone == Zone.ExtraDeck;

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
                    var changed = DeckEditPolicy.ResolveRemovedCount(
                        removedFromExtraDeck,
                        _mainDeckMrks.Count,
                        _extraDeckMrks.Count);
                    string zoneName = changed.IsExtraDeck
                        ? Loc.Get("deck_zone_extra_name")
                        : Loc.Get("deck_zone_main_name");
                    ScreenReader.Say(Loc.Get(
                        "deck_card_removed_zone_count",
                        name,
                        zoneName,
                        changed.Count));
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

        /// <summary>
        /// Opens the deck accessory dialog (card sleeve, game mat, ace card)
        /// via DeckEdit2ViewController.AccessoryClicked() — the same entry
        /// point the on-screen accessory button uses. The dialog lands on the
        /// dialog stack where DialogHandler takes over; its accessory-aware
        /// relabel pass names the sleeve/mat/ace items.
        /// </summary>
        private void OpenAccessories()
        {
            try
            {
                if (_vc == null)
                {
                    ScreenReader.Say(Loc.Get("deck_operation_error"));
                    return;
                }

                ScreenReader.Say(Loc.Get("deck_edit_accessories_opening"));
                _vc.AccessoryClicked();
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    "AccessoryClicked() invoked");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"OpenAccessories error: {ex.Message}");
                ScreenReader.Say(Loc.Get("deck_operation_error"));
            }
        }

        /// <summary>
        /// Opens the Auto Deck Build dialog via DeckEdit2ViewController.AutoClicked()
        /// — the same entry point the on-screen "Auto" button uses. The dialog
        /// (AutoDeckDialogViewController) lands on the dialog stack where
        /// DialogHandler takes over; its auto-deck-aware relabel pass names the
        /// All/Rest/skill toggles with their current state.
        /// </summary>
        private void OpenAutoBuild()
        {
            try
            {
                if (_vc == null)
                {
                    ScreenReader.Say(Loc.Get("deck_operation_error"));
                    return;
                }

                ScreenReader.Say(Loc.Get("deck_auto_opening"));
                _vc.AutoClicked();
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    "AutoClicked() invoked");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "DeckEdit",
                    $"OpenAutoBuild error: {ex.Message}");
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
            // Verbose (V key) is a re-read of an already-navigated card, so
            // the "X of Y" position prefix is redundant — drop it. Compact
            // navigation reads keep it for orientation.
            string announcement = verbose
                ? cardText
                : Loc.Get("deck_card_position", pos, total, cardText);

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
            AnnounceDeckOwner();
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
