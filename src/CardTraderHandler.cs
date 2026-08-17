using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using Il2CppYgomGame.Single;
using Il2CppYgomGame.Utility;

namespace DuelLinksAccess
{
    /// <summary>
    /// Keyboard-driven accessibility handler for CardTraderViewController2.
    /// Provides item browsing and trade execution for the Card Trader NPC screen.
    /// </summary>
    public class CardTraderHandler
    {
        #region Fields

        private CardTraderViewController2 _vc;
        private bool _wasActive;
        private string _lastVcGoName = "";

        private readonly List<CardTraderInfoBase> _items = new();

        /// <summary>
        /// The ChangeCard trade item most recently opened via gotoChangeCardList.
        /// CardCatalogHandler passes it to OpenCardConfirmDialog so the exchange
        /// confirm binds to this trade — a null item builds a generic dialog
        /// whose TRADE button doesn't execute the exchange (2026-08-11 log).
        /// </summary>
        internal static CardTraderInfoBase LastChangeCardItem { get; private set; }
        private int _focusIndex;

        private float _operationCooldown;
        private const float OperationCooldownTime = 0.5f;

        private float _scanDelay;
        private bool _scanDone;
        private int _scanAttempts;

        // Footer tab labels captured at scan (GetFooterButtonLabel probe). The
        // footer is where the trader keeps its secondary flows — card
        // conversion catalog and (per Florian) a "special processing" menu we
        // couldn't identify from static analysis. Exposing every footer index
        // as a numbered, speakable tab makes that menu reachable whatever its
        // index turns out to be, without hardcoding a guess.
        private bool _footerDumped;
        // Real footer tabs only: (game index, label). GetFooterButtonLabel
        // returns null for non-existent indices, so we keep just the populated
        // ones — the trader has a single real footer tab (conversion catalog).
        private readonly List<(int index, string label)> _footerTabs = new();

        // Diagnostic: ~1s after a trade attempt, log the top content VC and, if
        // a confirm dialog is up, whether its clickYesAction is wired — the
        // decisive signal for whether the game's own OnClickedYes will execute
        // the trade. A trade that opens an unhandled sub-screen also shows up
        // here instead of silently going unnoticed.
        private float _postTradeVcCheckDelay;

        // Trade selection poll. The trade always executes for the trader's
        // CURRENTLY-CENTERED carousel item (getCurrentItem), NOT for any item
        // we name to OnClickCard — calling OnClickCard(mrk,rare) directly does
        // not move the selection (2026-07-30 tester log: intended Meteor
        // Dragon's Nails 19693, but getCurrentItem stayed Night Sword Serpent
        // 16936 and the trade executed for the wrong card). So we steer the
        // carousel to the focused item (SetCurrent + cell click), then poll
        // getCurrentItem each frame and only open the confirm once it actually
        // matches the intended item — and abort rather than ever trade the
        // wrong one. The carousel snap can drift for a few frames, so we watch
        // for the match instead of confirming after a fixed delay.
        private bool _tradePollActive;
        private float _tradePollTimer;
        private const float TradePollTimeout = 1.0f;
        private CardTraderInfoBase _tradeIntendedItem;
        private int _tradeIntendedId;
        private string _tradeIntendedName = "";
        private bool _tradePollLoggedFirst;

        #endregion

        #region Properties

        /// <summary>Whether this handler is actively managing the Card Trader screen.</summary>
        public bool IsActive { get; private set; }

        #endregion

        #region Public Methods

        /// <summary>Called each frame from Main.UpdateHandlers().</summary>
        public void Update()
        {
            if (_operationCooldown > 0f)
                _operationCooldown -= Time.deltaTime;

            if (_postTradeVcCheckDelay > 0f)
            {
                _postTradeVcCheckDelay -= Time.deltaTime;
                if (_postTradeVcCheckDelay <= 0f)
                    LogPostTradeVcState();
            }

            if (_tradePollActive)
                PollTradeSelection();

            var vc = TryGetTraderVC();
            if (vc == null)
            {
                if (_wasActive) Deactivate();
                return;
            }

            string goName = vc.gameObject?.name ?? "";
            if (!_wasActive || goName != _lastVcGoName)
                Activate(vc, goName);

            if (!_scanDone)
            {
                _scanDelay -= Time.deltaTime;
                if (_scanDelay <= 0f)
                    DoScan();
                return;
            }

            ProcessInput();
        }

        #endregion

        #region Lifecycle

        private void Activate(CardTraderViewController2 vc, string goName)
        {
            _vc = vc;
            _lastVcGoName = goName;
            _wasActive = true;
            IsActive = true;
            _focusIndex = 0;
            _scanDone = false;
            _scanDelay = 1.0f;
            _scanAttempts = 0;
            _footerDumped = false;
            _footerTabs.Clear();
            _postTradeVcCheckDelay = 0f;
            CancelTradePoll();

            ScreenReader.Say(Loc.Get("trader_entered"));
            DebugLogger.Log(LogCategory.Handler, "CardTrader", $"Activated GO={goName}");
        }

