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

            if (!_scanDone)
            {
                _scanDelay -= Time.deltaTime;
                if (_scanDelay <= 0f)
                    DoScan();
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

            ScreenReader.Say(Loc.Get("catalog_entered"));
            DebugLogger.Log(LogCategory.Handler, "CardCatalog", $"Activated GO={goName}");
        }

        private void Deactivate()
        {
            _wasActive = false;
            IsActive = false;
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
                    try
                    {
                        var btn = vc.goldButton;
                        if (btn == null || btn.gameObject?.activeInHierarchy != true)
                            return null;
                    }
                    catch (Exception ex2)
                    {
                        DebugLogger.Log(LogCategory.Handler, "CardCatalog", $"goldButton check error: {ex2.Message}");
                        return null;
                    }
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

            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                AnnounceCurrentCard();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter))
            {
                OpenDetail();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                OpenDetail();
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

        private void OpenDetail()
        {
            if (_cooldown > 0f || _vc == null) return;

            _cachedListNum = SafeGetListNum();
            if (_cachedListNum == 0) return;
            if (_focusIndex >= _cachedListNum) _focusIndex = 0;

            int mrk = SafeGetListItemMrk(_focusIndex);
            if (mrk <= 0) return;

            string name = GetCardName(mrk);
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

        private void ConfirmConversion()
        {
            if (_cooldown > 0f || _vc == null) return;

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

            // Always announce convertibility so the user can identify eligible cards while browsing.
            parts.Add(goldable ? Loc.Get("catalog_convertible") : Loc.Get("catalog_not_convertible"));

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
