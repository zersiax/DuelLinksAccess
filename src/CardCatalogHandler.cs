using System;
using System.Collections.Generic;
using Il2CppYgomGame.CardList;
using UnityEngine;
using UnityEngine.UI;

namespace DuelLinksAccess
{
    /// <summary>
    /// Keyboard-driven accessibility handler for CardListViewController in SelProcess mode.
    /// Provides card navigation and conversion for the Card Catalog (conversion catalog) screen.
    /// Activated only when the top content VC is CardListViewController with Mode == SelProcess
    /// or SelProcessDetail — these are the modes used when converting cards at the Card Trader.
    /// </summary>
    public class CardCatalogHandler
    {
        #region Fields

        private CardListViewController _vc;
        private bool _wasActive;
        private string _lastGoName = "";

        private int _focusIndex;
        private int _cachedListNum;

        private bool _scanDone;
        private float _scanDelay;

        private float _cooldown;
        private const float CooldownTime = 0.3f;

        private CardListViewController.MODE _prevMode = CardListViewController.MODE.Card;
        private int _detailMrk;

        // Special Processing two-step commit. Tapping a card opens its detail
        // (SelProcessDetail); a second tap checks/commits the card, which is
        // what enables the CONFIRM (detailProcessButton). We remember the
        // chosen card's mrk, replay the commit tap on confirm, then poll for
        // CONFIRM to turn interactable and fire it. Timer < 0 means idle.
        private int _processMrk;
        private float _processConfirmTimer = -1f;

        // ChangeCard two-step: first Enter on a card opens its detail panel
        // (cardClicked, which also syncs the trader's current card); a second
        // Enter on that same card fires the trader's exchange-confirm dialog.
        private int _tradeDetailMrk;

        // One-time dump of native code-pointer RVAs for the ChangeCard methods,
        // so Ghidra can decompile them from the *current* GameAssembly.dll.
        private static bool _rvaDumped;

        // True when we activated on the conversion catalog (goldButton present),
        // false when on the ChangeCard "give a card up" picker. Gates the
        // card→gold conversion confirm so it can't fire in the ChangeCard flow.
        private bool _hasGoldButton;

        // Zone toggle. The card grid and the header menu (Filter, Sort, Search,
        // category tabs) coexist on CardList_PC, but this handler claims the
        // whole screen and would otherwise hide the menu. In menu mode we drop
        // IsActive so the update chain falls through to ScreenButtonHandler,
        // which navigates the menu; Tab flips back to the grid.
        private bool _menuMode;
        private ScreenButtonHandler _sbh;

        // How long we've been in detail mode waiting for goldButton to become active.
        private float _detailWaitTimer;
        private const float DetailWaitMax = 6f;
        private bool _detailButtonReady;

        #endregion

        #region Properties

        /// <summary>Whether this handler is actively managing the Card Catalog screen.</summary>
        public bool IsActive { get; private set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Supplies the shared ScreenButtonHandler so the catalog can hand the
        /// header menu (filters, sort, search, category tabs) back to it via the
        /// Tab zone-toggle. Wired once by Main at construction.
        /// </summary>
        public void SetScreenButtonHandler(ScreenButtonHandler sbh) => _sbh = sbh;

        /// <summary>Called each frame from Main.UpdateHandlers().</summary>
        public void Update()
        {
            if (_cooldown > 0f)
                _cooldown -= Time.deltaTime;

            // On first activation require goldButton active (confirms conversion context).
            // Once active, stay active as long as CardList_PC is on top — goldButton
            // hides during the detail overlay but we still need to handle input there.
            var vc = TryGetCatalogVC(requireGoldButton: !_wasActive);
            if (vc == null)
            {
                if (_wasActive) Deactivate();
                return;
            }

            string goName = vc.gameObject?.name ?? "";
            if (!_wasActive || goName != _lastGoName)
                Activate(vc, goName);

            // Menu mode: we've handed the header menu to ScreenButtonHandler
            // (IsActive is false, so the chain falls through to it). Only watch
            // for Tab to return to the grid — every other key belongs to SBH.
            if (_menuMode)
            {
                if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
                    ExitMenuMode();
                return;
            }

            if (!_scanDone)
            {
                _scanDelay -= Time.deltaTime;
                if (_scanDelay <= 0f)
                    DoScan();
                return;
            }

            // Special Processing: after the commit tap, poll for CONFIRM to
            // enable and fire it. Hold all other input during this short window
            // so a repeated Enter can't re-tap the card (which would toggle the
            // selection back off).
            if (_processConfirmTimer >= 0f)
            {
                PollProcessConfirm();
                return;
            }

            TrackModeTransition();
            ProcessInput();
        }

        #endregion

        #region Lifecycle

        private void Activate(CardListViewController vc, string goName)
        {
            _vc = vc;
            _lastGoName = goName;
            _wasActive = true;
            IsActive = true;
            _focusIndex = 0;
            _cachedListNum = 0;
            _scanDone = false;
            _scanDelay = 0.5f;
            _prevMode = GetCurrentMode();
            _detailMrk = 0;
            _processMrk = 0;
            _processConfirmTimer = -1f;
            _tradeDetailMrk = 0;

            // Record whether the conversion goldButton is present. Its absence
            // marks the ChangeCard "give a card up for the target" picker, which
            // reuses CardList_PC in a different mode and needs a different confirm
            // action than card→gold conversion. Capturing mode + goldButton here
            // lets us wire that confirm once we've seen the ChangeCard flow.
            _hasGoldButton = false;
            try { _hasGoldButton = _vc.goldButton?.gameObject?.activeInHierarchy == true; } catch { }

            if (!_rvaDumped)
            {
                _rvaDumped = true;
                DumpMethodRvas();
            }

            ScreenReader.Say(Loc.Get("catalog_entered"));
            // goldButton present => card->gold conversion catalog. Absent => the
            // shared SelProcess card-select picker, which several trader flows
            // reuse (ChangeCard "give a card for this", and Chronicle "Special
            // Processing" cosmetic customization). We can't yet tell those apart
            // from the VC alone, so keep the label neutral.
            DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                $"Activated GO={goName}, mode={_prevMode}, goldButton={_hasGoldButton} "
                + $"(context={(_hasGoldButton ? "conversion" : "card-select")})");
        }

