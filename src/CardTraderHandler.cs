using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppYgomGame.Single;

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

                return contentMgr.GetStackTopViewController()?.TryCast<CardTraderViewController2>();
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

            // gId is the trade item's unique game identifier.
            // rare=0 is an initial attempt — log all ids so we can refine if needed.
            int mrk = item.gId;
            long rare = 0;

            DebugLogger.Log(LogCategory.Handler, "CardTrader",
                $"SelectItem: name={name}, gId={item.gId}, costCardMrk={item.costCardMrk}, "
                + $"destCards=[{string.Join(",", GetDestCardList(item))}], rare={rare}");

            try
            {
                _vc.OnClickCard(mrk, rare);
                _operationCooldown = OperationCooldownTime;
                ScreenReader.Say(Loc.Get("trader_selected", name));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"SelectCurrentItem error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        private void ConfirmTrade()
        {
            if (_operationCooldown > 0f || _vc == null) return;

            try
            {
                // confirmListButton is the trade confirmation button on the VC
                var btn = _vc.confirmListButton;
                if (btn != null && btn.gameObject?.activeInHierarchy == true && btn.interactable)
                {
                    btn.onClick.Invoke();
                    DebugLogger.Log(LogCategory.Handler, "CardTrader", "Clicked confirmListButton");
                }
                else
                {
                    _vc.OnClickExchange();
                    DebugLogger.Log(LogCategory.Handler, "CardTrader", "Called OnClickExchange");
                }

                _operationCooldown = OperationCooldownTime;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTrader", $"ConfirmTrade error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
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

            if (item.goldUse > 0)
                parts.Add(Loc.Get("trader_cost_gold", item.goldUse));

            if (item.costCardMrk > 0 && item.costCardUse > 0)
            {
                string costName = GetCardName(item.costCardMrk);
                parts.Add(Loc.Get("trader_cost_card", item.costCardUse, costName));
            }

            if (verbose)
            {
                if (item.stock > 0)
                    parts.Add(Loc.Get("trader_stock", item.stock));

                if (!string.IsNullOrWhiteSpace(item.notice))
                    parts.Add(item.notice.Trim());
            }

            return string.Join(", ", parts);
        }

        private string GetItemName(CardTraderInfoBase item)
        {
            if (!string.IsNullOrWhiteSpace(item.name))
                return item.name.Trim();

            var dest = item.destCards;
            if (dest != null && dest.Count > 0)
                return GetCardName(dest[0]);

            return Loc.Get("duel_unknown_card");
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
