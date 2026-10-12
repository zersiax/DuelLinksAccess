using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

namespace DuelLinksAccess
{
    /// <summary>
    /// Centralized label extraction for UI elements across all handlers.
    /// Tries multiple strategies to find meaningful text for buttons, sliders,
    /// and other interactive elements. Used by ScreenButtonHandler and DialogHandler.
    /// </summary>
    public static class LabelExtractor
    {
        #region Public Methods

        /// <summary>
        /// Extracts a human-readable label for a UI element.
        /// Tries multiple strategies in priority order, falling back to
        /// a cleaned-up GameObject name if no text is found.
        /// </summary>
        public static string GetLabel(GameObject go)
        {
            if (go == null) return "按钮";

            string result;

            // 0. SkillSelectItem2 — skill selection list with owned/locked status
            result = TrySkillSelectItem(go);
            if (result != null) return result;

            // 1. YgomButton.textLabel — explicit text reference on the button
            result = TryYgomButtonTextLabel(go);
            if (result != null) return result;

            // 2. ButtonWidget — Htjson system buttons with labelText/label fields
            result = TryButtonWidget(go);
            if (result != null) return result;

            // 3. PullDown entries — image-based entries need special handling
            result = TryPullDownEntry(go);
            if (result != null) return result;

            // 4. ShortCutPanel BG buttons — image-only, identify via panel dictionary
            result = TryShortCutPanelButton(go);
            if (result != null) return result;

            // 5. Duel Trials quiz banners — image-only, label from ClientWork data
            result = TryDuelTrialsBanner(go);
            if (result != null) return result;

            // 6. HtjsonNode replaceParam — Htjson pages store content in dictionaries
            result = TryHtjsonNodeData(go);
            if (result != null) return result;

            // 7. ButtonSet → sibling TextArea association
            result = TryButtonSetSiblingText(go);
            if (result != null) return result;

            // 8. Child YgomTextAccessor components
            result = TryChildYgomTextAccessors(go);
            if (result != null) return result;

            // 9. Child Unity Text components
            result = TryChildUnityText(go);
            if (result != null) return result;

            // 10. Parent/sibling text — button may have its label nearby
            result = TryParentSiblingText(go);
            if (result != null) return result;

            // 11. Cleaned-up GO name as last resort
            return CleanGoName(go.name);
        }

        /// <summary>
        /// Text-only label lookup for world objects (e.g. map billboards):
        /// checks child text components but never falls back to the
        /// GameObject name. Returns null when the hierarchy carries no
        /// readable text, so callers can apply their own fallback.
        /// </summary>
        public static string GetChildText(GameObject go)
        {
            if (go == null) return null;

            string result = TryChildYgomTextAccessors(go);
            if (result != null) return result;

            return TryChildUnityText(go);
        }

