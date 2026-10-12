using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Il2CppYgomGame.CharaGetTicket;
using Il2CppYgomGame.Utility;
using Il2CppYgomSystem.UI;

namespace DuelLinksAccess
{
    /// <summary>
    /// 无障碍角色兑换券处理器 (CharaGetHandler)
    /// 专用于处理礼品盒/交换奖券中打开的“角色兑换”界面 (CharaGetViewController)。
    /// 遵循零硬编码原则，运行时自省动态获取所有可兑换角色列表与详细信息，
    /// 自动过滤掉无法兑换的无效项，仅呈现可兑换角色，提供纯键盘方向键网格漫游与精准回车兑换。
    /// </summary>
    public class CharaGetHandler
    {
        #region Fields

        private CharaGetViewController _vc;
        private bool _wasActive;
        private string _lastVcGoName = "";

        private int _focusIndex;
        private readonly List<int> _characterIds = new();
        private readonly HashSet<int> _canGetSet = new();

        // 交互冷却保护
        private float _operationCooldown;
        private const float OperationCooldownTime = 0.25f;

        // 异步初筛延迟
        private float _initialScanDelay;
        private bool _initialScanDone;
        private int _initialScanAttempts;

        // 对话框后刷新守卫
        private bool _awaitingDialogResult;
        private bool _dialogSeen;

        #endregion

        #region Properties

