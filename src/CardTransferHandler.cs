using System;
using System.Collections.Generic;
using Il2CppYgomGame.CardList;
using Il2CppYgomGame.Deck;
using UnityEngine;

namespace DuelLinksAccess
{
    /// <summary>
    /// Handler for CardTransferViewController — the "Select the card to
    /// exchange" screen reached at the end of a Card Trader ChangeCard trade
    /// (opened by CardTraderViewController2.gotoChangeCard).
    ///
    /// The screen is a grid of exchange candidates (CardBaseF cells) plus a
    /// Trade footer button (xferButton). The Trade button is gated: it only
    /// becomes interactable once at least one card is selected in the grid
    /// (CardClicked -> setXferability). The generic ScreenButtonHandler surfaces
    /// neither the grid cells nor the YgomButton, so this handler claims the
    /// screen and provides: Left/Right to browse the candidate cards, Enter to
    /// choose/unchoose the focused card (the game's own CardClicked, which runs
    /// its selection + button-enable logic), T to trade (xferClicked, only once
    /// the game has enabled it), and Escape to cancel.
    ///
    /// A real trade spends the user's cards, so xferClicked is fired only on an
    /// explicit T press AND only when the game's own xferButton.interactable is
    /// true — never automatically. The game may still show its own final
    /// confirm (activateTmpDialog), which DialogHandler reads.
    /// </summary>
    public class CardTransferHandler
    {
        private CardTransferViewController _vc;
        private bool _wasActive;
        private int _focusIndex;
        private float _cooldown;
        private const float CooldownTime = 0.3f;

        // The exchange candidate list is fetched from the server after the
        // screen opens (setup registers a callback that fires setupAfterLoad ->
        // the cell builder once the data lands), so the grid is empty for the
        // first ~1-3s. We poll until cells appear rather than scanning once.
        private bool _contentReady;
        private bool _gaveUpWaiting;
        private float _pollTimer;
        private float _waitedTotal;
        private float _logThrottle;
        // The terminal message once we stop waiting — either the specific
        // cost-card-shortage explanation or the generic load-failure hint —
        // reused when the user presses a key after we've given up.
        private string _loadResultMsg = "";
        private const float PollInterval = 0.4f;
        private const float MaxWait = 12f;

        /// <summary>Whether this handler is currently managing the transfer screen.</summary>
        public bool IsActive { get; private set; }

        /// <summary>Called each frame by Main.UpdateHandlers().</summary>
        public void Update()
        {
            if (_cooldown > 0f) _cooldown -= Time.deltaTime;

            var vc = TryGetVC();
            if (vc == null)
            {
                if (_wasActive) Deactivate();
                return;
            }
            if (!_wasActive) Activate(vc);

            if (!_contentReady)
            {
                PollForContent();
                return;
            }

            ProcessInput();
        }