        /// <summary>
        /// Extracts a label for a slider by checking parent context for text.
        /// </summary>
        public static string GetSliderLabel(Slider slider)
        {
            try
            {
                var parent = slider.transform.parent;
                if (parent != null)
                {
                    // Try YgomTextAccessor in parent's children
                    try
                    {
                        var ytas = parent.GetComponentsInChildren<Il2CppYgomSystem.UI.YgomTextAccessor>(true);
                        if (ytas != null)
                        {
                            foreach (var t in ytas)
                            {
                                if (t == null) continue;
                                string txt = StripRichText(t.text);
                                if (IsValidLabel(txt))
                                    return txt;
                            }
                        }
                    }
                    catch { }

                    // Try Unity Text in parent's children
                    try
                    {
                        var texts = parent.GetComponentsInChildren<Text>(true);
                        if (texts != null)
                        {
                            foreach (var t in texts)
                            {
                                if (t == null) continue;
                                string txt = StripRichText(t.text);
                                if (IsValidLabel(txt))
                                    return txt;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            int val = Mathf.RoundToInt(slider.value);
            return Loc.Get("screen_slider", val);
        }

        /// <summary>
        /// Resolves a character id to a display name via the CharaUtil
        /// resolver chain (series-qualified name preferred). Null when the
        /// id is unset or no resolver knows it.
        /// </summary>
        public static string ResolveCharaName(int cid)
        {
            if (cid <= 0) return null;
            foreach (var resolver in new System.Func<int, string>[]
            {
                Il2CppYgomGame.Utility.CharaUtil.GetNameWithSeries,
                Il2CppYgomGame.Utility.CharaUtil.GetNameAndSeries,
                Il2CppYgomGame.Utility.CharaUtil.GetName
            })
            {
                try
                {
                    string v = resolver(cid);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                catch { }
            }
            return null;
        }

        /// <summary>
        /// Strips Unity rich text tags from a string.
        /// </summary>
        public static string StripRichText(string text)
        {
            return SpeechTextFormatter.StripRichText(text);
        }

        /// <summary>
        /// Collapses all whitespace runs (spaces, newlines) to single spaces
        /// and trims. Used to compare text that different UI elements render
        /// with different line breaks (e.g. dialog vs scenario message window).
        /// </summary>
        public static string NormalizeWhitespace(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return string.Join(" ", text.Split(
                (char[])null, System.StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>
        /// True when two already-normalized texts are the same line rendered
        /// by different UI surfaces: equal, or one contains the other. Both
        /// must be at least 8 chars so short labels can't false-positive.
        /// </summary>
        public static bool NormalizedOverlap(string normA, string normB)
        {
            if (string.IsNullOrEmpty(normA) || normA.Length < 8) return false;
            if (string.IsNullOrEmpty(normB) || normB.Length < 8) return false;
            return normA == normB
                || normA.Contains(normB)
                || normB.Contains(normA);
        }

        /// <summary>
        /// Cleans a GameObject name for use as a fallback label.
        /// Removes common suffixes, splits CamelCase, filters known junk names.
        /// </summary>
        public static string CleanGoName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "按钮";

            // Remove (Clone) suffix
            if (name.EndsWith("(Clone)"))
                name = name.Substring(0, name.Length - 7).TrimEnd();

            // Known junk names that should not be announced
            if (_junkNames.Contains(name))
                return "按钮";

            // Remove common suffixes to get a meaningful core name
            foreach (var suffix in _nameSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
                {
                    name = name.Substring(0, name.Length - suffix.Length);
                    break;
                }
            }

            // Insert spaces before uppercase (CamelCase -> words)
            // Keep consecutive uppercase together (LP stays LP)
            name = CamelCaseRegex.Replace(name, " ");
            name = AcronymRegex.Replace(name, " ");

            return TranslateGoName(name.Trim());
        }

        /// <summary>
        /// Returns true if the string contains Japanese characters (Hiragana,
        /// Katakana, or CJK ideographs). Some game labels are hardcoded JP
        /// regardless of game language; non-JP TTS voices skip or garble them,
        /// so callers should fall back to a cleaned GO name or Loc string.
        /// </summary>
        public static bool ContainsJapanese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if ((c >= '぀' && c <= 'ヿ')     // Hiragana + Katakana
                    || (c >= '一' && c <= '鿿')  // CJK Unified Ideographs
                    || (c >= '㐀' && c <= '䶿')) // CJK Extension A
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Checks if a string is a valid, meaningful label.
        /// Rejects null, empty, all-digit, and too-short strings.
        /// </summary>
        public static bool IsValidLabel(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (text.Length < 2) return false;
            if (text.All(char.IsDigit)) return false;
            return true;
        }

        /// <summary>
        /// Checks if a GameObject reference is still alive (not destroyed).
        /// IL2CPP objects can become invalid when the underlying Unity object is destroyed.
        /// </summary>
        public static bool IsAlive(GameObject go)
        {
            try
            {
                if (go == null) return false;
                // Accessing .name forces IL2CPP to validate the pointer
                _ = go.name;
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Private Fields

        private static readonly Regex CamelCaseRegex = new(@"(?<=[a-z])(?=[A-Z])", RegexOptions.Compiled);
        private static readonly Regex AcronymRegex = new(@"(?<=[A-Z])(?=[A-Z][a-z])", RegexOptions.Compiled);

        private static readonly string[] _nameSuffixes = { "Button", "Btn", "Set", "Widget" };

        /// <summary>
        /// GO names that are never useful as labels. Returns generic "Button" instead.
        /// </summary>
        private static readonly HashSet<string> _junkNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "ButtonSet", "Icon", "Crst", "dummybutton", "switch",
            "YgomButton", "Button", "Btn", "BtnBase", "btn",
            "PullDownEntry", "PullDown",
            "Image", "RawImage", "Container", "Root", "Base",
            "Panel", "Content", "Viewport", "Frame",
            "vl", "hl", "div", "img"
        };

        private static readonly Dictionary<string, string> _uiTranslations = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Back", "返回" },
            { "Close", "关闭" },
            { "Next", "下一步" },
            { "Prev", "上一步" },
            { "Previous", "上一步" },
            { "OK", "确定" },
            { "Cancel", "取消" },
            { "Confirm", "确认" },
            { "Button", "按钮" },
            { "Btn", "按钮" },
            { "Card Center Banner Mini", "卡片中心精选横幅" },
            { "Card Center Banner", "卡片中心横幅" },
            { "Chara Deck", "角色卡组" },
            { "Deck Select Item", "卡组选择项" },
            { "Deck Select", "卡组选择" },
            { "Banner", "活动横幅" },
            { "Destruction Geas", "冥铭途之祸 (Destruction Geas)" },
            { "Scrollbar Vertical", "垂直滚动条" },
            { "Scrollbar Horizontal", "水平滚动条" },
            { "Scrollbar", "滚动条" },
            { "Structure", "预组卡组" },
            { "Deck", "卡组" },
            { "Chara", "角色" },
            { "Character", "角色" },
            { "Filter", "筛选" },
            { "Sort", "排序" },
            { "Search", "搜索" },
            { "Edit", "编辑" },
            { "Save", "保存" },
            { "Help", "帮助" },
            { "Setting", "设置" },
            { "Settings", "设置" },
            { "Detail", "详情" },
            { "Details", "详情" },
            { "Menu", "菜单" },
            { "Notice", "公告" },
            { "Mission", "任务" },
            { "Present", "礼物" },
            { "Gift", "礼物" },
            { "Shop", "商店" },
            { "Exchange", "交换" },
            { "Buy", "购买" },
            { "Purchase", "购买" },
            { "Item", "道具" },
            { "Duel", "决斗" },
            { "Gate", "传送门" },
            { "Box", "卡盒" },
            { "Pack", "卡包" },
            { "Stage", "关卡" },
            { "World", "世界" },
            { "Area", "区域" },
            { "Tab", "标签页" },
            { "Icon", "图标" },
            { "Reset", "重置" },
            { "Select", "选择" }
        };

        private static string TranslateGoName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "按钮";

            if (_uiTranslations.TryGetValue(name, out string translated))
                return translated;

            // Handle trailing numbers, e.g. "Chara Deck0", "Deck Select Item2", "Banner1"
            var m = Regex.Match(name, @"^(.*?)\s*(\d+)$");
            if (m.Success)
            {
                string stem = m.Groups[1].Value.Trim();
                string digits = m.Groups[2].Value;
                if (_uiTranslations.TryGetValue(stem, out string stemTrans))
                    return $"{stemTrans} {digits}";
            }

            return name;
        }

        #endregion

        #region Extraction Strategies

        /// <summary>
        /// Strategy 0: SkillSelectItem2 — annotates skill name with owned/locked/new status.
        /// </summary>
        private static string TrySkillSelectItem(GameObject go)
        {
            try
            {
                var skillItem = go.GetComponent<Il2CppYgomGame.Deck.SkillSelectItem2>();
                if (skillItem == null) return null;

                var nameText = skillItem.skillName;
                string name = nameText?.text;
                if (string.IsNullOrEmpty(name)) return null;

                // Check if skill is locked by comparing text color to ngColor
                bool isLocked = false;
                try
                {
                    var currentColor = nameText.color;
                    var ngColor = skillItem.ngColor;
                    // Colors match (within tolerance) = locked
                    isLocked = Math.Abs(currentColor.r - ngColor.r) < 0.1f
                        && Math.Abs(currentColor.g - ngColor.g) < 0.1f
                        && Math.Abs(currentColor.b - ngColor.b) < 0.1f;
                }
                catch { }

                // Check for NEW badge
                bool isNew = false;
                try { isNew = skillItem.isNew; }
                catch { }

                // Check if currently set
                bool isSet = false;
                try { isSet = skillItem.nowInSet?.activeSelf == true; }
                catch { }

                if (isLocked)
                    return name + "，未解锁";
                else if (isSet)
                    return isNew ? name + "，当前装备，新获得" : name + "，当前装备";
                else if (isNew)
                    return name + "，新获得";
                else
                    return name;
            }
            catch { return null; }
        }

        /// <summary>
        /// Strategy 1: YgomButton.textLabel — the button's explicit text reference.
        /// </summary>
        private static string TryYgomButtonTextLabel(GameObject go)
        {
            try
            {
                var ygomBtn = go.GetComponent<Il2CppYgomSystem.UI.YgomButton>();
                if (ygomBtn == null || ygomBtn.textLabel == null) return null;

                // textLabel is a Graphic — try Text component on it
                var textComp = ygomBtn.textLabel.GetComponent<Text>();
                if (textComp != null)
                {
                    string t = StripRichText(textComp.text);
                    if (IsValidLabel(t)) return t;
                }

                // Try YgomTextAccessor on the textLabel
                var ytaComp = ygomBtn.textLabel.GetComponent<Il2CppYgomSystem.UI.YgomTextAccessor>();
                if (ytaComp != null)
                {
                    string t = StripRichText(ytaComp.text);
                    if (IsValidLabel(t)) return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Strategy 2: ButtonWidget — Htjson system buttons.
        /// Checks the GO itself and up to 2 parent levels, since ButtonWidget
        /// often lives on a parent container above the Selectable.
        /// </summary>
        private static string TryButtonWidget(GameObject go)
        {
            try
            {
                var current = go.transform;
                int depth = 0;
                while (current != null && depth < 3)
                {
                    var bw = current.GetComponent<Il2CppYgomSystem.Htjson.ButtonWidget>();
                    if (bw != null)
                    {
                        // Priority 1: labelText (direct Text reference)
                        try
                        {
                            if (bw.labelText != null)
                            {
                                string t = StripRichText(bw.labelText.text);
                                if (IsValidLabel(t)) return t;
                            }
                        }
                        catch { }

                        // Priority 2: label string field
                        try
                        {
                            string t = bw.label;
                            if (IsValidLabel(t)) return t;
                        }
                        catch { }

                        // Priority 3: toggleLabel string field
                        try
                        {
                            string t = bw.toggleLabel;
                            if (IsValidLabel(t)) return t;
                        }
                        catch { }

                        // Found a ButtonWidget but no text — stop searching parents
                        break;
                    }
                    current = current.parent;
                    depth++;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Strategy 3: PullDown entry — entries are often image-based with no visible text.
        /// Reads the entry label from the PullDownViewController's Args "entrys" string array,
        /// falling back to child Text components and then numbered position.
        /// </summary>
        private static string TryPullDownEntry(GameObject go)
        {
            try
            {
                if (!go.name.Contains("PullDownEntry")) return null;

                // Try ALL Text components regardless of active state first
                var allTexts = go.GetComponentsInChildren<Text>(true);
                if (allTexts != null)
                {
                    foreach (var text in allTexts)
                    {
                        if (text == null) continue;
                        string t = StripRichText(text.text);
                        if (IsValidLabel(t)) return t;
                    }
                }

                // Get the parent PullDownViewController
                var pullDownVc = go.GetComponentInParent<Il2CppYgomGame.Menu.PullDownViewController>();
                if (pullDownVc == null) return null;

                // Find this entry's index in the entrys GO array
                var entryGOs = pullDownVc.entrys;
                int entryIndex = -1;
                if (entryGOs != null)
                {
                    for (int i = 0; i < entryGOs.Length; i++)
                    {
                        try
                        {
                            if (entryGOs[i] != null && entryGOs[i] == go)
                            {
                                entryIndex = i;
                                break;
                            }
                        }
                        catch { }
                    }
                }

                // Read the "entrys" string array from ViewController.Args
                if (entryIndex >= 0)
                {
                    try
                    {
                        var args = pullDownVc.Args;
                        if (args != null)
                        {
                            Il2CppSystem.Object entrysObj;
                            if (args.TryGetValue("entrys", out entrysObj) && entrysObj != null)
                            {
                                string label = ExtractStringFromArray(entrysObj, entryIndex);
                                if (IsValidLabel(label))
                                {
                                    DebugLogger.Log(LogCategory.Handler, "LabelEx",
                                        $"PullDown entry {entryIndex}: \"{label}\" (from Args)");
                                    return label;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Handler, "LabelEx",
                            $"PullDown Args read error: {ex.Message}");
                    }

                    // Numbered fallback
                    return $"条目 {entryIndex + 1}";
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Strategy 4: Search children for YgomTextAccessor components.
        /// <summary>
        /// Extracts a string at a given index from an IL2CPP object that represents
        /// a string array. Tries multiple cast approaches since IL2CPP wrapping varies.
        /// </summary>
        private static string ExtractStringFromArray(Il2CppSystem.Object obj, int index)
        {
            // Approach 1: Il2CppStringArray (native string array)
            try
            {
                var arr = obj.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStringArray>();
                if (arr != null && index < arr.Length)
                    return arr[index];
            }
            catch { }

            // Approach 2: Il2CppReferenceArray<Il2CppSystem.String>
            try
            {
                var arr = obj.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.String>>();
                if (arr != null && index < arr.Length)
                    return arr[index]?.ToString();
            }
            catch { }

            // Approach 3: Il2CppReferenceArray<Il2CppSystem.Object> (generic object array)
            try
            {
                var arr = obj.TryCast<Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Il2CppSystem.Object>>();
                if (arr != null && index < arr.Length)
                    return arr[index]?.ToString();
            }
            catch { }

            // Approach 4: Unbox as Il2CppSystem.Array and index into it
            try
            {
                var sysArr = obj.TryCast<Il2CppSystem.Array>();
                if (sysArr != null && index < sysArr.Length)
                {
                    var element = sysArr.GetValue(index);
                    return element?.ToString();
                }
            }
            catch { }

            DebugLogger.Log(LogCategory.Handler, "LabelEx",
                $"Failed to extract string[{index}] from {obj.GetIl2CppType()?.Name ?? "?"}");
            return null;
        }

        /// </summary>
        private static string TryChildYgomTextAccessors(GameObject go)
        {
            try
            {
                var ytas = go.GetComponentsInChildren<Il2CppYgomSystem.UI.YgomTextAccessor>(true);
                if (ytas == null) return null;

                foreach (var yta in ytas)
                {
                    if (yta == null) continue;
                    if (yta.gameObject == null || !yta.gameObject.activeInHierarchy) continue;
                    string t = StripRichText(yta.text);
                    if (IsValidLabel(t)) return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Strategy 4: Search children for Unity Text components.
        /// Inactive labels are ignored; stateful controls often retain stale text there.
        /// </summary>
        private static string TryChildUnityText(GameObject go)
        {
            try
            {
                var texts = go.GetComponentsInChildren<Text>(true);
                if (texts == null) return null;

                foreach (var text in texts)
                {
                    if (text == null) continue;
                    if (text.gameObject == null
                        || !text.gameObject.activeInHierarchy) continue;
                    string t = StripRichText(text.text);
                    if (IsValidLabel(t)) return t;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Strategy 5: Check parent and sibling elements for nearby text.
        /// Some buttons have their label in a sibling Text element rather than
        /// as a child. Walks up 1-2 parents and checks siblings.
        /// </summary>
        /// <summary>
        /// Strategy 4: HtjsonNode data — reads replaceParam dictionary for content keys.
        /// Htjson pages (ShopLineup, GiftTicket, etc.) store actual content in
        /// HtjsonNode dictionaries rather than in standard Text components.
        /// </summary>
        private static string TryHtjsonNodeData(GameObject go)
        {
            try
            {
                // Check the GO and up to 2 parent levels for HtjsonNode
                var current = go.transform;
                int depth = 0;
                while (current != null && depth < 3)
                {
                    var node = current.GetComponent<Il2CppYgomSystem.Htjson.HtjsonNode>();
                    if (node != null)
                    {
                        var dict = node.replaceParam;
                        if (dict != null)
                        {
                            // Try common content keys
                            string[] keys = { "title", "name", "label", "text", "summary",
                                              "header", "desc", "caption", "content", "item_name" };
                            foreach (var key in keys)
                            {
                                try
                                {
                                    Il2CppSystem.Object val;
                                    if (dict.TryGetValue(key, out val) && val != null)
                                    {
                                        string text = val.ToString();
                                        if (IsValidLabel(text) && !IsPlaceholderText(text)
                                            && !IsGenericBoxCategory(text))
                                        {
                                            DebugLogger.Log(LogCategory.Handler, "LabelEx",
                                                $"HtjsonNode replaceParam[{key}]=\"{text}\" on {go.name}");
                                            return StripRichText(text);
                                        }
                                    }
                                }
                                catch { }
                            }

                            // Try %iid% — card/item ID that can be resolved to a name
                            try
                            {
                                Il2CppSystem.Object iidVal;
                                if (dict.TryGetValue("%iid%", out iidVal) && iidVal != null)
                                {
                                    string iidStr = iidVal.ToString();
                                    if (int.TryParse(iidStr, out int iid) && iid > 0)
                                    {
                                        // 1. Try ItemUtil.GetName first (items, orbs, keys, tickets, boosts)
                                        string itemName = null;
                                        try { itemName = Il2CppYgomGame.Utility.ItemUtil.GetName(iid); } catch { }
                                        if (IsValidLabel(itemName))
                                        {
                                            DebugLogger.Log(LogCategory.Handler, "LabelEx",
                                                $"Item name from %%iid%%={iid}: \"{itemName}\" on {go.name}");
                                            return itemName;
                                        }

                                        // 2. Try CardFormatter.GetName (cards)
                                        string cardName = CardFormatter.GetName(iid);
                                        if (IsValidLabel(cardName) && cardName != Loc.Get("duel_unknown_card"))
                                        {
                                            DebugLogger.Log(LogCategory.Handler, "LabelEx",
                                                $"Card name from %%iid%%={iid}: \"{cardName}\" on {go.name}");
                                            return cardName;
                                        }
                                    }
                                }
                            }
                            catch { }

                            // Try %datapath% or %sublooppath% — query ClientWork data store
                            // for item name. Some Htjson templates (e.g. AnotherMissionDialog)
                            // use %sublooppath% instead of %datapath%.
                            try
                            {
                                Il2CppSystem.Object dpVal;
                                if ((!dict.TryGetValue("%datapath%", out dpVal) || dpVal == null)
                                    && !dict.TryGetValue("%sublooppath%", out dpVal))
                                    dpVal = null;
                                if (dpVal != null)
                                {
                                    string dataPath = dpVal.ToString();
                                    if (!string.IsNullOrEmpty(dataPath))
                                    {
                                        string found = TryClientWorkName(dataPath, go.name, go);
                                        if (found != null) return found;

                                        // Fallback: try product path lookup for items with empty titles
                                        Il2CppSystem.Object ppVal;
                                        Il2CppSystem.Object dnVal;
                                        if (dict.TryGetValue("%productpath%", out ppVal) && ppVal != null
                                            && dict.TryGetValue("%dataname%", out dnVal) && dnVal != null)
                                        {
                                            string productPath = ppVal.ToString() + "." + dnVal.ToString();
                                            found = TryClientWorkName(productPath, go.name, go);
                                            if (found != null) return found;
                                        }
                                    }
                                }
                            }
                            catch { }

                            // Debug: dump all keys when in debug mode to help discover content
                            if (Main.DebugMode)
                            {
                                try
                                {
                                    var enumerator = dict.GetEnumerator();
                                    var keyList = new List<string>();
                                    while (enumerator.MoveNext())
                                    {
                                        var entry = enumerator.Current;
                                        string k = entry.Key;
                                        string v = entry.Value?.ToString() ?? "null";
                                        if (v.Length > 60) v = v.Substring(0, 60) + "...";
                                        keyList.Add($"{k}={v}");
                                    }
                                    if (keyList.Count > 0)
                                    {
                                        DebugLogger.Log(LogCategory.Handler, "LabelEx",
                                            $"HtjsonNode on {go.name}: [{string.Join(", ", keyList)}]");
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    current = current.parent;
                    depth++;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Strategy 5: ButtonSet → sibling TextArea association.
        /// On Htjson pages, a ButtonSet element is paired with a sibling TextArea
        /// that contains the item label. Checks the previous sibling for text content.
        /// </summary>
        private static string TryButtonSetSiblingText(GameObject go)
        {
            try
            {
                // Only apply to elements named "ButtonSet" or "btn" that lack labels
                string goName = go.name;
                if (goName != "ButtonSet" && goName != "btn" && goName != "card") return null;

                var parent = go.transform.parent;
                if (parent == null) return null;

                // Find this GO's sibling index
                int myIndex = go.transform.GetSiblingIndex();

                // Check the previous sibling for text (TextArea typically comes before ButtonSet)
                if (myIndex > 0)
                {
                    var prevSibling = parent.GetChild(myIndex - 1);
                    if (prevSibling != null)
                    {
                        string text = ExtractTextFromTransform(prevSibling);
                        if (text != null && !IsPlaceholderText(text))
                        {
                            DebugLogger.Log(LogCategory.Handler, "LabelEx",
                                $"ButtonSet sibling text: \"{text}\" from {prevSibling.name}");
                            return text;
                        }
                    }
                }

                // Also check next sibling
                if (myIndex < parent.childCount - 1)
                {
                    var nextSibling = parent.GetChild(myIndex + 1);
                    if (nextSibling != null)
                    {
                        string text = ExtractTextFromTransform(nextSibling);
                        if (text != null && !IsPlaceholderText(text))
                        {
                            DebugLogger.Log(LogCategory.Handler, "LabelEx",
                                $"ButtonSet sibling text: \"{text}\" from {nextSibling.name}");
                            return text;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Extracts text from a transform by checking its Text/YgomTextAccessor components.
        /// </summary>
        private static string ExtractTextFromTransform(Transform t)
        {
            try
            {
                // Check YgomTextAccessor
                var yta = t.GetComponent<Il2CppYgomSystem.UI.YgomTextAccessor>();
                if (yta != null)
                {
                    string text = StripRichText(yta.text);
                    if (IsValidLabel(text)) return text;
                }

                // Check Text component
                var textComp = t.GetComponent<Text>();
                if (textComp != null)
                {
                    string text = StripRichText(textComp.text);
                    if (IsValidLabel(text)) return text;
                }

                // Check children
                var childTexts = t.GetComponentsInChildren<Text>(false);
                if (childTexts != null)
                {
                    foreach (var ct in childTexts)
                    {
                        if (ct == null) continue;
                        string text = StripRichText(ct.text);
                        if (IsValidLabel(text)) return text;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Checks if a string is placeholder text (e.g., Japanese "ああああ" placeholders).
        /// </summary>
        /// <summary>
        /// Checks if a string represents a generic box or pack category
        /// rather than a specific box/product title.
        /// </summary>
        public static bool IsGenericBoxCategory(string s) => ShopBoxPolicy.IsGenericBoxCategory(s);

        /// <summary>
        /// Tries to resolve a name from ClientWork data store at the given path.
        /// Checks common name keys (title, name, headline, summary).
        /// In debug mode, dumps the full data dictionary for unknown items.
        /// </summary>
        private static string TryClientWorkName(string dataPath, string goName, GameObject go = null)
        {
            // Debug: dump keys when in debug mode (helps discover new data fields)
            Il2CppSystem.Collections.Generic.Dictionary<string, Il2CppSystem.Object> dataDict = null;
            try
            {
                var obj = Il2CppYgomSystem.Utility.ClientWork.getByJsonPath(dataPath);
                if (obj != null)
                {
                    dataDict = obj.TryCast<Il2CppSystem.Collections.Generic
                        .Dictionary<string, Il2CppSystem.Object>>();
                }
            }
            catch { }

            if (Main.DebugMode && dataDict != null)
            {
                try
                {
                    var keyList = new List<string>();
                    var enumerator = dataDict.GetEnumerator();
                    while (enumerator.MoveNext())
                    {
                        var entry = enumerator.Current;
                        string k = entry.Key;
                        var valObj = entry.Value;
                        string v = valObj?.ToString() ?? "null";

                        // Recursively dump sub-dictionaries (footer, icon)
                        if (v.Contains("Dictionary"))
                        {
                            try
                            {
                                var subDict = valObj?.TryCast<Il2CppSystem.Collections.Generic
                                    .Dictionary<string, Il2CppSystem.Object>>();
                                if (subDict != null)
                                {
                                    var subKeys = new List<string>();
                                    var subEnum = subDict.GetEnumerator();
                                    while (subEnum.MoveNext())
                                    {
                                        var se = subEnum.Current;
                                        string sv = se.Value?.ToString() ?? "null";
                                        if (sv.Length > 150) sv = sv.Substring(0, 150) + "...";
                                        subKeys.Add($"{se.Key}={sv}");
                                    }
                                    v = "{" + string.Join(", ", subKeys) + "}";
                                }
                            }
                            catch { }
                        }

                        if (v.Length > 200) v = v.Substring(0, 200) + "...";
                        keyList.Add($"{k}={v}");
                    }
                    DebugLogger.Log(LogCategory.Handler, "LabelEx",
                        $"[CW-DUMP] {dataPath}: [{string.Join(", ", keyList)}]");
                }
                catch { }
            }

            // Build composite label from available fields
            var parts = new List<string>();

            // Mission rows (RankMissions) carry BOTH a descriptive `label` (the
            // objective, e.g. "Summon Warrior-Type monsters 3 time(s).") and a
            // `name` (the world/character the mission belongs to, e.g. "GX").
            // The generic key order below would announce just the name and drop
            // the objective, so resolve missions here: prefer the objective and
            // use the name only as a qualifying prefix when it isn't already in
            // the text (progress from extext is appended further down).
            bool primaryResolved = false;
            if (dataPath.Contains("RankMissions"))
            {
                try
                {
                    string label = StripRichText(Il2CppYgomSystem.Utility.ClientWork
                        .getStringByJsonPath(dataPath + ".label", ""));
                    if (!string.IsNullOrEmpty(label) && !IsPlaceholderText(label))
                    {
                        string name = StripRichText(Il2CppYgomSystem.Utility.ClientWork
                            .getStringByJsonPath(dataPath + ".name", ""));
                        if (!string.IsNullOrEmpty(name) && !IsPlaceholderText(name)
                            && !label.Contains(name))
                            label = name + ": " + label;
                        parts.Add(label);
                        primaryResolved = true;
                    }
                }
                catch { }
            }

            // Primary name: check title, name, headline, label, text, sub_title, caption, etc.
            string foundCategory = null;
            string foundSpecificName = null;

            if (!primaryResolved)
            {
                // 0. Check iid/item_id directly from ClientWork to resolve item/card name
                try
                {
                    int cwIid = Il2CppYgomSystem.Utility.ClientWork.getIntByJsonPath(dataPath + ".iid", 0);
                    if (cwIid <= 0) cwIid = Il2CppYgomSystem.Utility.ClientWork.getIntByJsonPath(dataPath + ".item_id", 0);
                    if (cwIid <= 0 && dataDict != null)
                    {
                        Il2CppSystem.Object o;
                        if ((dataDict.TryGetValue("iid", out o) || dataDict.TryGetValue("item_id", out o)) && o != null)
                            int.TryParse(o.ToString(), out cwIid);
                    }
                    if (cwIid > 0)
                    {
                        string iName = null;
                        try { iName = Il2CppYgomGame.Utility.ItemUtil.GetName(cwIid); } catch { }
                        if (IsValidLabel(iName))
                        {
                            foundSpecificName = iName;
                        }
                        else
                        {
                            string cName = CardFormatter.GetName(cwIid);
                            if (IsValidLabel(cName) && cName != Loc.Get("duel_unknown_card"))
                                foundSpecificName = cName;
                        }
                    }
                }
                catch { }

                // 1. Check title first to see if it provides a category or name
                try
                {
                    string tVal = StripRichText(Il2CppYgomSystem.Utility.ClientWork
                        .getStringByJsonPath(dataPath + ".title", ""));
                    if (!string.IsNullOrEmpty(tVal) && !IsPlaceholderText(tVal))
                    {
                        if (IsGenericBoxCategory(tVal))
                            foundCategory = tVal;
                        else if (string.IsNullOrEmpty(foundSpecificName))
                            foundSpecificName = tVal;
                    }
                }
                catch { }

                // 2. Candidate keys for the actual item/box name
                string[] candidateKeys = {
                    "name", "headline", "sub_title", "subtitle", "caption",
                    "box_name", "box_title", "pack_name", "package_name",
                    "item_name", "product_name", "title", "label", "text",
                    "footer.summary", "footer.title", "detail.name", "detail.title",
                    "banner.name", "banner.title", "box.name", "box.title", "item.name"
                };

                foreach (var key in candidateKeys)
                {
                    if (!string.IsNullOrEmpty(foundSpecificName)) break;
                    if (key == "title" && foundCategory != null) continue;

                    try
                    {
                        string val = StripRichText(Il2CppYgomSystem.Utility.ClientWork
                            .getStringByJsonPath(dataPath + "." + key, ""));
                        if (!string.IsNullOrEmpty(val) && !IsPlaceholderText(val))
                        {
                            if (IsGenericBoxCategory(val))
                            {
                                if (string.IsNullOrEmpty(foundCategory))
                                    foundCategory = val;
                            }
                            else if (val != foundCategory)
                            {
                                foundSpecificName = val;
                                break;
                            }
                        }
                    }
                    catch { }
                }

                // 3. Fallback: query dataDict keys directly if available
                if (string.IsNullOrEmpty(foundSpecificName) && dataDict != null)
                {
                    try
                    {
                        foreach (var key in candidateKeys)
                        {
                            Il2CppSystem.Object valObj;
                            if (dataDict.TryGetValue(key, out valObj) && valObj != null)
                            {
                                string s = StripRichText(valObj.ToString());
                                if (!string.IsNullOrEmpty(s) && !IsPlaceholderText(s) && !IsGenericBoxCategory(s))
                                {
                                    foundSpecificName = s;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 4. Volume / edition / box number if available
                string boxNum = null;
                if (dataDict != null)
                {
                    try
                    {
                        foreach (var numKey in new[] { "vol", "box_no", "volume", "series_no", "box_num", "pack_id", "edition" })
                        {
                            Il2CppSystem.Object numObj;
                            if (dataDict.TryGetValue(numKey, out numObj) && numObj != null)
                            {
                                string nStr = numObj.ToString()?.Trim();
                                if (!string.IsNullOrEmpty(nStr) && nStr != "0")
                                {
                                    boxNum = nStr;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 5. Fallback: inspect child YgomTextAccessor and Text components of go
                if (string.IsNullOrEmpty(foundSpecificName) && go != null)
                {
                    try
                    {
                        // Check YgomTextAccessor first
                        var childYtas = go.GetComponentsInChildren<Il2CppYgomSystem.UI.YgomTextAccessor>(true);
                        if (childYtas != null)
                        {
                            foreach (var yta in childYtas)
                            {
                                if (yta == null) continue;
                                string txt = StripRichText(yta.text)?.Trim();
                                if (IsValidLabel(txt) && !IsPlaceholderText(txt) && !IsGenericBoxCategory(txt)
                                    && txt != "Sale" && txt != "New" && txt != "NEW" && txt != "SALE"
                                    && !txt.All(char.IsDigit))
                                {
                                    foundSpecificName = txt;
                                    break;
                                }
                            }
                        }

                        // Check Unity Text
                        if (string.IsNullOrEmpty(foundSpecificName))
                        {
                            var childTexts = go.GetComponentsInChildren<Text>(true);
                            if (childTexts != null)
                            {
                                foreach (var ct in childTexts)
                                {
                                    if (ct == null) continue;
                                    string txt = StripRichText(ct.text)?.Trim();
                                    if (IsValidLabel(txt) && !IsPlaceholderText(txt) && !IsGenericBoxCategory(txt)
                                        && txt != "Sale" && txt != "New" && txt != "NEW" && txt != "SALE"
                                        && !txt.All(char.IsDigit))
                                    {
                                        foundSpecificName = txt;
                                        break;
                                    }
                                }
                            }
                        }

                        // Sniff box/pack edition from Image / RawImage sprite or texture name
                        if (string.IsNullOrEmpty(boxNum))
                        {
                            var imgs = go.GetComponentsInChildren<Image>(true);
                            if (imgs != null)
                            {
                                foreach (var img in imgs)
                                {
                                    if (img?.sprite != null)
                                    {
                                        var m = Regex.Match(img.sprite.name, @"(?i)(?:vol|box)[_.-]?(\d+)");
                                        if (m.Success) { boxNum = m.Groups[1].Value; break; }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 6. Combine category and specific name
                if (!string.IsNullOrEmpty(foundSpecificName))
                {
                    if (!string.IsNullOrEmpty(foundCategory))
                    {
                        if (foundCategory.StartsWith("发布日期：") || foundCategory.StartsWith("发布日期:"))
                            parts.Add($"{foundSpecificName} ({foundCategory})");
                        else if (!foundSpecificName.Contains(foundCategory))
                            parts.Add($"{foundCategory}：{foundSpecificName}");
                        else
                            parts.Add(foundSpecificName);
                    }
                    else
                    {
                        parts.Add(foundSpecificName);
                    }
                    primaryResolved = true;
                }
                else if (!string.IsNullOrEmpty(foundCategory))
                {
                    if (string.IsNullOrEmpty(boxNum) && go != null)
                    {
                        var mGo = Regex.Match(go.name, @"(?i)(?:item|box|pack|banner|cell)[_.-]?(\d+)");
                        if (mGo.Success) boxNum = mGo.Groups[1].Value;
                    }

                    if (!string.IsNullOrEmpty(boxNum) && !foundCategory.Contains(boxNum))
                        parts.Add($"{foundCategory} 第{boxNum}期");
                    else
                        parts.Add(foundCategory);
                    primaryResolved = true;
                }
            }

            // Description: footer.summary or summary — adds context like "Destruction Is Inevitable"
            string[] descKeys = { "footer.summary", "summary", "footer.title", "desc" };
            foreach (var key in descKeys)
            {
                try
                {
                    string value = Il2CppYgomSystem.Utility.ClientWork
                        .getStringByJsonPath(dataPath + "." + key, "");
                    if (!string.IsNullOrEmpty(value) && !IsPlaceholderText(value))
                    {
                        string cleaned = StripRichText(value);
                        if (!parts.Contains(cleaned) && cleaned != foundSpecificName && cleaned != foundCategory)
                            parts.Add(cleaned);
                        break;
                    }
                }
                catch { }
            }

            // Sale/New flags from icon sub-dict
            try
            {
                string saleVal = Il2CppYgomSystem.Utility.ClientWork
                    .getStringByJsonPath(dataPath + ".icon.sale", "");
                string newVal = Il2CppYgomSystem.Utility.ClientWork
                    .getStringByJsonPath(dataPath + ".icon.new", "");
                if (saleVal == "True") parts.Add("Sale");
                if (newVal == "True") parts.Add("New");
            }
            catch { }

            // Mission progress from extext (e.g. "(0/1)", "(3/5)")
            try
            {
                string extext = Il2CppYgomSystem.Utility.ClientWork
                    .getStringByJsonPath(dataPath + ".extext", "");
                if (!string.IsNullOrEmpty(extext))
                {
                    string progress = StripRichText(extext);
                    if (!string.IsNullOrEmpty(progress))
                        parts.Add(progress);
                }
            }
            catch { }

            // Price / cost if available directly in clientwork data
            try
            {
                string priceVal = Il2CppYgomSystem.Utility.ClientWork
                    .getStringByJsonPath(dataPath + ".price", "");
                if (string.IsNullOrEmpty(priceVal))
                    priceVal = Il2CppYgomSystem.Utility.ClientWork.getStringByJsonPath(dataPath + ".price_text", "");
                if (!string.IsNullOrEmpty(priceVal) && !parts.Contains(priceVal))
                {
                    string cleanedPrice = StripRichText(priceVal).Trim();
                    if (!string.IsNullOrEmpty(cleanedPrice) && !cleanedPrice.StartsWith("0"))
                        parts.Add($"价格：{cleanedPrice}");
                }
            }
            catch { }

            if (parts.Count > 0)
            {
                string result = string.Join(", ", parts);
                DebugLogger.Log(LogCategory.Handler, "LabelEx",
                    $"ClientWork composite: \"{result}\" on {goName}");
                return result;
            }

            return null;
        }

        /// <summary>
        /// Debug helper: dumps all Text components (including inactive) in a GO hierarchy.
        /// </summary>
        internal static void DumpChildTexts(GameObject go, string context)
        {
            try
            {
                // Check all Text components including inactive
                var texts = go.GetComponentsInChildren<Text>(true);
                if (texts != null && texts.Count > 0)
                {
                    for (int i = 0; i < texts.Count; i++)
                    {
                        var t = texts[i];
                        if (t == null) continue;
                        string val = t.text ?? "(null)";
                        bool active = t.gameObject?.activeInHierarchy ?? false;
                        string path = t.gameObject?.name ?? "?";
                        DebugLogger.Log(LogCategory.Handler, "LabelEx",
                            $"[ChildText] {context}: \"{val}\" active={active} go={path}");
                    }
                }
                else
                {
                    DebugLogger.Log(LogCategory.Handler, "LabelEx",
                        $"[ChildText] {context}: no Text components");
                }

                // Check for ShopListWidget in parents
                var shopWidget = go.GetComponentInParent<Il2CppYgomGame.Htjson.ShopListWidget>();
                if (shopWidget != null)
                {
                    DebugLogger.Log(LogCategory.Handler, "LabelEx",
                        $"[ShopWidget] Found ShopListWidget on parent of {go.name}");
                    var bannerParent = shopWidget.bannerParent;
                    if (bannerParent != null)
                        DebugLogger.Log(LogCategory.Handler, "LabelEx",
                            $"[ShopWidget] bannerParent={bannerParent.name}, children={bannerParent.transform.childCount}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "LabelEx",
                    $"[ChildText] dump error: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if text is Htjson placeholder content (e.g., repeated "ああああ").
        /// </summary>
        public static bool IsPlaceholderText(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            // "ああああ" pattern — repeating hiragana 'a' used as placeholder
            if (text.Length >= 4 && text.All(c => c == 'あ')) return true;
            return false;
        }

        private static string TryParentSiblingText(GameObject go)
        {
            try
            {
                var current = go.transform.parent;
                int depth = 0;
                while (current != null && depth < 2)
                {
                    // Check each sibling of the current level
                    for (int i = 0; i < current.childCount; i++)
                    {
                        var sibling = current.GetChild(i);
                        if (sibling == null) continue;
                        // Skip the button itself and its children
                        if (sibling == go.transform) continue;
                        if (go.transform.IsChildOf(sibling)) continue;

                        // Check for text on this sibling
                        try
                        {
                            var yta = sibling.GetComponent<Il2CppYgomSystem.UI.YgomTextAccessor>();
                            if (yta != null)
                            {
                                string t = StripRichText(yta.text);
                                if (IsValidLabel(t)) return t;
                            }

                            var text = sibling.GetComponent<Text>();
                            if (text != null)
                            {
                                string t = StripRichText(text.text);
                                if (IsValidLabel(t)) return t;
                            }
                        }
                        catch { }
                    }

                    current = current.parent;
                    depth++;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Identifies BG buttons within the ShortCutPanel by matching against
        /// the panel's Buttons dictionary. These character-style buttons are
        /// image-only and need their key looked up for a readable label.
        /// </summary>
        // Cache: maps BG GO instance IDs to shortcut labels (built once per panel)
        private static readonly Dictionary<int, string> _shortcutBgCache = new();
        private static int _shortcutCacheFrame = -1;

        private static string TryShortCutPanelButton(GameObject go)
        {
            if (go.name != "BG") return null;
            try
            {
                var panel = Il2CppYgomGame.Single.OverLap.ShortCutPanel.spanel;
                if (panel == null) return null;

                // Verify this BG is under the shortcut panel
                if (!go.transform.IsChildOf(panel.transform)) return null;

                // Build cache once per frame: collect all PrefabCharaPinButton
                // children in order and match to character-style dictionary keys
                int frame = UnityEngine.Time.frameCount;
                if (frame != _shortcutCacheFrame)
                {
                    _shortcutCacheFrame = frame;
                    _shortcutBgCache.Clear();
                    BuildShortcutBgCache(panel);
                }

                int id = go.GetInstanceID();
                if (_shortcutBgCache.TryGetValue(id, out string cached))
                    return cached;
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "LabelExtract",
                    $"ShortCutPanel BG match error: {ex.Message}");
            }
            return null;
        }

        // The 4 character-style shortcut keys that render as PrefabCharaPinButton
        // with BG image buttons. The remaining 5 keys use ShortCutButtonEntry icons.
        // Order matches the panel's creation order (dictionary iteration order).
        private static readonly string[] _charShortcutKeys =
            { "trader", "duelchallenge", "traderEx", "eve1004" };

        /// <summary>
        /// Builds a mapping from BG button instance IDs to shortcut labels.
        /// Finds all PrefabCharaPinButton containers anywhere in the panel
        /// hierarchy and matches them to the known character-style keys by order.
        /// </summary>
        private static void BuildShortcutBgCache(
            Il2CppYgomGame.Single.OverLap.ShortCutPanel panel)
        {
            try
            {
                // Find all PrefabCharaPinButton(Clone) GOs anywhere in the panel
                var allTransforms = panel.GetComponentsInChildren<Transform>(true);
                if (allTransforms == null) return;

                var bgButtons = new List<GameObject>();
                foreach (var tf in allTransforms)
                {
                    if (tf?.gameObject == null) continue;
                    if (!tf.gameObject.name.StartsWith("PrefabCharaPinButton")) continue;
                    if (!tf.gameObject.activeInHierarchy) continue;

                    // Find the BG button inside this container
                    var bgBtn = tf.Find("BG");
                    if (bgBtn?.gameObject != null)
                        bgButtons.Add(bgBtn.gameObject);
                }

                DebugLogger.Log(LogCategory.Handler, "LabelExtract",
                    $"Found {bgButtons.Count} PrefabCharaPinButton BG buttons");

                // Match by order to the known character-style keys
                int count = Math.Min(bgButtons.Count, _charShortcutKeys.Length);
                for (int i = 0; i < count; i++)
                {
                    string label = Loc.GetShortcutLabel(_charShortcutKeys[i]);
                    _shortcutBgCache[bgButtons[i].GetInstanceID()] = label;
                    DebugLogger.Log(LogCategory.Handler, "LabelExtract",
                        $"ShortCut mapped BG #{i} -> {_charShortcutKeys[i]} = \"{label}\"");
                }
            }
            catch (System.Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "LabelExtract",
                    $"BuildShortcutBgCache error: {ex.Message}");
            }
        }

        /// <summary>
        /// Identifies Duel Trials quiz banners (Banner1/2/3) by reading
        /// the resource type from ClientWork data. These are image-only
        /// elements with no text — the data lives in DuelChallenge.top.data.list.
        /// </summary>
        private static string TryDuelTrialsBanner(GameObject go)
        {
            if (GameStateTracker.CurrentScreen != GameScreen.DuelTrials)
                return null;

            // Match BannerN pattern
            string name = go.name;
            if (!name.StartsWith("Banner")) return null;
            string numStr = name.Substring("Banner".Length);
            if (!int.TryParse(numStr, out int bannerNum) || bannerNum < 1)
                return null;

            // Try to get resource type from ClientWork
            try
            {
                string dataPath = $"DuelChallenge.top.data.list.{bannerNum}.data";
                string resource = Il2CppYgomSystem.Utility.ClientWork
                    .getStringByJsonPath(dataPath + ".resource", "");
                if (!string.IsNullOrEmpty(resource) && resource == "Quiz")
                    return Loc.Get("duel_trials_quiz", bannerNum);

                // Non-Quiz resource: use resource name + number
                if (!string.IsNullOrEmpty(resource))
                    return $"{resource} {bannerNum}";
            }
            catch { }

            // Fallback: generic quiz label
            return Loc.Get("duel_trials_quiz", bannerNum);
        }

        #endregion
    }
}
