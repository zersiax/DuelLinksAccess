using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using Il2CppYgomGame.CardList;
using Il2CppYgomGame.Deck;
using Il2CppYgomGame.Single;
using Il2CppYgomGame.Utility;

namespace DuelLinksAccess
{
    /// <summary>
    /// Keyboard-driven ticket exchange accessibility handler.
    /// Provides card browsing, selection, and exchange confirmation
    /// for CardGetterViewController (ticket exchange, card trader, etc.).
    /// </summary>
    public class TicketExchangeHandler
    {
        #region Fields

        private CardGetterViewController _vc;
        private bool _wasActive;
        private string _lastVcGoName = "";

        private int _focusIndex;

        // Card list from the exchange — stored as card IDs (mrk values)
        private readonly List<int> _cardIds = new();

        // Parallel to _cardIds: the Kirarity (rarity + kira treatment) of each
        // card, kept strictly in lockstep. Selecting a card MUST feed the game
        // the exact CardAndRarity it handed us — both Mrk AND Kirarity.
        // Reconstructing from the mrk alone leaves Kirarity=0; the exchange then
        // submits a malformed card and the game hard-resets to the title screen
        // ("An error has occurred") right after TRADE is confirmed.
        // cardExchangeList carries only mrk (no rarity), so entries from that
        // source store 0.
        private readonly List<long> _cardKirarities = new();

        // Cooldown
        private float _operationCooldown;
        private const float OperationCooldownTime = 0.5f;

        // Delayed initial scan
        private float _initialScanDelay;
        private bool _initialScanDone;
        private int _initialScanAttempts;

        // Post-exchange stale-list guard. DecideClicked()/exchangeButton open
        // a confirm dialog that DialogHandler drives entirely on its own —
        // this handler has no callback for when it resolves. Without this,
        // a just-traded card stays in _cardIds at its old index; selecting
        // it again feeds an already-consumed card into native game calls.
        // (2026-07-22 tester log: traded Needle Sunfish, the list still
        // showed it at the same position afterward, re-selecting it
        // preceded the app resetting to the Logo/Title screens.)
        private bool _awaitingExchangeResult;
        private bool _exchangeDialogSeen;

        // Post-filter / search refresh guard. When user opens filter or search
        // (F or Ctrl+S), we watch for the dialog to open and close, then refilter
        // and refresh the displayed card list automatically.
        private bool _awaitingFilterResult;
        private bool _filterDialogSeen;

        #endregion

        #region Properties

        /// <summary>
        /// Whether the handler is actively managing a ticket exchange screen.
        /// </summary>
        public bool IsActive { get; private set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Called each frame from Main.UpdateHandlers().
        /// </summary>
        public void Update()
        {
            if (_operationCooldown > 0f)
                _operationCooldown -= Time.deltaTime;

            // CardGetterViewController can appear on various screens.
            // We detect it by trying to cast the top content VC.
            var vc = TryGetCardGetterVC();
            if (vc == null)
            {
                if (_wasActive) Deactivate();
                return;
            }

            string goName = vc.gameObject?.name ?? "";
            if (!_wasActive || goName != _lastVcGoName)
            {
                Activate(vc, goName);
            }

            if (!_initialScanDone)
            {
                _initialScanDelay -= Time.deltaTime;
                if (_initialScanDelay <= 0f)
                    DoInitialScan();
                return;
            }

            if (_awaitingExchangeResult)
                CheckPostExchangeRefresh();

            if (_awaitingFilterResult)
                CheckPostFilterRefresh();

            ProcessInput();
        }

        #endregion

        #region Lifecycle

        private void Activate(CardGetterViewController vc, string goName)
        {
            _vc = vc;
            _lastVcGoName = goName;
            _wasActive = true;
            IsActive = true;
            _focusIndex = 0;
            _initialScanDone = false;
            _initialScanDelay = 1.0f;
            _initialScanAttempts = 0;
            _awaitingExchangeResult = false;
            _exchangeDialogSeen = false;
            _awaitingFilterResult = false;
            _filterDialogSeen = false;

            DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                $"Activated (mode={GetModeName()}), waiting for initial scan...");
        }