        /// <summary>
        /// Waits for the server-fetched exchange list to populate the grid. The
        /// candidate cells don't exist when the screen first pushes, so we poll
        /// (throttled state-logging for the tester log) until cells appear, then
        /// announce the first card. Escape still works while loading; other keys
        /// report that the list is still loading.
        /// </summary>
        private void PollForContent()
        {
            _pollTimer -= Time.deltaTime;
            _waitedTotal += Time.deltaTime;

            if (_pollTimer <= 0f)
            {
                _pollTimer = PollInterval;
                var cells = ScanCells();
                if (cells.Count > 0)
                {
                    _contentReady = true;
                    _focusIndex = 0;
                    DumpState("content ready");
                    AnnounceFocus(cells);
                    return;
                }

                // NOTE: do NOT force the builders (setupAfterLoad/createList).
                // They throw KeyNotFoundException partway and corrupt the screen
                // (2026-08-11 log). We only wait, then report and let the user
                // back out.

                // The Exchange grid is built from the player's OWNED copies of the
                // cost card (createList sources from currentMrk, NOT the reward
                // pool — disasm 2026-08-13), so owning fewer than the exchange
                // needs means it can never build. Report the real reason at once
                // instead of waiting out MaxWait for a grid that will never come.
                if (!_gaveUpWaiting
                    && CostCardShortage(out string costName, out int owned, out int need))
                {
                    _gaveUpWaiting = true;
                    _loadResultMsg = Loc.Get("transfer_cost_shortage", costName, owned, need);
                    DumpState("cost-card shortage");
                    ScreenReader.Say(_loadResultMsg);
                    return;
                }

                // Throttled progress logging (~every 1.6s) so the log shows
                // whether/when the underlying lists populate while cells lag.
                _logThrottle -= PollInterval;
                if (_logThrottle <= 0f)
                {
                    _logThrottle = 1.6f;
                    DumpState($"waiting ({_waitedTotal:F1}s)");
                }

                if (_waitedTotal >= MaxWait && !_gaveUpWaiting)
                {
                    _gaveUpWaiting = true;
                    _loadResultMsg = Loc.Get("transfer_load_failed");
                    DumpState("gave up waiting");
                    DumpHierarchy();
                    ScreenReader.Say(_loadResultMsg);
                }
            }

            // Allow leaving while the list loads; a loading hint for other keys.
            if (_cooldown > 0f) return;
            if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.Backspace))
            {
                GoBack();
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.LeftArrow)
                || InputManager.TryConsumeKeyDown(KeyCode.RightArrow)
                || InputManager.TryConsumeKeyDown(KeyCode.UpArrow)
                || InputManager.TryConsumeKeyDown(KeyCode.DownArrow)
                || InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter)
                || InputManager.TryConsumeKeyDown(KeyCode.Space)
                || InputManager.TryConsumeKeyDown(KeyCode.T))
            {
                _cooldown = CooldownTime;
                ScreenReader.Say(_gaveUpWaiting ? _loadResultMsg : Loc.Get("transfer_loading"));
            }
        }

        #region VC detection / lifecycle

        private static CardTransferViewController TryGetVC()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return null;

                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedMgr.TryGetValue("content", out contentMgr) || contentMgr == null)
                    return null;

                var top = contentMgr.GetStackTopViewController();
                if (top == null || top.gameObject?.name != "CardTransfer") return null;

                return top.TryCast<CardTransferViewController>();
            }
            catch { return null; }
        }

        private void Activate(CardTransferViewController vc)
        {
            _vc = vc;
            _wasActive = true;
            IsActive = true;
            _contentReady = false;
            _gaveUpWaiting = false;
            _pollTimer = 0.5f;
            _waitedTotal = 0f;
            _logThrottle = 0f;
            _loadResultMsg = "";
            _focusIndex = 0;

            string info = "";
            try { info = LabelExtractor.StripRichText(_vc.exchangeInfo ?? ""); } catch { }

            ScreenReader.Say(string.IsNullOrWhiteSpace(info)
                ? Loc.Get("transfer_entered")
                : Loc.Get("transfer_entered_info", info));
            DebugLogger.Log(LogCategory.Handler, "CardTransfer", $"Activated: info='{info}'");
        }

        private void Deactivate()
        {
            _wasActive = false;
            IsActive = false;
            _contentReady = false;
            _gaveUpWaiting = false;
            _vc = null;
            DebugLogger.Log(LogCategory.Handler, "CardTransfer", "Deactivated");
        }

        #endregion

        #region Input

        private void ProcessInput()
        {
            if (_cooldown > 0f || _vc == null) return;

            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.LeftArrow)
                || InputManager.TryConsumeKeyDownOrRepeat(KeyCode.UpArrow))
            {
                Navigate(-1);
                return;
            }
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.RightArrow)
                || InputManager.TryConsumeKeyDownOrRepeat(KeyCode.DownArrow))
            {
                Navigate(1);
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Home))
            {
                _focusIndex = 0;
                AnnounceFocus(ScanCells());
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.End))
            {
                var cells = ScanCells();
                _focusIndex = Math.Max(0, cells.Count - 1);
                AnnounceFocus(cells);
                return;
            }

            // Enter chooses/unchooses the focused card in the exchange grid.
            if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter)
                || InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                ChooseFocused();
                return;
            }

            // T commits the trade — deliberately a separate key from the
            // selection Enter so a real trade is never one keypress away.
            if (InputManager.TryConsumeKeyDown(KeyCode.T))
            {
                Trade();
                return;
            }

            // Re-read the focused card (C/I, the mod's standard "current item" keys).
            if (InputManager.TryConsumeKeyDown(KeyCode.C)
                || InputManager.TryConsumeKeyDown(KeyCode.I))
            {
                AnnounceFocus(ScanCells());
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.Backspace))
            {
                GoBack();
                return;
            }
        }

        private void Navigate(int delta)
        {
            var cells = ScanCells();
            if (cells.Count == 0)
            {
                ScreenReader.Say(Loc.Get("transfer_empty"));
                return;
            }
            _focusIndex = Mathf.Clamp(_focusIndex + delta, 0, cells.Count - 1);
            AnnounceFocus(cells);
        }

        /// <summary>
        /// Toggles the focused card's selection via the game's own CardClicked,
        /// which runs the real selection bookkeeping and re-evaluates the Trade
        /// button (setXferability). Feedback is driven by the change in the
        /// game's selected-count, so it doesn't depend on guessing the internal
        /// index semantics — a no-op click (maxed out / ineligible card) is
        /// reported honestly.
        /// </summary>
        private void ChooseFocused()
        {
            var cells = ScanCells();
            if (cells.Count == 0)
            {
                ScreenReader.Say(Loc.Get("transfer_empty"));
                return;
            }
            _focusIndex = Mathf.Clamp(_focusIndex, 0, cells.Count - 1);
            var cell = cells[_focusIndex];

            int before = SelectedCount();
            try
            {
                _vc.CardClicked(cell);
                _cooldown = CooldownTime;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTransfer", $"CardClicked error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
                return;
            }

            int after = SelectedCount();
            if (after > before)
                ScreenReader.Say(Loc.Get("transfer_chose", after));
            else if (after < before)
                ScreenReader.Say(Loc.Get("transfer_removed", after));
            else
                ScreenReader.Say(Loc.Get("transfer_not_selectable"));

            DumpState($"after CardClicked (mrk={SafeMrk(cell)} idx={SafeIndex(cell)})");
        }

        /// <summary>
        /// Commits the exchange. Fires xferClicked only when the game's own Trade
        /// button is active and interactable — i.e. a valid selection exists.
        /// Otherwise it tells the user to choose a card first and logs the button
        /// state so the next log shows why the game withheld the trade.
        /// </summary>
        private void Trade()
        {
            bool active = false, interactable = false;
            try
            {
                var b = _vc.xferButton;
                active = b?.gameObject?.activeInHierarchy == true;
                interactable = b != null && b.interactable;
            }
            catch { }

            int sel = SelectedCount();
            if (!active || !interactable)
            {
                ScreenReader.Say(Loc.Get("transfer_need_selection"));
                DebugLogger.Log(LogCategory.Handler, "CardTransfer",
                    $"Trade blocked: xferButton active={active} interactable={interactable} selected={sel}");
                return;
            }

            try
            {
                _vc.xferClicked();
                _cooldown = CooldownTime;
                ScreenReader.Say(Loc.Get("transfer_trading"));
                DebugLogger.Log(LogCategory.Handler, "CardTransfer",
                    $"xferClicked() fired (selected={sel})");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTransfer", $"xferClicked error: {ex.Message}");
                ScreenReader.Say(Loc.Get("ticket_activate_error"));
            }
        }

        private void GoBack()
        {
            try
            {
                _vc.OnBack(null);
                _cooldown = CooldownTime;
                ScreenReader.Say(Loc.Get("screen_back"));
                DebugLogger.Log(LogCategory.Handler, "CardTransfer", "OnBack()");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTransfer", $"OnBack error: {ex.Message}");
            }
        }

        #endregion

        #region Reading

        /// <summary>
        /// Announces the focused exchange card: name, position, and — best-effort
        /// — whether it is currently chosen or unavailable. Selection state is a
        /// hint only (it reads the game's selected-index set, whose exact keying
        /// is confirmed from the logs); the authoritative selection feedback
        /// comes from ChooseFocused's count delta.
        /// </summary>
        private void AnnounceFocus(List<CardBaseF> cells)
        {
            if (cells.Count == 0)
            {
                ScreenReader.Say(Loc.Get("transfer_empty"));
                return;
            }
            _focusIndex = Mathf.Clamp(_focusIndex, 0, cells.Count - 1);
            var cell = cells[_focusIndex];

            int mrk = SafeMrk(cell);
            string name = GetCardName(mrk);
            string msg = Loc.Get("transfer_pos", name, _focusIndex + 1, cells.Count);

            if (IsSelected(cell)) msg += ", " + Loc.Get("transfer_state_selected");
            if (IsGray(cell)) msg += ", " + Loc.Get("transfer_state_unavailable");

            ScreenReader.Say(msg);
        }

        /// <summary>
        /// Collects the on-screen exchange candidate cells (CardBaseF with a real
        /// Mrk), ordered by their logical grid index. CardTransferViewController
        /// has no cardGrid field, so the cells are found by walking the VC's own
        /// GameObject subtree.
        /// </summary>
        private List<CardBaseF> ScanCells()
        {
            var result = new List<CardBaseF>();
            try
            {
                var root = _vc?.gameObject;
                if (root == null) return result;

                var comps = root.GetComponentsInChildren<CardBaseF>(true);
                if (comps != null)
                {
                    foreach (var cb in comps)
                    {
                        if (cb == null) continue;
                        GameObject go = null;
                        try { go = cb.gameObject; } catch { }
                        if (go == null || !go.activeInHierarchy) continue;
                        if (SafeMrk(cb) <= 0) continue;
                        result.Add(cb);
                    }
                }
                result.Sort((a, b) => SafeIndex(a).CompareTo(SafeIndex(b)));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTransfer", $"ScanCells error: {ex.Message}");
            }
            return result;
        }

        private int SelectedCount()
        {
            try { return _vc?.selected?.Count ?? 0; }
            catch { return 0; }
        }

        private bool IsSelected(CardBaseF cell)
        {
            try
            {
                var sel = _vc?.selected;
                return sel != null && sel.Contains(SafeIndex(cell));
            }
            catch { return false; }
        }

        private static bool IsGray(CardBaseF cell)
        {
            try { return cell.IsGrayscale; }
            catch { return false; }
        }

        private static int SafeMrk(CardBaseF cell)
        {
            try { return cell.Mrk; }
            catch { return 0; }
        }

        private static int SafeIndex(CardBaseF cell)
        {
            try { return cell.index; }
            catch { return 0; }
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

        /// <summary>
        /// Always-on diagnostic: dumps the screen's exchange state so a tester
        /// log reveals whether the grid is virtualized (getCardListNum vs cells),
        /// the selection/enable state, and whether CardClicked actually populated
        /// the game's selected set + enabled the Trade button.
        /// </summary>
        private void DumpState(string tag)
        {
            try
            {
                string mode = "?";
                try { mode = _vc.Mode.ToString(); } catch { }
                int listNum = -1; try { listNum = _vc.getCardListNum(); } catch { }
                int cardList = -1; try { cardList = _vc.cardList?.Count ?? -1; } catch { }
                int exchangeList = -1; try { exchangeList = _vc.exchangeList?.Count ?? -1; } catch { }
                int selected = -1; try { selected = _vc.selected?.Count ?? -1; } catch { }
                int maxXfer = -1; try { maxXfer = _vc.maxXferNum; } catch { }
                int curMrk = -1; try { curMrk = _vc.currentMrk; } catch { }

                bool xa = false, xi = false;
                try { var b = _vc.xferButton; xa = b?.gameObject?.activeInHierarchy == true; xi = b != null && b.interactable; } catch { }

                var cells = ScanCells();
                DebugLogger.Log(LogCategory.Handler, "CardTransfer",
                    $"[state] {tag}: mode={mode} currentMrk={curMrk} getCardListNum={listNum} cardList={cardList} "
                    + $"exchangeList={exchangeList} selected={selected} maxXfer={maxXfer} "
                    + $"xferButton(active={xa},interactable={xi}) cells={cells.Count} focus={_focusIndex}");

                LogCostCardOwnership(curMrk);

                int limit = Math.Min(cells.Count, 16);
                for (int i = 0; i < limit; i++)
                {
                    var c = cells[i];
                    int mrk = SafeMrk(c);
                    DebugLogger.Log(LogCategory.Handler, "CardTransfer",
                        $"  cell[{i}] index={SafeIndex(c)} mrk={mrk} name={GetCardName(mrk)} "
                        + $"gray={IsGray(c)} selected={IsSelected(c)}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTransfer", $"DumpState error: {ex.Message}");
            }
        }

        /// <summary>
        /// Diagnostic (2026-08-13): logs how many copies of the COST card the
        /// player owns. Disasm of createList (0xC29EB0) shows the Exchange grid
        /// is built from the player's owned copies of the cost card (currentMrk),
        /// NOT from the 45-card reward pool (exchangeList). So an empty grid most
        /// likely means the account owns zero of the cost card — in which case
        /// the grid is CORRECTLY empty and our navigation is fine. This line
        /// distinguishes "own 0 → correctly empty" from "builder never ran":
        /// owned==0 with cells==0 confirms the former; owned&gt;0 with cells==0
        /// points to the load callback not firing. CardPossAll() (from the offer
        /// item) is the authoritative all-rarity count; CardPoss(mrk,0) is a
        /// secondary probe usable even when the offer item isn't available.
        /// </summary>
        /// <summary>
        /// True when the account owns fewer copies of the cost card than this
        /// exchange requires — in which case the Exchange grid can never build
        /// (it's sourced from those owned copies), so continuing to wait is
        /// pointless. Returns the cost card's name and the owned/needed counts
        /// for a clear announcement. Returns false while currentMrk isn't
        /// populated yet, so the caller keeps waiting rather than reporting
        /// prematurely.
        /// </summary>
        private bool CostCardShortage(out string costName, out int owned, out int need)
        {
            costName = ""; owned = -1; need = -1;
            try
            {
                int mrk = -1;
                try { mrk = _vc.currentMrk; } catch { }
                if (mrk <= 0) return false; // not ready yet — keep waiting

                costName = GetCardName(mrk);

                var offer = CardTraderHandler.LastChangeCardItem;
                if (offer != null)
                {
                    try { need = offer.costCardUse; } catch { }
                    try { owned = offer.CardPossAll(); } catch { }
                }
                if (owned < 0)
                {
                    try { owned = Il2CppYgomGame.Single.CardTraderInfoBase.CardPoss(mrk, 0); }
                    catch { }
                }
                if (need <= 0) need = 1; // any exchange needs at least one cost card

                return owned >= 0 && owned < need;
            }
            catch { return false; }
        }

        private static void LogCostCardOwnership(int currentMrk)
        {
            try
            {
                int ownedByMrk = -1;
                try { ownedByMrk = Il2CppYgomGame.Single.CardTraderInfoBase.CardPoss(currentMrk, 0); }
                catch { }

                var offer = CardTraderHandler.LastChangeCardItem;
                int costMrk = -1, costUse = -1, ownedAll = -1;
                if (offer != null)
                {
                    try { costMrk = offer.costCardMrk; } catch { }
                    try { costUse = offer.costCardUse; } catch { }
                    try { ownedAll = offer.CardPossAll(); } catch { }
                }

                DebugLogger.Log(LogCategory.Handler, "CardTransfer",
                    $"[owned] currentMrk={currentMrk} CardPoss(mrk,0)={ownedByMrk} | "
                    + $"offer(costCardMrk={costMrk} costCardUse={costUse} CardPossAll={ownedAll})");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTransfer", $"[owned] error: {ex.Message}");
            }
        }

        /// <summary>
        /// Fallback diagnostic used when the underlying lists populate but no
        /// CardBaseF cells are found — walks the VC subtree and logs the
        /// GameObject names + their card-ish components, so the log reveals what
        /// the real candidate cells are (a different component/type) when the
        /// CardBaseF assumption doesn't hold.
        /// </summary>
        private void DumpHierarchy()
        {
            try
            {
                var root = _vc?.gameObject?.transform;
                if (root == null) return;
                DebugLogger.Log(LogCategory.Handler, "CardTransfer",
                    $"[hierarchy] root='{root.gameObject.name}' children={root.childCount}");
                DumpTransform(root, 0, 0);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CardTransfer", $"DumpHierarchy error: {ex.Message}");
            }
        }

        private static void DumpTransform(Transform t, int depth, int maxDepthUnused)
        {
            if (t == null || depth > 5) return;
            int n = t.childCount;
            for (int i = 0; i < n && i < 40; i++)
            {
                var child = t.GetChild(i);
                if (child == null) continue;
                GameObject go = child.gameObject;
                string comps = "";
                try
                {
                    var cs = go.GetComponents<Component>();
                    if (cs != null)
                        foreach (var c in cs)
                        {
                            if (c == null) continue;
                            try { comps += c.GetIl2CppType().Name + " "; } catch { }
                        }
                }
                catch { }
                DebugLogger.Log(LogCategory.Handler, "CardTransfer",
                    $"[hierarchy] {new string(' ', depth * 2)}{go.name} active={go.activeInHierarchy} [{comps.Trim()}]");
                DumpTransform(child, depth + 1, maxDepthUnused);
            }
        }

        #endregion
    }
}