        /// <summary>
        /// 当前是否正处于角色兑换界面的活动状态。
        /// </summary>
        public bool IsActive { get; private set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// 每帧由 Main.UpdateHandlers() 调用。
        /// </summary>
        public void Update()
        {
            if (_operationCooldown > 0f)
                _operationCooldown -= Time.deltaTime;

            var vc = TryGetCharaGetVC();
            if (vc == null || vc.gameObject == null || !vc.gameObject.activeInHierarchy)
            {
                if (_wasActive) Deactivate();
                return;
            }

            string goName = vc.gameObject.name;
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

            if (_awaitingDialogResult)
                CheckPostDialogRefresh();

            ProcessInput();
        }

        #endregion

        #region Lifecycle

        private void Activate(CharaGetViewController vc, string goName)
        {
            _vc = vc;
            _lastVcGoName = goName;
            _wasActive = true;
            IsActive = true;
            _focusIndex = 0;
            _initialScanDone = false;
            _initialScanDelay = 0.35f;
            _initialScanAttempts = 0;
            _awaitingDialogResult = false;
            _dialogSeen = false;

            // 尝试激活游戏原生的“仅显示可兑换”筛选，使游戏原生 UI 也与读屏保持一致
            try
            {
                if (_vc != null && !_vc.onlycanget)
                {
                    _vc.OnlyCanGetClicked();
                }
            }
            catch { }

            DebugLogger.Log(LogCategory.Handler, "CharaGet",
                $"CharaGetViewController activated (GO={goName}), awaiting initial scan...");
        }

        private void Deactivate()
        {
            _wasActive = false;
            IsActive = false;
            _vc = null;
            _characterIds.Clear();
            _canGetSet.Clear();
            _awaitingDialogResult = false;
            _dialogSeen = false;

            DebugLogger.Log(LogCategory.Handler, "CharaGet", "CharaGetHandler deactivated");
        }

        private void DoInitialScan()
        {
            _initialScanAttempts++;
            RefreshCharacterList();

            if (_characterIds.Count == 0 && _initialScanAttempts < 6)
            {
                _initialScanDelay = 0.25f;
                return;
            }

            _initialScanDone = true;

            string title = GetScreenTitle();
            string ticketInfo = GetTicketBalanceText();
            string countMsg = _characterIds.Count > 0
                ? $"共有 {_characterIds.Count} 位可兑换角色。方向键浏览，回车键兑换，Esc返回。"
                : "当前无可兑换角色。";

            ScreenReader.Say($"{title}。{ticketInfo}{countMsg}");

            if (_characterIds.Count > 0)
            {
                AnnounceCurrentCharacter(queued: true);
            }
        }

        private void CheckPostDialogRefresh()
        {
            if (GameStateTracker.CurrentScreen == GameScreen.Dialog)
            {
                _dialogSeen = true;
                return;
            }

            if (!_dialogSeen) return;

            _awaitingDialogResult = false;
            _dialogSeen = false;

            RefreshCharacterList();

            if (_focusIndex >= _characterIds.Count)
                _focusIndex = Math.Max(0, _characterIds.Count - 1);

            if (_characterIds.Count > 0)
            {
                AnnounceCurrentCharacter(queued: true);
            }
        }

        #endregion

        #region VC Detection

        private static CharaGetViewController TryGetCharaGetVC()
        {
            try
            {
                var namedMgr = ViewControllerManager.namedManager;
                if (namedMgr != null && namedMgr.TryGetValue("content", out var contentMgr) && contentMgr != null)
                {
                    var topVc = contentMgr.GetStackTopViewController();
                    if (topVc != null)
                    {
                        var charaVc = topVc.TryCast<CharaGetViewController>();
                        if (charaVc != null) return charaVc;
                    }
                }

                // 兜底：场景全局嗅探活动组件
                return UnityEngine.Object.FindObjectOfType<CharaGetViewController>();
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CharaGet", $"TryGetCharaGetVC error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Data Refresh

        private void RefreshCharacterList()
        {
            _characterIds.Clear();
            _canGetSet.Clear();

            if (_vc == null) return;

            try
            {
                // 1. 获取官方可兑换集合 (cangetcharas)
                var canGetList = CharaGetViewController.cangetcharas;
                if (canGetList != null)
                {
                    for (int i = 0; i < canGetList.Count; i++)
                    {
                        _canGetSet.Add(canGetList[i]);
                    }
                }

                // 2. 严格只添加真正“可兑换”的角色
                // 如果官方提供了 cangetcharas 列表，直接以此为准
                if (canGetList != null && canGetList.Count > 0)
                {
                    for (int i = 0; i < canGetList.Count; i++)
                    {
                        int cid = canGetList[i];
                        if (!_characterIds.Contains(cid))
                        {
                            _characterIds.Add(cid);
                        }
                    }
                }
                else
                {
                    // 降级兜底：从当前所有角色中，逐一比对 CanGet
                    var filtered = _vc.GetFilteredCharalist();
                    if (filtered != null)
                    {
                        for (int i = 0; i < filtered.Count; i++)
                        {
                            int cid = filtered[i];
                            if (IsCharacterCanGet(cid) && !_characterIds.Contains(cid))
                            {
                                _characterIds.Add(cid);
                            }
                        }
                    }
                }

                DebugLogger.Log(LogCategory.Handler, "CharaGet",
                    $"Filtered to {_characterIds.Count} obtainable characters.");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CharaGet", $"RefreshCharacterList error: {ex.Message}");
            }
        }

        #endregion

        #region Input Processing

        private void ProcessInput()
        {
            if (_operationCooldown > 0f) return;

            int colNum = GetColumnNum();

            // 左右方向键：单步移动
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

            // 上下方向键：按列跨步（若项数较少，则单步循环或跨列移动）
            if (InputManager.TryConsumeKeyDown(KeyCode.UpArrow))
            {
                int step = _characterIds.Count <= colNum ? 1 : colNum;
                NavigateBy(-step);
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.DownArrow))
            {
                int step = _characterIds.Count <= colNum ? 1 : colNum;
                NavigateBy(step);
                return;
            }

            // Home / End
            if (InputManager.TryConsumeKeyDown(KeyCode.Home))
            {
                if (_characterIds.Count > 0)
                {
                    _focusIndex = 0;
                    SyncScrollView();
                    AnnounceCurrentCharacter();
                }
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.End))
            {
                if (_characterIds.Count > 0)
                {
                    _focusIndex = _characterIds.Count - 1;
                    SyncScrollView();
                    AnnounceCurrentCharacter();
                }
                return;
            }

            // Enter：选择/兑换当前角色
            if (InputManager.TryConsumeKeyDown(KeyCode.Return) || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter))
            {
                SelectCurrentCharacter();
                return;
            }

            // Tab：重读当前角色信息
            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                AnnounceCurrentCharacter();
                return;
            }

