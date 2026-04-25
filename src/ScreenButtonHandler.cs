using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace DuelLinksAccess
{
    /// <summary>
    /// Generic handler for any non-dialog, non-duel screen.
    /// Two modes of operation:
    ///
    /// Button mode: When interactive elements are found, provides keyboard navigation.
    ///   Up/Down to cycle items, Enter to click, Left/Right for sliders,
    ///   Tab to re-read, Space to re-scan, Escape to go back.
    ///
    /// Text mode: When no buttons are found but text exists (e.g. scenario dialogue,
    ///   tutorials), reads text aloud and lets Enter/Space advance by simulating a click.
    ///   Polls for text changes to auto-read new dialogue.
    /// </summary>
    public class ScreenButtonHandler
    {
        #region Fields

        private string _lastVcName = "";
        private bool _scanned;
        private float _scanDelay;
        private int _scanAttempts;
        private readonly List<ScreenItem> _items = new();
        private int _focusIndex;

        // Text mode fields
        private bool _textMode;
        private string _lastReadText = "";
        private GameObject _screenRoot;

        // Duel World map area cycling
        private int _currentMapArea = 0;
        private static readonly string[] _mapAreaNames = { "Street", "Alley", "Park", "Shop" };

        // Result screen delayed rescan (after NEXT button press)
        private float _resultRescanTimer = -1f;

        // Tab-aware rescan: after clicking a tab button, force rescan
        private float _tabRescanDelay = -1f;

        /// <summary>
        /// Screens that this handler should NOT process.
        /// Dialog is handled by DialogHandler; Duel will get its own handler.
        /// </summary>
        private static readonly HashSet<GameStateTracker.GameScreen> _excludedScreens = new()
        {
            GameStateTracker.GameScreen.Unknown,
            GameStateTracker.GameScreen.Dialog,
            GameStateTracker.GameScreen.Duel,
            GameStateTracker.GameScreen.Home,  // handled by HomeHandler
        };

        #endregion

        #region Types

        private enum ItemType { Button, Slider, MapObject }

        private class ScreenItem
        {
            public GameObject Go;
            public string Label;
            public ItemType Type;
            public Slider SliderComponent;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Whether this handler is currently active (processing a screen).
        /// </summary>
        public bool IsActive => !_excludedScreens.Contains(GameStateTracker.CurrentScreen)
            && (_items.Count > 0 || _textMode);

        /// <summary>
        /// Called every frame by Main.UpdateHandlers().
        /// </summary>
        public void Update()
        {
            var screen = GameStateTracker.CurrentScreen;

            // Don't handle excluded screens
            if (_excludedScreens.Contains(screen))
            {
                if (_lastVcName != "")
                    Reset();
                return;
            }

            string currentVc = GameStateTracker.LastViewControllerName;

            // New screen or VC changed — start scan
            if (currentVc != _lastVcName)
            {
                _lastVcName = currentVc;
                _scanned = false;
                _scanDelay = 0.5f;
                _scanAttempts = 0;
                _textMode = false;
                _lastReadText = "";
                _items.Clear();
                _screenRoot = null;
                _resultRescanTimer = -1f;
                _tabRescanDelay = -1f;
            }

            if (!_scanned)
            {
                _scanDelay -= Time.deltaTime;
                if (_scanDelay <= 0f)
                {
                    _scanAttempts++;
                    ScanScreen();

                    if (_items.Count == 0 && !_textMode && _scanAttempts < 3)
                    {
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            $"No items or text found, retrying (attempt {_scanAttempts})");
                        _scanDelay = 1.0f;
                    }
                    else
                    {
                        _scanned = true;
                    }
                }
            }

            // In text mode, poll for text changes to auto-read new dialogue
            if (_textMode)
                PollTextChanges();

            // Delayed rescan for result screens (after NEXT button animations)
            if (_resultRescanTimer > 0f)
            {
                _resultRescanTimer -= Time.deltaTime;
                if (_resultRescanTimer <= 0f && _screenRoot != null)
                {
                    ScanResultScreen(_screenRoot);
                    _focusIndex = 0;
                }
            }

            // Tab-aware rescan: after clicking a tab button, rescan content
            if (_tabRescanDelay >= 0f)
            {
                _tabRescanDelay -= Time.deltaTime;
                if (_tabRescanDelay <= 0f)
                {
                    _tabRescanDelay = -1f;
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        "Tab rescan: refreshing content");
                    _scanned = false;
                    _scanDelay = 0.1f;
                    _scanAttempts = 0;
                }
            }

            ProcessInput();
        }

        #endregion

        #region Scanning

        private void Reset()
        {
            _lastVcName = "";
            _scanned = false;
            _textMode = false;
            _lastReadText = "";
            _items.Clear();
            _screenRoot = null;
        }

        private void ScanScreen()
        {
            _items.Clear();
            _focusIndex = 0;
            _textMode = false;

            try
            {
                _screenRoot = GetActiveScreenRoot();
                if (_screenRoot == null)
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        "No screen root found");
                    return;
                }

                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"Scanning: {_screenRoot.name}");

                // Log VC type for diagnostics (helps identify unknown screens)
                try
                {
                    var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                    if (namedMgr != null)
                    {
                        Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                        if (namedMgr.TryGetValue("content", out contentMgr))
                        {
                            var vc = contentMgr?.GetStackTopViewController();
                            if (vc != null)
                            {
                                MelonLoader.MelonLogger.Msg(
                                    $"[ScreenBtn][Type] {_screenRoot.name} -> VC type: {vc.GetType()?.Name}");

                                // Dump VC Args for Htjson screens in debug mode
                                if (Main.DebugMode && vc.Args != null)
                                {
                                    string goName = _screenRoot.name;
                                    if (goName == "GiftTicket" || goName.Contains("Shop")
                                        || goName == "HtjsonPage")
                                    {
                                        DumpArgsToLog(goName, vc.Args);
                                    }
                                }
                            }
                        }
                    }
                }
                catch { }

                // Find sliders first
                FindSliders(_screenRoot);

                // Find clickable buttons
                FindButtons(_screenRoot);

                // Also scan the header/footer area (separate from content VC)
                var headerRoot = GetHeaderRoot();
                if (headerRoot != null && headerRoot != _screenRoot)
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"Also scanning header: {headerRoot.name}");
                    FindSliders(headerRoot);
                    FindButtons(headerRoot);
                }

                // Post-process: remove duplicates and junk
                DeduplicateItems();
                FilterItems();

                // On the Home/Duel World screen, also scan 3D map objects (NPCs, events, etc.)
                if (GameStateTracker.CurrentScreen == GameStateTracker.GameScreen.Home)
                    FindMapObjects();

                // On the Deck screen, scan DeckSelectItem components (not standard Selectables)
                if (GameStateTracker.CurrentScreen == GameStateTracker.GameScreen.Deck)
                    FindDeckItems(_screenRoot);

                // Result screens: replace the generic button scan with structured text extraction
                bool isResultScreen = _screenRoot.name == "ResultBasePage"
                    || _screenRoot.name == "ReplayResult";
                if (isResultScreen)
                    ScanResultScreen(_screenRoot);

                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"Found {_items.Count} items");

                if (_items.Count > 0 && !isResultScreen)
                {
                    // Result screens handle their own announcement in ScanResultScreen
                    ScreenReader.SayQueued(
                        Loc.Get("screen_buttons", _items.Count));

                    // Htjson pages often have explanatory text alongside buttons
                    // (e.g. Duel Trials quiz descriptions, hint text). Capture it
                    // so T can re-read it, and announce on entry.
                    if (_screenRoot.name == "HtjsonPage" || _screenRoot.name == "TutorialArrowPart")
                    {
                        string pageText = ReadScreenText(_screenRoot);
                        if (!string.IsNullOrEmpty(pageText))
                        {
                            _lastReadText = pageText;
                            ScreenReader.SayQueued(pageText);
                            DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                $"Htjson page text ({pageText.Length} chars)");
                        }
                    }

                    AnnounceCurrentItem(queued: true);
                }
                else
                {
                    // No buttons — try text mode for scenario/dialogue screens
                    string text = ReadScreenText(_screenRoot);
                    if (!string.IsNullOrEmpty(text))
                    {
                        _textMode = true;
                        _lastReadText = text;
                        ScreenReader.Say(text);
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            $"Text mode: {text.Substring(0, Math.Min(100, text.Length))}");
                    }
                    else if (_screenRoot.name.Contains("Scenario"))
                    {
                        // ScenarioPlayerPart with no visible text — cutscene/transition.
                        // Enter text mode so Enter/Space advances via OnPointerClick,
                        // and PollTextChanges detects when dialogue text appears.
                        _textMode = true;
                        _lastReadText = "";
                        ScreenReader.Say(Loc.Get("screen_cutscene"));
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            "Cutscene mode: ScenarioPlayerPart with no visible text");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] Scan error: {ex.Message}");
            }
        }

        /// <summary>
        /// Gets the header/footer root GameObject from the "base" manager.
        /// Footer navigation buttons live here, separate from the content VC.
        /// </summary>
        private GameObject GetHeaderRoot()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return null;

                return GetTopViewGameObject(namedManager, "base");
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Removes duplicate items where a child GO shares a label with a parent GO.
        /// Keeps the child (more specific) since it typically has the actual click handler.
        /// Parent containers often have bare Selectables for visual state only.
        /// </summary>
        private void DeduplicateItems()
        {
            var toRemove = new HashSet<int>();

            for (int i = 0; i < _items.Count; i++)
            {
                if (toRemove.Contains(i)) continue;
                for (int j = i + 1; j < _items.Count; j++)
                {
                    if (toRemove.Contains(j)) continue;
                    if (_items[i].Label != _items[j].Label) continue;

                    try
                    {
                        // If j is a child of i, remove the PARENT (i)
                        if (_items[j].Go.transform.IsChildOf(_items[i].Go.transform))
                        {
                            toRemove.Add(i);
                            DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                $"Dedup: removing parent \"{_items[i].Label}\" ({_items[i].Go.name}), keeping child ({_items[j].Go.name})");
                            break;
                        }
                        // If i is a child of j, remove the PARENT (j)
                        else if (_items[i].Go.transform.IsChildOf(_items[j].Go.transform))
                        {
                            toRemove.Add(j);
                            DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                $"Dedup: removing parent \"{_items[j].Label}\" ({_items[j].Go.name}), keeping child ({_items[i].Go.name})");
                        }
                    }
                    catch { }
                }
            }

            if (toRemove.Count > 0)
            {
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (toRemove.Contains(i))
                        _items.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Removes items that are likely notification badges, decorative elements,
        /// or other non-functional UI clutter.
        /// </summary>
        private void FilterItems()
        {
            _items.RemoveAll(item =>
            {
                // Remove purely numeric labels (notification badges like "99", "1")
                if (item.Type == ItemType.Button && item.Label.All(char.IsDigit))
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"Filter: removing numeric \"{item.Label}\" ({item.Go.name})");
                    return true;
                }

                // Remove Htjson TextArea elements with placeholder text ("ああああ...")
                if (item.Go.name == "TextArea" && LabelExtractor.IsPlaceholderText(item.Label))
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"Filter: removing placeholder TextArea");
                    return true;
                }

                return false;
            });
        }

        /// <summary>
        /// Replaces the generic button scan for ResultBasePage and ReplayResult screens.
        /// Extracts meaningful text (level, EXP, score, campaign info, player names/skills)
        /// and creates structured items with NEXT/OK/END buttons for navigation.
        /// The level-up rewards scrollable list is excluded (just "Lvl: N" milestone markers).
        /// </summary>
        private void ScanResultScreen(GameObject root)
        {
            _items.Clear();

            try
            {
                var texts = root.GetComponentsInChildren<Text>(true);
                if (texts == null) return;

                // Collect meaningful text, filtering out level-up milestone spam
                var textParts = new List<string>();
                bool inLevelUpSection = false;

                foreach (var text in texts)
                {
                    if (text == null || text.gameObject == null) continue;
                    if (!text.gameObject.activeInHierarchy) continue;
                    string val = LabelExtractor.StripRichText(text.text);
                    if (string.IsNullOrWhiteSpace(val)) continue;

                    // Skip placeholder text
                    if (val.StartsWith("\u3042\u3042\u3042")) continue;
                    if (val == "0" && text.gameObject.name == "CardNum") continue;

                    // Detect level-up rewards section boundary
                    if (val == "Character Level-Up Rewards")
                        inLevelUpSection = true;

                    // Skip level-up milestone entries: "Lvl: N", "Next" marker,
                    // and standalone numbers (EXP thresholds like 10, 15, 25...)
                    if (inLevelUpSection)
                    {
                        if (val.StartsWith("Lvl:")) continue;
                        if (val == "Next") continue;
                        if (val.All(char.IsDigit)) continue;

                        // End the section on known page 2 headers or button text
                        if (val == "[Duel Assessment]" || val == "NEXT"
                            || val == "SCORE" || val == "REWARDS")
                            inLevelUpSection = false;
                        else
                            continue; // Still in the section, skip
                    }

                    // Skip button labels (handled separately below)
                    if (val == "NEXT" || val == "OK" || val == "END"
                        || val == "WATCH AGAIN" || val == "Rate")
                        continue;

                    // Skip duplicate numbers that are just reward quantities
                    // (stray "1", "10" from reward item counts)
                    if (val.All(char.IsDigit) && val.Length <= 2
                        && textParts.Count > 0)
                    {
                        // Keep only the first few numbers (level, EXP)
                        string lastPart = textParts[textParts.Count - 1];
                        if (lastPart == "SCORE" || lastPart == "REWARDS")
                        {
                            // These are meaningful: score value, reward count
                            textParts.Add(val);
                            continue;
                        }
                        // Skip stray small numbers in reward sections
                        if (textParts.Contains("REWARDS")) continue;
                    }

                    textParts.Add(val);
                }

                // Build and read duel stats from Args if available
                string argsSummary = ReadResultArgs();
                if (!string.IsNullOrEmpty(argsSummary))
                    textParts.Insert(0, argsSummary);

                string summary = string.Join(". ", textParts);
                if (!string.IsNullOrEmpty(summary))
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"Result screen text: {summary}");
                }

                // Find the actionable buttons (NEXT, OK, END, WATCH AGAIN, Rate)
                FindResultButtons(root, texts);

                // Announce the summary text directly, then let user navigate to buttons
                if (!string.IsNullOrEmpty(summary))
                {
                    if (_items.Count > 0)
                        ScreenReader.Say(summary + ". " +
                            Loc.Get("screen_buttons", _items.Count));
                    else
                        ScreenReader.Say(summary);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"Result screen scan error: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds NEXT/OK/END/WATCH AGAIN/Rate buttons in the result screen.
        /// </summary>
        private void FindResultButtons(GameObject root, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<Text> texts)
        {
            foreach (var text in texts)
            {
                if (text == null || text.gameObject == null) continue;
                if (!text.gameObject.activeInHierarchy) continue;

                string val = text.text?.Trim();
                if (string.IsNullOrEmpty(val)) continue;

                if (val != "NEXT" && val != "OK" && val != "END"
                    && val != "WATCH AGAIN" && val != "Rate") continue;

                // Find the parent button GO (Label → btn)
                var btnGo = text.transform.parent?.gameObject;
                if (btnGo == null) continue;

                var selectable = btnGo.GetComponent<Selectable>();
                if (selectable == null) continue;

                // Avoid duplicates
                if (_items.Any(i => i.Go == btnGo)) continue;

                _items.Add(new ScreenItem
                {
                    Go = btnGo,
                    Label = val,
                    Type = ItemType.Button
                });
            }
        }

        /// <summary>
        /// Reads duel stats from the ResultBasePage VC Args dictionary.
        /// Unboxes IL2CPP values to extract result, turn count, LP, and score.
        /// Returns a formatted summary string, or null if not available.
        /// </summary>
        private string ReadResultArgs()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return null;

                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedMgr.TryGetValue("content", out contentMgr)) return null;

                var vc = contentMgr?.GetStackTopViewController();
                if (vc == null) return null;

                var args = vc.Args;
                if (args == null) return null;

                // Dump all keys for diagnostics (debug mode only)
                if (Main.DebugMode)
                    DumpArgsToLog(vc.gameObject.name, args);

                // Unbox scalar values
                int res = UnboxInt(args, "res", -1);
                int turn = UnboxInt(args, "turn", -1);
                int lp = UnboxInt(args, "lp", -1);
                int lp2 = UnboxInt(args, "lp2", -1);
                int finish = UnboxInt(args, "finish", -1);
                int finisher = UnboxInt(args, "finisher", 0);

                // Try to dump list values for reward data discovery
                if (Main.DebugMode)
                {
                    DumpArgsList(args, "v32", "card IDs (32-bit)");
                    DumpArgsList(args, "v16", "card IDs (16-bit)");
                    DumpArgsList(args, "counts", "reward counts");
                    DumpArgsList(args, "rare", "rarities");
                    DumpArgsList(args, "grave", "graveyard cards");
                    DumpArgsList(args, "brkcard", "destroyed cards");
                    DumpArgsList(args, "activate", "activated cards");
                    DumpArgsUintArray(args, "dat", "raw result data");
                }

                // Build summary from what we extracted
                var parts = new List<string>();

                if (res >= 0)
                {
                    // res: 1=Win, 2=Loss, 3=Draw (based on DuelEndMessage.resultType pattern)
                    string result = res switch
                    {
                        1 => Loc.Get("duel_result_win"),
                        2 => Loc.Get("duel_result_lose"),
                        3 => Loc.Get("duel_result_draw"),
                        _ => $"Result {res}"
                    };
                    parts.Add(result);
                }

                if (turn > 0)
                    parts.Add(Loc.Get("duel_result_turns", turn));

                if (lp >= 0 && lp2 >= 0)
                    parts.Add(Loc.Get("duel_result_lp", lp, lp2));

                if (finisher > 0)
                {
                    try
                    {
                        var content = Il2CppYgomGame.Card.Content.Instance;
                        string cardName = content?.GetName(finisher);
                        if (!string.IsNullOrEmpty(cardName))
                            parts.Add(Loc.Get("duel_result_finisher", cardName));
                    }
                    catch { }
                }

                return parts.Count > 0 ? string.Join(". ", parts) : null;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"ReadResultArgs error: {ex.Message}");
                return null;
            }
        }

        /// <summary>Unboxes an IL2CPP boxed int from the Args dictionary.</summary>
        private static int UnboxInt(Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> args, string key, int defaultValue)
        {
            try
            {
                Il2CppSystem.Object obj;
                if (!args.TryGetValue(key, out obj) || obj == null) return defaultValue;
                return obj.Unbox<int>();
            }
            catch { return defaultValue; }
        }

        /// <summary>Dumps an Args list value to the debug log for discovery.</summary>
        private static void DumpArgsList(Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> args, string key, string label)
        {
            try
            {
                Il2CppSystem.Object obj;
                if (!args.TryGetValue(key, out obj) || obj == null) return;

                var list = obj.TryCast<Il2CppSystem.Collections.Generic.List<Il2CppSystem.Object>>();
                if (list == null) return;

                var vals = new List<string>();
                for (int i = 0; i < Math.Min(list.Count, 20); i++)
                {
                    try
                    {
                        var item = list[i];
                        if (item == null) { vals.Add("null"); continue; }
                        try { vals.Add(item.Unbox<int>().ToString()); }
                        catch
                        {
                            try { vals.Add(item.Unbox<long>().ToString()); }
                            catch { vals.Add(item.ToString()); }
                        }
                    }
                    catch { vals.Add("?"); }
                }

                string suffix = list.Count > 20 ? $"... ({list.Count} total)" : "";
                MelonLogger.Msg($"[ResultArgs] {key} ({label}): [{string.Join(", ", vals)}{suffix}]");
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ResultArgs] {key} dump error: {ex.Message}");
            }
        }

        /// <summary>Dumps a UInt32[] Args value to the debug log.</summary>
        private static void DumpArgsUintArray(Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> args, string key, string label)
        {
            try
            {
                Il2CppSystem.Object obj;
                if (!args.TryGetValue(key, out obj) || obj == null) return;

                var arr = obj.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<uint>>();
                if (arr == null) return;

                var vals = new List<string>();
                for (int i = 0; i < Math.Min(arr.Length, 40); i++)
                    vals.Add(arr[i].ToString());

                string suffix = arr.Length > 40 ? $"... ({arr.Length} total)" : "";
                MelonLogger.Msg($"[ResultArgs] {key} ({label}) [{arr.Length}]: [{string.Join(", ", vals)}{suffix}]");
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ResultArgs] {key} dump error: {ex.Message}");
            }
        }

        /// <summary>Dumps all Args keys/values to the debug log.</summary>
        private static void DumpArgsToLog(string vcName, Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> args)
        {
            try
            {
                MelonLogger.Msg($"[ResultArgs] === Args for {vcName} ({args.Count} keys) ===");
                foreach (var entry in args)
                {
                    string key = entry.Key;
                    var val = entry.Value;
                    string valStr = "(null)";
                    if (val != null)
                    {
                        // Try unboxing as int first
                        try
                        {
                            int intVal = val.Unbox<int>();
                            valStr = intVal.ToString();
                        }
                        catch
                        {
                            try { valStr = val.ToString(); }
                            catch { valStr = $"({val.GetType()?.Name})"; }
                            if (valStr == val.GetType()?.FullName || valStr.Length > 200)
                                valStr = $"[{val.GetType()?.Name}]";
                        }
                    }
                    MelonLogger.Msg($"[ResultArgs]   {key} = {valStr}");
                }
                MelonLogger.Msg("[ResultArgs] === End Args ===");
            }
            catch { }
        }

        /// <summary>
        /// Diagnostic: dumps the full UI tree of a result/reward screen.
        /// Logs every GameObject with its depth, name, and any Text component values.
        /// This helps us understand where reward data (card names, quantities) lives.
        /// </summary>
        private void DumpResultScreenTree(GameObject root)
        {
            try
            {
                MelonLogger.Msg($"[ResultDiag] === UI Tree for {root.name} ===");
                DumpTransformTree(root.transform, 0);

                // Also scan siblings (e.g. TIM_LvUp_ItemGet is a sibling, not a child)
                var parent = root.transform.parent;
                if (parent != null)
                {
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        var sibling = parent.GetChild(i);
                        if (sibling != null && sibling.gameObject != root
                            && sibling.gameObject.activeSelf)
                        {
                            MelonLogger.Msg($"[ResultDiag] --- Sibling: {sibling.gameObject.name} ---");
                            DumpTransformTree(sibling, 0);
                        }
                    }
                }

                MelonLogger.Msg($"[ResultDiag] === End UI Tree ===");
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ResultDiag] Dump error: {ex.Message}");
            }
        }

        private void DumpTransformTree(Transform t, int depth)
        {
            if (t == null) return;

            string indent = new string(' ', depth * 2);
            string goName = t.gameObject.name;
            bool active = t.gameObject.activeSelf;

            // Collect component info
            var parts = new List<string>();

            // Text component
            try
            {
                var text = t.GetComponent<Text>();
                if (text != null && !string.IsNullOrWhiteSpace(text.text))
                {
                    string val = text.text.Replace("\n", "\\n");
                    if (val.Length > 80) val = val.Substring(0, 80) + "...";
                    parts.Add($"Text=\"{val}\"");
                }
            }
            catch { }

            // Image component (note sprite name for card identification)
            try
            {
                var img = t.GetComponent<UnityEngine.UI.Image>();
                if (img != null && img.sprite != null)
                {
                    string spriteName = img.sprite.name;
                    if (!string.IsNullOrEmpty(spriteName) && spriteName != goName)
                        parts.Add($"Sprite={spriteName}");
                }
            }
            catch { }

            // Button/Selectable
            try
            {
                var btn = t.GetComponent<UnityEngine.UI.Selectable>();
                if (btn != null)
                    parts.Add($"Selectable({btn.GetType().Name})");
            }
            catch { }

            string info = parts.Count > 0 ? " [" + string.Join(", ", parts) + "]" : "";
            string activeStr = active ? "" : " (INACTIVE)";

            // Only log if active or has meaningful info, to reduce noise
            if (active || parts.Count > 0)
                MelonLogger.Msg($"[ResultDiag] {indent}{goName}{activeStr}{info}");

            // Recurse children (limit depth to avoid excessive output)
            if (depth < 16)
            {
                for (int i = 0; i < t.childCount; i++)
                {
                    try { DumpTransformTree(t.GetChild(i), depth + 1); }
                    catch { }
                }
            }
        }

        /// <summary>
        /// Polls for text changes on the current screen root.
        /// When text changes (new dialogue line), auto-reads it.
        /// </summary>
        private void PollTextChanges()
        {
            if (_screenRoot == null) return;

            try
            {
                string currentText = ReadScreenText(_screenRoot);
                if (string.IsNullOrEmpty(currentText)) return;

                if (currentText != _lastReadText)
                {
                    _lastReadText = currentText;
                    ScreenReader.Say(currentText);
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"Text changed: {currentText.Substring(0, Math.Min(100, currentText.Length))}");
                }
            }
            catch { }
        }

        /// <summary>
        /// Reads all visible text from a screen hierarchy.
        /// Returns combined text from all active Text/YgomTextAccessor components.
        /// </summary>
        private string ReadScreenText(GameObject root)
        {
            var parts = new List<string>();

            // Try YgomTextAccessor first
            try
            {
                var ytas = root.GetComponentsInChildren<Il2CppYgomSystem.UI.YgomTextAccessor>(true);
                if (ytas != null)
                {
                    foreach (var yta in ytas)
                    {
                        if (yta == null) continue;
                        if (yta.gameObject == null || !yta.gameObject.activeInHierarchy) continue;
                        string t = LabelExtractor.StripRichText(yta.text);
                        if (string.IsNullOrWhiteSpace(t) || t.Length < 2) continue;
                        if (t.All(char.IsDigit)) continue;
                        if (!parts.Contains(t))
                            parts.Add(t);
                    }
                }
            }
            catch { }

            // Fall back to Unity Text if no YTA text found
            if (parts.Count == 0)
            {
                try
                {
                    var texts = root.GetComponentsInChildren<Text>(true);
                    if (texts != null)
                    {
                        foreach (var text in texts)
                        {
                            if (text == null) continue;
                            if (text.gameObject == null || !text.gameObject.activeInHierarchy) continue;
                            string t = LabelExtractor.StripRichText(text.text);
                            if (string.IsNullOrWhiteSpace(t) || t.Length < 2) continue;
                            if (t.All(char.IsDigit)) continue;
                            if (!parts.Contains(t))
                                parts.Add(t);
                        }
                    }
                }
                catch { }
            }

            return string.Join(". ", parts);
        }

        /// <summary>
        /// Gets the root GameObject of the currently active screen.
        /// Checks content and base managers (not dialog — that's DialogHandler's domain).
        /// </summary>
        private GameObject GetActiveScreenRoot()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return null;

                // Try content manager first (main game screens)
                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (namedManager.TryGetValue("content", out contentMgr) && contentMgr != null)
                {
                    var topVc = contentMgr.GetStackTopViewController();
                    if (topVc?.gameObject != null)
                    {
                        string topName = topVc.gameObject.name;

                        // TutorialArrowPart is a tutorial overlay pushed on top of
                        // the actual content page (e.g. Duel Trials HtjsonPage).
                        // Scan the page underneath instead of the empty arrow.
                        if (topName == "TutorialArrowPart" || topName == "TutorialArrow")
                        {
                            int count = contentMgr.GetStackCount();
                            if (count >= 2)
                            {
                                var belowVc = contentMgr.GetStackViewController(count - 2);
                                if (belowVc?.gameObject != null)
                                {
                                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                        $"Skipping {topName}, scanning {belowVc.gameObject.name} underneath");
                                    return belowVc.gameObject;
                                }
                            }
                        }

                        return topVc.gameObject;
                    }
                }

                // Fall back to base manager
                var root = GetTopViewGameObject(namedManager, "base");
                if (root != null) return root;

                return null;
            }
            catch
            {
                return null;
            }
        }

        private GameObject GetTopViewGameObject(
            Il2CppSystem.Collections.Generic.Dictionary<string,
                Il2CppYgomSystem.UI.ViewControllerManager> managers,
            string managerName)
        {
            try
            {
                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!managers.TryGetValue(managerName, out mgr)) return null;
                if (mgr == null) return null;

                var topVc = mgr.GetStackTopViewController();
                if (topVc == null) return null;

                return topVc.gameObject;
            }
            catch
            {
                return null;
            }
        }

        private void FindSliders(GameObject root)
        {
            try
            {
                var sliders = root.GetComponentsInChildren<Slider>(true);
                if (sliders == null) return;

                foreach (var slider in sliders)
                {
                    if (slider == null) continue;
                    var go = slider.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;

                    string label = LabelExtractor.GetSliderLabel(slider);
                    _items.Add(new ScreenItem
                    {
                        Go = go,
                        Label = label,
                        Type = ItemType.Slider,
                        SliderComponent = slider
                    });

                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"Slider: \"{label}\" value={slider.value}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] FindSliders error: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds 3D map objects (NPCs, events, gifts) on the Duel World screen.
        /// These are MapObjectBase components on GameObjects in the scene,
        /// not UI selectables, so they require a separate scan.
        /// </summary>
        private void FindMapObjects()
        {
            try
            {
                var mapObjects = UnityEngine.Object.FindObjectsOfType<
                    Il2CppYgomGame.Single.MapObjectBase>();
                if (mapObjects == null) return;

                foreach (var mapObj in mapObjects)
                {
                    if (mapObj == null) continue;
                    var go = mapObj.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;

                    // Log every map object before filtering, to catch special types
                    bool isSchool = false;
                    try
                    {
                        isSchool = mapObj.TryCast<Il2CppYgomGame.Single.SchoolObject>() != null;
                    }
                    catch { }

                    var data = mapObj.mapObjectData;
                    if (data == null)
                    {
                        if (isSchool)
                            DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                $"SchoolObject FOUND but mapObjectData is null. GO={go.name}");
                        continue;
                    }

                    // Skip non-tappable or hidden objects
                    try
                    {
                        if (data.notTap || data.hidden)
                        {
                            if (isSchool)
                                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                    $"SchoolObject FOUND but filtered: notTap={data.notTap} hidden={data.hidden} GO={go.name}");
                            continue;
                        }
                    }
                    catch { continue; }

                    // Build a label from the object type and GO name
                    string typeName;
                    try
                    {
                        var objType = data.type;
                        typeName = objType switch
                        {
                            Il2CppYgomGame.Single.MapObjectType.NPCChallenge => Loc.Get("map_npc_challenge"),
                            Il2CppYgomGame.Single.MapObjectType.NPCMob => Loc.Get("map_npc_standard"),
                            Il2CppYgomGame.Single.MapObjectType.NPCOrigin => Loc.Get("map_npc_legendary"),
                            Il2CppYgomGame.Single.MapObjectType.FoundGift => Loc.Get("map_gift"),
                            Il2CppYgomGame.Single.MapObjectType.CardTrader => Loc.Get("map_card_trader"),
                            Il2CppYgomGame.Single.MapObjectType.NPCTrainer => Loc.Get("map_npc_trainer"),
                            Il2CppYgomGame.Single.MapObjectType.BonusDuelist => Loc.Get("map_npc_bonus"),
                            _ => go.name
                        };
                    }
                    catch
                    {
                        typeName = go.name;
                    }

                    // Identify special map objects by IL2CPP type
                    try
                    {
                        var school = mapObj.TryCast<Il2CppYgomGame.Single.SchoolObject>();
                        if (school != null)
                            typeName = Loc.Get("map_school");
                    }
                    catch { }

                    _items.Add(new ScreenItem
                    {
                        Go = go,
                        Label = typeName,
                        Type = ItemType.MapObject
                    });

                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"MapObject: \"{typeName}\" GO={go.name}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] FindMapObjects error: {ex.Message}");
            }
        }

        /// <summary>
        /// Finds DeckSelectItem components on deck screens.
        /// These are MonoBehaviours (not Selectables), so FindButtons() won't find them.
        /// Each has a deckNameText (Text) for the label and OnClicked() for activation.
        /// </summary>
        private void FindDeckItems(GameObject root)
        {
            try
            {
                var deckItems = root.GetComponentsInChildren<
                    Il2CppYgomGame.Deck.DeckSelectItem>(true);
                if (deckItems == null) return;

                foreach (var deckItem in deckItems)
                {
                    if (deckItem == null) continue;
                    var go = deckItem.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;

                    string label = "";
                    try
                    {
                        var nameText = deckItem.deckNameText;
                        if (nameText != null)
                            label = LabelExtractor.StripRichText(nameText.text);
                    }
                    catch { }

                    if (string.IsNullOrWhiteSpace(label))
                    {
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            $"DeckItem: skipping (no name text) GO={go.name}");
                        continue;
                    }

                    _items.Add(new ScreenItem
                    {
                        Go = go,
                        Label = label,
                        Type = ItemType.Button
                    });

                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"DeckItem: \"{label}\" GO={go.name}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] FindDeckItems error: {ex.Message}");
            }
        }

        private void FindButtons(GameObject root)
        {
            try
            {
                // Find all Selectables (Button, YgomButton, Toggle, etc.)
                var selectables = root.GetComponentsInChildren<Selectable>(true);
                if (selectables == null) return;

                var processed = new HashSet<GameObject>();
                foreach (var item in _items)
                    processed.Add(item.Go);

                foreach (var selectable in selectables)
                {
                    if (selectable == null) continue;
                    if (!selectable.interactable) continue;

                    var go = selectable.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;
                    if (processed.Contains(go)) continue;

                    // Skip slider sub-elements (already found as Sliders)
                    if (selectable is Slider) continue;
                    if (IsChildOfSlider(go, root)) continue;

                    // Skip common layout/background elements
                    string goName = go.name.ToLower();
                    if (IsLayoutElement(goName)) continue;

                    // Skip Htjson noise elements (decorative icons, promo banners)
                    if (IsJunkHtjsonElement(go.name)) continue;

                    string label = LabelExtractor.GetLabel(go);

                    // Annotate tab buttons so users can distinguish tabs from content
                    try
                    {
                        var ygomTab = go.GetComponent<Il2CppYgomSystem.UI.YgomTabButton>();
                        if (ygomTab == null)
                            ygomTab = go.GetComponentInParent<Il2CppYgomSystem.UI.YgomTabButton>();
                        if (ygomTab != null)
                            label += ygomTab.isSelected ? ", selected tab" : ", tab";
                    }
                    catch { }

                    processed.Add(go);
                    _items.Add(new ScreenItem
                    {
                        Go = go,
                        Label = label,
                        Type = ItemType.Button
                    });

                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"Button: \"{label}\" ({go.name})");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] FindButtons error: {ex.Message}");
            }
        }

        private static bool IsLayoutElement(string goName)
        {
            return goName == "vl" || goName == "hl" || goName == "background"
                || goName == "mask" || goName == "fillbg" || goName == "fill"
                || goName == "fillmask" || goName == "handle"
                || goName == "handleslidearea" || goName == "fillarea"
                || goName == "slidingarea" || goName == "frame"
                || goName == "bgcover" || goName == "titleset";
        }

        /// <summary>
        /// Filters out Htjson noise elements that clutter the scan without adding value.
        /// "Icon" elements are decorative images next to ButtonSet items.
        /// "Crst" is the crystal store promotional banner in screen headers.
        /// </summary>
        private static bool IsJunkHtjsonElement(string goName)
        {
            return goName == "Icon" || goName == "Crst";
        }

        private bool IsChildOfSlider(GameObject go, GameObject root)
        {
            try
            {
                var sliders = root.GetComponentsInChildren<Slider>(true);
                if (sliders == null) return false;

                foreach (var slider in sliders)
                {
                    if (slider == null) continue;
                    if (go.transform.IsChildOf(slider.transform))
                        return true;
                }
            }
            catch { }
            return false;
        }

        #endregion

        #region Input

        private void ProcessInput()
        {
            if (_textMode)
            {
                ProcessTextModeInput();
                return;
            }

            if (_items.Count == 0) return;

            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.UpArrow))
            {
                _focusIndex--;
                if (_focusIndex < 0) _focusIndex = _items.Count - 1;
                AnnounceCurrentItem();
            }
            else if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.DownArrow))
            {
                _focusIndex++;
                if (_focusIndex >= _items.Count) _focusIndex = 0;
                AnnounceCurrentItem();
            }
            else if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.LeftArrow))
            {
                if (GameStateTracker.CurrentScreen == GameStateTracker.GameScreen.Home)
                    CycleMapArea(-1);
                else
                    AdjustSlider(-1);
            }
            else if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.RightArrow))
            {
                if (GameStateTracker.CurrentScreen == GameStateTracker.GameScreen.Home)
                    CycleMapArea(1);
                else
                    AdjustSlider(1);
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter))
            {
                ActivateCurrentItem();
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                AnnounceCurrentItem();
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.T))
            {
                // Re-read page text (Htjson pages with explanatory content)
                if (!string.IsNullOrEmpty(_lastReadText))
                    ScreenReader.Say(_lastReadText);
                else
                    ScreenReader.Say(Loc.Get("screen_no_text"));
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                // If TutorialArrow is active, dismiss it instead of rescanning
                if (IsTutorialArrowActive())
                {
                    DismissTutorialArrow();
                    return;
                }
                _scanned = false;
                _scanDelay = 0.3f;
                _scanAttempts = 0;
                ScreenReader.Say(Loc.Get("screen_rescan"));
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.Backspace))
            {
                GoBack();
            }
        }

        /// <summary>
        /// Input processing for text-only screens (scenarios, tutorials).
        /// Enter/Space to advance, Tab to re-read.
        /// </summary>
        private void ProcessTextModeInput()
        {
            if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter)
                || InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                AdvanceScreen();
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                // Re-read current text
                if (!string.IsNullOrEmpty(_lastReadText))
                    ScreenReader.Say(_lastReadText);
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.Backspace))
            {
                GoBack();
            }
        }

        #endregion

        #region Actions

        private void AnnounceCurrentItem(bool queued = false)
        {
            if (_focusIndex < 0 || _focusIndex >= _items.Count) return;

            var item = _items[_focusIndex];

            // Guard: button may have been destroyed since scan
            if (!LabelExtractor.IsAlive(item.Go))
            {
                _items.RemoveAt(_focusIndex);
                if (_focusIndex >= _items.Count) _focusIndex = Math.Max(0, _items.Count - 1);
                return;
            }

            int pos = _focusIndex + 1;
            int total = _items.Count;

            string msg;
            if (item.Type == ItemType.Slider && item.SliderComponent != null)
            {
                int val = Mathf.RoundToInt(item.SliderComponent.value);
                int min = Mathf.RoundToInt(item.SliderComponent.minValue);
                int max = Mathf.RoundToInt(item.SliderComponent.maxValue);
                msg = Loc.Get("screen_slider_item", pos, total, item.Label, val, min, max);
            }
            else
            {
                msg = Loc.Get("screen_button_item", pos, total, item.Label);
            }

            if (queued)
                ScreenReader.SayQueued(msg);
            else
                ScreenReader.Say(msg);
        }

        private void AdjustSlider(int direction)
        {
            if (_focusIndex < 0 || _focusIndex >= _items.Count) return;

            var item = _items[_focusIndex];
            if (item.Type != ItemType.Slider || item.SliderComponent == null)
                return;

            var slider = item.SliderComponent;
            float step = slider.wholeNumbers ? 1f : (slider.maxValue - slider.minValue) / 20f;
            float newValue = Mathf.Clamp(
                slider.value + direction * step,
                slider.minValue,
                slider.maxValue);

            slider.value = newValue;
            int val = Mathf.RoundToInt(newValue);
            ScreenReader.Say(val.ToString());
        }

        /// <summary>
        /// Cycles between Duel World map areas by calling SingleViewController.StartmapRotation(int).
        /// </summary>
        private void CycleMapArea(int direction)
        {
            _currentMapArea += direction;
            if (_currentMapArea < 0) _currentMapArea = _mapAreaNames.Length - 1;
            if (_currentMapArea >= _mapAreaNames.Length) _currentMapArea = 0;

            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("content", out mgr) || mgr == null) return;

                var topVc = mgr.GetStackTopViewController();
                if (topVc == null) return;

                var singleVcType = FindVcType("Single");
                if (singleVcType == null)
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        "SingleViewController type not found");
                    return;
                }

                var method = singleVcType.GetMethod("StartmapRotation",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(int) }, null);
                if (method == null)
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        "StartmapRotation(int) not found");
                    return;
                }

                var ctor = singleVcType.GetConstructor(new[] { typeof(IntPtr) });
                if (ctor == null) return;

                var castVc = ctor.Invoke(new object[] { topVc.Pointer });
                method.Invoke(castVc, new object[] { _currentMapArea });

                string areaName = Loc.Get($"map_area_{_mapAreaNames[_currentMapArea].ToLower()}");
                ScreenReader.Say(areaName);

                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"Map area changed to {_mapAreaNames[_currentMapArea]} ({_currentMapArea})");
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] CycleMapArea error: {ex.Message}");
            }
        }

        /// <summary>
        /// Activates a 3D map object by calling MapObjectBase.TapObject(ViewControllerManager).
        /// </summary>
        private void ActivateMapObject(ScreenItem item)
        {
            try
            {
                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"Tapping map object: {item.Label} ({item.Go.name})");

                var mapObj = item.Go.GetComponent<Il2CppYgomGame.Single.MapObjectBase>();
                if (mapObj == null)
                {
                    ScreenReader.Say(Loc.Get("screen_click_error"));
                    return;
                }

                // Get the content ViewControllerManager to pass to TapObject
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null)
                {
                    ScreenReader.Say(Loc.Get("screen_click_error"));
                    return;
                }

                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedManager.TryGetValue("content", out contentMgr) || contentMgr == null)
                {
                    ScreenReader.Say(Loc.Get("screen_click_error"));
                    return;
                }

                mapObj.TapObject(contentMgr);
                ScreenReader.Say(item.Label);
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] ActivateMapObject error: {ex.Message}");
                ScreenReader.Say(Loc.Get("screen_click_error"));
            }
        }

        private void ActivateCurrentItem()
        {
            if (_focusIndex < 0 || _focusIndex >= _items.Count) return;

            var item = _items[_focusIndex];

            // Guard: button may have been destroyed since scan
            if (!LabelExtractor.IsAlive(item.Go))
            {
                ScreenReader.Say(Loc.Get("screen_click_error"));
                _items.RemoveAt(_focusIndex);
                if (_focusIndex >= _items.Count) _focusIndex = Math.Max(0, _items.Count - 1);
                return;
            }

            // Map objects: call TapObject on the MapObjectBase component
            if (item.Type == ItemType.MapObject)
            {
                ActivateMapObject(item);
                return;
            }

            // Header Back button: use GoBack() directly (SendBack on content VC).
            // The BackButton has no onClick listener and TryCallVcMethod only checks
            // the content VC, not the header VC where BackButton lives.
            if (item.Go.name == "BackButton")
            {
                GoBack();
                return;
            }

            // Deck items: call OnClicked() directly (DeckSelectItem is not a Selectable)
            try
            {
                var deckItem = item.Go.GetComponent<Il2CppYgomGame.Deck.DeckSelectItem>();
                if (deckItem != null)
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"Activating deck item: {item.Label}");
                    deckItem.OnClicked();
                    ScreenReader.Say(item.Label);
                    return;
                }
            }
            catch { }

            // Route through TutorialArrow ipclick if present — direct clicks
            // don't satisfy the tutorial condition (documented in game-api.md)
            if (IsTutorialArrowActive() && ActivateViaTutorialArrow(item.Label))
                return;

            try
            {
                // On result screens, schedule a rescan after pressing NEXT/OK
                // to pick up page 2 content (assessment, score, rewards)
                if (_screenRoot != null
                    && (_screenRoot.name == "ResultBasePage" || _screenRoot.name == "ReplayResult"))
                {
                    _resultRescanTimer = 2f;
                }

                // Detect tab button activation — schedule content rescan
                try
                {
                    var tabBtn = item.Go.GetComponent<Il2CppYgomSystem.UI.YgomTabButton>();
                    if (tabBtn == null)
                        tabBtn = item.Go.GetComponentInParent<Il2CppYgomSystem.UI.YgomTabButton>();
                    if (tabBtn != null)
                    {
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            "Tab button activated, scheduling content rescan");
                        _tabRescanDelay = 1.0f;
                    }
                }
                catch { }

                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"Activating: {item.Label} ({item.Go.name})");

                var eventData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);

                // Strategy 1: Htjson ButtonWidget — get its YgomButton and fire
                // the full click cycle so the Htjson dispatch chain fires properly.
                bool handled = false;
                var buttonWidget = item.Go.GetComponentInParent<Il2CppYgomSystem.Htjson.ButtonWidget>();
                if (buttonWidget != null)
                {
                    var ygomBtn = buttonWidget.button;
                    if (ygomBtn != null && ygomBtn.gameObject != null)
                    {
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            $"Htjson ButtonWidget found, clicking YgomButton on {ygomBtn.gameObject.name}");
                        var btnGo = ygomBtn.gameObject;
                        UnityEngine.EventSystems.ExecuteEvents.Execute(
                            btnGo, eventData,
                            UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
                        UnityEngine.EventSystems.ExecuteEvents.Execute(
                            btnGo, eventData,
                            UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
                        UnityEngine.EventSystems.ExecuteEvents.Execute(
                            btnGo, eventData,
                            UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                        handled = true;
                    }
                }

                // Strategy 1b: Htjson CheckBoxWidget — extends Button directly,
                // OnPointerClick dispatches to its HtjsonReceiver.
                if (!handled)
                {
                    var checkBox = item.Go.GetComponent<Il2CppYgomSystem.Htjson.CheckBoxWidget>();
                    if (checkBox != null)
                    {
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            $"Htjson CheckBoxWidget found on {item.Go.name}");
                        checkBox.OnPointerClick(eventData);
                        handled = true;
                    }
                }

                // Strategy 2: Full click cycle via ExecuteEvents (standard Unity buttons).
                // Uses pointerDown → pointerUp → pointerClick to better simulate real
                // touch input. Some UI elements need the full cycle to trigger properly.
                if (!handled)
                {
                    UnityEngine.EventSystems.ExecuteEvents.Execute(
                        item.Go, eventData,
                        UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
                    UnityEngine.EventSystems.ExecuteEvents.Execute(
                        item.Go, eventData,
                        UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
                    handled = UnityEngine.EventSystems.ExecuteEvents.Execute(
                        item.Go, eventData,
                        UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                }

                // Strategy 3: Try On{GoName}Button() on the content ViewController.
                // Many game screens wire world/map buttons to VC methods by convention.
                // Always attempt this even if ExecuteEvents returned true — world buttons
                // (Gate, Shop, Labo) have YgomButton children whose OnPointerClick only
                // plays a sound effect without navigating. The actual navigation is done
                // by the VC method (OnShopButton, OnGateButton, etc.).
                if (!handled)
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"ExecuteEvents found no handler on {item.Go.name}, trying VC method");
                }
                if (TryCallVcMethod(item.Go))
                    handled = true;

                // Strategy 4: Htjson ButtonSet / div — ExecuteEvents on these only
                // fires YgomButton's sound effect (IPointerClickHandler), not the
                // Button.onClick listeners where the Htjson link/expand handler is
                // registered. Fire onClick.Invoke() directly on the item and its
                // parents to trigger the actual handler.
                if (item.Go.name == "ButtonSet" || item.Go.name == "div")
                {
                    try
                    {
                        // Try onClick.Invoke on the item itself
                        var btn = item.Go.GetComponent<Button>();
                        if (btn != null && btn.onClick != null)
                        {
                            DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                $"ButtonSet onClick.Invoke on {item.Go.name}");
                            btn.onClick.Invoke();
                            handled = true;
                        }

                        // Also walk up parents and fire onClick on any Button found.
                        // The expand/link handler is often on the container parent.
                        var parent = item.Go.transform.parent;
                        int depth = 0;
                        while (parent != null && depth < 4)
                        {
                            var parentBtn = parent.GetComponent<Button>();
                            if (parentBtn != null && parentBtn.onClick != null)
                            {
                                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                    $"ButtonSet parent onClick.Invoke on {parent.gameObject.name}");
                                parentBtn.onClick.Invoke();
                                handled = true;
                                break;
                            }
                            parent = parent.parent;
                            depth++;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            $"ButtonSet onClick failed: {ex.Message}");
                    }
                }

                // Strategy 4b: TextArea with sibling ButtonSet — Htjson link handler.
                // SetButtonLink binds webapi URLs as runtime onClick listeners on
                // the ButtonSet, not the TextArea. Check siblings when on a TextArea.
                if (!handled && item.Go.name == "TextArea")
                {
                    var itemParent = item.Go.transform.parent;
                    if (itemParent != null)
                    {
                        var siblingBtnSet = itemParent.Find("ButtonSet");
                        if (siblingBtnSet != null)
                        {
                            var btnSetBtn = siblingBtnSet.GetComponent<Button>();
                            if (btnSetBtn != null && btnSetBtn.onClick != null)
                            {
                                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                    $"TextArea -> sibling ButtonSet onClick.Invoke");
                                btnSetBtn.onClick.Invoke();
                                handled = true;
                            }
                        }
                    }
                }

                // Strategy 5: onClick.Invoke as last resort
                if (!handled)
                {
                    var button = item.Go.GetComponent<Button>();
                    if (button != null)
                    {
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            $"Trying onClick.Invoke on {item.Go.name}");
                        button.onClick.Invoke();
                        handled = true;
                    }
                }

                if (handled)
                    ScreenReader.Say(item.Label);
                else
                    ScreenReader.Say(Loc.Get("screen_click_error"));
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] Activate error: {ex.Message}");
                ScreenReader.Say(Loc.Get("screen_click_error"));
            }
        }

        /// <summary>
        /// Tries to call a matching method on the content ViewController for a button.
        /// Searches for On{GoName}Button() or On{GoName}() on the VC via reflection.
        /// This is the generic pattern used by many game screens (e.g. SingleViewController
        /// has OnGateButton, OnShopButton, OnLaboButton, OnColosseumButton).
        ///
        /// If the initial GO name doesn't match, walks up the parent hierarchy to find
        /// a meaningful name. This handles dedup'd buttons where the child "YgomButton"
        /// was kept but the parent "Gate"/"Shop"/"Labo" has the name the VC method expects.
        ///
        /// IL2CPP note: GetStackTopViewController() returns the base ViewController type.
        /// We must find the actual derived type (e.g. SingleViewController) by searching
        /// loaded assemblies, then create a properly-typed wrapper around the same pointer.
        /// </summary>
        private bool TryCallVcMethod(GameObject buttonGo)
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return false;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("content", out mgr) || mgr == null)
                    return false;

                var topVc = mgr.GetStackTopViewController();
                if (topVc == null || topVc.gameObject == null) return false;

                string vcGoName = topVc.gameObject.name;
                var actualType = FindVcType(vcGoName);
                if (actualType == null)
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"No VC type found for GO '{vcGoName}'");
                    return false;
                }

                // Try the button's GO name, then walk up parents to find a match.
                // World buttons have a parent hierarchy like Gate > YgomButton,
                // and the VC method is OnGateButton(), not OnYgomButtonButton().
                var current = buttonGo.transform;
                int maxDepth = 4;
                while (current != null && maxDepth-- > 0)
                {
                    string cleanName = current.gameObject.name.Replace("(Clone)", "").Trim();

                    string[] methodNames = {
                        $"On{cleanName}Button",
                        $"On{cleanName}"
                    };

                    foreach (var methodName in methodNames)
                    {
                        var method = actualType.GetMethod(methodName,
                            BindingFlags.Public | BindingFlags.Instance,
                            null, Type.EmptyTypes, null);

                        if (method != null)
                        {
                            var ctor = actualType.GetConstructor(new[] { typeof(IntPtr) });
                            if (ctor == null) continue;

                            var castVc = ctor.Invoke(new object[] { topVc.Pointer });
                            DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                $"Calling {actualType.Name}.{methodName}()");
                            method.Invoke(castVc, null);
                            return true;
                        }
                    }

                    current = current.parent;
                }

                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"No matching VC method for {buttonGo.name} (checked parents)");
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] VC method error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Cache of VC GO name → actual managed Type for derived ViewControllers.
        /// </summary>
        private static readonly Dictionary<string, Type> _vcTypeCache = new();

        /// <summary>
        /// Finds the actual derived ViewController type for a given GO name.
        /// Searches loaded assemblies for {GoName}ViewController with an IntPtr constructor
        /// (the standard IL2CPP interop wrapper pattern).
        /// </summary>
        private static Type FindVcType(string vcGoName)
        {
            if (_vcTypeCache.TryGetValue(vcGoName, out var cached))
                return cached;

            string targetName = vcGoName + "ViewController";

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name != targetName) continue;
                        if (type.GetConstructor(new[] { typeof(IntPtr) }) == null) continue;

                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            $"Found VC type: {type.FullName} for GO '{vcGoName}'");
                        _vcTypeCache[vcGoName] = type;
                        return type;
                    }
                }
                catch { }
            }

            DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                $"No VC type found matching '{targetName}'");
            _vcTypeCache[vcGoName] = null;
            return null;
        }

        /// <summary>
        /// Advances a text-mode screen by simulating a pointer click on the root.
        /// This triggers OnPointerClick on scenario/dialogue screens.
        /// </summary>
        private void AdvanceScreen()
        {
            if (_screenRoot == null) return;

            try
            {
                // ScenarioPlayerPart: advance the scenario text via click events.
                // Always try direct ExecuteEvents first — TutorialArrow ipclick may
                // point to an unrelated UI element (e.g., DuelButton) rather than
                // the scenario advancement control.
                if (_screenRoot.name.Contains("Scenario"))
                {
                    var scenarioVc = _screenRoot.GetComponent<Il2CppYgomGame.Scenario.ScenarioPlayViewController>();
                    if (scenarioVc != null)
                    {
                        var go = scenarioVc.gameObject;
                        var eventData = new UnityEngine.EventSystems.PointerEventData(
                            UnityEngine.EventSystems.EventSystem.current);
                        eventData.position = new Vector2(
                            Screen.width / 2f, Screen.height / 2f);
                        eventData.button = UnityEngine.EventSystems.PointerEventData.InputButton.Left;
                        eventData.clickCount = 1;

                        // Populate raycast data so OnPointerClick sees a valid hit.
                        // Some scenarios reject clicks with empty raycast info
                        // (stage-up animations, transition screens).
                        var rayResult = new UnityEngine.EventSystems.RaycastResult();
                        rayResult.gameObject = go;
                        eventData.pointerCurrentRaycast = rayResult;
                        eventData.pointerPressRaycast = rayResult;
                        eventData.pointerPress = go;
                        eventData.pressPosition = eventData.position;

                        // Full click cycle: down → up → click
                        UnityEngine.EventSystems.ExecuteEvents.Execute(
                            go, eventData, UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
                        UnityEngine.EventSystems.ExecuteEvents.Execute(
                            go, eventData, UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
                        bool handled = UnityEngine.EventSystems.ExecuteEvents.Execute(
                            go, eventData, UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);

                        // Fallback: call OnPointerClick directly on the VC instance.
                        // ExecuteEvents may fail if the VC's IPointerClickHandler
                        // isn't found via interface lookup on this GO.
                        if (!handled)
                            scenarioVc.OnPointerClick(eventData);

                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            $"Advancing ScenarioPlayerPart (ExecuteEvents={handled})");
                        return;
                    }
                }

                // For non-scenario screens, try TutorialArrow ipclick if one is
                // on the dialog stack (the arrow may be the advancement mechanism).
                if (AdvanceViaTutorialArrow())
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        "Advancing via TutorialArrow ipclick (dialog stack had arrow)");
                    return;
                }

                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    "Advancing screen (click)");
                ClickGameObject(_screenRoot);
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] Advance error: {ex.Message}");
            }
        }

        /// <summary>
        /// Routes a click through the TutorialArrowPart's ipclick handlers.
        /// The game requires scenario clicks to go through the arrow — direct
        /// OnPointerClick on ScenarioPlayViewController doesn't advance the text
        /// while the arrow is on the dialog stack.
        /// </summary>
        private bool AdvanceViaTutorialArrow()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return false;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("dialog", out mgr)) return false;

                var topVc = mgr?.GetStackTopViewController();
                if (topVc?.gameObject == null) return false;

                string name = topVc.gameObject.name;
                if (name != "TutorialArrow" && name != "TutorialArrowPart")
                    return false;

                var arrowVc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                if (arrowVc == null) return false;

                var ipclick = arrowVc.ipclick;
                if (ipclick == null || ipclick.Length == 0) return false;

                var eventData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);
                eventData.position = new Vector2(Screen.width / 2f, Screen.height / 2f);

                for (int i = 0; i < ipclick.Length; i++)
                {
                    try
                    {
                        var handler = ipclick[i];
                        if (handler == null) continue;

                        var button = handler.TryCast<Il2CppYgomSystem.UI.YgomButton>();
                        if (button != null)
                        {
                            DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                $"Advancing scenario via ipclick[{i}] YgomButton on {button.gameObject?.name ?? "?"}");
                            button.OnPointerClick(eventData);
                            return true;
                        }

                        var mb = handler.TryCast<MonoBehaviour>();
                        if (mb?.gameObject != null)
                        {
                            DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                $"Advancing scenario via ipclick[{i}] {mb.GetIl2CppType().Name} on {mb.gameObject.name}");
                            UnityEngine.EventSystems.ExecuteEvents.Execute(
                                mb.gameObject, eventData,
                                UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                            return true;
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Msg($"[ScreenBtn] ipclick[{i}] error: {ex.Message}");
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Simulates a pointer click on a GameObject via ExecuteEvents.
        /// </summary>
        private void ClickGameObject(GameObject target)
        {
            var eventData = new UnityEngine.EventSystems.PointerEventData(
                UnityEngine.EventSystems.EventSystem.current);
            UnityEngine.EventSystems.ExecuteEvents.Execute(
                target, eventData,
                UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
        }

        /// <summary>
        /// Triggers back navigation via HeaderViewController.OnBackButton().
        /// This is what the game's actual back button calls, so it handles
        /// TutorialArrow and other blockers correctly.
        /// Falls back to SendBack() on the content VC if no header is found.
        /// </summary>
        private void GoBack()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return;

                // Capture content VC name before attempting back navigation
                // so we can detect if OnBackButton actually navigated.
                string vcNameBefore = null;
                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (namedManager.TryGetValue("content", out contentMgr) && contentMgr != null)
                {
                    var contentVc = contentMgr.GetStackTopViewController();
                    if (contentVc?.gameObject != null)
                        vcNameBefore = contentVc.gameObject.name;
                }

                // Try HeaderViewController.OnBackButton() first — this is the game's
                // native back button handler and works even with TutorialArrow active.
                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (namedManager.TryGetValue("base", out mgr) && mgr != null)
                {
                    var topVc = mgr.GetStackTopViewController();
                    if (topVc != null)
                    {
                        var header = topVc.TryCast<Il2CppYgomGame.Menu.HeaderViewController>();
                        if (header != null)
                        {
                            // Some screens set lockOnBack to block the back button.
                            // Clear it so our accessibility back navigation always works.
                            bool wasLocked = header.lockOnBack;
                            if (wasLocked)
                            {
                                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                    "GoBack: clearing lockOnBack");
                                header.lockOnBack = false;
                            }
                            DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                "GoBack via HeaderViewController.OnBackButton()");
                            header.OnBackButton();

                            // Check if the content VC actually changed.
                            // Some screens (e.g. DeckSelect from pre-duel menu)
                            // don't respond to OnBackButton — try their own OnBack().
                            string vcNameAfter = null;
                            if (contentMgr != null)
                            {
                                var afterVc = contentMgr.GetStackTopViewController();
                                if (afterVc?.gameObject != null)
                                    vcNameAfter = afterVc.gameObject.name;
                            }

                            if (vcNameBefore != null && vcNameBefore == vcNameAfter)
                            {
                                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                                    $"OnBackButton did not navigate, trying content VC OnBack()");
                                if (TryCallVcOnBack())
                                {
                                    ScreenReader.Say(Loc.Get("screen_back"));
                                    return;
                                }
                            }

                            ScreenReader.Say(Loc.Get("screen_back"));
                            return;
                        }
                    }
                }

                // Fallback: content VC OnBack, then SendBack
                if (TryCallVcOnBack())
                {
                    ScreenReader.Say(Loc.Get("screen_back"));
                    return;
                }

                if (contentMgr != null)
                {
                    var topVc = contentMgr.GetStackTopViewController();
                    if (topVc != null)
                    {
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            "GoBack via content VC SendBack()");
                        topVc.SendBack();
                        ScreenReader.Say(Loc.Get("screen_back"));
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"GoBack error: {ex.Message}");
            }
        }

        /// <summary>
        /// Tries to pop the content VC when OnBackButton didn't navigate.
        /// First tries SendBack() on the content VC (base ViewController method),
        /// then tries popVC() on the derived type (e.g. DeckSelectViewController).
        /// </summary>
        private bool TryCallVcOnBack()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return false;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("content", out mgr) || mgr == null)
                    return false;

                var topVc = mgr.GetStackTopViewController();
                if (topVc?.gameObject == null) return false;

                // Try SendBack first — standard VC back navigation
                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    "GoBack fallback: trying content VC SendBack()");
                topVc.SendBack();

                // Check if SendBack worked
                var afterVc = mgr.GetStackTopViewController();
                string afterName = afterVc?.gameObject?.name;
                if (afterName != topVc.gameObject.name)
                    return true;

                // SendBack didn't navigate — try popVC() on derived type
                string vcGoName = topVc.gameObject.name;
                var actualType = FindVcType(vcGoName);
                if (actualType == null) return false;

                var method = actualType.GetMethod("popVC",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (method == null) return false;

                var ctor = actualType.GetConstructor(new[] { typeof(IntPtr) });
                if (ctor == null) return false;

                var castVc = ctor.Invoke(new object[] { topVc.Pointer });
                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"GoBack fallback: calling {actualType.Name}.popVC()");
                method.Invoke(castVc, null);
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"TryCallVcOnBack error: {ex.Message}");
                return false;
            }
        }

        #endregion

        #region TutorialArrow

        /// <summary>
        /// Checks if TutorialArrow is the top VC on the dialog stack.
        /// TutorialArrow overlays block navigation until dismissed.
        /// </summary>
        private static bool IsTutorialArrowActive()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return false;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("dialog", out mgr)) return false;

                var topVc = mgr?.GetStackTopViewController();
                if (topVc?.gameObject == null) return false;

                string name = topVc.gameObject.name;
                return name == "TutorialArrow" || name == "TutorialArrowPart";
            }
            catch { return false; }
        }

        /// <summary>
        /// Handles TutorialArrow overlay when activating a button.
        /// For click-to-continue arrows (no ipclick): dismisses and returns true.
        /// For pointing arrows: taps the arrow at the physicTarget's screen position
        /// so the arrow VC recognizes it as a click on its target. This is how the
        /// tutorial system expects interaction — it checks the click position against
        /// the target collider. Using screen center silently fails.
        /// After the arrow tap, also falls through to TryCallVcMethod (returns false)
        /// in case the tutorial system blocks navigation.
        /// </summary>
        private bool ActivateViaTutorialArrow(string label)
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return false;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("dialog", out mgr)) return false;

                var topVc = mgr?.GetStackTopViewController();
                if (topVc == null) return false;

                var arrowVc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                if (arrowVc == null) return false;

                var ipclick = arrowVc.ipclick;
                if (ipclick == null || ipclick.Length == 0)
                {
                    // No ipclick handlers — click-to-continue arrow, dismiss it
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        "TutorialArrow has no ipclick, dismissing via OnPointerClick");
                    var dismissData = new UnityEngine.EventSystems.PointerEventData(
                        UnityEngine.EventSystems.EventSystem.current);
                    arrowVc.OnPointerClick(dismissData);
                    ScreenReader.Say(label);
                    return true;
                }

                // Pointing arrow: click at the physicTarget's screen position.
                // The arrow VC checks if the click position overlaps with the
                // target collider — screen center doesn't hit it.
                var physicTarget = arrowVc.physicTarget;
                var eventData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);

                if (physicTarget != null)
                {
                    // Use the arrow's own targetCamera — Camera.main is wrong
                    // for 3D world targets like Collider_Cardshop
                    var cam = arrowVc.targetCamera;
                    if (cam == null) cam = Camera.main;

                    if (cam != null)
                    {
                        Vector3 screenPos = cam.WorldToScreenPoint(
                            physicTarget.transform.position);
                        eventData.position = new Vector2(screenPos.x, screenPos.y);
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            $"Clicking TutorialArrow at physicTarget " +
                            $"({screenPos.x:F0}, {screenPos.y:F0}) via " +
                            $"{cam.name} for {physicTarget.gameObject?.name}");
                    }
                    else
                    {
                        eventData.position = new Vector2(
                            Screen.width / 2f, Screen.height / 2f);
                        DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                            "No camera for physicTarget, using screen center");
                    }
                }
                else
                {
                    eventData.position = new Vector2(Screen.width / 2f, Screen.height / 2f);
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        "No physicTarget, using screen center");
                }

                // RegistPointerCurrentRaycast populates raycast data that
                // IsCollider needs — without it, OnPointerClick silently fails.
                try { arrowVc.RegistPointerCurrentRaycast(eventData); }
                catch (System.Exception ex)
                {
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        $"RegistPointerCurrentRaycast error: {ex.Message}");
                }

                arrowVc.OnPointerClick(eventData);
                ScreenReader.Say(label);

                // Return false so ActivateCurrentItem also tries TryCallVcMethod
                // as a backup in case the tutorial system blocks navigation.
                return false;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"ActivateViaTutorialArrow error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Dismisses the TutorialArrow by calling OnPointerClick on the
        /// TutorialArrowViewController at the screen center.
        /// </summary>
        private static void DismissTutorialArrow()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("dialog", out mgr)) return;

                var topVc = mgr?.GetStackTopViewController();
                if (topVc?.gameObject == null) return;

                var arrowVc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                if (arrowVc == null)
                {
                    // Fallback: generic pointer click
                    var fallbackData = new UnityEngine.EventSystems.PointerEventData(
                        UnityEngine.EventSystems.EventSystem.current);
                    UnityEngine.EventSystems.ExecuteEvents.Execute(
                        topVc.gameObject, fallbackData,
                        UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                    DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                        "TutorialArrow dismissed via fallback click");
                    return;
                }

                var eventData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);
                eventData.position = new Vector2(
                    Screen.width / 2f, Screen.height / 2f);

                arrowVc.OnPointerClick(eventData);
                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    "TutorialArrow dismissed via OnPointerClick");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"DismissTutorialArrow error: {ex.Message}");
            }
        }

        #endregion

        #region Diagnostics

        /// <summary>
        /// Dumps diagnostic info when ScenarioPlayerPart appears with no readable content.
        /// Logs all text components, dialog manager state, and VC hierarchy to help
        /// figure out how to read and dismiss this screen.
        /// </summary>
        private void DumpScenarioState(GameObject root)
        {
            MelonLogger.Msg("[ScenarioDiag] === ScenarioPlayerPart diagnostic dump ===");
            MelonLogger.Msg($"[ScenarioDiag] Root GO: {root.name}, active: {root.activeInHierarchy}");

            // Dump all Text components (including inactive)
            try
            {
                var texts = root.GetComponentsInChildren<Text>(true);
                MelonLogger.Msg($"[ScenarioDiag] Unity Text components: {texts?.Length ?? 0}");
                if (texts != null)
                {
                    foreach (var t in texts)
                    {
                        if (t == null) continue;
                        string content = t.text ?? "(null)";
                        if (content.Length > 100) content = content.Substring(0, 100) + "...";
                        MelonLogger.Msg($"[ScenarioDiag]   Text: \"{content}\" active={t.gameObject?.activeInHierarchy} go={t.gameObject?.name}");
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Msg($"[ScenarioDiag] Text scan error: {ex.Message}"); }

            // Dump all YgomTextAccessor components
            try
            {
                var ytas = root.GetComponentsInChildren<Il2CppYgomSystem.UI.YgomTextAccessor>(true);
                MelonLogger.Msg($"[ScenarioDiag] YgomTextAccessor components: {ytas?.Length ?? 0}");
                if (ytas != null)
                {
                    foreach (var yta in ytas)
                    {
                        if (yta == null) continue;
                        string content = yta.text ?? "(null)";
                        if (content.Length > 100) content = content.Substring(0, 100) + "...";
                        MelonLogger.Msg($"[ScenarioDiag]   YTA: \"{content}\" active={yta.gameObject?.activeInHierarchy} go={yta.gameObject?.name}");
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Msg($"[ScenarioDiag] YTA scan error: {ex.Message}"); }

            // Dump child hierarchy (first 3 levels)
            try
            {
                MelonLogger.Msg("[ScenarioDiag] Child hierarchy:");
                DumpChildren(root.transform, 0, 3);
            }
            catch (Exception ex) { MelonLogger.Msg($"[ScenarioDiag] Hierarchy error: {ex.Message}"); }

            // Check dialog manager for TutorialArrow state
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager != null)
                {
                    Il2CppYgomSystem.UI.ViewControllerManager dialogMgr;
                    if (namedManager.TryGetValue("dialog", out dialogMgr) && dialogMgr != null)
                    {
                        var topVc = dialogMgr.GetStackTopViewController();
                        if (topVc?.gameObject != null)
                        {
                            MelonLogger.Msg($"[ScenarioDiag] Dialog top VC: {topVc.gameObject.name}");
                            var arrowVc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                            if (arrowVc != null)
                            {
                                var ipclick = arrowVc.ipclick;
                                MelonLogger.Msg($"[ScenarioDiag] TutorialArrow ipclick: {ipclick?.Length ?? 0} handler(s)");
                                MelonLogger.Msg($"[ScenarioDiag] TutorialArrow physicTarget: {arrowVc.physicTarget?.name ?? "(null)"}");
                                MelonLogger.Msg($"[ScenarioDiag] TutorialArrow dispTarget: {arrowVc.dispTarget?.name ?? "(null)"}");
                            }
                        }
                        else
                        {
                            MelonLogger.Msg("[ScenarioDiag] Dialog top VC: (none)");
                        }
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Msg($"[ScenarioDiag] Dialog check error: {ex.Message}"); }

            // Try to get ScenarioPlayViewController from the root
            try
            {
                var scenarioVc = root.GetComponent<Il2CppYgomGame.Scenario.ScenarioPlayViewController>();
                if (scenarioVc != null)
                {
                    MelonLogger.Msg($"[ScenarioDiag] ScenarioPlayVC found! messageText GO: {scenarioVc.messageText?.name ?? "(null)"}, active: {scenarioVc.messageText?.activeInHierarchy}");
                    MelonLogger.Msg($"[ScenarioDiag] npcMessage GO: {scenarioVc.npcMessage?.name ?? "(null)"}, active: {scenarioVc.npcMessage?.activeInHierarchy}");

                    // Try reading text from messageText GO
                    if (scenarioVc.messageText != null)
                    {
                        var msgText = scenarioVc.messageText.GetComponentInChildren<Text>(true);
                        if (msgText != null)
                            MelonLogger.Msg($"[ScenarioDiag] messageText.Text: \"{msgText.text}\"");
                        var msgYta = scenarioVc.messageText.GetComponentInChildren<Il2CppYgomSystem.UI.YgomTextAccessor>(true);
                        if (msgYta != null)
                            MelonLogger.Msg($"[ScenarioDiag] messageText.YTA: \"{msgYta.text}\"");
                    }
                }
                else
                {
                    MelonLogger.Msg("[ScenarioDiag] No ScenarioPlayVC on root — checking children");
                    var childVc = root.GetComponentInChildren<Il2CppYgomGame.Scenario.ScenarioPlayViewController>(true);
                    MelonLogger.Msg($"[ScenarioDiag] ScenarioPlayVC in children: {(childVc != null ? "found" : "not found")}");
                }
            }
            catch (Exception ex) { MelonLogger.Msg($"[ScenarioDiag] ScenarioVC error: {ex.Message}"); }
        }

        private void DumpChildren(Transform parent, int depth, int maxDepth)
        {
            if (depth >= maxDepth) return;
            string indent = new string(' ', depth * 2);
            for (int i = 0; i < parent.childCount; i++)
            {
                try
                {
                    var child = parent.GetChild(i);
                    if (child == null) continue;
                    var go = child.gameObject;
                    if (go == null) continue;
                    MelonLogger.Msg($"[ScenarioDiag] {indent}- {go.name} (active={go.activeSelf})");
                    DumpChildren(child, depth + 1, maxDepth);
                }
                catch { }
            }
        }

        #endregion
    }
}