        private void Deactivate()
        {
            _wasActive = false;
            IsActive = false;
            _vc = null;
            _items.Clear();
            _lastVcGoName = "";
            _footerDumped = false;
            _footerTabs.Clear();
            _postTradeVcCheckDelay = 0f;
            CancelTradePoll();

            DebugLogger.Log(LogCategory.Handler, "CardTrader", "Deactivated");
        }

        #endregion

        #region VC Detection

        private static CardTraderViewController2 TryGetTraderVC()
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

                // CardTrader2 is directly on top — normal case
                var direct = top.TryCast<CardTraderViewController2>();
                if (direct != null) return direct;

                // DDGuide is on top blocking the trade list — pop it so CardTrader2
                // becomes the top VC on the next frame. PopDDGuide is async and
                // takes multiple frames to take effect, so we call it on every
                // frame DDGuide is still on top.
                if ((top.gameObject?.name ?? "") == "DDGuide")
                {
                    int count = contentMgr.GetStackCount();
                    if (count >= 2)
                    {
                        var below = contentMgr.GetStackViewController(count - 2);
                        var traderBelow = below?.TryCast<CardTraderViewController2>();
                        if (traderBelow != null)
                        {
                            try
                            {
                                traderBelow.DDGuideMng?.PopDDGuide();
                            }
                            catch (Exception ex2)
                            {
                                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                                    $"PopDDGuide error: {ex2.Message}");
                            }
                            // Return null this frame; next frame CardTrader2 will be top
                            return null;
                        }
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"TryGetTraderVC error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Scanning

        private void DoScan()
        {
            _scanAttempts++;

            // The VC goes through DataWait → Start → TutoWait (first visit) → Init → Browse.
            // Don't scan until Browse mode to avoid reading stale/empty data.
            CardTraderViewController2.MODE mode;
            try { mode = CardTraderViewController2.currentMode; }
            catch { mode = CardTraderViewController2.MODE.DataWait; }

            bool notReady = mode == CardTraderViewController2.MODE.DataWait
                || mode == CardTraderViewController2.MODE.Start1
                || mode == CardTraderViewController2.MODE.Start2
                || mode == CardTraderViewController2.MODE.Start3
                || mode == CardTraderViewController2.MODE.TutoWait
                || mode == CardTraderViewController2.MODE.TutoWait2
                || mode == CardTraderViewController2.MODE.Init
                || mode == CardTraderViewController2.MODE.Wait1
                || mode == CardTraderViewController2.MODE.ItemListReceiveWait;

            if (notReady && _scanAttempts < 12)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"Scan {_scanAttempts}: mode={mode}, waiting...");
                _scanDelay = 0.5f;
                return;
            }

            RefreshItems();
            DumpFooterButtons();
            DumpSpecialItems();

            DebugLogger.Log(LogCategory.Handler, "CardTrader",
                $"Scan {_scanAttempts}: mode={mode}, {_items.Count} items");

            if (_items.Count == 0 && _scanAttempts < 10)
            {
                _scanDelay = 0.5f;
                return;
            }

            _scanDone = true;

            if (_items.Count > 0)
            {
                ScreenReader.Say(Loc.Get("trader_items", _items.Count));
                AnnounceCurrentItem(queued: true);
            }
            else
            {
                ScreenReader.Say(Loc.Get("trader_no_items"));
            }
        }

        private void RefreshItems()
        {
            _items.Clear();
            if (_vc == null) return;

            try
            {
                var filter = _vc.itemInfoFilter;
                if (filter == null || filter.Count == 0)
                    filter = _vc.itemInfoMaster;
                if (filter == null) return;

                for (int i = 0; i < filter.Count; i++)
                {
                    var item = filter[i];
                    if (item != null) _items.Add(item);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"RefreshItems error: {ex.Message}");
            }
        }

        /// <summary>
        /// Probes the game's own GetFooterButtonLabel(int) for each footer tab,
        /// caches the labels (for F / number-key navigation) and always-on logs
        /// them once per activation. Enumeration stops at the first index that
        /// throws, which signals the real footer-button count.
        /// </summary>
        private void DumpFooterButtons()
        {
            if (_footerDumped || _vc == null) return;
            _footerDumped = true;
            _footerTabs.Clear();

            for (int i = 0; i < 8; i++)
            {
                string label;
                try
                {
                    label = _vc.GetFooterButtonLabel(i);
                }
                catch (Exception ex)
                {
                    DebugLogger.Log(LogCategory.Handler, "CardTrader",
                        $"GetFooterButtonLabel({i}) threw ({ex.Message}) — stopping, {i} index(es) probed");
                    break;
                }

                // Non-existent footer indices return null; only keep real tabs
                // so we don't offer the user phantom numbers that do nothing.
                if (!string.IsNullOrWhiteSpace(label))
                    _footerTabs.Add((i, label));

                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"Footer button {i}: \"{label ?? "(null)"}\"");
            }