            // Escape：返回上一层
            if (InputManager.TryConsumeKeyDown(KeyCode.Escape))
            {
                GoBack();
                return;
            }
        }

        private void NavigateBy(int delta)
        {
            if (_characterIds.Count == 0) return;

            int prev = _focusIndex;
            _focusIndex = Mathf.Clamp(_focusIndex + delta, 0, _characterIds.Count - 1);

            if (_focusIndex != prev)
            {
                SyncScrollView();
                AnnounceCurrentCharacter();
            }
        }

        private void SelectCurrentCharacter()
        {
            if (_characterIds.Count == 0 || _focusIndex < 0 || _focusIndex >= _characterIds.Count)
                return;

            int cid = _characterIds[_focusIndex];
            string charaName = ResolveCharacterName(cid);

            try
            {
                int colNum = GetColumnNum();

                // 计算在游戏底层真实网格中的位置
                int gridIndex = _focusIndex;
                var fullList = _vc?.GetFilteredCharalist();
                if (fullList != null)
                {
                    int idx = fullList.IndexOf(cid);
                    if (idx >= 0) gridIndex = idx;
                }

                int targetRow = gridIndex / colNum;
                int targetCol = gridIndex % colNum;

                // 同步滚动条使目标行进入视口
                SyncScrollViewByRow(targetRow, fullList?.Count ?? _characterIds.Count, colNum);

                // 触发对应按钮的点击
                bool clicked = TriggerRowClick(targetRow, targetCol);
                if (clicked)
                {
                    _awaitingDialogResult = true;
                    _dialogSeen = false;
                    _operationCooldown = OperationCooldownTime;
                    DebugLogger.Log(LogCategory.Handler, "CharaGet",
                        $"Clicked character '{charaName}' (cid={cid}) at row {targetRow}, col {targetCol}");
                    return;
                }

                ScreenReader.Say($"正在请求兑换 {charaName}...");
                _operationCooldown = OperationCooldownTime;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CharaGet", $"SelectCurrentCharacter error: {ex.Message}");
                ScreenReader.Say("选择角色失败，请重试。");
            }
        }

