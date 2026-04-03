using System;
using System.Collections.Generic;
using UnityEngine;
using Il2CppYgomGame.CardList;
using Il2CppYgomGame.Deck;

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

        // Cooldown
        private float _operationCooldown;
        private const float OperationCooldownTime = 0.5f;

        // Delayed initial scan
        private float _initialScanDelay;
        private bool _initialScanDone;
        private int _initialScanAttempts;

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

            DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                $"Activated (mode={GetModeName()}), waiting for initial scan...");
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
                ScreenReader.Say(Loc.Get("ticket_entered", _cardIds.Count));
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

            try
            {
                if (_vc == null) return;

                // Try cardExchangeList first (List<int> of card IDs for ticket exchange)
                var exchangeList = _vc.cardExchangeList;
                if (exchangeList != null && exchangeList.Count > 0)
                {
                    for (int i = 0; i < exchangeList.Count; i++)
                        _cardIds.Add(exchangeList[i]);

                    DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                        $"Loaded {_cardIds.Count} cards from cardExchangeList");
                    return;
                }

                // Fallback: try exchangeList (List<CardAndRarity>)
                var carList = _vc.exchangeList;
                if (carList != null && carList.Count > 0)
                {
                    for (int i = 0; i < carList.Count; i++)
                        _cardIds.Add(carList[i].Mrk);

                    DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                        $"Loaded {_cardIds.Count} cards from exchangeList");
                    return;
                }

                // Another fallback: try choiceList (List<CardAndRarity>)
                var choiceList = _vc.choiceList;
                if (choiceList != null && choiceList.Count > 0)
                {
                    for (int i = 0; i < choiceList.Count; i++)
                        _cardIds.Add(choiceList[i].Mrk);

                    DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                        $"Loaded {_cardIds.Count} cards from choiceList");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                    $"RefreshCardList error: {ex.Message}");
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
            if (InputManager.TryConsumeKeyDown(KeyCode.Return))
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

            // G — announce ticket/exchange info
            if (InputManager.TryConsumeKeyDown(KeyCode.G))
            {
                AnnounceExchangeInfo();
                return;
            }

            // Tab — rescan card list
            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                RefreshCardList();
                ScreenReader.Say(Loc.Get("ticket_entered", _cardIds.Count));
                if (_cardIds.Count > 0)
                    AnnounceCurrentCard(queued: true);
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
            string name = CardFormatter.GetName(mrk);

            try
            {
                var car = new CardAndRarity(mrk);

                // Check if addible
                if (_vc.isAddible(car))
                {
                    bool result = _vc.addToRewardView(car);
                    if (result)
                    {
                        ScreenReader.Say(Loc.Get("ticket_selected", name));
                        DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                            $"Selected card mrk={mrk} ({name})");
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
                // Try the exchange button first
                var exchangeBtn = _vc?.exchangeButton;
                if (exchangeBtn != null && exchangeBtn.interactable)
                {
                    exchangeBtn.onClick.Invoke();
                    DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                        "Clicked exchange button");
                    _operationCooldown = OperationCooldownTime;
                    return;
                }

                // Fallback: DecideClicked
                _vc?.DecideClicked();
                DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                    "Called DecideClicked");
                _operationCooldown = OperationCooldownTime;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                    $"ConfirmExchange error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        private void AnnounceExchangeInfo()
        {
            try
            {
                // Try to read exchange info text
                string info = _vc?.cardExchangeInfo;
                if (!string.IsNullOrEmpty(info))
                {
                    ScreenReader.Say(info);
                    return;
                }

                // Try reading the item number text
                var itemNumText = _vc?.itemNum;
                if (itemNumText != null && !string.IsNullOrEmpty(itemNumText.text))
                {
                    ScreenReader.Say(Loc.Get("ticket_count", itemNumText.text));
                    return;
                }

                // Fallback: just announce card count
                ScreenReader.Say(Loc.Get("ticket_entered", _cardIds.Count));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                    $"AnnounceExchangeInfo error: {ex.Message}");
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
            string announcement = Loc.Get("ticket_card_position", pos, total, cardText);

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
