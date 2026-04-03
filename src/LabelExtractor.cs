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

            // 4. Child YgomTextAccessor components
            result = TryChildYgomTextAccessors(go);
            if (result != null) return result;

            // 5. Child Unity Text components
            result = TryChildUnityText(go);
            if (result != null) return result;

            // 6. Parent/sibling text — button may have its label nearby
            result = TryParentSiblingText(go);
            if (result != null) return result;

            // 7. Cleaned-up GO name as last resort
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

        #endregion
    }
}