        private bool TriggerRowClick(int targetRow, int targetCol)
        {
            if (_vc == null) return false;

            try
            {
                CharaGetFilterRow targetRowComp = null;

                // 1. 尝试直接从 contentRoot 子物体中按行索引获取
                if (_vc.contentRoot != null && targetRow < _vc.contentRoot.childCount)
                {
                    targetRowComp = _vc.contentRoot.GetChild(targetRow)?.GetComponent<CharaGetFilterRow>();
                }

                // 2. 备选方案：按 SiblingIndex 排序获取
                if (targetRowComp == null)
                {
                    var rows = _vc.GetComponentsInChildren<CharaGetFilterRow>(false);
                    if (rows != null && rows.Length > 0)
                    {
                        var rowList = new List<CharaGetFilterRow>();
                        for (int i = 0; i < rows.Length; i++)
                        {
                            if (rows[i] != null) rowList.Add(rows[i]);
                        }
                        rowList.Sort((a, b) => a.transform.GetSiblingIndex().CompareTo(b.transform.GetSiblingIndex()));
                        if (targetRow >= 0 && targetRow < rowList.Count)
                            targetRowComp = rowList[targetRow];
                    }
                }

                if (targetRowComp != null && targetRowComp.buttons != null)
                {
                    if (targetCol >= 0 && targetCol < targetRowComp.buttons.Length)
                    {
                        var btn = targetRowComp.buttons[targetCol];
                        if (btn != null && btn.gameObject != null && btn.gameObject.activeInHierarchy)
                        {
                            btn.onClick?.Invoke();

                            var eventData = new PointerEventData(EventSystem.current)
                            {
                                button = PointerEventData.InputButton.Left
                            };
                            ExecuteEvents.Execute(btn.gameObject, eventData, ExecuteEvents.pointerClickHandler);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CharaGet", $"TriggerRowClick error: {ex.Message}");
            }

            return false;
        }

        private void SyncScrollView()
        {
            if (_vc == null || _vc.scrollRect == null || _characterIds.Count == 0) return;

            int colNum = GetColumnNum();
            int gridIndex = _focusIndex;
            var fullList = _vc?.GetFilteredCharalist();
            if (fullList != null && _focusIndex < _characterIds.Count)
            {
                int idx = fullList.IndexOf(_characterIds[_focusIndex]);
                if (idx >= 0) gridIndex = idx;
            }

            int targetRow = gridIndex / colNum;
            SyncScrollViewByRow(targetRow, fullList?.Count ?? _characterIds.Count, colNum);
        }

        private void SyncScrollViewByRow(int targetRow, int totalCount, int colNum)
        {
            if (_vc == null || _vc.scrollRect == null || totalCount == 0) return;

            try
            {
                int totalRows = Mathf.CeilToInt((float)totalCount / colNum);
                if (totalRows <= 1) return;

                float norm = 1f - (float)targetRow / (totalRows - 1);
                _vc.scrollRect.verticalNormalizedPosition = Mathf.Clamp01(norm);
            }
            catch { }
        }

        private void GoBack()
        {
            try
            {
                if (_vc != null)
                {
                    _vc.SendBack();
                }
                else
                {
                    var namedMgr = ViewControllerManager.namedManager;
                    if (namedMgr != null && namedMgr.TryGetValue("content", out var contentMgr) && contentMgr != null)
                    {
                        contentMgr.GetStackTopViewController()?.SendBack();
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "CharaGet", $"GoBack error: {ex.Message}");
            }
        }

        #endregion

        #region Announcements

        private void AnnounceCurrentCharacter(bool queued = false)
        {
            if (_characterIds.Count == 0)
            {
                ScreenReader.Say("当前无可兑换角色。");
                return;
            }

            if (_focusIndex < 0 || _focusIndex >= _characterIds.Count)
                _focusIndex = 0;

            int cid = _characterIds[_focusIndex];
            string charaName = ResolveCharacterName(cid);
            int pos = _focusIndex + 1;
            int total = _characterIds.Count;

            string announcement = $"{charaName}，可兑换，第 {pos} 项，共 {total} 项";

            if (queued)
                ScreenReader.SayQueued(announcement);
            else
                ScreenReader.Say(announcement);
        }

        private bool IsCharacterCanGet(int cid)
        {
            if (_canGetSet.Contains(cid)) return true;

            try
            {
                int status = CharaGetViewController.CanGet(cid);
                return status == 1;
            }
            catch
            {
                return false;
            }
        }

        private string ResolveCharacterName(int cid)
        {
            try
            {
                string nameWithSeries = CharaUtil.GetNameWithSeries(cid);
                if (!string.IsNullOrWhiteSpace(nameWithSeries))
                    return nameWithSeries;

                string name = CharaUtil.GetName(cid);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;

                string extName = LabelExtractor.ResolveCharaName(cid);
                if (!string.IsNullOrWhiteSpace(extName))
                    return extName;
            }
            catch { }

            return $"角色 {cid}";
        }

        private string GetScreenTitle()
        {
            try
            {
                string title = _vc?.TitleLabel();
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }
            catch { }

            return "兑换角色";
        }

        private string GetTicketBalanceText()
        {
            try
            {
                if (_vc?.infoText != null && !string.IsNullOrWhiteSpace(_vc.infoText.text))
                {
                    return $"{_vc.infoText.text.Trim()}。";
                }

                var texts = _vc?.GetComponentsInChildren<Text>(true);
                if (texts != null)
                {
                    foreach (var t in texts)
                    {
                        if (t == null) continue;
                        string txt = t.text?.Trim();
                        if (!string.IsNullOrEmpty(txt) && (txt.Contains("持有") || txt.Contains("券")))
                        {
                            return $"{txt}。";
                        }
                    }
                }
            }
            catch { }

            return "";
        }

        private int GetColumnNum()
        {
            try
            {
                if (_vc?.rowPrefab != null && _vc.rowPrefab.ColumnNum > 0)
                    return _vc.rowPrefab.ColumnNum;
            }
            catch { }

            return 5;
        }

        #endregion
    }
}