            DebugLogger.Log(LogCategory.Handler, "CardTrader",
                $"{_footerTabs.Count} real footer tab(s)");
        }

        /// <summary>
        /// Always-on logs any non-standard trade entries (Process, ChangeCard,
        /// Chroniclizer, ChangeSkill, RespectOrb, ExItem, BoxChip) with their
        /// index/type/name, so the "special processing" flow shows up in the log
        /// whether it lives in a footer tab or as a list item.
        /// </summary>
        private void DumpSpecialItems()
        {
            try
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    var it = _items[i];
                    if (it == null) continue;
                    var t = it.itemType;
                    bool special = t == CardTraderInfoBase.Type.Process
                        || t == CardTraderInfoBase.Type.ChangeCard
                        || t == CardTraderInfoBase.Type.Chroniclizer
                        || t == CardTraderInfoBase.Type.ChangeSkill
                        || t == CardTraderInfoBase.Type.RespectOrb
                        || t == CardTraderInfoBase.Type.ExItem
                        || t == CardTraderInfoBase.Type.BoxChip;
                    if (!special) continue;

                    DebugLogger.Log(LogCategory.Handler, "CardTrader",
                        $"Special item [{i}]: type={t}, name={GetItemName(it)}, "
                        + $"itemId={it.itemId}, gId={it.gId}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"DumpSpecialItems error: {ex.Message}");
            }
        }

        /// <summary>
        /// Diagnostic: logs the top content VC ~1s after any trade/tab attempt.
        /// If a confirm dialog is on top, logs whether its clickYesAction is
        /// wired — the decisive signal for whether OnClickedYes (driven by
        /// DialogHandler when the user picks TRADE) will actually execute the
        /// trade, versus opening a decorative dialog that does nothing.
        /// </summary>
        private void LogPostTradeVcState()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                string topName = "(unknown)";
                if (namedMgr != null && namedMgr.TryGetValue("content", out var contentMgr) && contentMgr != null)
                {
                    var top = contentMgr.GetStackTopViewController();
                    topName = top?.gameObject?.name ?? "(null)";
                }

                string dialogInfo = "no confirm dialog";
                if (namedMgr != null && namedMgr.TryGetValue("dialog", out var dialogMgr) && dialogMgr != null)
                {
                    var dTop = dialogMgr.GetStackTopViewController();
                    var confirm = dTop?.TryCast<Il2CppYgomGame.Menu.ConfirmDialogViewController>();
                    if (confirm != null)
                    {
                        bool yesWired = false, noWired = false;
                        try { yesWired = confirm.clickYesAction != null; } catch { }
                        try { noWired = confirm.clickNoAction != null; } catch { }
                        dialogInfo = $"ConfirmDialog up: clickYesAction wired={yesWired}, clickNoAction wired={noWired}";
                    }
                    else if (dTop != null)
                    {
                        dialogInfo = $"dialog top = {dTop.gameObject?.name ?? "(null)"}";
                    }
                }

                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"Post-trade check: top content VC = {topName}, screen = {GameStateTracker.CurrentScreen}; {dialogInfo}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"LogPostTradeVcState error: {ex.Message}");
            }
        }

        #endregion

        #region Input

        private void ProcessInput()
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
                if (_items.Count > 0) { _focusIndex = 0; AnnounceCurrentItem(); }
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.End))
            {
                if (_items.Count > 0) { _focusIndex = _items.Count - 1; AnnounceCurrentItem(); }
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter))
            {
                SelectCurrentItem();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                ConfirmTrade();
                return;
            }

            // F — list the trader's footer tabs (e.g. conversion catalog).
            if (InputManager.TryConsumeKeyDown(KeyCode.F))
            {
                AnnounceFooterTabs();
                return;
            }

            // Number keys — activate the Nth listed footer tab (1-based over the
            // real tabs only, not raw game indices).
            for (int d = 0; d < 6; d++)
            {
                if (InputManager.TryConsumeKeyDown(KeyCode.Alpha1 + d)
                    || InputManager.TryConsumeKeyDown(KeyCode.Keypad1 + d))
                {
                    ActivateFooterTab(d);
                    return;
                }
            }

            // B — shortcut for the first footer tab (conversion catalog).
            if (InputManager.TryConsumeKeyDown(KeyCode.B))
            {
                ActivateFooterTab(0);
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.C) || InputManager.TryConsumeKeyDown(KeyCode.I))
            {
                AnnounceCurrentItem(verbose: true);
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.G))
            {
                AnnounceGoldBalance();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                AnnounceCurrentItem();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.Backspace))
            {
                GoBack();
                return;
            }
        }

        #endregion

        #region Navigation

        private void Navigate(int delta)
        {
            if (_items.Count == 0)
            {
                ScreenReader.Say(Loc.Get("trader_no_items"));
                return;
            }

            // Moving the focus cancels any in-flight trade selection, so a
            // pending confirm can't fire for a card the user has navigated off.
            if (_tradePollActive) CancelTradePoll();

            _focusIndex = Math.Max(0, Math.Min(_items.Count - 1, _focusIndex + delta));
            AnnounceCurrentItem();
        }

        #endregion

        #region Item Operations

        private void SelectCurrentItem()
        {
            if (_operationCooldown > 0f || _vc == null || _items.Count == 0) return;
            if (_tradePollActive) return; // a selection is already resolving

            var item = _items[_focusIndex];
            string name = GetItemName(item);
            var type = item.itemType;

            DebugLogger.Log(LogCategory.Handler, "CardTrader",
                $"SelectItem: type={type}, name={name}, itemId={item.itemId}, "
                + $"gId={item.gId}, rareId={item.rareId}, focusIndex={_focusIndex}");

            try
            {
                switch (type)
                {
                    // ChangeCard opens its own reward sub-screen (CardList_PC),
                    // driven by CardCatalogHandler once it appears. Enter it the
                    // way a real tap does: center the carousel on THIS offer
                    // first (SetCurrent), then replay the genuine cell tap
                    // (OnClickCard dispatches by the centered item's type).
                    // Calling gotoChangeCardList directly left the carousel
                    // centered on the previously-shown offer, so the trader's
                    // current-item stayed wrong (e.g. a regular purchase card)
                    // and the whole exchange ran in a phantom layer the trade
                    // could never complete from — getCurrentItem returned the
                    // wrong card and the exchange screen never built visually.
                    case CardTraderInfoBase.Type.ChangeCard:
                        LastChangeCardItem = item;
                        LogChangeCardBreakdown(item);
                        var ccMgr = _vc.CardTrader2DMgr;
                        if (ccMgr != null)
                        {
                            try { ccMgr.SetCurrent(_focusIndex); }
                            catch (Exception exCc)
                            {
                                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                                    $"ChangeCard SetCurrent error: {exCc.Message}");
                            }
                        }
                        int ccCurId = -1;
                        try { var cc = _vc.getCurrentItem(); if (cc != null) ccCurId = cc.itemId; } catch { }
                        // gotoChangeCardList opens the reward list; OnClickCard is
                        // a no-op for ChangeCard offers (verified 2026-08-13 log).
                        // With SetCurrent above, the list now opens against the
                        // correct current-item (getCurrentItem == this offer),
                        // so the downstream exchange dispatches in the right
                        // context instead of the previous phantom layer.
                        _vc.gotoChangeCardList(item);
                        _operationCooldown = OperationCooldownTime;
                        _postTradeVcCheckDelay = 1.0f;
                        ScreenReader.Say(Loc.Get("trader_selected", name));
                        DebugLogger.Log(LogCategory.Handler, "CardTrader",
                            $"  -> SetCurrent({_focusIndex}) [getCurrentItem={ccCurId}] "
                            + $"+ gotoChangeCardList({item.itemId})");
                        return;

                    // Rarity-list header — drills into the R/SR/UR sub-list.
                    case CardTraderInfoBase.Type.List:
                        _vc.OnClickOpenList();
                        _operationCooldown = OperationCooldownTime;
                        ScreenReader.Say(Loc.Get("trader_selected", name));
                        DebugLogger.Log(LogCategory.Handler, "CardTrader", "  -> OnClickOpenList");
                        return;

                    case CardTraderInfoBase.Type.SoldOut:
                        ScreenReader.Say(Loc.Get("trader_sold_out"));
                        return;
                }

                // Steer the carousel to this item. SetCurrent(index) maps 1:1 to
                // the carousel (2026-07-30 log: SetCurrent(44) → currIdx=44,
                // getCurrentItem=116) and is what actually sets the trader's
                // selection — OnClickCard(mrk) does not. The old scroll-snap
                // "drift" no longer occurs; getCurrentItem matches immediately.
                var mgr = _vc.CardTrader2DMgr;
                if (mgr != null)
                {
                    try { mgr.SetCurrent(_focusIndex); }
                    catch (Exception ex2)
                    {
                        DebugLogger.Log(LogCategory.Handler, "CardTrader", $"SetCurrent error: {ex2.Message}");
                    }
                }

                int curIdAfter = -1;
                try { var c = _vc.getCurrentItem(); if (c != null) curIdAfter = c.itemId; } catch { }
                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"  -> SetCurrent({_focusIndex}); currIdx={SafeCurrIdx()}, getCurrentItem itemId={curIdAfter}");

                // Special-processing cells (itemId<=0: Process/Chroniclizer/
                // ChangeSkill) open their own sub-screen. They route through the
                // carousel's real tap callback: CardTrader2DManager.StartCardList
                // wires every cell to the VC's OnClickCard(mrk, rare), which then
                // dispatches by the *centered* item's type. The cell's own
                // Button.onClick is NOT the tap path — invoking it silently
                // no-ops (2026-08-02 log: invoked onClick, no sub-screen opened).
                // Since we just centered the carousel with SetCurrent, calling
                // OnClickCard replays a genuine tap on this cell.
                if (item.itemId <= 0)
                {
                    _vc.OnClickCard(item.itemId, item.rareId);
                    _operationCooldown = OperationCooldownTime;
                    _postTradeVcCheckDelay = 1.0f;
                    ScreenReader.Say(Loc.Get("trader_selected", name));
                    DebugLogger.Log(LogCategory.Handler, "CardTrader",
                        $"  -> OnClickCard({item.itemId}, {item.rareId}) for special type={type}");
                    return;
                }

                // Begin verifying the carousel actually settled on our item
                // before opening the trade confirm.
                _tradePollActive = true;
                _tradePollTimer = 0f;
                _tradePollLoggedFirst = false;
                _tradeIntendedItem = item;
                _tradeIntendedId = item.itemId;
                _tradeIntendedName = name;
                _operationCooldown = OperationCooldownTime;
                ScreenReader.Say(Loc.Get("trader_trading", name));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"SelectCurrentItem error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        /// <summary>
        /// Each frame after a purchase selection: watches getCurrentItem until
        /// the carousel actually centers our intended item, then opens the trade
        /// confirm. If it never matches within the timeout we abort — the trade
        /// executes for getCurrentItem, so confirming on a mismatch would trade
        /// the wrong card.
        /// </summary>
        private void PollTradeSelection()
        {
            if (_vc == null) { CancelTradePoll(); return; }

            _tradePollTimer += Time.deltaTime;

            int curId = -1;
            string curName = "?";
            try
            {
                var cur = _vc.getCurrentItem();
                if (cur != null) { curId = cur.itemId; curName = GetItemName(cur); }
            }
            catch { }

            if (!_tradePollLoggedFirst)
            {
                _tradePollLoggedFirst = true;
                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"PollTrade start: intended={_tradeIntendedId} ({_tradeIntendedName}), "
                    + $"currIdx={SafeCurrIdx()}, getCurrentItem={curId} ({curName})");
            }

            if (curId == _tradeIntendedId)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"PollTrade matched after {_tradePollTimer:F2}s — opening confirm");
                var intendedItem = _tradeIntendedItem;
                var intendedName = _tradeIntendedName;
                CancelTradePoll();
                OpenTradeConfirm(intendedItem, intendedName);
                return;
            }

            if (_tradePollTimer >= TradePollTimeout)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"PollTrade timed out after {_tradePollTimer:F2}s — intended={_tradeIntendedId}, "
                    + $"getCurrentItem={curId} ({curName}), currIdx={SafeCurrIdx()}; aborting to avoid wrong trade");
                ScreenReader.Say(Loc.Get("trader_select_failed", _tradeIntendedName));
                CancelTradePoll();
            }
        }

        private void CancelTradePoll()
        {
            _tradePollActive = false;
            _tradePollTimer = 0f;
            _tradePollLoggedFirst = false;
            _tradeIntendedItem = null;
            _tradeIntendedId = 0;
            _tradeIntendedName = "";
        }

        private int SafeCurrIdx()
        {
            try { return _vc?.CardTrader2DMgr?.currIdx ?? -1; }
            catch { return -1; }
        }

        /// <summary>
        /// Opens the trade confirmation, now that getCurrentItem is verified to
        /// be the intended item. Uses OpenExchangeConfirmDialog, which opens the
        /// game's own TRADE/NO dialog for the current item (proven 2026-07-30 to
        /// execute the trade via DialogHandler's OnClickedYes). exchangeButton
        /// stays non-interactable from keyboard, so its onClick isn't a usable
        /// path here.
        /// </summary>
        private void OpenTradeConfirm(CardTraderInfoBase item, string name)
        {
            try
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"OpenTradeConfirm via OpenExchangeConfirmDialog: intended={item?.itemId} ({name})");

                _vc.OpenExchangeConfirmDialog(item);
                _postTradeVcCheckDelay = 1.0f;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"OpenTradeConfirm error: {ex.Message}");
                ScreenReader.Say(Loc.Get("trader_cannot_trade"));
            }
        }

        /// <summary>
        /// Space — confirms the trader's own confirm-list (the ChangeCard flow,
        /// where you pick which cards to give up and then commit the list).
        /// Regular purchases go through the game TRADE/NO dialog opened by
        /// SelectCurrentItem and don't need this.
        /// </summary>
        private void ConfirmTrade()
        {
            if (_operationCooldown > 0f || _vc == null) return;

            try
            {
                var btn = _vc.confirmListButton;
                bool active = btn != null && btn.gameObject?.activeInHierarchy == true;
                bool interactable = btn != null && btn.interactable;

                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"ConfirmTrade: confirmListButton active={active} interactable={interactable}");

                if (btn != null && active && interactable)
                {
                    btn.onClick.Invoke();
                    _postTradeVcCheckDelay = 1.0f;
                    DebugLogger.Log(LogCategory.Handler, "CardTrader", "  -> invoked confirmListButton.onClick");
                }
                else
                {
                    DebugLogger.Log(LogCategory.Handler, "CardTrader", "  -> confirmListButton unavailable, no-op");
                }

                _operationCooldown = OperationCooldownTime;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"ConfirmTrade error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        #endregion

        #region Footer tabs

        private void AnnounceFooterTabs()
        {
            if (_footerTabs.Count == 0)
            {
                ScreenReader.Say(Loc.Get("trader_footer_none"));
                return;
            }

            var parts = new List<string>();
            for (int i = 0; i < _footerTabs.Count; i++)
                parts.Add($"{i + 1} {StripMarkup(_footerTabs[i].label.Trim())}");
            ScreenReader.Say(Loc.Get("trader_footer_tabs", string.Join(", ", parts)));
        }

        /// <summary>Activates the position-th listed footer tab (0-based over real tabs).</summary>
        private void ActivateFooterTab(int position)
        {
            if (_operationCooldown > 0f || _vc == null) return;

            if (position < 0 || position >= _footerTabs.Count)
            {
                ScreenReader.Say(Loc.Get("trader_footer_invalid"));
                return;
            }

            try
            {
                var tab = _footerTabs[position];
                string label = StripMarkup(tab.label.Trim());

                _vc.OnFooterButton(tab.index);
                _operationCooldown = OperationCooldownTime;
                _postTradeVcCheckDelay = 1.0f;
                ScreenReader.Say(Loc.Get("trader_footer_activated", label));
                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"OnFooterButton({tab.index}) — \"{tab.label}\"");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"ActivateFooterTab(pos {position}) error: {ex.Message}");
            }
        }

        private void GoBack()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return;

                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedMgr.TryGetValue("content", out contentMgr) || contentMgr == null) return;

                contentMgr.GetStackTopViewController()?.SendBack();
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"GoBack error: {ex.Message}");
            }
        }

        #endregion

        #region Announcements

        private void AnnounceCurrentItem(bool queued = false, bool verbose = false)
        {
            if (_items.Count == 0)
            {
                ScreenReader.Say(Loc.Get("trader_no_items"));
                return;
            }

            if (_focusIndex < 0 || _focusIndex >= _items.Count) _focusIndex = 0;

            var item = _items[_focusIndex];
            string label = FormatItem(item, verbose);
            string text = Loc.Get("ticket_card_position", _focusIndex + 1, _items.Count, label);

            if (queued) ScreenReader.SayQueued(text);
            else ScreenReader.Say(text);
        }

        private void AnnounceGoldBalance()
        {
            if (_items.Count == 0)
            {
                ScreenReader.Say(Loc.Get("trader_gold_unknown"));
                return;
            }

            try
            {
                int gold = _items[0].GoldPoss();
                ScreenReader.Say(Loc.Get("trader_gold", gold));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"AnnounceGoldBalance error: {ex.Message}");
                ScreenReader.Say(Loc.Get("trader_gold_unknown"));
            }
        }

        #endregion

        #region Formatting

        private string FormatItem(CardTraderInfoBase item, bool verbose)
        {
            var parts = new List<string> { GetItemName(item) };

            // Rush Duel cards appear in the trader's normal card lists but a
            // sighted player recognizes them by their distinct card frame; a
            // blind user has no such cue and can spend gold/jewels on a card
            // that can't go in a normal deck (confirmed 2026-08-13: Beast Gear
            // Buggy Dog). Speak the cue the frame conveys, right after the name.
            if (IsRushCardItem(item))
                parts.Add(Loc.Get("card_rush_tag"));

            // Affordability — drives the user's first decision when browsing.
            // ChangeCard items are the exception: you trade a card you own for
            // this one, so IsCardShort() always reports a shortage of the
            // *target* card (which you're trying to obtain). That made every
            // ChangeCard read "cannot trade" even though the picker opens fine
            // (2026-08-02 log). The real gate is choosing an eligible card in
            // the sub-picker, so announce that instead of a bogus verdict.
            if (item.itemType == CardTraderInfoBase.Type.ChangeCard)
            {
                // A ChangeCard trade spends copies of the cost card (costCardMrk)
                // you already OWN; the reward is one of destCards. The exchange
                // screen's grid is literally your owned copies of that cost card
                // (disasm 2026-08-13), so owning fewer than costCardUse means the
                // trade can't complete — the screen would show an empty grid.
                // IsCardShort() reports on the reward card here, not the cost, so
                // read possession directly. Announcing real affordability is also
                // how the user finds a ChangeCard offer they can actually finish
                // (own the cost card), since most cost cards you may not have.
                int ccNeed = 0, ccOwned = 0, ccMrk = 0;
                try { ccNeed = item.costCardUse; } catch { }
                try { ccMrk = item.costCardMrk; } catch { }
                try { ccOwned = item.CardPossAll(); } catch { }
                if (ccMrk > 0 && ccNeed > 0)
                {
                    string ccName = GetCardName(ccMrk);
                    parts.Add(ccOwned >= ccNeed
                        ? Loc.Get("trader_changecard_ok", ccNeed, ccName, ccOwned)
                        : Loc.Get("trader_changecard_short", ccNeed, ccName, ccOwned));
                }
                else
                {
                    parts.Add(Loc.Get("trader_opens_picker"));
                }
            }
            else
            {
                bool goldShort = false, itemShort = false, cardShort = false;
                try { goldShort = item.IsGoldShort(); } catch { }
                try { itemShort = item.IsItemShort(); } catch { }
                try { cardShort = item.IsCardShort() == CardTraderInfoBase.CardShortage.Shortage; } catch { }
                bool canTrade = !goldShort && !itemShort && !cardShort;
                parts.Add(canTrade ? Loc.Get("trader_can_trade") : Loc.Get("trader_cannot_trade"));
            }

            // Gold cost
            if (item.goldUse > 0)
            {
                if (verbose)
                {
                    int goldPoss = 0;
                    try { goldPoss = item.GoldPoss(); } catch { }
                    parts.Add(Loc.Get("trader_cost_gold_poss", item.goldUse, goldPoss));
                }
                else
                {
                    parts.Add(Loc.Get("trader_cost_gold", item.goldUse));
                }
            }

            // Jewel / extra item costs (itemParam: array of [itemId, itemUse] pairs)
            try
            {
                var ip = item.itemParam;
                if (ip != null && ip.Length > 0)
                {
                    int itemPoss = 0;
                    try { itemPoss = item.ItemPoss(); } catch { }

                    for (int i = 0; i < ip.Length; i++)
                    {
                        var entry = ip[i];
                        if (entry == null || entry.Length < 2) continue;
                        int itemId = entry[0];
                        int itemUse = entry[1];
                        if (itemId <= 0 || itemUse <= 0) continue;

                        string itemName = "";
                        try { itemName = ItemUtil.GetName(itemId) ?? ""; } catch { }
                        if (string.IsNullOrWhiteSpace(itemName)) itemName = $"item {itemId}";

                        if (verbose)
                            parts.Add(Loc.Get("trader_cost_item_poss", itemName, itemUse, itemPoss));
                        else
                            parts.Add(Loc.Get("trader_cost_item", itemName, itemUse));
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"itemParam read error: {ex.Message}");
            }

            // Material card cost
            if (item.costCardMrk > 0 && item.costCardUse > 0)
            {
                string costName = GetCardName(item.costCardMrk);
                if (verbose)
                {
                    int cardPoss = 0;
                    try { cardPoss = item.CardPossAll(); } catch { }
                    parts.Add(Loc.Get("trader_cost_card_poss", item.costCardUse, costName, cardPoss));
                }
                else
                {
                    parts.Add(Loc.Get("trader_cost_card", item.costCardUse, costName));
                }
            }

            if (verbose)
            {
                if (item.itemType == CardTraderInfoBase.Type.ChangeCard)
                {
                    var dest = item.destCards;
                    int count = dest?.Count ?? 0;
                    if (count > 0)
                        parts.Add(Loc.Get("trader_exchange_for", count));
                }

                if (item.stock > 0)
                    parts.Add(Loc.Get("trader_stock", item.stock));
                if (!string.IsNullOrWhiteSpace(item.notice))
                {
                    string clean = StripMarkup(item.notice.Trim());
                    if (!string.IsNullOrWhiteSpace(clean))
                        parts.Add(clean);
                }
            }

            return string.Join(", ", parts);
        }

        private static string StripMarkup(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            // Unity rich text: <color=...>, <b>, <size=...>, <quad=...>, etc.
            text = Regex.Replace(text, @"<[^>]*>", "");
            // Game markup: [Dragon/Fusion/Effect], [REQUIREMENT], [CONTINUOUS EFFECT], etc.
            text = Regex.Replace(text, @"\[[^\]]*\]", "");
            // Collapse runs of whitespace left behind by stripped tags
            text = Regex.Replace(text, @"\s+", " ");
            return text.Trim();
        }

        private string GetItemName(CardTraderInfoBase item)
        {
            if (!string.IsNullOrWhiteSpace(item.name))
                return StripMarkup(item.name.Trim());

            int itemId = item.itemId;
            var type = item.itemType;

            switch (type)
            {
                case CardTraderInfoBase.Type.Card:
                case CardTraderInfoBase.Type.ChangeCard:
                    if (itemId > 0)
                    {
                        string n = GetCardName(itemId);
                        if (!string.IsNullOrWhiteSpace(n) && n != Loc.Get("duel_unknown_card"))
                            return n;
                    }
                    break;

                case CardTraderInfoBase.Type.Item:
                case CardTraderInfoBase.Type.BoxChip:
                case CardTraderInfoBase.Type.RespectOrb:
                case CardTraderInfoBase.Type.SkillTicket:
                case CardTraderInfoBase.Type.ExItem:
                case CardTraderInfoBase.Type.Pack:
                case CardTraderInfoBase.Type.Skill:
                    if (itemId > 0)
                    {
                        string n = "";
                        try { n = ItemUtil.GetName(itemId) ?? ""; } catch { }
                        if (!string.IsNullOrWhiteSpace(n)) return StripMarkup(n);
                    }
                    return TypeFallbackName(type);

                case CardTraderInfoBase.Type.List:
                    return Loc.Get("trader_rarity_list", RarityLabel(item.rareId));

                case CardTraderInfoBase.Type.Chroniclizer:
                    return Loc.Get("trader_chroniclizer");

                case CardTraderInfoBase.Type.Process:
                    return Loc.Get("trader_process");

                case CardTraderInfoBase.Type.ChangeSkill:
                    return Loc.Get("trader_change_skill");

                case CardTraderInfoBase.Type.SoldOut:
                    return Loc.Get("trader_sold_out");
            }

            var dest = item.destCards;
            if (dest != null && dest.Count > 0)
                return GetCardName(dest[0]);

            return Loc.Get("duel_unknown_card");
        }

        private static string TypeFallbackName(CardTraderInfoBase.Type type) => type switch
        {
            CardTraderInfoBase.Type.SkillTicket => Loc.Get("trader_skill_ticket"),
            CardTraderInfoBase.Type.ExItem      => Loc.Get("trader_ex_item"),
            CardTraderInfoBase.Type.Pack        => Loc.Get("trader_pack"),
            CardTraderInfoBase.Type.BoxChip     => Loc.Get("trader_box_chip"),
            CardTraderInfoBase.Type.RespectOrb  => Loc.Get("trader_respect_orb"),
            _                                   => Loc.Get("duel_unknown_card"),
        };

        private static string RarityLabel(long rareId) => rareId switch
        {
            1 => "Rare",
            2 => "Super Rare",
            3 => "Ultra Rare",
            _ => $"Rarity {rareId}",
        };

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

        /// <summary>
        /// True when this trade item's card is a Rush Duel card. Only card-type
        /// offers carry a card mrk in itemId (Card and ChangeCard); other item
        /// types (packs, chips, tickets, gold) aren't cards. Uses the game's own
        /// DeckUtil.IsRushCard so the verdict matches the card frame the game
        /// draws for sighted players.
        /// </summary>
        private static bool IsRushCardItem(CardTraderInfoBase item)
        {
            try
            {
                var t = item.itemType;
                if (t != CardTraderInfoBase.Type.Card && t != CardTraderInfoBase.Type.ChangeCard)
                    return false;
                int mrk = item.itemId;
                if (mrk <= 0) return false;
                return Il2CppYgomGame.Deck.DeckUtil.IsRushCard(mrk);
            }
            catch { return false; }
        }

        /// <summary>
        /// Diagnostic: logs a ChangeCard trade's two sides so a tester log makes
        /// the exchange mechanic explicit — the cost side (the card + count you
        /// pay, plus any gold) and the destination side (destCards: the cards you
        /// can receive). This resolves whether the CardTransfer "one of the
        /// following cards" screen is choosing what you give or what you get.
        /// </summary>
        private static void LogChangeCardBreakdown(CardTraderInfoBase item)
        {
            try
            {
                int costMrk = 0, costUse = 0, gold = 0, itemId = 0, num = 0, stock = 0;
                try { costMrk = item.costCardMrk; } catch { }
                try { costUse = item.costCardUse; } catch { }
                try { gold = item.goldUse; } catch { }
                try { itemId = item.itemId; } catch { }
                try { num = item.num; } catch { }
                try { stock = item.stock; } catch { }

                // The game's own tile strings — what a sighted player actually
                // reads for this offer (vs. our itemId-derived card name).
                string gName = "", gNotice = "";
                try { gName = item.name ?? ""; } catch { }
                try { gNotice = item.notice ?? ""; } catch { }

                var dest = item.destCards;
                int destCount = 0; try { destCount = dest?.Count ?? 0; } catch { }
                string destNames = "";
                try
                {
                    int limit = Math.Min(destCount, 20);
                    for (int i = 0; i < limit; i++)
                        destNames += $"{dest[i]}({GetCardName(dest[i])}) ";
                }
                catch { }

                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"[ChangeCard breakdown] itemId={itemId}({GetCardName(itemId)}) "
                    + $"gameName='{gName}' notice='{gNotice}' num={num} stock={stock} "
                    + $"costCardMrk={costMrk}({GetCardName(costMrk)}) costCardUse={costUse} goldUse={gold} "
                    + $"destCards[{destCount}]: {destNames.Trim()}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"LogChangeCardBreakdown error: {ex.Message}");
            }
        }

        #endregion
    }
}
