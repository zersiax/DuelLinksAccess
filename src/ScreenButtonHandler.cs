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

        /// <summary>
        /// Screens that this handler should NOT process.
        /// Dialog is handled by DialogHandler; Duel will get its own handler.
        /// </summary>
        private static readonly HashSet<GameStateTracker.GameScreen> _excludedScreens = new()
        {
            GameStateTracker.GameScreen.Unknown,
            GameStateTracker.GameScreen.Dialog,
            GameStateTracker.GameScreen.Duel,
        };

        #endregion

        #region Types

        private enum ItemType { Button, Slider }

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

                // Find sliders first
                FindSliders(_screenRoot);

                // Find clickable buttons
                FindButtons(_screenRoot);

                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"Found {_items.Count} items");

                if (_items.Count > 0)
                {
                    ScreenReader.SayQueued(
                        Loc.Get("screen_buttons", _items.Count));
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
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] Scan error: {ex.Message}");
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
                        string t = StripRichText(yta.text);
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
                            string t = StripRichText(text.text);
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
                var root = GetTopViewGameObject(namedManager, "content");
                if (root != null) return root;

                // Fall back to base manager
                root = GetTopViewGameObject(namedManager, "base");
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

                    string label = GetSliderLabel(slider);
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

                    string label = GetLabel(go);
                    if (string.IsNullOrEmpty(label)) label = go.name;

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

        #region Labels

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

        private string GetSliderLabel(Slider slider)
        {
            try
            {
                var parent = slider.transform.parent;
                if (parent != null)
                {
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

            int val = Mathf.RoundToInt(slider.value);
            return Loc.Get("screen_slider", val);
        }

        private static readonly Regex RichTextRegex = new(@"<[^>]+>", RegexOptions.Compiled);

        private string StripRichText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            return RichTextRegex.Replace(text, "").Trim();
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
                AnnounceCurrentItem();
            }
            else if (InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
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

        private void ActivateCurrentItem()
        {
            if (_focusIndex < 0 || _focusIndex >= _items.Count) return;

            var item = _items[_focusIndex];

            try
            {
                DebugLogger.Log(LogCategory.Handler, "ScreenBtn",
                    $"Activating: {item.Label} ({item.Go.name})");

                // Try Unity Button onClick
                var button = item.Go.GetComponent<Button>();
                if (button != null)
                {
                    button.onClick.Invoke();
                    ScreenReader.Say(item.Label);
                    return;
                }

                // Try YgomButton
                var ygomBtn = item.Go.GetComponent<Il2CppYgomSystem.UI.YgomButton>();
                if (ygomBtn != null)
                {
                    ygomBtn.onClick.Invoke();
                    ScreenReader.Say(item.Label);
                    return;
                }

                // Try ScaleTransitionButton
                var stBtn = item.Go.GetComponent<Il2CppYgomSystem.UI.ScaleTransitionButton>();
                if (stBtn != null)
                {
                    stBtn.onClick.Invoke();
                    ScreenReader.Say(item.Label);
                    return;
                }

                // Generic pointer click via ExecuteEvents
                ClickGameObject(item.Go);
                ScreenReader.Say(item.Label);
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[ScreenBtn] Activate error: {ex.Message}");
                ScreenReader.Say(Loc.Get("screen_click_error"));
            }
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
        /// Triggers back navigation on the current ViewController.
        /// </summary>
        private void GoBack()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return;

                // Try content manager first
                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (namedManager.TryGetValue("content", out mgr) && mgr != null)
                {
                    var topVc = mgr.GetStackTopViewController();
                    if (topVc != null)
                    {
                        topVc.SendBack();
                        ScreenReader.Say(Loc.Get("screen_back"));
                        return;
                    }
                }

                // Try base manager
                if (namedManager.TryGetValue("base", out mgr) && mgr != null)
                {
                    var topVc = mgr.GetStackTopViewController();
                    if (topVc != null)
                    {
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

        #endregion
    }
}
