using System;
using System.Collections.Generic;
using System.Linq;
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
        private int _lastDialogInstanceId = -1;
        private bool _scanned;
        private float _scanDelay;
        private int _scanAttempts;
        private readonly List<DialogItem> _items = new();
        private int _focusIndex;

        // Text mode: dialog has text but no buttons — Enter/Space dismisses
        private bool _textMode;
        private string _lastDialogText = "";
        private GameObject _dialogRoot;

        // Passthrough: TutorialArrowPart with no content — let other handlers run
        private bool _passthrough;

        // Which manager the current dialog VC lives in (usually "dialog",
        // but TutorialArrowPart can appear in "content" manager)
        private string _activeManager = "dialog";

        // Post-activation rescan: after clicking a button that may open a
        // stacked dialog, we rescan after a delay to detect the new content
        private float _postActivationRescanDelay = -1f;

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
                    _lastDialogInstanceId = -1;
                    _scanned = false;
                    _textMode = false;
                    _lastDialogText = "";
                    _dialogRoot = null;
                    _items.Clear();
                    _postActivationRescanDelay = -1f;
                    _passthrough = false;
                    _activeManager = "dialog";
                }
                return;
            }

            string currentName = GameStateTracker.LastViewControllerName;

            // Check if the top VC instance changed (stacked dialog push/pop).
            // Name alone isn't enough — two HtjsonDialogs have the same name.
            int currentInstanceId = GetTopVcInstanceId("dialog");
            bool vcChanged = currentName != _lastDialogName
                || (currentInstanceId != _lastDialogInstanceId && currentInstanceId != -1);

            // New dialog appeared or stacked dialog changed — start delayed scan
            if (vcChanged)
            {
                _lastDialogName = currentName;
                _lastDialogInstanceId = currentInstanceId;
                _scanned = false;
                _textMode = false;
                _lastDialogText = "";
                _scanDelay = 2.0f;
                _scanAttempts = 0;
                _postActivationRescanDelay = -1f;
                _passthrough = false;
                GameStateTracker.SkipTutorialArrowPart = false;
                // Don't announce "Dialog" during duels — the dialog text
                // itself will be read once scanned
                if (!DuelEventAnnouncer.InDuel)
                    ScreenReader.Say(Loc.Get("screen_dialog"));
            }

            // Post-activation rescan: after clicking a button, check if a new
            // dialog was pushed on top (e.g. Privacy Notice opens policy viewer)
            if (_postActivationRescanDelay >= 0f)
            {
                _postActivationRescanDelay -= Time.deltaTime;
                if (_postActivationRescanDelay <= 0f)
                {
                    _postActivationRescanDelay = -1f;
                    int newId = GetTopVcInstanceId("dialog");
                    if (newId != _lastDialogInstanceId && newId != -1)
                    {
                        MelonLogger.Msg("[Dialog] Post-activation: stacked dialog detected, rescanning");
                        _lastDialogInstanceId = newId;
                        _scanned = false;
                        _scanDelay = 0.5f;
                        _scanAttempts = 0;
                    }
                }
            }

            if (!_scanned)
            {
                _scanDelay -= Time.deltaTime;
                if (_scanDelay <= 0f)
                {
                    _scanAttempts++;
                    ScanDialog();

                    // TutorialArrowPart with no content: skip retries entirely.
                    // These never have scannable items — retrying wastes 6 seconds.
                    // Pass through immediately so the orphan handler can dismiss it.
                    bool isTutorialArrowEmpty = _items.Count == 0 && !_textMode && IsTutorialArrowOnTop();

                    // Retry if we found nothing — but not for empty TutorialArrows
                    if (_items.Count == 0 && !_textMode && _scanAttempts < 3 && !isTutorialArrowEmpty)
                    {
                        MelonLogger.Msg($"[Dialog] No dialog items found, retrying in 2s (attempt {_scanAttempts})");
                        _scanDelay = 2.0f;
                    }
                    else
                    {
                        _scanned = true;

                        if (isTutorialArrowEmpty)
                        {
                            MelonLogger.Msg("[Dialog] TutorialArrowPart has no content, passing through immediately");
                            _passthrough = true;
                            // Tell GameStateTracker to skip this arrow so the screen
                            // reclassifies from Dialog to the content VC (e.g. Home).
                            // ForceReevaluate resets the debounce so the change applies
                            // on the next frame instead of waiting 150ms.
                            GameStateTracker.SkipTutorialArrowPart = true;
                            GameStateTracker.ForceReevaluate();
                        }
                    }
                }
            }

            ProcessInput();
        }

        /// <summary>
        /// Whether a dialog is currently being handled.
        /// </summary>
        public bool IsActive =>
            GameStateTracker.CurrentScreen == GameStateTracker.GameScreen.Dialog
            && !_passthrough;

        #endregion

        #region Scanning

        private void ScanDialog()
        {
            _items.Clear();
            _focusIndex = 0;

            try
            {
                // Find the TOP dialog VC — scan only the active dialog, not
                // the entire manager (which may contain multiple stacked dialogs).
                // TutorialArrowPart can appear in "content" manager instead of "dialog".
                GameObject dialogRoot = GetTopVcRoot("dialog");
                _activeManager = "dialog";
                if (dialogRoot == null)
                {
                    var contentRoot = GetTopVcRoot("content");
                    if (contentRoot != null &&
                        (contentRoot.name == "TutorialArrow" || contentRoot.name == "TutorialArrowPart"))
                    {
                        dialogRoot = contentRoot;
                        _activeManager = "content";
                    }
                }
                if (dialogRoot == null)
                {
                    MelonLogger.Msg("[Dialog] No top dialog VC found");
                    return;
                }

                MelonLogger.Msg($"[Dialog] Scanning dialog root: {dialogRoot.name}");

                // Log the actual VC type + full dialog stack for diagnostics
                LogDialogVcType(dialogRoot.name);

                // Diagnostics: dump toggles, button interactable states, and special VCs
                DumpDialogDiagnostics(dialogRoot);

                // Read and announce dialog text first (Say to interrupt "Dialog" announcement).
                // Skip re-announcing identical text on retry scans.
                string dialogText = ReadDialogText(dialogRoot);
                if (!string.IsNullOrEmpty(dialogText) && dialogText != _lastDialogText)
                {
                    ScreenReader.Say(dialogText);
                    MelonLogger.Msg($"[Dialog] Text: {dialogText.Substring(0, Math.Min(200, dialogText.Length))}");
                }

                // Find sliders in the dialog hierarchy (including inactive — just loaded)
                FindSliders(dialogRoot);

                // Find clickable buttons in the dialog hierarchy
                FindButtons(dialogRoot);

                // If top VC has nothing (empty overlay like TutorialArrowPart),
                // dismiss it or pass through so other handlers can run
                if (_items.Count == 0 && string.IsNullOrEmpty(dialogText))
                {
                    if (IsTutorialArrowOnTop())
                    {
                        // Don't click the arrow — screen-center click lands on
                        // underlying quiz banners in Duel Trials. Just return;
                        // the passthrough logic in Update() will set
                        // SkipTutorialArrowPart + ForceReevaluate so the screen
                        // reclassifies and ScreenButtonHandler takes over.
                        return;
                    }

                    var managerRoot = GetManagerRoot("dialog");
                    if (managerRoot != null && managerRoot != dialogRoot)
                    {
                        MelonLogger.Msg($"[Dialog] Top VC empty, falling back to manager root");
                        dialogRoot = managerRoot;

                        dialogText = ReadDialogText(dialogRoot);
                        if (!string.IsNullOrEmpty(dialogText) && dialogText != _lastDialogText)
                        {
                            ScreenReader.Say(dialogText);
                            MelonLogger.Msg($"[Dialog] Text: {dialogText.Substring(0, Math.Min(200, dialogText.Length))}");
                        }

                        FindSliders(dialogRoot);
                        FindButtons(dialogRoot);
                    }
                }

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
        /// Dumps diagnostic info for the dialog: toggles, button states, special VCs.
        /// Helps debug cases where buttons appear but don't respond (e.g. Privacy Notice).
        /// </summary>
        private void DumpDialogDiagnostics(GameObject root)
        {
            try
            {
                // Check for PolicySettingsDialogViewController anywhere in the hierarchy
                var policyVc = root.GetComponentInChildren<Il2CppYgomGame.Settings.PolicySettingsDialogViewController>(true);
                if (policyVc != null)
                {
                    MelonLogger.Msg($"[Dialog][Diag] PolicySettingsDialogVC FOUND");
                    MelonLogger.Msg($"[Dialog][Diag]   optionCheck GO: {(policyVc.optionCheck != null ? policyVc.optionCheck.name : "null")}");
                    MelonLogger.Msg($"[Dialog][Diag]   optionDesc GO: {(policyVc.optionDesc != null ? policyVc.optionDesc.name : "null")}");
                    if (policyVc.optionCheck != null)
                    {
                        var toggle = policyVc.optionCheck.GetComponent<Toggle>();
                        if (toggle != null)
                            MelonLogger.Msg($"[Dialog][Diag]   optionCheck Toggle: isOn={toggle.isOn}, interactable={toggle.interactable}");
                        else
                            MelonLogger.Msg($"[Dialog][Diag]   optionCheck has no Toggle component");
                    }
                }

                // Also check the manager root (PolicyVC may not be on top VC)
                var managerRoot = GetManagerRoot("dialog");
                if (managerRoot != null && managerRoot != root)
                {
                    var policyVc2 = managerRoot.GetComponentInChildren<Il2CppYgomGame.Settings.PolicySettingsDialogViewController>(true);
                    if (policyVc2 != null && policyVc2 != policyVc)
                        MelonLogger.Msg($"[Dialog][Diag] PolicySettingsDialogVC found on manager root (not top VC)");
                }

                // Dump all Toggles in the dialog
                var toggles = root.GetComponentsInChildren<Toggle>(true);
                if (toggles != null && toggles.Length > 0)
                {
                    MelonLogger.Msg($"[Dialog][Diag] Found {toggles.Length} Toggle(s):");
                    foreach (var t in toggles)
                    {
                        if (t == null) continue;
                        MelonLogger.Msg($"[Dialog][Diag]   Toggle: {t.gameObject.name} isOn={t.isOn} interactable={t.interactable} path={GetGameObjectPath(t.gameObject)}");
                    }
                }

                // Dump all Buttons with their interactable state
                var buttons = root.GetComponentsInChildren<Button>(true);
                if (buttons != null && buttons.Length > 0)
                {
                    MelonLogger.Msg($"[Dialog][Diag] Found {buttons.Length} Button(s):");
                    foreach (var b in buttons)
                    {
                        if (b == null) continue;
                        string label = LabelExtractor.GetLabel(b.gameObject);
                        MelonLogger.Msg($"[Dialog][Diag]   Button: \"{label}\" ({b.gameObject.name}) interactable={b.interactable} path={GetGameObjectPath(b.gameObject)}");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog][Diag] Error: {ex.Message}");
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
                        string t = LabelExtractor.StripRichText(text.text);
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
                            string t = LabelExtractor.StripRichText(text.text);
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

                    string label = LabelExtractor.GetSliderLabel(slider);
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
                        label = LabelExtractor.GetLabel(go);
                    }
                    else if (goName.Contains("btn") || goName.Contains("button")
                        || goName.Contains("close") || goName.Contains("ok"))
                    {
                        isButton = true;
                        label = LabelExtractor.GetLabel(go);
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

        /// <summary>
        /// Gets the manager's own gameObject — includes ALL stacked dialogs.
        /// Used as fallback when the top VC is an empty overlay.
        /// </summary>
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

        /// <summary>
        /// Gets the top ViewController's gameObject from a named manager.
        /// Unlike GetManagerRoot, this returns only the active dialog — not the
        /// entire manager hierarchy. Critical for layered dialogs (e.g.
        /// TutorialDuelMessage on top of Mission).
        /// </summary>
        private GameObject GetTopVcRoot(string managerName)
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return null;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue(managerName, out mgr)) return null;
                if (mgr == null) return null;

                var topVc = mgr.GetStackTopViewController();
                if (topVc?.gameObject == null) return null;

                return topVc.gameObject;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Gets the instance ID of the top ViewController in a named manager.
        /// Used to detect stacked dialog changes (same name, different instance).
        /// </summary>
        private int GetTopVcInstanceId(string managerName)
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return -1;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue(managerName, out mgr)) return -1;
                if (mgr == null) return -1;

                var topVc = mgr.GetStackTopViewController();
                if (topVc?.gameObject == null) return -1;

                return topVc.gameObject.GetInstanceID();
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>
        /// Gets the top ViewController component from a named manager.
        /// Used for calling SendBack() to dismiss dialogs.
        /// </summary>
        private Il2CppYgomSystem.UI.ViewController GetTopVc(string managerName)
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return null;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue(managerName, out mgr)) return null;
                if (mgr == null) return null;

                return mgr.GetStackTopViewController();
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
                // Re-read dialog text from the active dialog VC
                var root = GetTopVcRoot(_activeManager);
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

            if (!LabelExtractor.IsAlive(item.Go))
            {
                ScreenReader.Say(Loc.Get("dialog_click_error"));
                _items.RemoveAt(_focusIndex);
                if (_focusIndex >= _items.Count) _focusIndex = Math.Max(0, _items.Count - 1);
                return;
            }

            // If TutorialArrow is on top, route through its ipclick handler.
            // The tutorial system requires clicks to go through the arrow's
            // routing — direct clicks on the target button don't register.
            if (IsTutorialArrowOnTop() && ActivateViaTutorialArrow(item.Label))
                return;

            try
            {
                MelonLogger.Msg($"[Dialog] Activating: {item.Label} ({item.Go.name})");

                // Diagnostics: log interactable state and toggle status at activation time
                var selectable = item.Go.GetComponent<Selectable>();
                if (selectable != null)
                    MelonLogger.Msg($"[Dialog]   Selectable interactable={selectable.interactable}");
                else
                    MelonLogger.Msg($"[Dialog]   No Selectable component on button GO");

                var btn = item.Go.GetComponent<Button>();
                if (btn != null)
                    MelonLogger.Msg($"[Dialog]   Button component: interactable={btn.interactable}, onClick listeners={btn.onClick.GetPersistentEventCount()}");

                // Check for any Toggle on or near this button
                var toggle = item.Go.GetComponent<Toggle>();
                if (toggle != null)
                    MelonLogger.Msg($"[Dialog]   Toggle on button: isOn={toggle.isOn}, interactable={toggle.interactable}");
                var parentToggle = item.Go.GetComponentInParent<Toggle>();
                if (parentToggle != null)
                    MelonLogger.Msg($"[Dialog]   Parent Toggle: isOn={parentToggle.isOn}, interactable={parentToggle.interactable}");

                // Special case: Privacy Notice "I've confirmed" button has no click
                // handlers — must call PolicySettingsDialogViewController.OnConfirm() directly
                if (TryActivateSpecialDialog(item))
                    return;

                var eventData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);

                // Strategy 1: Htjson ButtonWidget — fire full click cycle on its YgomButton
                bool activated = false;
                var buttonWidget = item.Go.GetComponentInParent<Il2CppYgomSystem.Htjson.ButtonWidget>();
                if (buttonWidget != null)
                {
                    var ygomBtn = buttonWidget.button;
                    if (ygomBtn != null && ygomBtn.gameObject != null)
                    {
                        MelonLogger.Msg($"[Dialog] Htjson ButtonWidget, clicking YgomButton on {ygomBtn.gameObject.name}");
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
                        activated = true;
                    }
                }

                // Strategy 2: Htjson CheckBoxWidget — OnPointerClick dispatches to receiver
                if (!activated)
                {
                    var checkBox = item.Go.GetComponent<Il2CppYgomSystem.Htjson.CheckBoxWidget>();
                    if (checkBox != null)
                    {
                        MelonLogger.Msg($"[Dialog] Htjson CheckBoxWidget on {item.Go.name}");
                        checkBox.OnPointerClick(eventData);
                        activated = true;
                    }
                }

                // Strategy 3: Button.onClick.Invoke — works for runtime-added
                // listeners (Htjson buttons) where ExecuteEvents fails
                if (!activated && btn != null)
                {
                    MelonLogger.Msg($"[Dialog] Using Button.onClick.Invoke()");
                    btn.onClick.Invoke();
                    activated = true;
                }

                // Strategy 3b: Htjson ButtonSet / div — handler may be on a parent
                // container (e.g. Task0_0). Walk up and fire onClick on any Button.
                if (!activated && (item.Go.name == "ButtonSet" || item.Go.name == "div"))
                {
                    var parent = item.Go.transform.parent;
                    int depth = 0;
                    while (parent != null && depth < 4)
                    {
                        var parentBtn = parent.GetComponent<Button>();
                        if (parentBtn != null && parentBtn.onClick != null)
                        {
                            MelonLogger.Msg($"[Dialog] ButtonSet parent onClick.Invoke on {parent.gameObject.name}");
                            parentBtn.onClick.Invoke();
                            activated = true;
                            break;
                        }
                        parent = parent.parent;
                        depth++;
                    }
                }

                // Strategy 4: Full click cycle via ExecuteEvents as fallback
                if (!activated)
                {
                    UnityEngine.EventSystems.ExecuteEvents.Execute(
                        item.Go, eventData,
                        UnityEngine.EventSystems.ExecuteEvents.pointerDownHandler);
                    UnityEngine.EventSystems.ExecuteEvents.Execute(
                        item.Go, eventData,
                        UnityEngine.EventSystems.ExecuteEvents.pointerUpHandler);
                    UnityEngine.EventSystems.ExecuteEvents.Execute(
                        item.Go, eventData,
                        UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                }

                ScreenReader.Say(item.Label);

                // Schedule a post-activation rescan to detect stacked dialogs
                // (e.g. clicking "Privacy Notice" opens a new dialog on top)
                _postActivationRescanDelay = 1.5f;
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog] Activate error: {ex.Message}");
                ScreenReader.Say(Loc.Get("dialog_click_error"));
            }
        }

        /// <summary>
        /// Handles dialogs where standard click simulation doesn't work.
        /// Returns true if this method handled the activation.
        /// </summary>
        private bool TryActivateSpecialDialog(DialogItem item)
        {
            try
            {
                // Privacy Notice: "I've confirmed" has no click handlers.
                // Must call PolicySettingsDialogViewController.OnConfirm() directly.
                if (item.Label == "I've confirmed")
                {
                    // Walk the dialog manager VC stack to find PolicySettingsDialogVC.
                    // FindObjectOfType fails for this IL2CPP type.
                    var policyVc = FindVcInStack<
                        Il2CppYgomGame.Settings.PolicySettingsDialogViewController>("dialog");
                    if (policyVc != null)
                    {
                        MelonLogger.Msg("[Dialog] Calling PolicySettingsDialogVC.OnConfirm()");
                        policyVc.OnConfirm();
                        ScreenReader.Say(item.Label);
                        return true;
                    }
                    MelonLogger.Msg("[Dialog] PolicySettingsDialogVC not found in stack, falling back to click");
                }

                // ConfirmDialogViewController (ticket exchange, card trader, etc.)
                // Both "btn" GOs share the same onClick handler, so Button.onClick.Invoke()
                // always triggers YES/TRADE. We need OnClickedNo() / OnClickedYes() directly.
                var confirmVc = FindVcInStack<
                    Il2CppYgomGame.Menu.ConfirmDialogViewController>("dialog");
                if (confirmVc != null)
                {
                    // Determine which button by label
                    string label = item.Label?.ToUpperInvariant() ?? "";
                    if (label == "NO" || label == "CANCEL" || label == "CLOSE"
                        || label == "いいえ" || label == "キャンセル")
                    {
                        MelonLogger.Msg("[Dialog] ConfirmDialog: calling OnClickedNo()");
                        confirmVc.OnClickedNo();
                        ScreenReader.Say(item.Label);
                        return true;
                    }
                    else if (label == "TRADE" || label == "YES" || label == "OK"
                        || label == "EXCHANGE" || label == "CONFIRM"
                        || label == "はい" || label == "交換")
                    {
                        MelonLogger.Msg("[Dialog] ConfirmDialog: calling OnClickedYes()");
                        confirmVc.OnClickedYes();
                        ScreenReader.Say(item.Label);
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog] TryActivateSpecialDialog error: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Walks the VC stack in a named manager and returns the first VC
        /// that can be cast to the requested type. Used when FindObjectOfType
        /// fails for IL2CPP types.
        /// </summary>
        private T FindVcInStack<T>(string managerName) where T : Il2CppSystem.Object
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return null;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue(managerName, out mgr)) return null;
                if (mgr == null) return null;

                int count = mgr.GetStackCount();
                MelonLogger.Msg($"[Dialog] Searching VC stack ({count} entries) for {typeof(T).Name}");

                for (int i = 0; i < count; i++)
                {
                    var vc = mgr.GetStackViewController(i);
                    if (vc == null) continue;

                    MelonLogger.Msg($"[Dialog]   Stack[{i}]: {vc.gameObject?.name} ({vc.GetIl2CppType()?.Name})");

                    var cast = vc.TryCast<T>();
                    if (cast != null)
                    {
                        MelonLogger.Msg($"[Dialog]   Found {typeof(T).Name} at stack index {i}");
                        return cast;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog] FindVcInStack error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Dismisses a text-only dialog. For TutorialDuelMessage, uses OnPointerClick
        /// to simulate a real tap — SendBack skips the tutorial advancement callback.
        /// For other dialogs, uses SendBack (proper VC dismiss), then falls back to
        /// click simulation on the dialog root.
        /// </summary>
        private void DismissDialog()
        {
            try
            {
                var topVc = GetTopVc(_activeManager);
                if (topVc != null)
                {
                    string vcName = topVc.gameObject?.name ?? "";

                    // TutorialDuelMessage must be dismissed via OnPointerClick, not
                    // SendBack. The tutorial system passes a callback via Open() that
                    // only fires on proper dismissal — SendBack bypasses it, leaving
                    // the tutorial (and stage advancement) stuck.
                    if (vcName == "TutorialDuelMessage")
                    {
                        MelonLogger.Msg("[Dialog] Dismissing TutorialDuelMessage via OnPointerClick");
                        var eventData = new UnityEngine.EventSystems.PointerEventData(
                            UnityEngine.EventSystems.EventSystem.current);
                        eventData.position = new Vector2(Screen.width / 2f, Screen.height / 2f);
                        UnityEngine.EventSystems.ExecuteEvents.Execute(
                            topVc.gameObject, eventData,
                            UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                        return;
                    }

                    // Other dialogs: SendBack (proper ViewController dismiss)
                    MelonLogger.Msg($"[Dialog] Dismissing via SendBack on {vcName}");
                    topVc.SendBack();
                    return;
                }

                // Strategy 2: Click on the stored dialog root
                if (_dialogRoot != null)
                {
                    MelonLogger.Msg("[Dialog] Dismissing via click on dialog root");
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

        #region TutorialArrow

        /// <summary>
        /// Checks if a TutorialArrow overlay is the top dialog VC.
        /// These overlays have no content but block interaction with dialogs underneath.
        /// </summary>
        private bool IsTutorialArrowOnTop()
        {
            try
            {
                var topGo = GetTopVcRoot(_activeManager);
                if (topGo == null) return false;
                string name = topGo.name;
                return name == "TutorialArrow" || name == "TutorialArrowPart";
            }
            catch { return false; }
        }

        /// <summary>
        /// Routes a click through the TutorialArrow by tapping the arrow VC itself.
        /// Clicks the TutorialArrow's ipclick target directly. The tutorial system
        /// requires clicks to route through the arrow — but arrowVc.OnPointerClick
        /// silently fails when the click position doesn't hit the physicTarget.
        /// Calling ipclick handlers directly bypasses that position check.
        /// Returns false so ActivateCurrentItem still runs btn.onClick as backup.
        /// </summary>
        private bool ActivateViaTutorialArrow(string label)
        {
            try
            {
                var topVc = GetTopVc(_activeManager);
                if (topVc == null) return false;

                var arrowVc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                if (arrowVc == null) return false;

                var ipclick = arrowVc.ipclick;
                if (ipclick == null || ipclick.Length == 0)
                {
                    MelonLogger.Msg("[Dialog] TutorialArrow has no ipclick handlers");
                    return false;
                }

                var eventData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);
                eventData.position = new Vector2(Screen.width / 2f, Screen.height / 2f);

                // Click ipclick handlers directly — bypasses arrow's position check
                for (int i = 0; i < ipclick.Length; i++)
                {
                    try
                    {
                        var handler = ipclick[i];
                        if (handler == null) continue;

                        var button = handler.TryCast<Il2CppYgomSystem.UI.YgomButton>();
                        if (button != null)
                        {
                            MelonLogger.Msg($"[Dialog] Clicking ipclick[{i}] YgomButton on {button.gameObject?.name ?? "?"}");
                            button.OnPointerClick(eventData);
                            ScreenReader.Say(label);
                            return false;
                        }

                        var mb = handler.TryCast<MonoBehaviour>();
                        if (mb?.gameObject != null)
                        {
                            MelonLogger.Msg($"[Dialog] Clicking ipclick[{i}] {mb.GetType().Name} on {mb.gameObject.name}");
                            UnityEngine.EventSystems.ExecuteEvents.Execute(
                                mb.gameObject, eventData,
                                UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
                            ScreenReader.Say(label);
                            return false;
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Msg($"[Dialog] ipclick[{i}] error: {ex.Message}");
                    }
                }

                MelonLogger.Msg("[Dialog] All ipclick handlers failed, falling through");
                return false;
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog] ActivateViaTutorialArrow error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Dismisses a TutorialArrow overlay via OnPointerClick or fallback click.
        /// </summary>
        private void DismissTutorialArrow()
        {
            try
            {
                var topVc = GetTopVc(_activeManager);
                if (topVc?.gameObject == null) return;

                MelonLogger.Msg($"[Dialog] Dismissing TutorialArrow: {topVc.gameObject.name}");

                var arrowVc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                if (arrowVc != null)
                {
                    // Use the proper click method with physicTarget + RegistPointerCurrentRaycast
                    Main.ClickArrowAtTarget(arrowVc);
                    return;
                }

                // Fallback: generic pointer click
                var fallbackData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);
                UnityEngine.EventSystems.ExecuteEvents.Execute(
                    topVc.gameObject, fallbackData,
                    UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog] DismissTutorialArrow error: {ex.Message}");
            }
        }

        #endregion

        #region Diagnostics

        /// <summary>
        /// Logs the VC type and full dialog stack for diagnostics.
        /// Helps identify unknown dialog types when debugging stuck screens.
        /// </summary>
        private static void LogDialogVcType(string goName)
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("dialog", out mgr)) return;

                var topVc = mgr?.GetStackTopViewController();
                if (topVc == null) return;

                // Log the actual C# type of the VC
                string typeName = topVc.GetType()?.Name ?? "?";
                MelonLogger.Msg($"[Dialog][Type] {goName} -> VC type: {typeName}");

                // Check for TutorialArrow/Part overlay
                bool isArrow = goName == "TutorialArrow" || goName == "TutorialArrowPart";
                if (isArrow)
                {
                    var arrowVc = topVc.TryCast<Il2CppYgomGame.Menu.TutorialArrowViewController>();
                    if (arrowVc != null)
                    {
                        var ipclick = arrowVc.ipclick;
                        var target = arrowVc.physicTarget;
                        MelonLogger.Msg($"[Dialog][Type]   Arrow: ipclick={ipclick?.Length ?? 0}, physicTarget={target?.gameObject?.name ?? "null"}");
                    }
                }

                // Log the full dialog stack
                // Walk content manager too for context
                foreach (string mgrName in new[] { "dialog", "dialogbase", "content" })
                {
                    Il2CppYgomSystem.UI.ViewControllerManager m;
                    if (namedManager.TryGetValue(mgrName, out m))
                    {
                        var vc = m?.GetStackTopViewController();
                        if (vc?.gameObject != null)
                            MelonLogger.Msg($"[Dialog][Stack] {mgrName} = {vc.gameObject.name} (type: {vc.GetType()?.Name})");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Msg($"[Dialog][Type] Error: {ex.Message}");
            }
        }

        #endregion
    }
}
