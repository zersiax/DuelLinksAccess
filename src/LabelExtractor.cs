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
            if (go == null) return "Button";

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

            // 4. HtjsonNode replaceParam — Htjson pages store content in dictionaries
            result = TryHtjsonNodeData(go);
            if (result != null) return result;

            // 5. ButtonSet → sibling TextArea association
            result = TryButtonSetSiblingText(go);
            if (result != null) return result;

            // 6. Child YgomTextAccessor components
            result = TryChildYgomTextAccessors(go);
            if (result != null) return result;

            // 7. Child Unity Text components
            result = TryChildUnityText(go);
            if (result != null) return result;

            // 8. Parent/sibling text — button may have its label nearby
            result = TryParentSiblingText(go);
            if (result != null) return result;

            // 9. ShortCutPanel BG buttons — image-only, identify via panel dictionary
            result = TryShortCutPanelButton(go);
            if (result != null) return result;

            // 10. Duel Trials quiz banners — image-only, label from ClientWork data
            result = TryDuelTrialsBanner(go);
            if (result != null) return result;

            // 11. Cleaned-up GO name as last resort
            return CleanGoName(go.name);
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
        /// Strips Unity rich text tags from a string.
        /// </summary>
        public static string StripRichText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return RichTextRegex.Replace(text, "").Trim();
        }

        /// <summary>
        /// Cleans a GameObject name for use as a fallback label.
        /// Removes common suffixes, splits CamelCase, filters known junk names.
        /// </summary>
        public static string CleanGoName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Button";

            // Remove (Clone) suffix
            if (name.EndsWith("(Clone)"))
                name = name.Substring(0, name.Length - 7).TrimEnd();

            // Known junk names that should not be announced
            if (_junkNames.Contains(name))
                return "Button";

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

            return name.Trim();
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

        private static readonly Regex RichTextRegex = new(@"<[^>]+>", RegexOptions.Compiled);
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
                    return name + ", locked";
                else if (isSet)
                    return isNew ? name + ", currently set, new" : name + ", currently set";
                else if (isNew)
                    return name + ", new";
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
                    return $"Entry {entryIndex + 1}";
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
        /// Includes inactive children (some entries have text on inactive sub-objects).
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
                    // Skip inactive text unless it's a direct child of the target
                    // (PullDown entries may have text on inactive sub-objects)
                    if (text.gameObject != go && !text.gameObject.activeInHierarchy)
                    {
                        // Allow if it's a direct child (depth 1)
                        if (text.transform.parent != go.transform) continue;
                    }
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
                                        if (IsValidLabel(text) && !IsPlaceholderText(text))
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
                                        string found = TryClientWorkName(dataPath, go.name);
                                        if (found != null) return found;

                                        // Fallback: try product path lookup for items with empty titles
                                        Il2CppSystem.Object ppVal;
                                        Il2CppSystem.Object dnVal;
                                        if (dict.TryGetValue("%productpath%", out ppVal) && ppVal != null
                                            && dict.TryGetValue("%dataname%", out dnVal) && dnVal != null)
                                        {
                                            string productPath = ppVal.ToString() + "." + dnVal.ToString();
                                            found = TryClientWorkName(productPath, go.name);
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
        /// Tries to resolve a name from ClientWork data store at the given path.
        /// Checks common name keys (title, name, headline, summary).
        /// In debug mode, dumps the full data dictionary for unknown items.
        /// </summary>
        private static string TryClientWorkName(string dataPath, string goName)
        {
            // Debug: dump keys when in debug mode (helps discover new data fields)
            if (Main.DebugMode)
            {
                try
                {
                    var obj = Il2CppYgomSystem.Utility.ClientWork.getByJsonPath(dataPath);
                    if (obj != null)
                    {
                        var dataDict = obj.TryCast<Il2CppSystem.Collections.Generic
                            .Dictionary<string, Il2CppSystem.Object>>();
                        if (dataDict != null)
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
                    }
                }
                catch { }
            }

            // Build composite label from available fields
            var parts = new List<string>();

            // Primary name: title, name, or label (missions use "label" for text)
            string[] primaryKeys = { "title", "name", "headline", "label" };
            foreach (var key in primaryKeys)
            {
                try
                {
                    string value = Il2CppYgomSystem.Utility.ClientWork
                        .getStringByJsonPath(dataPath + "." + key, "");
                    if (!string.IsNullOrEmpty(value) && !IsPlaceholderText(value))
                    {
                        parts.Add(StripRichText(value));
                        break;
                    }
                }
                catch { }
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
                        if (!parts.Contains(cleaned))
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
            if (GameStateTracker.CurrentScreen != GameStateTracker.GameScreen.DuelTrials)
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
