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
    /// Handles generic game dialogs by finding all interactive elements
    /// in the dialog hierarchy and making them keyboard-navigable.
    /// Up/Down to cycle through items, Left/Right to adjust sliders,
    /// Enter to click buttons, Tab to re-read current item, Space to re-scan.
    /// </summary>
    public class DialogHandler
    {
        #region Fields

        private string _lastDialogName = "";
        private bool _scanned;
        private float _scanDelay;
        private int _scanAttempts;
        private readonly List<DialogItem> _items = new();
        private int _focusIndex;

        // Text mode: dialog has text but no buttons — Enter/Space dismisses
        private bool _textMode;
        private string _lastDialogText = "";
        private GameObject _dialogRoot;

        #endregion

        #region Types

        private enum ItemType { Button, Slider, Other }

        private class DialogItem
        {
            public GameObject Go;
            public string Label;
            public ItemType Type;
            public Slider SliderComponent; // non-null for sliders
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Called every frame by Main.Update().
        /// Active when GameStateTracker detects a Dialog screen.
        /// </summary>
        public void Update()
        {
            if (GameStateTracker.CurrentScreen != GameStateTracker.GameScreen.Dialog)
            {
                if (_lastDialogName != "")
                {
                    _lastDialogName = "";
                    _scanned = false;
                    _textMode = false;
                    _lastDialogText = "";
                    _dialogRoot = null;
                    _items.Clear();
                }
                return;
            }

            string currentName = GameStateTracker.LastViewControllerName;

            // New dialog appeared — start delayed scan
            if (currentName != _lastDialogName)
            {
                _lastDialogName = currentName;
                _scanned = false;
                _textMode = false;
                _lastDialogText = "";
                _scanDelay = 2.0f;
                _scanAttempts = 0;
                // Don't announce "Dialog" during duels — the dialog text
                // itself will be read once scanned
                if (!DuelEventAnnouncer.InDuel)
                    ScreenReader.Say(Loc.Get("screen_dialog"));
            }

            if (!_scanned)
            {
                _scanDelay -= Time.deltaTime;
                if (_scanDelay <= 0f)
                {
                    _scanAttempts++;
                    ScanDialog();

                    // Retry if we found nothing in the dialog hierarchy
                    if (_items.Count == 0 && _scanAttempts < 3)
                    {
                        MelonLogger.Msg($"[Dialog] No dialog items found, retrying in 2s (attempt {_scanAttempts})");
                        _scanDelay = 2.0f;
                    }
                    else
                    {
                        _scanned = true;
                    }
                }
            }

            ProcessInput();
        }

        /// <summary>
        /// Whether a dialog is currently being handled.
        /// </summary>
        public bool IsActive =>
            GameStateTracker.CurrentScreen == GameStateTracker.GameScreen.Dialog;

        #endregion

        #region Scanning

        private void ScanDialog()
        {
            _items.Clear();
            _focusIndex = 0;

            try
            {
                // Find the dialog root — HtjsonDialog content lives under DialogManager
                GameObject dialogRoot = GetManagerRoot("dialog");
                if (dialogRoot == null)
                {
                    MelonLogger.Msg("[Dialog] No dialog manager root found");
                    return;
                }

                MelonLogger.Msg($"[Dialog] Scanning dialog root: {dialogRoot.name}");

                // Read and announce dialog text first (Say to interrupt "Dialog" announcement)
                string dialogText = ReadDialogText(dialogRoot);
                if (!string.IsNullOrEmpty(dialogText))
                {
                    ScreenReader.Say(dialogText);
                    MelonLogger.Msg($"[Dialog] Text: {dialogText.Substring(0, Math.Min(200, dialogText.Length))}");
                }

                // Find sliders in the dialog hierarchy (including inactive — just loaded)
                FindSliders(dialogRoot);

                // Find clickable buttons in the dialog hierarchy
                FindButtons(dialogRoot);

                MelonLogger.Msg($"[Dialog] Found {_items.Count} interactive items");

                _dialogRoot = dialogRoot;

                if (_items.Count > 0)
                {
                    _textMode = false;
                    ScreenReader.SayQueued(Loc.Get("dialog_buttons", _items.Count));
                    AnnounceCurrentItem(queued: true);
                }
                else if (!string.IsNullOrEmpty(dialogText))
                {
                    // Text found but no buttons — enter text mode
                    _textMode = true;
                    _lastDialogText = dialogText;
                    ScreenReader.SayQueued(Loc.Get("dialog_text_mode"));
                }
                else
                {
                    ScreenReader.SayQueued(Loc.Get("dialog_no_buttons"));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog] ScanDialog error: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads all text content from the dialog hierarchy.
        /// </summary>
        private string ReadDialogText(GameObject root)
        {
            var parts = new List<string>();

            // Try YgomTextAccessor first (game's text system)
            try
            {
                var ytas = root.GetComponentsInChildren<Il2CppYgomSystem.UI.YgomTextAccessor>(true);
                if (ytas != null)
                {
                    foreach (var text in ytas)
                    {
                        if (text == null) continue;
                        string t = StripRichText(text.text);
                        if (string.IsNullOrWhiteSpace(t) || t.Length < 2) continue;
                        if (t.All(char.IsDigit)) continue;
                        if (!parts.Contains(t))
                            parts.Add(t);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog] ReadDialogText YTA error: {ex.Message}");
            }

            // Also try Unity Text as fallback
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
                            string t = StripRichText(text.text);
                            if (string.IsNullOrWhiteSpace(t) || t.Length < 2) continue;
                            if (t.All(char.IsDigit)) continue;
                            if (!parts.Contains(t))
                                parts.Add(t);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Msg($"[Dialog] ReadDialogText Text error: {ex.Message}");
                }
            }

            return string.Join(". ", parts);
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
                    if (go == null) continue;

                    string label = GetSliderLabel(slider);
                    _items.Add(new DialogItem
                    {
                        Go = go,
                        Label = label,
                        Type = ItemType.Slider,
                        SliderComponent = slider
                    });

                    MelonLogger.Msg($"[Dialog] Slider: \"{label}\" value={slider.value} min={slider.minValue} max={slider.maxValue} wholeNumbers={slider.wholeNumbers}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog] FindSliders error: {ex.Message}");
            }
        }

        private void FindButtons(GameObject root)
        {
            try
            {
                // Find all Graphics with raycastTarget in dialog hierarchy
                var graphics = root.GetComponentsInChildren<Graphic>(true);
                if (graphics == null) return;

                // Track which GOs we've already processed
                var processed = new HashSet<GameObject>();
                foreach (var item in _items)
                    processed.Add(item.Go);

                foreach (var graphic in graphics)
                {
                    if (graphic == null) continue;
                    if (!graphic.raycastTarget) continue;

                    var go = graphic.gameObject;
                    if (go == null) continue;
                    if (processed.Contains(go)) continue;

                    // Skip non-interactive elements (backgrounds, layouts, masks)
                    string goName = go.name.ToLower();
                    if (goName == "vl" || goName == "hl" || goName == "background"
                        || goName == "mask" || goName == "fillbg" || goName == "fill"
                        || goName == "fillmask" || goName == "handle"
                        || goName == "handleslidearea" || goName == "fillarea"
                        || goName == "slidingarea" || goName == "frame"
                        || goName == "bgcover" || goName == "titleset")
                        continue;

                    // Skip slider sub-elements (already handled as Slider)
                    if (IsChildOfSlider(go, root)) continue;

                    // Must look like a button — has a Selectable, or has "btn"/"button" in name,
                    // or is an Image with text children
                    bool isButton = false;
                    string label = null;

                    var selectable = go.GetComponent<Selectable>();
                    if (selectable != null && selectable.interactable)
                    {
                        isButton = true;
                        label = GetLabel(go);
                    }
                    else if (goName.Contains("btn") || goName.Contains("button")
                        || goName.Contains("close") || goName.Contains("ok"))
                    {
                        isButton = true;
                        label = GetLabel(go);
                    }

                    if (!isButton) continue;
                    if (string.IsNullOrEmpty(label)) label = go.name;

                    processed.Add(go);
                    _items.Add(new DialogItem
                    {
                        Go = go,
                        Label = label,
                        Type = ItemType.Button
                    });

                    MelonLogger.Msg($"[Dialog] Button: \"{label}\" ({go.name}) path={GetGameObjectPath(go)}");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog] FindButtons error: {ex.Message}");
            }
        }

        private bool IsChildOfSlider(GameObject go, GameObject root)
        {
            try
            {
                var parentSliders = root.GetComponentsInChildren<Slider>(true);
                if (parentSliders == null) return false;

                foreach (var slider in parentSliders)
                {
                    if (slider == null) continue;
                    if (go.transform.IsChildOf(slider.transform))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private string GetSliderLabel(Slider slider)
        {
            // Look for a sibling or parent Text that describes the slider
            try
            {
                var parent = slider.transform.parent;
                if (parent != null)
                {
                    // Try YgomTextAccessor first
                    var ytas = parent.GetComponentsInChildren<Il2CppYgomSystem.UI.YgomTextAccessor>(true);
                    if (ytas != null)
                    {
                        foreach (var t in ytas)
                        {
                            if (t == null) continue;
                            string txt = StripRichText(t.text);
                            if (!string.IsNullOrEmpty(txt) && txt.Length >= 2
                                && !txt.All(char.IsDigit))
                                return txt;
                        }
                    }

                    // Fall back to Unity Text
                    var texts = parent.GetComponentsInChildren<Text>(true);
                    if (texts != null)
                    {
                        foreach (var t in texts)
                        {
                            if (t == null) continue;
                            string txt = StripRichText(t.text);
                            if (!string.IsNullOrEmpty(txt) && txt.Length >= 2
                                && !txt.All(char.IsDigit))
                                return txt;
                        }
                    }
                }
            }
            catch { }

            // Fallback: use current value
            int val = Mathf.RoundToInt(slider.value);
            return Loc.Get("dialog_slider", val);
        }

        private string GetLabel(GameObject go)
        {
            // Try YgomTextAccessor first (game's text system)
            try
            {
                var yta = go.GetComponentInChildren<Il2CppYgomSystem.UI.YgomTextAccessor>(true);
                if (yta != null)
                {
                    string t = StripRichText(yta.text);
                    if (!string.IsNullOrEmpty(t) && t.Length >= 1)
                        return t;
                }
            }
            catch { }

            // Fall back to Unity Text
            try
            {
                var text = go.GetComponentInChildren<Text>(true);
                if (text != null)
                {
                    string t = StripRichText(text.text);
                    if (!string.IsNullOrEmpty(t) && t.Length >= 1)
                        return t;
                }
            }
            catch { }

            return go.name;
        }

        private static readonly Regex RichTextRegex = new(@"<[^>]+>", RegexOptions.Compiled);

        private string StripRichText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return RichTextRegex.Replace(text, "").Trim();
        }

        private string GetGameObjectPath(GameObject go)
        {
            try
            {
                string path = go.name;
                var t = go.transform.parent;
                int depth = 0;
                while (t != null && depth < 6)
                {
                    path = t.name + "/" + path;
                    t = t.parent;
                    depth++;
                }
                return path;
            }
            catch
            {
                return go.name;
            }
        }

        private GameObject GetManagerRoot(string managerName)
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return null;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue(managerName, out mgr)) return null;
                return mgr?.gameObject;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Input

        private void ProcessInput()
        {
            // Text mode: no buttons, just text — Enter/Space to dismiss
            if (_textMode)
            {
                if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                    || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter)
                    || InputManager.TryConsumeKeyDown(KeyCode.Space))
                {
                    DismissDialog();
                    return;
                }
                if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
                {
                    if (!string.IsNullOrEmpty(_lastDialogText))
                        ScreenReader.Say(_lastDialogText);
                    return;
                }
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
                AdjustSlider(-1);
            }
            else if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.RightArrow))
            {
                AdjustSlider(1);
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter))
            {
                ActivateCurrentItem();
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                // Re-read dialog text
                var root = GetManagerRoot("dialog");
                if (root != null)
                {
                    string text = ReadDialogText(root);
                    if (!string.IsNullOrEmpty(text))
                        ScreenReader.Say(text);
                    else
                        AnnounceCurrentItem();
                }
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                _scanned = false;
                _scanDelay = 0.5f;
                _scanAttempts = 0;
            }
        }

        #endregion

        #region Actions

        private void AnnounceCurrentItem(bool queued = false)
        {
            if (_focusIndex < 0 || _focusIndex >= _items.Count) return;

            var item = _items[_focusIndex];
            int pos = _focusIndex + 1;
            int total = _items.Count;

            string msg;
            if (item.Type == ItemType.Slider && item.SliderComponent != null)
            {
                int val = Mathf.RoundToInt(item.SliderComponent.value);
                int min = Mathf.RoundToInt(item.SliderComponent.minValue);
                int max = Mathf.RoundToInt(item.SliderComponent.maxValue);
                msg = Loc.Get("dialog_slider_item", pos, total, val, min, max);
            }
            else
            {
                msg = Loc.Get("dialog_button_item", pos, total, item.Label);
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

        private void ActivateCurrentItem()
        {
            if (_focusIndex < 0 || _focusIndex >= _items.Count) return;

            var item = _items[_focusIndex];

            try
            {
                MelonLogger.Msg($"[Dialog] Activating: {item.Label} ({item.Go.name})");

                // Try multiple click methods
                // 1. Unity Button onClick
                var button = item.Go.GetComponent<Button>();
                if (button != null)
                {
                    button.onClick.Invoke();
                    ScreenReader.Say(item.Label);
                    return;
                }

                // 2. YgomButton
                var ygomBtn = item.Go.GetComponent<Il2CppYgomSystem.UI.YgomButton>();
                if (ygomBtn != null)
                {
                    ygomBtn.onClick.Invoke();
                    ScreenReader.Say(item.Label);
                    return;
                }

                // 3. ScaleTransitionButton
                var stBtn = item.Go.GetComponent<Il2CppYgomSystem.UI.ScaleTransitionButton>();
                if (stBtn != null)
                {
                    stBtn.onClick.Invoke();
                    ScreenReader.Say(item.Label);
                    return;
                }

                // 4. Generic pointer click via ExecuteEvents
                var eventData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);
                UnityEngine.EventSystems.ExecuteEvents.Execute(
                    item.Go, eventData,
                    UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                ScreenReader.Say(item.Label);
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog] Activate error: {ex.Message}");
                ScreenReader.Say(Loc.Get("dialog_click_error"));
            }
        }

        /// <summary>
        /// Dismisses a text-only dialog by simulating a click on the dialog root.
        /// </summary>
        private void DismissDialog()
        {
            try
            {
                if (_dialogRoot == null)
                {
                    _dialogRoot = GetManagerRoot("dialog");
                }

                if (_dialogRoot != null)
                {
                    MelonLogger.Msg("[Dialog] Dismissing text-only dialog via click");
                    var eventData = new UnityEngine.EventSystems.PointerEventData(
                        UnityEngine.EventSystems.EventSystem.current);
                    UnityEngine.EventSystems.ExecuteEvents.Execute(
                        _dialogRoot, eventData,
                        UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog] DismissDialog error: {ex.Message}");
            }
        }

        #endregion
    }
}