        private void Deactivate()
        {
            _wasActive = false;
            IsActive = false;
            _menuMode = false;
            _processConfirmTimer = -1f;
            _processMrk = 0;
            _tradeDetailMrk = 0;
            _vc = null;
            _lastGoName = "";
            DebugLogger.Log(LogCategory.Handler, "CardCatalog", "Deactivated");
        }

        #endregion

        #region VC Detection

        /// <summary>
        /// Returns the VC only when the content top is CardList_PC.
        /// goldButton is only checked when <paramref name="requireGoldButton"/> is true
        /// (used for initial activation — goldButton hides in detail view so must not be
        /// checked each frame after we're already active).
        /// </summary>
        private static CardListViewController TryGetCatalogVC(bool requireGoldButton = false)
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return null;

                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedMgr.TryGetValue("content", out contentMgr) || contentMgr == null)
                    return null;

                var top = contentMgr.GetStackTopViewController();
                if (top == null) return null;

                string goName = top.gameObject?.name ?? "";
                if (goName != "CardList_PC") return null;

                var vc = top.TryCast<CardListViewController>();
                if (vc == null) return null;

                if (requireGoldButton)
                {
                    // First-activation readiness gate. The conversion catalog is
                    // ready once its goldButton is active. The ChangeCard "give a
                    // card up for the target" picker reuses CardList_PC but has no
                    // goldButton, so we also accept it once its card grid has
                    // populated. CardList_PC only ever appears in Card-Trader
                    // contexts (2026-08-02: it was falling through to
                    // ScreenButtonHandler, which only exposed the top-bar menu and
                    // never the cards), so both variants are ours to claim.
                    bool ready = false;
                    try
                    {
                        var btn = vc.goldButton;
                        ready = btn != null && btn.gameObject?.activeInHierarchy == true;
                    }
                    catch (Exception ex2)
                    {
                        DebugLogger.Log(LogCategory.Handler, "CardCatalog", $"goldButton check error: {ex2.Message}");
                    }
                    if (!ready)
                    {
                        try { ready = vc.getListNum() > 0; }
                        catch { }
                    }
                    if (!ready) return null;
                }