        /// <summary>
        /// Watches for the filter or search dialog to appear and then close,
        /// then forces a fresh refilter and card list reload.
        /// </summary>
        private void CheckPostFilterRefresh()
        {
            if (GameStateTracker.CurrentScreen == GameScreen.Dialog)
            {
                _filterDialogSeen = true;
                return;
            }

            // Dialog hasn't appeared yet (still opening) — keep waiting
            if (!_filterDialogSeen) return;

            _awaitingFilterResult = false;
            _filterDialogSeen = false;

            MelonLogger.Msg("[TicketExchange] Filter/Search dialog closed, updating filtered cards...");
            try { _vc?.refilter(); } catch { }
            RefreshCardList();

            _focusIndex = 0;
            if (_cardIds.Count > 0)
            {
                ScreenReader.Say($"搜索与筛选已更新，共有 {_cardIds.Count} 张卡片。");
                AnnounceCurrentCard(queued: true);
            }
            else
            {
                ScreenReader.Say("搜索与筛选已更新，没有找到符合条件的卡片。");
            }
        }

        /// <summary>
        /// Watches for the confirm dialog opened by ConfirmExchange() to
        /// appear and then close, then forces a fresh read of the exchange
        /// list. A card successfully traded away must never remain
        /// selectable at its old cached index.
        /// </summary>
        private void CheckPostExchangeRefresh()
        {
            if (GameStateTracker.CurrentScreen == GameScreen.Dialog)
            {
                _exchangeDialogSeen = true;
                return;
            }

            // Dialog hasn't appeared yet (still opening) — keep waiting
            // rather than refreshing prematurely and missing the real close.
            if (!_exchangeDialogSeen) return;

            _awaitingExchangeResult = false;
            _exchangeDialogSeen = false;

            int previousMrk = (_cardIds.Count > 0 && _focusIndex >= 0 && _focusIndex < _cardIds.Count)
                ? _cardIds[_focusIndex] : -1;

            RefreshCardList();

            if (_cardIds.Count == 0)
            {
                _focusIndex = 0;
                ScreenReader.Say(Loc.Get("ticket_no_cards"));
                return;
            }

            if (_focusIndex >= _cardIds.Count)
                _focusIndex = _cardIds.Count - 1;

            bool sameCard = previousMrk >= 0 && _cardIds[_focusIndex] == previousMrk;
            if (!sameCard)
                AnnounceCurrentCard(queued: true);

            DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                $"Post-exchange refresh: {_cardIds.Count} cards, focus now " +
                $"{CardFormatter.GetName(_cardIds[_focusIndex])}");
        }

        private void DoInitialScan()
        {
            _initialScanAttempts++;
            RefreshCardList();

            DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                $"Scan attempt {_initialScanAttempts}: {_cardIds.Count} cards, mode={GetModeName()}");

            if (_cardIds.Count == 0 && _initialScanAttempts < 8)
            {
                _initialScanDelay = 0.5f;
                return;
            }

            _initialScanDone = true;

