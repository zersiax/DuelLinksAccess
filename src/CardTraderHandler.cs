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
        private int _focusIndex;

        private float _operationCooldown;
        private const float OperationCooldownTime = 0.5f;

        private float _scanDelay;
        private bool _scanDone;
        private int _scanAttempts;

        // Two-press confirmation for resource-spending trades.
        private int _pendingTradeIndex = -1;
        private float _pendingTradeCooldown;
        private const float PendingTradeWindow = 5.0f;

        private bool _cellsDumped;

        // Delayed exchangeButton click: cell click sets _currentItem and enables
        // the button, but the button isn't interactable in the same frame, so we
        // defer the click by a short delay (game ticks several frames first).
        private float _pendingExchangeClickDelay;
        // The (mrk, rare) we expected the trader to be selecting when scheduling
        // the exchangeButton click. If currentItem drifts to something else
        // before the click fires, we re-assert by calling OnClickCard again so
        // the trade fires for the intended card, not whatever drifted in.
        private int _pendingExpectedItemId;
        private long _pendingExpectedRareId;
        private string _pendingExpectedItemName = "";

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
            if (_pendingTradeCooldown > 0f)
            {
                _pendingTradeCooldown -= Time.deltaTime;
                if (_pendingTradeCooldown <= 0f) _pendingTradeIndex = -1;
            }
            if (_pendingExchangeClickDelay > 0f)
            {
                _pendingExchangeClickDelay -= Time.deltaTime;
                if (_pendingExchangeClickDelay <= 0f && _vc != null)
                {
                    // Verify the trader still has our intended item selected. The
                    // scroll-snap can replace currentItem between our cell click
                    // and the exchangeButton click — aborting prevents trading
                    // for the wrong card.
                    bool drift = true;
                    int actualId = -1;
                    string actualName = "?";
                    try
                    {
                        var nowItem = _vc.getCurrentItem();
                        if (nowItem != null)
                        {
                            actualId = nowItem.itemId;
                            actualName = GetItemName(nowItem);
                            drift = (actualId != _pendingExpectedItemId);
                        }
                    }
                    catch { }

                    DebugLogger.Log(LogCategory.Handler, "CardTrader",
                        $"  pre-exchange: expected itemId={_pendingExpectedItemId} ({_pendingExpectedItemName}), "
                        + $"actual itemId={actualId} ({actualName}), drift={drift}");

                    if (drift)
                    {
                        // Announce the drift so the user knows the dialog about
                        // to open is for a different card than they selected.
                        ScreenReader.Say(Loc.Get("trader_drift_warn", _pendingExpectedItemName, actualName));
                    }

                    var exBtn = _vc.exchangeButton;
                    if (exBtn != null && exBtn.gameObject?.activeInHierarchy == true)
                    {
                        // Force-enable so a non-interactable visual state doesn't
                        // gate the click. The actual trade gate is the confirm
                        // dialog the click opens, where the user can press NO.
                        exBtn.interactable = true;
                        DebugLogger.Log(LogCategory.Handler, "CardTrader",
                            $"  delayed exchangeButton click (force-enabled): drift={drift}");
                        ClickGameObject(exBtn.gameObject);
                    }
                    else
                    {
                        DebugLogger.Log(LogCategory.Handler, "CardTrader",
                            "  delayed exchangeButton click skipped (button missing/inactive)");
                    }

                    _pendingExpectedItemId = 0;
                    _pendingExpectedRareId = 0;
                    _pendingExpectedItemName = "";
                }
            }

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
            _pendingTradeIndex = -1;
            _pendingTradeCooldown = 0f;
            _cellsDumped = false;
            _pendingExchangeClickDelay = 0f;
            _pendingExpectedItemId = 0;
            _pendingExpectedRareId = 0;
            _pendingExpectedItemName = "";

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

            if (InputManager.TryConsumeKeyDown(KeyCode.B))
            {
                GotoConversionCatalog();
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

            // Moving the focus cancels a pending trade — no surprise purchases.
            _pendingTradeIndex = -1;
            _pendingTradeCooldown = 0f;

            _focusIndex = Math.Max(0, Math.Min(_items.Count - 1, _focusIndex + delta));
            AnnounceCurrentItem();
        }

        #endregion

        #region Item Operations

        private void SelectCurrentItem()
        {
            if (_operationCooldown > 0f || _vc == null || _items.Count == 0) return;

            var item = _items[_focusIndex];
            string name = GetItemName(item);
            var type = item.itemType;

            DebugLogger.Log(LogCategory.Handler, "CardTrader",
                $"SelectItem: type={type}, name={name}, itemId={item.itemId}, "
                + $"gId={item.gId}, rareId={item.rareId}");

            try
            {
                string verb;
                switch (type)
                {
                    // Two-press confirmation flow for resource-spending trades:
                    // 1st Enter: arm (set _currentItem, announce confirm prompt)
                    // 2nd Enter (within window): commit (call OnCardExchange — the
                    //   actual trade execution). The game's OpenCardConfirmDialog
                    //   path appears decorative — OnClickedYes does NOT execute
                    //   the trade when the dialog was opened directly, so we
                    //   bypass it entirely.
                    case CardTraderInfoBase.Type.Card:
                    case CardTraderInfoBase.Type.Item:
                    case CardTraderInfoBase.Type.BoxChip:
                    case CardTraderInfoBase.Type.RespectOrb:
                    case CardTraderInfoBase.Type.SkillTicket:
                    case CardTraderInfoBase.Type.ExItem:
                    case CardTraderInfoBase.Type.Pack:
                    case CardTraderInfoBase.Type.Skill:
                    case CardTraderInfoBase.Type.Chroniclizer:
                    case CardTraderInfoBase.Type.Process:
                    case CardTraderInfoBase.Type.ChangeSkill:
                        if (_pendingTradeIndex == _focusIndex && _pendingTradeCooldown > 0f)
                        {
                            // Second press within window — commit.
                            // SetCurrent first to position cells so the cell at sibling
                            // _focusIndex actually holds our intended item's (mrk, rare).
                            var mgr = _vc.CardTrader2DMgr;
                            if (mgr != null)
                                mgr.SetCurrent(_focusIndex);

                            GameObject cellGo = FindCellGameObject(_focusIndex);
                            DebugLogger.Log(LogCategory.Handler, "CardTrader",
                                $"  intended itemId={item.itemId} ({name}); clicking cell GO at index {_focusIndex}: {cellGo?.name ?? "(null)"}");

                            if (cellGo != null)
                            {
                                ClickGameObject(cellGo);

                                // Read back what the trader actually selected — useful
                                // diagnostic in case cell click ever misses again.
                                try
                                {
                                    var actual = _vc.getCurrentItem();
                                    if (actual != null)
                                    {
                                        string actualName = GetItemName(actual);
                                        DebugLogger.Log(LogCategory.Handler, "CardTrader",
                                            $"  trader currentItem after click: itemId={actual.itemId} ({actualName})");
                                        if (actual.itemId != item.itemId)
                                            ScreenReader.SayQueued(Loc.Get("trader_actual_selected", actualName));
                                    }
                                }
                                catch (Exception ex)
                                {
                                    DebugLogger.Log(LogCategory.Handler, "CardTrader", $"getCurrentItem error: {ex.Message}");
                                }

                                // Schedule the exchangeButton click a few frames later.
                                // Same-frame click fails because the button hasn't
                                // become interactable yet, but too long a delay lets
                                // the trader's scroll-snap drift the selection.
                                _pendingExchangeClickDelay = 0.1f;
                                _pendingExpectedItemId = item.itemId;
                                _pendingExpectedRareId = item.rareId;
                                _pendingExpectedItemName = name;
                                verb = "cell click; exchangeButton click scheduled";
                            }
                            else
                            {
                                _vc.OnClickCard(item.itemId, item.rareId);
                                verb = "OnClickCard (no cell GO)";
                            }

                            _pendingTradeIndex = -1;
                            _pendingTradeCooldown = 0f;
                            ScreenReader.Say(Loc.Get("trader_trading", name));
                        }
                        else
                        {
                            // First press — arm
                            _vc.OnClickCard(item.itemId, item.rareId);
                            _pendingTradeIndex = _focusIndex;
                            _pendingTradeCooldown = PendingTradeWindow;
                            ScreenReader.Say(Loc.Get("trader_confirm_prompt", name));
                            _operationCooldown = OperationCooldownTime;
                            DebugLogger.Log(LogCategory.Handler, "CardTrader", "  -> armed trade (press Enter again)");
                            return;
                        }
                        break;

                    case CardTraderInfoBase.Type.ChangeCard:
                        _vc.gotoChangeCardList(item);
                        verb = "gotoChangeCardList";
                        break;

                    case CardTraderInfoBase.Type.List:
                        _vc.OnClickOpenList();
                        verb = "OnClickOpenList";
                        break;

                    default:
                        _vc.OnClickCard(item.itemId, item.rareId);
                        verb = "OnClickCard (fallback)";
                        break;
                }

                _operationCooldown = OperationCooldownTime;
                ScreenReader.Say(Loc.Get("trader_selected", name));
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"  -> called {verb}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"SelectCurrentItem error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        private GameObject FindCellGameObject(int index)
        {
            try
            {
                var mgr = _vc?.CardTrader2DMgr;
                var scrollRect = mgr?.itemScrollRect;
                var content = scrollRect?.content;
                if (content == null) return null;

                int count = content.childCount;

                // First time: dump everything so we can map our items[] to children[].
                if (!_cellsDumped)
                {
                    DebugLogger.Log(LogCategory.Handler, "CardTrader",
                        $"Cell dump: content has {count} children");
                    for (int i = 0; i < count; i++)
                    {
                        var c = content.GetChild(i);
                        if (c == null) continue;
                        var go = c.gameObject;
                        string firstText = "";
                        try
                        {
                            var texts = go.GetComponentsInChildren<UnityEngine.UI.Text>(true);
                            foreach (var t in texts)
                            {
                                if (t != null && !string.IsNullOrWhiteSpace(t.text))
                                {
                                    firstText = t.text.Replace("\n", " ");
                                    if (firstText.Length > 40) firstText = firstText.Substring(0, 40);
                                    break;
                                }
                            }
                        }
                        catch { }
                        DebugLogger.Log(LogCategory.Handler, "CardTrader",
                            $"  Cell[{i}] name={go.name} text='{firstText}' siblingIdx={c.GetSiblingIndex()}");
                    }
                    _cellsDumped = true;
                }

                if (index < 0 || index >= count)
                {
                    DebugLogger.Log(LogCategory.Handler, "CardTrader",
                        $"  cell index {index} out of range (content has {count} children)");
                    return null;
                }
                var child = content.GetChild(index);
                return child?.gameObject;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"FindCellGameObject error: {ex.Message}");
                return null;
            }
        }

        private static void ClickGameObject(GameObject go)
        {
            if (go == null) return;
            var ed = new UnityEngine.EventSystems.PointerEventData(
                UnityEngine.EventSystems.EventSystem.current);
            UnityEngine.EventSystems.ExecuteEvents.Execute(
                go, ed, UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
            UnityEngine.EventSystems.ExecuteEvents.Execute(
                go, ed, UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
            UnityEngine.EventSystems.ExecuteEvents.Execute(
                go, ed, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
        }

        private void ConfirmTrade()
        {
            if (_operationCooldown > 0f || _vc == null) return;

            try
            {
                // confirmListButton is the trader's Exchange button. Clicking it goes
                // through the trader's own handler, which sets clickYesAction on the
                // confirm dialog before showing it — so OnClickedYes will actually
                // execute the trade.
                var btn = _vc.confirmListButton;
                bool active = btn != null && btn.gameObject?.activeInHierarchy == true;
                bool interactable = btn != null && btn.interactable;

                DebugLogger.Log(LogCategory.Handler, "CardTrader",
                    $"ConfirmTrade: confirmListButton active={active} interactable={interactable}");

                if (btn != null && active && interactable)
                {
                    btn.onClick.Invoke();
                    DebugLogger.Log(LogCategory.Handler, "CardTrader", "  -> invoked confirmListButton.onClick");
                }
                else
                {
                    _vc.OnClickExchange();
                    DebugLogger.Log(LogCategory.Handler, "CardTrader", "  -> fallback OnClickExchange");
                }

                _operationCooldown = OperationCooldownTime;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"ConfirmTrade error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        private void GotoConversionCatalog()
        {
            if (_operationCooldown > 0f || _vc == null) return;
            try
            {
                _vc.OnFooterButton(0);
                _operationCooldown = OperationCooldownTime;
                ScreenReader.Say(Loc.Get("trader_goto_catalog"));
                DebugLogger.Log(LogCategory.Handler, "CardTrader", "OnFooterButton(0) — to conversion catalog");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"GotoConversionCatalog error: {ex.Message}");
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

            // Affordability — drives the user's first decision when browsing
            bool goldShort = false, itemShort = false, cardShort = false;
            try { goldShort = item.IsGoldShort(); } catch { }
            try { itemShort = item.IsItemShort(); } catch { }
            try { cardShort = item.IsCardShort() == CardTraderInfoBase.CardShortage.Shortage; } catch { }
            bool canTrade = !goldShort && !itemShort && !cardShort;
            parts.Add(canTrade ? Loc.Get("trader_can_trade") : Loc.Get("trader_cannot_trade"));

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

        private static List<int> GetDestCardList(CardTraderInfoBase item)
        {
            var result = new List<int>();
            try
            {
                var dest = item.destCards;
                if (dest != null)
                    for (int i = 0; i < dest.Count; i++)
                        result.Add(dest[i]);
            }
            catch { }
            return result;
        }

        #endregion
    }
}