                return vc;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardCatalog", $"TryGetCatalogVC error: {ex.Message}");
                return null;
            }
        }

        private static CardListViewController.MODE GetMode(CardListViewController vc)
        {
            try { return vc.Mode; }
            catch { return CardListViewController.MODE.Card; }
        }

        private CardListViewController.MODE GetCurrentMode()
        {
            if (_vc == null) return CardListViewController.MODE.Card;
            return GetMode(_vc);
        }

        #endregion

        #region Scanning

        private void DoScan()
        {
            _cachedListNum = SafeGetListNum();
            _scanDone = true;

            DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                $"Scan: {_cachedListNum} cards, mode={GetCurrentMode()}");

            if (_cachedListNum > 0)
            {
                ScreenReader.SayQueued(Loc.Get("catalog_card_count", _cachedListNum));
                AnnounceCurrentCard(queued: true);
            }
            else
            {
                ScreenReader.SayQueued(Loc.Get("catalog_no_cards"));
            }
        }

        #endregion

        #region Mode Tracking

        private void TrackModeTransition()
        {
            var currentMode = GetCurrentMode();
            if (currentMode == _prevMode) return;

            DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                $"Mode transition {_prevMode} → {currentMode}");

            bool nowInDetail = currentMode == CardListViewController.MODE.PossDetail;
            bool wasInDetail = _prevMode == CardListViewController.MODE.PossDetail;

            if (nowInDetail)
            {
                // OpenDetail() already set _detailMrk; only use the list as fallback
                // because in SelProcessDetail mode getListNum() returns 1 and any
                // _focusIndex > 0 would produce mrk=0.
                if (_detailMrk <= 0)
                    _detailMrk = SafeGetListItemMrk(_focusIndex);
                _detailWaitTimer = 0f;
                _detailButtonReady = false;
                AnnounceDetailMode();
            }
            else if (wasInDetail)
            {
                _detailMrk = 0;
                AnnounceCurrentCard();
            }

            _prevMode = currentMode;
        }

        private bool IsInDetailMode()
        {
            return GetCurrentMode() == CardListViewController.MODE.PossDetail;
        }

        #endregion

        #region Input

        private void ProcessInput()
        {
            if (IsInDetailMode())
            {
                ProcessDetailInput();
                return;
            }

            ProcessListInput();
        }

        private void ProcessListInput()
        {
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.LeftArrow))
            {
                Navigate(-1);
                return;
            }
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.RightArrow))
            {
                Navigate(1);
                return;
            }
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.UpArrow))
            {
                Navigate(-10);
                return;
            }
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.DownArrow))
            {
                Navigate(10);
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Home))
            {
                _cachedListNum = SafeGetListNum();
                if (_cachedListNum > 0) { _focusIndex = 0; AnnounceCurrentCard(); }
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.End))
            {
                _cachedListNum = SafeGetListNum();
                if (_cachedListNum > 0) { _focusIndex = _cachedListNum - 1; AnnounceCurrentCard(); }
                return;
            }

            // Tab cycles this screen's zones, mirroring the deck editor's
            // Tab-between-deck/trunk/extra convention. Here the two zones are the
            // card grid and the header menu (filters, sort, search, category
            // tabs), so Tab moves grid -> menu; Tab in the menu returns to grid.
            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                EnterMenuMode();
                return;
            }

            // Debug probe (card-select picker only): dump the card-grid cell
            // structure so we can find the focused card's real cell button and
            // invoke its bound tap callback — the public methods don't register
            // the selection (2026-08-04: currentDetailCard stays 0).
            if (!_hasGoldButton && InputManager.TryConsumeKeyDown(KeyCode.P))
            {
                DumpCardGrid();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter)
                || InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                ActivateFocused();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.C)
                || InputManager.TryConsumeKeyDown(KeyCode.I))
            {
                AnnounceCurrentCard(verbose: true);
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.B))
            {
                BatchConvert();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.Backspace))
            {
                GoBack();
                return;
            }
        }

        /// <summary>
        /// Zone switch to the header menu. Drops IsActive so the update chain
        /// falls through to ScreenButtonHandler, which owns the menu's
        /// navigation (filters, sort, search, category tabs). ForceRescan makes
        /// SBH re-announce the menu even though the content VC is unchanged —
        /// without it the dedup-by-VC-name would leave the menu silent.
        /// </summary>
        private void EnterMenuMode()
        {
            _menuMode = true;
            IsActive = false;
            _sbh?.ForceRescan();
            ScreenReader.Say(Loc.Get("catalog_zone_menu"));
            DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                "Zone -> menu (handoff to ScreenButtonHandler)");
        }

        /// <summary>
        /// Zone switch back to the card grid. A filter or sort applied in the
        /// menu may have changed the list, so re-scan and re-announce from the
        /// top. ForceRescan also parks SBH (empty item list) so it doesn't
        /// linger active over the grid.
        /// </summary>
        private void ExitMenuMode()
        {
            _menuMode = false;
            IsActive = true;
            _sbh?.ForceRescan();
            _scanDone = false;
            _scanDelay = 0.2f;
            _focusIndex = 0;
            DebugLogger.Log(LogCategory.Handler, "CardCatalog", "Zone -> cards (grid)");
        }

        private void ProcessDetailInput()
        {
            // Poll each frame until goldButton or processButton becomes active,
            // then announce once so the user knows conversion is ready.
            if (!_detailButtonReady)
            {
                _detailWaitTimer += Time.deltaTime;
                bool goldActive = false, processActive = false;
                try { goldActive = _vc.detailGoldButton?.gameObject?.activeInHierarchy == true && _vc.detailGoldButton.interactable; } catch { }
                try { processActive = _vc.detailProcessButton?.gameObject?.activeInHierarchy == true && _vc.detailProcessButton.interactable; } catch { }

                if (goldActive || processActive)
                {
                    _detailButtonReady = true;
                    ScreenReader.Say(Loc.Get("catalog_detail_ready"));
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                        $"Detail button ready after {_detailWaitTimer:F2}s (gold={goldActive} process={processActive})");
                }
                else if (_detailWaitTimer >= DetailWaitMax)
                {
                    _detailButtonReady = true; // stop polling, won't become ready
                    ScreenReader.Say(Loc.Get("catalog_detail_not_available"));
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                        $"Detail buttons never became active after {DetailWaitMax}s — not convertible via detail");
                }
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                AnnounceDetailMode();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter)
                || InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                ConfirmConversion();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.C)
                || InputManager.TryConsumeKeyDown(KeyCode.I))
            {
                AnnounceCurrentCard(verbose: true);
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.Backspace))
            {
                CloseDetail();
                return;
            }
        }

        #endregion

        #region Navigation

        private void Navigate(int delta)
        {
            _cachedListNum = SafeGetListNum();
            if (_cachedListNum == 0)
            {
                ScreenReader.Say(Loc.Get("catalog_no_cards"));
                return;
            }
            _focusIndex = Math.Max(0, Math.Min(_cachedListNum - 1, _focusIndex + delta));
            AnnounceCurrentCard();
        }

        #endregion

        #region Operations

        /// <summary>
        /// Enter/Space dispatcher for the list. Conversion catalog → open the
        /// card detail. Card-select picker → select the source card; tapping a
        /// card in Special Processing moves the VC into SelProcessDetail (card
        /// chosen, CONFIRM shown), where the next activation fires the CONFIRM
        /// (detailProcessClicked) instead of re-toggling the card.
        /// </summary>
        private void ActivateFocused()
        {
            if (!_hasGoldButton
                && GetCurrentMode() == CardListViewController.MODE.SelProcessDetail)
            {
                ConfirmProcessSelection();
                return;
            }
            OpenDetail();
        }

        /// <summary>
        /// Fires the Special Processing CONFIRM shown once a card is chosen
        /// (mode SelProcessDetail). This is the detailProcessButton handler,
        /// detailProcessClicked(): it binds the selected card to the Chronicle
        /// session (sets PROCESS_CID) and then advances to the customization
        /// page. Calling jumpToProcess() directly skipped that binding, so the
        /// Chronicle page opened with cid=0 and Apply failed with "Processing
        /// failed" (2026-08-10 log).
        /// </summary>
        private void ConfirmProcessSelection()
        {
            if (_cooldown > 0f || _vc == null) return;

            try
            {
                // The card's detail is open (SelProcessDetail) but the card is
                // not committed yet — the CONFIRM (detailProcessButton) stayed
                // active but non-interactable, and its decideProcessUrl was a
                // static placeholder that dispatched to nothing (2026-08-10 log:
                // UrlScheme.Open -> True but cid stayed 0). A second tap on the
                // same cell is the checkbox-check that actually commits the card
                // and enables CONFIRM. Replay that tap; PollProcessConfirm then
                // fires CONFIRM once it turns interactable.
                var cell = FindCellByMrk(_processMrk);
                bool before = false;
                try { before = _vc.detailProcessButton?.interactable == true; } catch { }

                if (cell != null)
                    _vc.cardClicked(cell);

                bool afterActive = false, afterInteract = false;
                try { afterActive = _vc.detailProcessButton?.gameObject?.activeInHierarchy == true; } catch { }
                try { afterInteract = _vc.detailProcessButton?.interactable == true; } catch { }

                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"ConfirmProcessSelection: commit-tap mrk={_processMrk} cellFound={cell != null} "
                    + $"mode={GetCurrentMode()} confirmBtn before={before} "
                    + $"afterActive={afterActive} afterInteract={afterInteract}");

                _cooldown = CooldownTime;
                _processConfirmTimer = 1.0f;
                ScreenReader.Say(Loc.Get("catalog_process_confirming"));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"ConfirmProcessSelection error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        /// <summary>
        /// Runs for a short window after the Special Processing commit tap. Once
        /// the CONFIRM (detailProcessButton) turns active + interactable, fires
        /// its handler detailProcessClicked() to advance to the customization
        /// page. If CONFIRM never enables within the window, reports that the
        /// commit tap didn't register (so the log shows the button state).
        /// </summary>
        private void PollProcessConfirm()
        {
            _processConfirmTimer -= Time.deltaTime;

            bool active = false, interact = false;
            try { active = _vc?.detailProcessButton?.gameObject?.activeInHierarchy == true; } catch { }
            try { interact = _vc?.detailProcessButton?.interactable == true; } catch { }

            if (active && interact)
            {
                _processConfirmTimer = -1f;
                _cooldown = CooldownTime;
                try
                {
                    _vc.detailProcessClicked();
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                        "PollProcessConfirm: CONFIRM enabled -> detailProcessClicked()");
                }
                catch (Exception ex)
                {
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                        $"PollProcessConfirm error: {ex.Message}");
                    ScreenReader.Say(Loc.Get("ticket_activate_error"));
                }
                return;
            }

            if (_processConfirmTimer <= 0f)
            {
                _processConfirmTimer = -1f;
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"PollProcessConfirm: CONFIRM never enabled (active={active} "
                    + $"interactable={interact}, mode={GetCurrentMode()}) — commit tap did not register");
                ScreenReader.Say(Loc.Get("catalog_card_not_selectable"));
            }
        }

        private void OpenDetail()
        {
            if (_cooldown > 0f || _vc == null) return;

            _cachedListNum = SafeGetListNum();
            if (_cachedListNum == 0) return;
            if (_focusIndex >= _cachedListNum) _focusIndex = 0;

            int mrk = SafeGetListItemMrk(_focusIndex);
            if (mrk <= 0) return;

            string name = GetCardName(mrk);

            // Card-select picker (ChangeCard trade / Special Processing): the
            // conversion poss-detail is the wrong action here (nothing converts,
            // and for cards with no poss data possDetailClickedSub throws
            // KeyNotFoundException inside the game — 2026-08-04 log: Weapon
            // Change). The correct action is the game's card-source selection.
            if (!_hasGoldButton)
            {
                // Special Processing (SelProcess) and ChangeCard trade (List)
                // reuse this picker in different modes and need different
                // selection calls: SelProcess cells respond to cardClicked; the
                // List (ChangeCard) cells do not (cardClicked is a no-op there),
                // so trade selection goes through the card-source API instead.
                if (GetCurrentMode() == CardListViewController.MODE.List)
                    SelectTradeCard(mrk, name);
                else
                    SelectSourceCard(mrk, name);
                return;
            }

            // Conversion catalog: only open the poss detail for cards that have
            // one. possDetailClickedSub throws for non-convertible cards (their
            // Kirarity dictionary has no key), so gate on the game's own check
            // instead of relying on the try/catch to swallow a half-entered
            // detail-mode transition.
            bool canDetail = false;
            try { canDetail = _vc.IsPossDetailButtonActivate(mrk); } catch { }
            if (!canDetail)
            {
                ScreenReader.Say(Loc.Get("catalog_not_convertible_card"));
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"OpenDetail skipped: mrk={mrk} name={name} has no poss detail (would throw)");
                return;
            }

            DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                $"OpenDetail mrk={mrk} name={name}");

            try
            {
                _vc.possDetailClickedSub(mrk, false);
                _cooldown = CooldownTime;
                _detailMrk = mrk;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"OpenDetail error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        /// <summary>
        /// Card-select picker (ChangeCard trade / Special Processing): picks the
        /// focused card as the source for the exchange or customization. Uses the
        /// game's own card-cell tap handler cardClicked(CardBaseF), passing the
        /// actual on-screen cell for this card. The cells are CardListItem :
        /// ShopContentItem : CardBaseF, so a cell *is* the CardBaseF the handler
        /// expects — this replays a genuine tap, including the game's bound
        /// callback that opens the confirm / next screen. The public
        /// OnClickCardSrcCallback and cardSourceClicked paths don't register the
        /// card at all (2026-08-04 log: currentDetailCard stayed 0). Gated on
        /// IsCardSrcButtonActivate so ineligible cards get a clear message.
        /// </summary>
        private void SelectSourceCard(int mrk, string name)
        {
            if (_cooldown > 0f || _vc == null) return;

            bool eligible = false;
            try { eligible = _vc.IsCardSrcButtonActivate(mrk); } catch { }
            if (!eligible)
            {
                ScreenReader.Say(Loc.Get("catalog_card_not_selectable"));
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"SelectSourceCard: mrk={mrk} name={name} ineligible (IsCardSrcButtonActivate=false)");
                return;
            }

            var cell = FindCellByMrk(mrk);
            if (cell == null)
            {
                ScreenReader.Say(Loc.Get("catalog_card_not_selectable"));
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"SelectSourceCard: no active cell found for mrk={mrk} name={name} — can't tap");
                return;
            }

            try
            {
                _vc.cardClicked(cell);
                _cooldown = CooldownTime;

                // Tapping in Special Processing flips the VC to SelProcessDetail
                // (card chosen, CONFIRM shown) — prompt for the confirming Enter.
                // The ChangeCard (List) flow advances on its own, so just announce.
                if (GetCurrentMode() == CardListViewController.MODE.SelProcessDetail)
                {
                    _processMrk = mrk;
                    ScreenReader.Say(Loc.Get("catalog_process_selected", name));
                }
                else
                    ScreenReader.Say(Loc.Get("catalog_card_selected", name));

                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"SelectSourceCard: cardClicked(cell mrk={mrk}) name={name} mode={GetCurrentMode()}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"SelectSourceCard error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        /// <summary>
        /// ChangeCard trade picker (mode List). Ghidra disasm (2026-08-11):
        /// cardClicked in List mode routes to a dispatch handler that opens the
        /// card's detail panel and syncs the trader's current card via the
        /// game's own callback; the exchange confirm is a separate step. So this
        /// is a two-step like Special Processing: the first Enter opens the
        /// detail; a second Enter on the same card fires the trader's
        /// OpenCardConfirmDialog, which builds the TRADE confirm naming the card
        /// (so a wrong pick is heard and declined, not executed). First tap is
        /// gated by IsCardSrcButtonActivate.
        /// </summary>
        private void SelectTradeCard(int mrk, string name)
        {
            if (_cooldown > 0f || _vc == null) return;
            _cooldown = CooldownTime;

            // Second Enter on the card whose detail is already open → open the
            // exchange confirm on the trader for the (now-synced) current card.
            if (_tradeDetailMrk == mrk)
            {
                _tradeDetailMrk = 0;
                var trader = GetTraderVC();
                if (trader == null)
                {
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                        "SelectTradeCard: trader VC not found for confirm");
                    ScreenReader.Say(Loc.Get("catalog_card_not_selectable"));
                    return;
                }
                try
                {
                    // Two game calls, verified by disasm (2026-08-13):
                    //  1. OnChangeCardDetail(index) (RVA 0xA85540) syncs the
                    //     trader's current reward to the chosen destCards index.
                    //     The arg indexes the offer's reward list (destCards);
                    //     the on-screen CardList shows exactly those cards, so map
                    //     the picked card's mrk to its destCards index.
                    //  2. gotoChangeCard(offerItem) (RVA 0xA7AB00) is the actual
                    //     ChangeCard dispatch target — OnClickExchange's jump
                    //     table routes type==ChangeCard here — and it opens the
                    //     CardTransfer screen (final xferClicked lives there,
                    //     handled by CardTransferHandler). Calling the trader's
                    //     OnClickExchange button directly no-opped: inside the
                    //     reward list its guards bail. Now that the entry fix
                    //     (SetCurrent) makes getCurrentItem the real offer, we
                    //     call gotoChangeCard directly, in the correct context,
                    //     so CardTransfer builds properly this time.
                    var item = CardTraderHandler.LastChangeCardItem;
                    int index = DestCardIndex(item, mrk);
                    string contentBefore = TopContentVcName();

                    if (index < 0)
                    {
                        DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                            $"SelectTradeCard: mrk={mrk} not found in destCards (item={(item != null)}); cannot confirm");
                        ScreenReader.Say(Loc.Get("catalog_card_not_selectable"));
                        return;
                    }

                    trader.OnChangeCardDetail(index);
                    trader.gotoChangeCard(item);
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                        $"SelectTradeCard: OnChangeCardDetail({index}) + gotoChangeCard({item?.itemId}) "
                        + $"mrk={mrk} name={name}; content '{contentBefore}'->'{TopContentVcName()}'");
                    ScreenReader.Say(Loc.Get("catalog_trade_confirm_opening", name));
                }
                catch (Exception ex)
                {
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                        $"SelectTradeCard confirm error: {ex.Message}");
                    ScreenReader.Say(Loc.Get("ticket_activate_error"));
                }
                return;
            }

            bool srcActive = false;
            try { srcActive = _vc.IsCardSrcButtonActivate(mrk); } catch { }
            if (!srcActive)
            {
                ScreenReader.Say(Loc.Get("catalog_card_not_selectable"));
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"SelectTradeCard: mrk={mrk} name={name} not selectable");
                return;
            }

            // First Enter: open the card detail (cardClicked also syncs the
            // trader's current card), then prompt for the confirming Enter.
            var cell = FindCellByMrk(mrk);
            if (cell == null)
            {
                ScreenReader.Say(Loc.Get("catalog_card_not_selectable"));
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"SelectTradeCard: no on-screen cell for mrk={mrk}");
                return;
            }
            try
            {
                _vc.cardClicked(cell);
                _tradeDetailMrk = mrk;
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"SelectTradeCard: cardClicked(cell mrk={mrk}) name={name} — detail opened");
                ScreenReader.Say(Loc.Get("catalog_process_selected", name));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"SelectTradeCard error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        /// <summary>
        /// Logs the RVA (offset into GameAssembly.dll) of the native code for
        /// the ChangeCard-related methods, resolved live from the running
        /// process. Il2CppInterop stores each method's MethodInfo* in a private
        /// static NativeMethodInfoPtr_&lt;name&gt;_… field; MethodInfo's first
        /// field is the code pointer, so RVA = *methodInfo - moduleBase. Feeds
        /// Ghidra headless decompilation of the current binary.
        /// </summary>
        private void DumpMethodRvas()
        {
            try
            {
                IntPtr baseAddr = IntPtr.Zero;
                foreach (System.Diagnostics.ProcessModule m in
                    System.Diagnostics.Process.GetCurrentProcess().Modules)
                {
                    if (string.Equals(m.ModuleName, "GameAssembly.dll",
                        StringComparison.OrdinalIgnoreCase))
                    { baseAddr = m.BaseAddress; break; }
                }
                MelonLoader.MelonLogger.Msg($"[RVA] GameAssembly base=0x{(long)baseAddr:X}");

                LogRvaByField(typeof(CardListViewController), baseAddr, new[]
                {
                    "cardClicked", "cardClickedSub", "cardSourceClicked",
                    "OnClickCardSrcCallback", "possDetailClickedSub",
                    "IsCardSrcButtonActivate", "isCardSourceEnable"
                });
                LogRvaByField(typeof(Il2CppYgomGame.Single.CardTraderViewController2), baseAddr, new[]
                {
                    "gotoChangeCardList", "gotoChangeCard", "OnChangeCardDetail",
                    "OpenCardConfirmDialog", "OnCardExchange", "setConfirmListButton"
                });
                LogRvaByField(typeof(CardTransferViewController), baseAddr, new[]
                {
                    "xferClicked", "CardClicked", "setXferability", "OnBack",
                    "setup", "setupAfterLoad", "getArg"
                });
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Msg($"[RVA] error: {ex.Message}");
            }
        }

        private static void LogRvaByField(Type t, IntPtr baseAddr, string[] methods)
        {
            var fields = t.GetFields(System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static);
            foreach (var name in methods)
            {
                string prefix = "NativeMethodInfoPtr_" + name;
                bool found = false;
                foreach (var f in fields)
                {
                    if (f.FieldType != typeof(IntPtr)) continue;
                    if (!f.Name.StartsWith(prefix)) continue;
                    // Guard prefix collisions (cardClicked vs cardClickedSub):
                    // the char after the name must be the signature separator.
                    string rest = f.Name.Substring(prefix.Length);
                    if (rest.Length > 0 && rest[0] != '_') continue;
                    try
                    {
                        IntPtr mi = (IntPtr)f.GetValue(null);
                        if (mi == IntPtr.Zero)
                        {
                            MelonLoader.MelonLogger.Msg($"[RVA] {t.Name}.{name}: MethodInfo null");
                            found = true;
                            continue;
                        }
                        IntPtr code = System.Runtime.InteropServices.Marshal.ReadIntPtr(mi);
                        long rva = (long)code - (long)baseAddr;
                        MelonLoader.MelonLogger.Msg(
                            $"[RVA] {t.Name}.{name} RVA=0x{rva:X} (field {f.Name})");
                        found = true;
                    }
                    catch (Exception ex)
                    {
                        MelonLoader.MelonLogger.Msg($"[RVA] {name}: {ex.Message}");
                    }
                }
                if (!found)
                    MelonLoader.MelonLogger.Msg($"[RVA] {t.Name}.{name}: no field");
            }
        }

        /// <summary>Finds the CardTraderViewController2 in the content VC stack, or null.</summary>
        private static Il2CppYgomGame.Single.CardTraderViewController2 GetTraderVC()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return null;
                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedMgr.TryGetValue("content", out contentMgr) || contentMgr == null)
                    return null;
                int count = contentMgr.GetStackCount();
                for (int i = count - 1; i >= 0; i--)
                {
                    var vc = contentMgr.GetStackViewController(i);
                    var t = vc?.TryCast<Il2CppYgomGame.Single.CardTraderViewController2>();
                    if (t != null) return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Joins the content VC stack's gameObject names bottom-to-top, for logging.</summary>
        private static string ContentStackNames()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return "";
                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedMgr.TryGetValue("content", out contentMgr) || contentMgr == null)
                    return "";
                int count = contentMgr.GetStackCount();
                var names = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    var vc = contentMgr.GetStackViewController(i);
                    names.Add(vc?.gameObject?.name ?? "?");
                }
                return string.Join(" > ", names);
            }
            catch { return ""; }
        }

        /// <summary>Returns the content manager's top VC gameObject name, or "".</summary>
        private static string TopContentVcName() => TopVcName("content");

        /// <summary>Returns the named manager's top VC gameObject name, or "".</summary>
        private static string TopVcName(string manager)
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return "";
                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedMgr.TryGetValue(manager, out mgr) || mgr == null)
                    return "";
                var top = mgr.GetStackTopViewController();
                return top?.gameObject?.name ?? "";
            }
            catch { return ""; }
        }

        /// <summary>
        /// Finds the on-screen card cell (CardBaseF) whose card matches
        /// <paramref name="mrk"/>. The grid is a pooled/virtualized list, so cells
        /// don't map to logical indices — we match by the cell's own Mrk. Empty
        /// pooled cells report Mrk 0 and won't match a real card's mrk.
        /// </summary>
        private Il2CppYgomGame.Deck.CardBaseF FindCellByMrk(int mrk)
        {
            if (mrk <= 0) return null;
            try
            {
                var grid = _vc?.cardGrid;
                if (grid == null) return null;
                var t = grid.transform;
                int n = t.childCount;
                for (int i = 0; i < n; i++)
                {
                    var go = t.GetChild(i)?.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;
                    var cb = go.GetComponent<Il2CppYgomGame.Deck.CardBaseF>();
                    if (cb == null) continue;
                    int cellMrk = 0;
                    try { cellMrk = cb.Mrk; } catch { }
                    if (cellMrk == mrk) return cb;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardCatalog", $"FindCellByMrk error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Diagnostic (P key, card-select picker): dumps the cardGrid cell
        /// hierarchy — child count, each cell's name/active state and the buttons
        /// inside it (with onClick listener counts) — so we can identify the real
        /// card cell button to invoke (it carries the game's bound tap callback,
        /// which the public OnClickCardSrcCallback path does not).
        /// </summary>
        private void DumpCardGrid()
        {
            try
            {
                int focusMrk = SafeGetListItemMrk(_focusIndex);
                var grid = _vc?.cardGrid;
                if (grid == null)
                {
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                        $"DumpCardGrid: cardGrid is null (focusIndex={_focusIndex} focusMrk={focusMrk})");
                    return;
                }

                var t = grid.transform;
                int n = t.childCount;
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"DumpCardGrid: {n} cells; focusIndex={_focusIndex} focusMrk={focusMrk} "
                    + $"listNum={SafeGetListNum()}");

                int limit = Math.Min(n, 12);
                for (int i = 0; i < limit; i++)
                {
                    var go = t.GetChild(i)?.gameObject;
                    if (go == null) continue;

                    var btns = go.GetComponentsInChildren<UnityEngine.UI.Button>(true);
                    string btnInfo = "";
                    if (btns != null)
                    {
                        foreach (var b in btns)
                        {
                            if (b == null) continue;
                            int lc = 0;
                            try { lc = b.onClick.GetPersistentEventCount(); } catch { }
                            btnInfo += $" [{b.gameObject.name} act={b.gameObject.activeInHierarchy} "
                                + $"int={b.interactable} persist={lc}]";
                        }
                    }
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                        $"  cell[{i}] name={go.name} active={go.activeInHierarchy} "
                        + $"buttons={btns?.Length ?? 0}:{btnInfo}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardCatalog", $"DumpCardGrid error: {ex.Message}");
            }
        }

        private void ConfirmConversion()
        {
            if (_cooldown > 0f || _vc == null) return;

            // The ChangeCard picker reuses this detail view, but converting the
            // card to gold here would be destructive and wrong — the user is
            // choosing a card to give up for the trade, not converting it. Until
            // the correct select action is wired, refuse to convert and log what
            // the detail exposes so the right call can be identified next round.
            if (!_hasGoldButton)
            {
                bool pA = false, gA = false;
                try { pA = _vc.detailProcessButton?.gameObject?.activeInHierarchy == true; } catch { }
                try { gA = _vc.detailGoldButton?.gameObject?.activeInHierarchy == true; } catch { }
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"card-select confirm on mrk={_detailMrk}: mode={GetCurrentMode()} "
                    + $"detailProcessActive={pA} detailGoldActive={gA} — select action not yet wired");
                ScreenReader.Say(Loc.Get("catalog_changecard_pending"));
                return;
            }

            try
            {
                string url = "";
                bool processActive = false, goldActive = false, goldable = false;
                try { url = _vc.decideProcessUrl ?? ""; } catch { }
                try { processActive = _vc.detailProcessButton?.gameObject?.activeInHierarchy == true && _vc.detailProcessButton.interactable; } catch { }
                try { goldActive = _vc.detailGoldButton?.gameObject?.activeInHierarchy == true && _vc.detailGoldButton.interactable; } catch { }
                try { goldable = _vc?.isGoldable(_detailMrk) ?? false; } catch { }

                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"ConfirmConversion: mode={GetCurrentMode()} url='{url}' processBtn={processActive} goldBtn={goldActive}");

                // isGoldable is a local check and is reliable; use it as the gate
                // rather than button.interactable which is never set in PossDetail.
                if (goldable)
                {
                    _vc.detailGoldButtonClicked();
                    _cooldown = CooldownTime;
                    ScreenReader.Say(Loc.Get("catalog_converting"));
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog", "detailGoldButtonClicked (goldable)");
                }
                else if (processActive)
                {
                    _vc.detailProcessClicked();
                    _cooldown = CooldownTime;
                    ScreenReader.Say(Loc.Get("catalog_converting"));
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog", "detailProcessClicked");
                }
                else
                {
                    ScreenReader.Say(Loc.Get("catalog_not_convertible_card"));
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                        $"ConfirmConversion: not goldable, no active button");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"ConfirmConversion error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        private void CloseDetail()
        {
            if (_vc == null) return;
            try
            {
                _vc.closePossDetail();
                _cooldown = CooldownTime;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"CloseDetail error: {ex.Message}");
            }
        }

        private void BatchConvert()
        {
            if (_cooldown > 0f || _vc == null) return;

            try
            {
                var btn = _vc.goldButton;
                if (btn != null && btn.gameObject?.activeInHierarchy == true
                    && btn.interactable)
                {
                    btn.onClick.Invoke();
                    _cooldown = CooldownTime;
                    ScreenReader.Say(Loc.Get("catalog_batch_convert"));
                    DebugLogger.Log(LogCategory.Handler, "CardCatalog", "BatchConvert clicked");
                }
                else
                {
                    ScreenReader.Say(Loc.Get("catalog_batch_not_available"));
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardCatalog",
                    $"BatchConvert error: {ex.Message}");
            }
        }

        private void GoBack()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return;

                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedMgr.TryGetValue("content", out contentMgr) || contentMgr == null)
                    return;

                contentMgr.GetStackTopViewController()?.SendBack();
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardCatalog", $"GoBack error: {ex.Message}");
            }
        }

        #endregion

        #region Announcements

        private void AnnounceCurrentCard(bool queued = false, bool verbose = false)
        {
            _cachedListNum = SafeGetListNum();
            if (_cachedListNum == 0)
            {
                var msg = Loc.Get("catalog_no_cards");
                if (queued) ScreenReader.SayQueued(msg); else ScreenReader.Say(msg);
                return;
            }
            if (_focusIndex < 0 || _focusIndex >= _cachedListNum) _focusIndex = 0;

            int mrk = SafeGetListItemMrk(_focusIndex);
            string label = FormatCard(mrk, verbose);
            string text = Loc.Get("ticket_card_position", _focusIndex + 1, _cachedListNum, label);

            if (queued) ScreenReader.SayQueued(text); else ScreenReader.Say(text);
        }

        private void AnnounceDetailMode()
        {
            int mrk = _detailMrk > 0 ? _detailMrk : SafeGetListItemMrk(_focusIndex);
            string name = GetCardName(mrk);
            bool goldable = false;
            try { goldable = _vc?.isGoldable(mrk) ?? false; } catch { }
            if (goldable)
                ScreenReader.Say(Loc.Get("catalog_detail_mode", name));
            else
                ScreenReader.Say(Loc.Get("catalog_detail_not_convertible", name));
        }

        #endregion

        #region Formatting

        private string FormatCard(int mrk, bool verbose)
        {
            if (mrk <= 0) return Loc.Get("duel_unknown_card");

            string name = GetCardName(mrk);
            var parts = new List<string> { name };

            int count = 0;
            bool goldable = false;
            try { count = _vc?.trunk?.GetNum(mrk) ?? 0; } catch { }
            try { goldable = _vc?.isGoldable(mrk) ?? false; } catch { }

            if (count > 0)
                parts.Add(Loc.Get("catalog_own_count", count));

            // Announce eligibility so the user can spot usable cards while
            // browsing. The conversion catalog uses isGoldable; the card-select
            // picker (ChangeCard trade / Special Processing) uses the game's
            // source-eligibility check instead — isGoldable is unrelated there
            // and would wrongly read "not convertible" against every card.
            if (_hasGoldButton)
            {
                parts.Add(goldable ? Loc.Get("catalog_convertible") : Loc.Get("catalog_not_convertible"));
            }
            else
            {
                bool selectable = false;
                try { selectable = _vc?.IsCardSrcButtonActivate(mrk) ?? false; } catch { }
                parts.Add(selectable ? Loc.Get("catalog_selectable") : Loc.Get("catalog_not_selectable"));
            }

            if (verbose)
            {
                string desc = "";
                try { desc = Il2CppYgomGame.Card.Content.Instance?.GetDesc(mrk) ?? ""; } catch { }
                if (!string.IsNullOrWhiteSpace(desc))
                    parts.Add(desc);
            }

            return string.Join(", ", parts);
        }

        private int SafeGetListNum()
        {
            try { return _vc?.getListNum() ?? 0; }
            catch { return 0; }
        }

        private int SafeGetListItemMrk(int index)
        {
            try { return _vc?.getListItemMrk(index) ?? 0; }
            catch { return 0; }
        }

        /// <summary>
        /// Finds the position of a reward card (by mrk) within a ChangeCard
        /// offer's destCards list — the index OnChangeCardDetail expects. Returns
        /// -1 if the offer or card is missing.
        /// </summary>
        private static int DestCardIndex(Il2CppYgomGame.Single.CardTraderInfoBase item, int mrk)
        {
            try
            {
                var dest = item?.destCards;
                if (dest == null) return -1;
                int n = dest.Count;
                for (int i = 0; i < n; i++)
                    if (dest[i] == mrk) return i;
            }
            catch { }
            return -1;
        }

        private static string GetCardName(int mrk)
        {
            if (mrk <= 0) return Loc.Get("duel_unknown_card");
            try
            {
                var name = Il2CppYgomGame.Card.Content.Instance?.GetName(mrk);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            return $"Card {mrk}";
        }

        #endregion
    }
}