            string modeName = GetModeName();
            if (_cardIds.Count > 0)
            {
                string title = GetScreenTitle();
                ScreenReader.Say($"{title}。共有 {_cardIds.Count} 张可选卡片。左右方向键浏览，回车键选择。");
                AnnounceCurrentCard(queued: true);
            }
            else
            {
                ScreenReader.Say(Loc.Get("ticket_no_cards"));
            }
        }

        private void Deactivate()
        {
            _wasActive = false;
            IsActive = false;
            _vc = null;
            _cardIds.Clear();
            _cardKirarities.Clear();
            _awaitingExchangeResult = false;
            _exchangeDialogSeen = false;

            DebugLogger.Log(LogCategory.Handler, "TicketExchange", "Deactivated");
        }

        #endregion

        #region VC Detection

        /// <summary>
        /// Attempts to find CardGetterViewController as the top content VC.
        /// </summary>
        private CardGetterViewController TryGetCardGetterVC()
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

                return topVc.TryCast<CardGetterViewController>();
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                    $"TryGetCardGetterVC error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Data Access

        /// <summary>
        /// Refreshes the available card list from the VC.
        /// </summary>
        private void RefreshCardList()
        {
            _cardIds.Clear();
            _cardKirarities.Clear();

            try
            {
                if (_vc == null) return;

                // Priority 1: Read from mainChoiceData (CardGetterChoiceData).
                // This is the active filtered/sorted list managed by the game's filter engine.
                var choiceData = _vc.mainChoiceData;
                if (choiceData != null)
                {
                    int listNum = choiceData.GetListNum();
                    if (listNum > 0)
                    {
                        for (int i = 0; i < listNum; i++)
                        {
                            var car = choiceData.GetListItem(i);
                            _cardIds.Add(car.Mrk);
                            _cardKirarities.Add(car.Kirarity);
                        }

                        MelonLogger.Msg($"[TicketExchange] Loaded {_cardIds.Count} cards from mainChoiceData (listNum={listNum})");
                        return;
                    }
                    else if (choiceData.filterCarList != null || choiceData.filterMrkList != null)
                    {
                        // Filter was applied and produced 0 results
                        MelonLogger.Msg("[TicketExchange] mainChoiceData reports 0 cards after filter");
                        return;
                    }
                }

                // Priority 2: Try choiceList (List<CardAndRarity> on VC)
                var choiceList = _vc.choiceList;
                if (choiceList != null && choiceList.Count > 0)
                {
                    for (int i = 0; i < choiceList.Count; i++)
                    {
                        var car = choiceList[i];
                        _cardIds.Add(car.Mrk);
                        _cardKirarities.Add(car.Kirarity);
                    }

                    MelonLogger.Msg($"[TicketExchange] Loaded {_cardIds.Count} cards from choiceList");
                    return;
                }

                // Priority 3: Try exchangeList (List<CardAndRarity>)
                var carList = _vc.exchangeList;
                if (carList != null && carList.Count > 0)
                {
                    for (int i = 0; i < carList.Count; i++)
                    {
                        var car = carList[i];
                        _cardIds.Add(car.Mrk);
                        _cardKirarities.Add(car.Kirarity);
                    }

                    MelonLogger.Msg($"[TicketExchange] Loaded {_cardIds.Count} cards from exchangeList");
                    return;
                }

                // Priority 4: Fallback to cardExchangeList (raw unfiltered List<int> of card IDs)
                var exchangeList = _vc.cardExchangeList;
                if (exchangeList != null && exchangeList.Count > 0)
                {
                    for (int i = 0; i < exchangeList.Count; i++)
                    {
                        _cardIds.Add(exchangeList[i]);
                        _cardKirarities.Add(0L);
                    }

                    MelonLogger.Msg($"[TicketExchange] Loaded {_cardIds.Count} cards from cardExchangeList fallback");
                    return;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[TicketExchange] RefreshCardList error: {ex.Message}");
            }
        }

        #endregion

        #region Input Processing

        private void ProcessInput()
        {
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

            // Home / End
            if (InputManager.TryConsumeKeyDown(KeyCode.Home))
            {
                if (_cardIds.Count > 0)
                {
                    _focusIndex = 0;
                    AnnounceCurrentCard();
                }
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.End))
            {
                if (_cardIds.Count > 0)
                {
                    _focusIndex = _cardIds.Count - 1;
                    AnnounceCurrentCard();
                }
                return;
            }

            // Enter — select/add current card
            if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter))
            {
                SelectCurrentCard();
                return;
            }

            // C or I — verbose card reading
            if (InputManager.TryConsumeKeyDown(KeyCode.C) || InputManager.TryConsumeKeyDown(KeyCode.I))
            {
                AnnounceCurrentCard(verbose: true);
                return;
            }

            // Space — confirm exchange (DecideClicked)
            if (InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                ConfirmExchange();
                return;
            }

            // F — open filter
            if (InputManager.TryConsumeKeyDown(KeyCode.F))
            {
                OpenFilter();
                return;
            }

            // Ctrl+S — open search
            bool isCtrlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (isCtrlHeld && InputManager.TryConsumeKeyDown(KeyCode.S))
            {
                OpenSearch();
                return;
            }

            // G — announce ticket/exchange info
            if (InputManager.TryConsumeKeyDown(KeyCode.G))
            {
                AnnounceExchangeInfo();
                return;
            }

            // Tab — rescan card list
            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                try { _vc?.refilter(); } catch { }
                RefreshCardList();
                string title = GetScreenTitle();
                ScreenReader.Say($"{title}。共有 {_cardIds.Count} 张可选卡片。按F键筛选，Ctrl+S搜索，左右键浏览，回车选择。");
                if (_cardIds.Count > 0)
                {
                    if (_focusIndex >= _cardIds.Count) _focusIndex = 0;
                    AnnounceCurrentCard(queued: true);
                }
                return;
            }

            // Escape / Backspace — go back
            if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.Backspace))
            {
                GoBack();
                return;
            }
        }

        #endregion

        #region Navigation

        private void NavigateBy(int delta)
        {
            if (_cardIds.Count == 0)
            {
                ScreenReader.Say(Loc.Get("ticket_no_cards"));
                return;
            }

            _focusIndex += delta;
            if (_focusIndex < 0) _focusIndex = 0;
            if (_focusIndex >= _cardIds.Count) _focusIndex = _cardIds.Count - 1;

            AnnounceCurrentCard();
        }

        #endregion

        #region Card Operations

        private void SelectCurrentCard()
        {
            if (_operationCooldown > 0f) return;
            if (_cardIds.Count == 0 || _focusIndex < 0 || _focusIndex >= _cardIds.Count) return;

            int mrk = _cardIds[_focusIndex];
            long kirarity = (_focusIndex < _cardKirarities.Count) ? _cardKirarities[_focusIndex] : 0L;
            string name = CardFormatter.GetName(mrk);

            try
            {
                // Feed the game the exact card it gave us, Kirarity included.
                // A card built from the mrk alone has Kirarity=0, which the
                // exchange later rejects and the game hard-resets to title.
                var car = new CardAndRarity(mrk, kirarity);

                // 1. If not addible, check if it's because this card is already selected (toggle deselect)
                // or because reward slot is already full (clear previous selection to replace)
                if (!_vc.isAddible(car))
                {
                    bool removed = false;
                    try { removed = _vc.delFromRewardView(car); } catch { }
                    if (removed)
                    {
                        try { _vc.setDecideButton(); } catch { }
                        try { _vc.updateRewardView(); } catch { }
                        ScreenReader.Say(Loc.Get("ticket_deselected", name));
                        _operationCooldown = OperationCooldownTime;
                        return;
                    }

                    // Slot may be full with another card — clear previous selection to replace
                    try { _vc.ClearClicked(); } catch { }
                }

                // 2. Add card to reward view
                if (_vc.isAddible(car))
                {
                    bool result = _vc.addToRewardView(car);
                    if (result)
                    {
                        // Explicitly update decide button and reward view so the game
                        // updates decideButton.interactable = true!
                        try { _vc.setDecideButton(); } catch { }
                        try { _vc.updateRewardView(); } catch { }

                        ScreenReader.Say(Loc.Get("ticket_selected", name));
                        DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                            $"Selected card mrk={mrk} kirarity={kirarity} ({name})");
                    }
                    else
                    {
                        ScreenReader.Say(Loc.Get("ticket_activate_error"));
                    }
                }
                else
                {
                    ScreenReader.Say(Loc.Get("ticket_activate_error"));
                }

                _operationCooldown = OperationCooldownTime;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                    $"SelectCurrentCard error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        private void ConfirmExchange()
        {
            if (_operationCooldown > 0f) return;

            try
            {
                // Synchronize decideButton state before evaluating
                try { _vc?.setDecideButton(); } catch { }

                var decideBtn = _vc?.decideButton;
                var exchangeBtn = _vc?.exchangeButton;

                bool decideActive = decideBtn != null && (decideBtn.gameObject?.activeInHierarchy ?? true);
                bool exchangeActive = exchangeBtn != null && (exchangeBtn.gameObject?.activeInHierarchy ?? true);

                bool decideInteractable = decideActive && decideBtn.interactable;
                bool exchangeInteractable = exchangeActive && exchangeBtn.interactable;

                // Also inspect if selList or rewardView actually has selected cards
                bool hasSelectedCards = false;
                try
                {
                    if (_vc?.selList != null && _vc.selList.Count > 0)
                        hasSelectedCards = true;
                }
                catch { }

                var action = TicketExchangePolicy.ChooseAction(
                    decideActive, decideInteractable || (decideActive && hasSelectedCards),
                    exchangeActive, exchangeInteractable);

                switch (action)
                {
                    case TicketExchangeAction.ConfirmViaDecide:
                        _vc.DecideClicked();
                        DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                            "Confirmed via decideButton (DecideClicked)");
                        _awaitingExchangeResult = true;
                        _exchangeDialogSeen = false;
                        break;

                    case TicketExchangeAction.InvokeExchangeButton:
                        exchangeBtn.onClick.Invoke();
                        DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                            "Confirmed via exchangeButton");
                        _awaitingExchangeResult = true;
                        _exchangeDialogSeen = false;
                        break;

                    default: // Reject
                        DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                            $"Confirm rejected: decide(present={decideActive}, " +
                            $"interactable={decideInteractable}, hasCards={hasSelectedCards}) " +
                            $"exchange(present={exchangeActive}, " +
                            $"interactable={exchangeInteractable})");
                        ScreenReader.Say(Loc.Get("ticket_nothing_to_exchange"));
                        break;
                }

                _operationCooldown = OperationCooldownTime;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                    $"ConfirmExchange error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        /// <summary>
        /// Dynamically introspects the currency or exchange medium name from the VC hierarchy.
        /// (Soft-coded: looks for title Text, item paths, or falls back gracefully).
        /// <summary>
        /// Derives the clean user-facing title of the active CardGetter screen.
        /// </summary>
        public string GetScreenTitle()
        {
            string raw = null;
            try { raw = _vc?.TitleLabel(); } catch { }
            return TicketExchangePolicy.ResolveTitle(raw, GetModeName());
        }

        /// <summary>
        /// Dynamically introspects the currency or exchange medium name from the VC hierarchy.
        /// (Soft-coded: looks for title Text, item paths, or falls back gracefully).
        /// </summary>
        public string GetCurrencyName()
        {
            if (_vc == null) return "兑换券";

            try
            {
                string raw = null;
                try { raw = _vc.TitleLabel(); } catch { }
                string mode = GetModeName();
                string path = null;
                try { path = _vc.needItemPath; } catch { }

                string candidate = TicketExchangePolicy.ResolveCurrencyName(raw, mode, path);
                if (candidate != "兑换券") return candidate;

                // Check all Text components on _vc to find screen header / title
                var texts = _vc.GetComponentsInChildren<UnityEngine.UI.Text>(true);
                if (texts != null)
                {
                    foreach (var t in texts)
                    {
                        if (t == null) continue;
                        string txt = t.text?.Trim();
                        if (string.IsNullOrEmpty(txt)) continue;

                        string resolved = TicketExchangePolicy.ResolveCurrencyName(txt, mode, path);
                        if (resolved != "兑换券") return resolved;
                    }
                }

                return candidate;
            }
            catch { }

            return "兑换券";
        }

        /// <summary>
        /// Reads the user's current currency balance on this exchange screen.
        /// </summary>
        public string GetCurrencyBalance()
        {
            if (_vc == null) return null;

            try
            {
                var itemNumText = _vc.itemNum;
                if (itemNumText != null && !string.IsNullOrEmpty(itemNumText.text?.Trim()))
                {
                    return itemNumText.text.Trim();
                }

                int num = _vc.getItemNum();
                if (num >= 0)
                {
                    return num.ToString();
                }

                if (_vc.needItemId > 0)
                {
                    int n = Il2CppYgomGame.Utility.ItemUtil.GetNum(_vc.needItemId);
                    if (n >= 0) return n.ToString();
                }

                if (_vc.itemId > 0)
                {
                    int n = Il2CppYgomGame.Utility.ItemUtil.GetNum(_vc.itemId);
                    if (n >= 0) return n.ToString();
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// Reads the price / cost for the currently focused card.
        /// </summary>
        public string GetCurrentCardCostText()
        {
            if (_vc == null) return null;

            string curName = GetCurrencyName();
            string balance = GetCurrencyBalance();
            string balanceSuffix = !string.IsNullOrEmpty(balance) ? $" (持有 {balance})" : "";

            try
            {
                // Card Trader mode: use official checkCost and createInfoBase
                if (IsTraderMode())
                {
                    int currentMrk = (_focusIndex >= 0 && _focusIndex < _cardIds.Count) ? _cardIds[_focusIndex] : -1;
                    if (currentMrk > 0)
                    {
                        bool canTrade = false;
                        try { canTrade = _vc.checkCost(currentMrk); } catch { }
                        string tradeStatus = canTrade ? "可兑换" : "无法兑换（材料或金币不足）";

                        var costParts = new List<string>();
                        try
                        {
                            var info = CardGetterViewController.createInfoBase(currentMrk);
                            if (info != null)
                            {
                                if (info.goldUse > 0)
                                    costParts.Add($"{info.goldUse} 金币");

                                var ip = info.itemParam;
                                if (ip != null)
                                {
                                    for (int i = 0; i < ip.Length; i++)
                                    {
                                        var entry = ip[i];
                                        if (entry != null && entry.Length >= 2 && entry[0] > 0)
                                        {
                                            int iid = entry[0];
                                            int count = entry[1];
                                            string iName = "";
                                            try { iName = ItemUtil.GetName(iid) ?? ""; } catch { }
                                            if (string.IsNullOrWhiteSpace(iName)) iName = $"道具 {iid}";
                                            costParts.Add($"{count} 个 {iName}");
                                        }
                                    }
                                }
                            }
                        }
                        catch { }

                        if (costParts.Count > 0)
                            return $"{tradeStatus}，消耗 {string.Join("、", costParts)}";

                        return tradeStatus;
                    }
                }

                // 1. Check choiceView for CardGetterItem at _focusIndex
                var choiceView = _vc.choiceView;
                if (choiceView != null)
                {
                    var items = choiceView.GetComponentsInChildren<CardGetterItem>(true);
                    if (items != null && _focusIndex >= 0 && _focusIndex < items.Length)
                    {
                        var item = items[_focusIndex];
                        if (item != null && item.costText != null && !string.IsNullOrEmpty(item.costText.text?.Trim()))
                        {
                            string cost = item.costText.text.Trim();
                            if (cost != "0" && cost != "")
                            {
                                return $"价格：{cost} {curName}{balanceSuffix}";
                            }
                        }
                    }
                }

                // 2. Check costText on VC
                var costText = _vc.costText;
                if (costText != null && !string.IsNullOrEmpty(costText.text?.Trim()))
                {
                    string cost = costText.text.Trim();
                    if (cost != "0" && cost != "")
                    {
                        return $"价格：{cost} {curName}{balanceSuffix}";
                    }
                }

                // 3. Check rewardItem
                var rewardItem = _vc.rewardItem;
                if (rewardItem != null)
                {
                    var rTexts = rewardItem.GetComponentsInChildren<UnityEngine.UI.Text>(true);
                    if (rTexts != null)
                    {
                        foreach (var rt in rTexts)
                        {
                            if (rt == null) continue;
                            string txt = rt.text?.Trim();
                            if (int.TryParse(txt, out int val) && val > 0)
                            {
                                return $"价格：{val} {curName}{balanceSuffix}";
                            }
                        }
                    }
                }
            }
            catch { }

            // 4. Mode-specific cost fallbacks:
            // BOX Chips exchange mode (always 100 Box Chips per card)
            if (curName.Contains("芯片") || curName.Contains("Chip") || curName.Contains("BOX"))
            {
                return $"消耗 100 {curName}{balanceSuffix}";
            }

            // Ticket exchange mode (1 ticket per card)
            if (curName.Contains("券") || curName.Contains("Ticket"))
            {
                return $"消耗 1 张 {curName}{balanceSuffix}";
            }

            // Generic fallback with balance if available
            if (!string.IsNullOrEmpty(balance))
            {
                return $"兑换媒介：{curName}{balanceSuffix}";
            }

            return null;
        }

        private void AnnounceExchangeInfo()
        {
            try
            {
                // Card Trader mode: report gold and required orb holdings
                if (IsTraderMode())
                {
                    var balances = new List<string>();

                    // 1. Gold balance
                    int gold = -1;
                    try { gold = ItemUtil.GetNum(ItemUtil.ItemIdType.Gold); } catch { }
                    if (gold < 0)
                    {
                        try { gold = ItemUtil.GetNum(2); } catch { }
                    }
                    if (gold >= 0)
                    {
                        balances.Add($"金币: {gold}");
                    }

                    // 2. Focused card required items possession
                    int currentMrk = (_focusIndex >= 0 && _focusIndex < _cardIds.Count) ? _cardIds[_focusIndex] : -1;
                    if (currentMrk > 0)
                    {
                        try
                        {
                            var info = CardGetterViewController.createInfoBase(currentMrk);
                            if (info?.itemParam != null)
                            {
                                for (int i = 0; i < info.itemParam.Length; i++)
                                {
                                    var entry = info.itemParam[i];
                                    if (entry != null && entry.Length >= 2 && entry[0] > 0)
                                    {
                                        int iid = entry[0];
                                        string iName = "";
                                        try { iName = ItemUtil.GetName(iid) ?? ""; } catch { }
                                        if (string.IsNullOrWhiteSpace(iName)) iName = $"道具 {iid}";
                                        int num = 0;
                                        try { num = ItemUtil.GetNum(iid); } catch { }
                                        string s = $"{iName}: {num}";
                                        if (!balances.Contains(s)) balances.Add(s);
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (balances.Count > 0)
                    {
                        ScreenReader.Say($"当前持有：{string.Join(", ", balances)}");
                        return;
                    }
                }

                string curName = GetCurrencyName();
                string balance = GetCurrencyBalance();

                if (!string.IsNullOrEmpty(balance))
                {
                    if (balance == "0" && (curName.Contains("券") || curName.Contains("Ticket")))
                    {
                        ScreenReader.Say($"当前正在使用兑换券挑选卡片（可兑换 1 张）。按回车键选择，空格键确认兑换。");
                        return;
                    }
                    ScreenReader.Say($"当前持有：{balance} {curName}");
                    return;
                }

                // Try to read exchange info text
                string exInfo = _vc?.cardExchangeInfo;
                if (!string.IsNullOrEmpty(exInfo))
                {
                    ScreenReader.Say(exInfo);
                    return;
                }

                string title = GetScreenTitle();
                ScreenReader.Say($"{title}。共有 {_cardIds.Count} 张可选卡片。当前兑换媒介：{curName}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                    $"AnnounceExchangeInfo error: {ex.Message}");
            }
        }

        private void OpenFilter()
        {
            try
            {
                if (_vc == null) return;
                if (_vc.filterAndSort == null)
                {
                    try { _vc.createFilter(); } catch { }
                }

                var fs = _vc.filterAndSort;
                if (fs == null)
                {
                    fs = _vc.gameObject?.GetComponentInChildren<Il2CppYgomGame.Deck.FilterAndSort>(true);
                }

                if (fs != null)
                {
                    _awaitingFilterResult = true;
                    _filterDialogSeen = false;
                    fs.FilterSettingClicked(true);
                    ScreenReader.Say("已打开卡片详细筛选");
                    MelonLogger.Msg("[TicketExchange] OpenFilter: FilterSettingClicked(true) invoked");
                }
                else
                {
                    ScreenReader.Say("未找到筛选菜单");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[TicketExchange] OpenFilter error: {ex.Message}");
                ScreenReader.Say("打开筛选失败");
            }
        }

        private void OpenSearch()
        {
            try
            {
                if (_vc == null) return;
                if (_vc.filterAndSort == null)
                {
                    try { _vc.createFilter(); } catch { }
                }

                var fs = _vc.filterAndSort;
                if (fs == null)
                {
                    fs = _vc.gameObject?.GetComponentInChildren<Il2CppYgomGame.Deck.FilterAndSort>(true);
                }

                if (fs != null)
                {
                    _awaitingFilterResult = true;
                    _filterDialogSeen = false;
                    fs.SearchSettingClicked(true);
                    ScreenReader.Say("已打开卡片文字搜索");
                    MelonLogger.Msg("[TicketExchange] OpenSearch: SearchSettingClicked(true) invoked");
                }
                else
                {
                    ScreenReader.Say("未找到搜索菜单");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[TicketExchange] OpenSearch error: {ex.Message}");
                ScreenReader.Say("打开搜索失败");
            }
        }

        private bool IsTraderMode()
        {
            try
            {
                if (_vc == null) return false;
                if (_vc.isTraderMode()) return true;
                if (_vc.Mode == CardGetterViewController.MODE.CardTrader) return true;
                string m = GetModeName();
                if (m.IndexOf("Trader", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch { }
            return false;
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

                var topVc = contentMgr.GetStackTopViewController();
                topVc?.SendBack();
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                    $"GoBack error: {ex.Message}");
            }
        }

        #endregion

        #region Announcements

        private void AnnounceCurrentCard(bool verbose = false, bool queued = false)
        {
            if (_cardIds.Count == 0)
            {
                ScreenReader.Say(Loc.Get("ticket_no_cards"));
                return;
            }

            if (_focusIndex < 0 || _focusIndex >= _cardIds.Count)
                _focusIndex = 0;

            int mrk = _cardIds[_focusIndex];
            int pos = _focusIndex + 1;
            int total = _cardIds.Count;

            string cardText = verbose ? CardFormatter.FormatVerbose(mrk) : CardFormatter.FormatCompact(mrk);
            string costText = GetCurrentCardCostText();

            string announcement;
            if (!string.IsNullOrEmpty(costText))
            {
                announcement = $"{cardText}，{costText}，第 {pos} 张，共 {total} 张";
            }
            else
            {
                announcement = Loc.Get("ticket_card_position", pos, total, cardText);
            }

            if (queued)
                ScreenReader.SayQueued(announcement);
            else
                ScreenReader.Say(announcement);
        }

        #endregion

        #region Utilities

        private string GetModeName()
        {
            try
            {
                if (_vc == null) return "Unknown";
                return _vc.Mode.ToString();
            }
            catch { return "Unknown"; }
        }

        #endregion
    }
}
