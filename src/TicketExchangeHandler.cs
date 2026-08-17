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

            DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                $"Activated (mode={GetModeName()}), waiting for initial scan...");
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

                // Try cardExchangeList first (List<int> of card IDs for ticket
                // exchange). This source carries only the mrk — no rarity — so
                // stored Kirarity is 0 for these entries.
                var exchangeList = _vc.cardExchangeList;
                if (exchangeList != null && exchangeList.Count > 0)
                {
                    for (int i = 0; i < exchangeList.Count; i++)
                    {
                        _cardIds.Add(exchangeList[i]);
                        _cardKirarities.Add(0L);
                    }

                    DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                        $"Loaded {_cardIds.Count} cards from cardExchangeList");
                    return;
                }

                // Fallback: try exchangeList (List<CardAndRarity>) — keep the
                // real Kirarity so the exchange submits the exact card.
                var carList = _vc.exchangeList;
                if (carList != null && carList.Count > 0)
                {
                    for (int i = 0; i < carList.Count; i++)
                    {
                        var car = carList[i];
                        _cardIds.Add(car.Mrk);
                        _cardKirarities.Add(car.Kirarity);
                    }

                    DebugLogger.Log(LogCategory.Handler, "TicketExchange",
                        $"Loaded {_cardIds.Count} cards from exchangeList");
                    return;
                }

                // Another fallback: try choiceList (List<CardAndRarity>).
                var choiceList = _vc.choiceList;
                if (choiceList != null && choiceList.Count > 0)
                {
                    for (int i = 0; i < choiceList.Count; i++)
                    {
                        var car = choiceList[i];
                        _cardIds.Add(car.Mrk);
                        _cardKirarities.Add(car.Kirarity);
                    }

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
            long kirarity = (_focusIndex < _cardKirarities.Count) ? _cardKirarities[_focusIndex] : 0L;
            string name = CardFormatter.GetName(mrk);

            try
            {
                // Feed the game the exact card it gave us, Kirarity included.
                // A card built from the mrk alone has Kirarity=0, which the
                // exchange later rejects and the game hard-resets to title.
                var car = new CardAndRarity(mrk, kirarity);

                // Check if addible
                if (_vc.isAddible(car))
                {
                    bool result = _vc.addToRewardView(car);
                    if (result)
                    {
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
                // The CardGetter has two possible confirm controls. The ticket /
                // dream-ticket / choice flow confirms through decideButton (driven
                // by the game's setDecideButton()); card-trader-style modes use
                // exchangeButton. Whichever the active mode uses, its interactable
                // flag is the game's own "a valid selection can be submitted now"
                // gate. Never bypass it: DecideClicked() with an empty or already-
                // consumed selection hard-errors the game to the title screen.
                var decideBtn = _vc?.decideButton;
                var exchangeBtn = _vc?.exchangeButton;

                var action = TicketExchangePolicy.ChooseAction(
                    decideBtn != null, decideBtn?.interactable == true,
                    exchangeBtn != null, exchangeBtn?.interactable == true);

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
                            $"Confirm rejected: decide(present={decideBtn != null}, " +
                            $"interactable={decideBtn?.interactable == true}) " +
                            $"exchange(present={exchangeBtn != null}, " +
                            $"interactable={exchangeBtn?.interactable == true})");
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
