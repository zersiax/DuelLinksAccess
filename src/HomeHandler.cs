using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Il2CppYgomGame.Menu;
using Il2CppYgomGame.Single;
using Il2CppYgomGame.Utility;

namespace DuelLinksAccess
{
    /// <summary>
    /// Keyboard navigation for the home/world-map screen (SingleViewController)
    /// and the character detail panel (HomeViewController).
    ///
    /// Replaces the generic ScreenButtonHandler scan for GameScreen.Home,
    /// presenting a curated list of named destinations instead of the
    /// raw 40-plus button soup that scan produces.
    ///
    /// Controls while active:
    ///   Up / Down    — cycle items
    ///   Enter        — activate current item
    ///   Left / Right — change map area (area selector row) or change character (character panel)
    ///   Space        — re-scan / rebuild list
    ///   Tab          — re-read current item
    /// </summary>
    public class HomeHandler
    {
        #region Types

        private sealed class HomeItem
        {
            public string Label;
            public Action Activate;
            public bool IsAreaSelector;
            public bool IsCharaSelector;
        }

        #endregion

        #region Fields

        private bool _wasActive;
        private string _lastVcGoName = "";

        private readonly List<HomeItem> _items = new();
        private int _focusIndex;

        // Map area tracking (area selector row)
        private int _mapAreaIndex;

        // Character panel tracking
        private int _charaListIndex;
        private List<int> _unlockedCharas;

        // Cooldown between activations
        private float _operationCooldown;
        private const float OperationCooldownTime = 0.4f;

        // Delayed initial scan (assets load asynchronously)
        private float _initialScanDelay;
        private bool _initialScanDone;

        #endregion

        #region Properties

        /// <summary>Whether this handler is actively managing the home screen.</summary>
        public bool IsActive { get; private set; }

        #endregion

        #region Public Methods

        /// <summary>Called each frame from Main.UpdateHandlers().</summary>
        public void Update()
        {
            if (_operationCooldown > 0f)
                _operationCooldown -= Time.deltaTime;

            if (GameStateTracker.CurrentScreen != GameStateTracker.GameScreen.Home)
            {
                if (_wasActive) Deactivate();
                return;
            }

            // Track VC changes so we rebuild on screen transitions
            var vcName = GameStateTracker.LastViewControllerName;
            if (vcName != _lastVcGoName)
            {
                _lastVcGoName = vcName;
                StartScan();
            }

            // Delayed scan — SingleViewController content loads asynchronously
            if (!_initialScanDone)
            {
                _initialScanDelay -= Time.deltaTime;
                if (_initialScanDelay <= 0f)
                {
                    if (TryBuildItems())
                    {
                        _initialScanDone = true;
                        Activate();
                    }
                    else
                    {
                        // Keep retrying — VC content loads asynchronously after
                        // scene transitions (e.g. returning from a duel).
                        _initialScanDelay = 0.5f;
                    }
                }
                return;
            }

            if (!IsActive) return;

            ProcessInput();
        }

        #endregion

        #region Activation

        private void StartScan()
        {
            _items.Clear();
            _focusIndex = 0;
            _initialScanDone = false;
            _initialScanDelay = 0.6f;
        }

        private void Activate()
        {
            IsActive = true;
            _wasActive = true;
            AccessStateManager.TryEnter(AccessStateManager.State.Home);
            AnnounceList();
            DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"Activated with {_items.Count} items");
        }

        private void Deactivate()
        {
            IsActive = false;
            _wasActive = false;
            _items.Clear();
            _lastVcGoName = "";
            _initialScanDone = false;
            AccessStateManager.Exit(AccessStateManager.State.Home);
            DebugLogger.Log(LogCategory.Handler, "HomeHandler", "Deactivated");
        }

        #endregion

        #region Input

        private void ProcessInput()
        {
            // Space — rebuild list
            if (InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                ScreenReader.Say(Loc.Get("screen_rescan"));
                StartScan();
                return;
            }

            // Tab — re-read current item
            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                AnnounceCurrentItem();
                return;
            }

            if (_items.Count == 0) return;

            var item = _items[_focusIndex];

            // Up / Down — navigate
            if (InputManager.TryConsumeKeyDown(KeyCode.UpArrow))
            {
                _focusIndex = (_focusIndex - 1 + _items.Count) % _items.Count;
                AnnounceCurrentItem();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.DownArrow))
            {
                _focusIndex = (_focusIndex + 1) % _items.Count;
                AnnounceCurrentItem();
                return;
            }

            // Left / Right — change area or character
            if (InputManager.TryConsumeKeyDown(KeyCode.LeftArrow))
            {
                if (item.IsAreaSelector) TryMoveArea(-1);
                else if (item.IsCharaSelector) TryMoveChara(-1);
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.RightArrow))
            {
                if (item.IsAreaSelector) TryMoveArea(1);
                else if (item.IsCharaSelector) TryMoveChara(1);
                return;
            }

            // G — gem balance
            if (InputManager.TryConsumeKeyDown(KeyCode.G))
            {
                AnnounceGemBalance();
                return;
            }

            // Enter — activate
            if (InputManager.TryConsumeKeyDown(KeyCode.Return) ||
                InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter))
            {
                if (_operationCooldown > 0f) return;

                if (item.IsAreaSelector)
                {
                    TryMoveArea(0); // reads current area without moving
                    return;
                }

                if (item.Activate != null)
                {
                    _operationCooldown = OperationCooldownTime;
                    DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"Activating: {item.Label}");
                    ScreenReader.Say(item.Label);
                    try { item.Activate(); }
                    catch (Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"Activate failed: {ex.Message}");
                    }
                }
                return;
            }
        }

        #endregion

        #region Item Building

        private bool TryBuildItems()
        {
            _items.Clear();
            _focusIndex = 0;

            // Character detail panel takes priority when visible
            var homeVc = FindActiveHomeViewController();
            if (homeVc != null)
            {
                BuildHomeViewItems(homeVc);
                return _items.Count > 0;
            }

            var singleVc = FindActiveSingleViewController();
            if (singleVc?.gameObject == null) return false;

            BuildSingleViewItems(singleVc);
            return _items.Count > 0;
        }

        // ── Single (world map) ────────────────────────────────────────────────

        private void BuildSingleViewItems(SingleViewController view)
        {
            SyncMapAreaIndex(view);

            // Area selector — Left/Right changes the active map area
            _items.Add(new HomeItem
            {
                Label = Loc.Get("home_area_selector", GetAreaLabel()),
                IsAreaSelector = true
            });

            // Characters and interactables on the current area
            AddMapObjectItems(view);

            // Footer area buttons (Gate shown only when not present as map object)
            bool hasGateObject = HasActiveMapObjectOfType<GateTouchObject>() ||
                                 HasActiveMapObjectOfType<GateTouchObjectDSOD>();
            if (!hasGateObject)
                AddButtonItem(view.gameObject, "Layer4/SingleFooter/AreaButtons/Gate/YgomButton", Loc.Get("home_gate"));

            AddButtonItem(view.gameObject, "Layer4/SingleFooter/AreaButtons/Colosseum/YgomButton", Loc.Get("home_colosseum"));
            AddButtonItem(view.gameObject, "Layer4/SingleFooter/AreaButtons/Shop/YgomButton", Loc.Get("home_shop"));
            AddButtonItem(view.gameObject, "Layer4/SingleFooter/AreaButtons/Labo/YgomButton", Loc.Get("home_labo"));

            // Area navigation arrows
            AddDirectButtonItem(view.ArrowLButton, Loc.Get("home_area_left"));
            AddDirectButtonItem(view.ArrowRButton, Loc.Get("home_area_right"));

            // Header buttons — Events badge shows active event count
            string eventsLabel = Loc.Get("home_events");
            string eventsBadge = ReadChildBadge(view.generalInfomationButton);
            if (!string.IsNullOrEmpty(eventsBadge))
                eventsLabel = $"{eventsLabel} ({eventsBadge})";
            AddButtonItem(view.gameObject, "Layer4/SingleHeaderBG/GeneralInfomationButton/YgomButton", eventsLabel);
            AddButtonItem(view.gameObject, "Layer4/SingleHeaderBG/HeaderButtons/PresentButton", Loc.Get("home_gifts"));
            AddButtonItem(view.gameObject, "Layer4/SingleHeaderBG/HeaderButtons/InfoButton", Loc.Get("home_info"));
            AddButtonItem(view.gameObject, "Layer4/SingleHeaderBG/HeaderButtons/UserInfoButton", Loc.Get("home_user_info"));
            AddButtonItem(view.gameObject, "Layer4/SingleHeaderBG/HeaderButtons/OptionButton", Loc.Get("home_option"));
            AddButtonItem(view.gameObject, "Layer4/SingleHeaderBG/NeuronCode(Clone)", Loc.Get("home_neuron_code"));

            // Footer right panel — character, deck
            AddButtonItem(view.gameObject, "Layer4/SingleFooter/MenuRoot/MenuRightBase/Mask/SelectChara",
                Loc.Get("home_character", GetCurrentCharacterName(view)));
            AddButtonItem(view.gameObject, "Layer4/SingleFooter/MenuRoot/MenuRightBase/SelectDeck", Loc.Get("home_deck_select"));
            AddButtonItem(view.gameObject, "Layer4/SingleFooter/MenuRoot/MenuRightBase/EditDeck", Loc.Get("home_deck_edit"));

            // Footer left — missions with live stage label
            AddButtonItem(view.gameObject, "Layer4/SingleFooter/MenuRoot/MenuLeftBase/Mission",
                GetMissionLabel());

            NumberDuplicateLabels();
        }

        private void AddMapObjectItems(SingleViewController view)
        {
            var currentArea = GetCurrentMapArea();

            AddActiveMapObjects<BillboardObject>(currentArea);
            AddActiveMapObjects<GateTouchObject>(null);
            AddActiveMapObjects<GateTouchObjectDSOD>(null);
            AddActiveMapObjects<ShopObject>(null);
            AddActiveMapObjects<SchoolObject>(null);
            AddActiveMapObjects<DuelCenterObject>(null);
        }

        private void AddActiveMapObjects<T>(MapArea? requiredArea)
            where T : MapObjectBase
        {
            T[] found;
            try { found = Resources.FindObjectsOfTypeAll<T>(); }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"FindObjectsOfTypeAll<{typeof(T).Name}> failed: {ex.Message}");
                return;
            }

            foreach (var mapObj in found)
            {
                if (mapObj?.gameObject == null || !mapObj.gameObject.activeInHierarchy) continue;

                if (requiredArea.HasValue && mapObj.mapObjectData != null &&
                    (MapArea)mapObj.mapObjectData.area != requiredArea.Value)
                    continue;

                var label = GetMapObjectLabel(mapObj);
                var captured = mapObj;
                _items.Add(new HomeItem
                {
                    Label = label,
                    Activate = () => ActivateMapObject(captured)
                });
            }
        }

        private void AddButtonItem(GameObject root, string relativePath, string label)
        {
            if (root == null) return;

            var t = root.transform.Find(relativePath);
            if (t == null || !t.gameObject.activeInHierarchy) return;

            var btn = t.GetComponent<Button>() ?? t.GetComponentInChildren<Button>(true);
            if (btn == null || !btn.gameObject.activeInHierarchy) return;

            // Prefer the game's own localized text; fall back to our label when
            // the extractor found nothing real (cleaned GO name / "Button")
            // or when the text is in Japanese (some buttons are hardcoded in Japanese
            // regardless of the game's display language, e.g. GeneralInfomationButton).
            string gameLabel = LabelExtractor.GetLabel(t.gameObject);
            bool isGenericFallback = gameLabel == "Button"
                || gameLabel == LabelExtractor.CleanGoName(t.gameObject.name)
                || ContainsJapanese(gameLabel);
            string finalLabel = isGenericFallback ? label : gameLabel;

            var captured = btn;
            _items.Add(new HomeItem
            {
                Label = finalLabel,
                Activate = () =>
                {
                    try { captured.onClick?.Invoke(); }
                    catch (Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"Button onClick failed path={relativePath}: {ex.Message}");
                    }
                }
            });
        }

        private void AddDirectButtonItem(Button btn, string label)
        {
            if (btn?.gameObject == null || !btn.gameObject.activeInHierarchy) return;
            var captured = btn;
            _items.Add(new HomeItem
            {
                Label = label,
                Activate = () =>
                {
                    try { captured.onClick?.Invoke(); }
                    catch (Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"Direct button onClick failed label={label}: {ex.Message}");
                    }
                }
            });
        }

        // ── HomeViewController (character panel) ──────────────────────────────

        private void BuildHomeViewItems(HomeViewController view)
        {
            // Populate unlocked characters list and find current index
            _unlockedCharas = new List<int>();
            _charaListIndex = 0;
            var statePanel = view.crntStatePanel;
            if (statePanel?.unlockedCharas != null)
            {
                foreach (var cid in statePanel.unlockedCharas)
                    _unlockedCharas.Add(cid);
                for (int i = 0; i < _unlockedCharas.Count; i++)
                {
                    if (_unlockedCharas[i] == view.crntChara) { _charaListIndex = i; break; }
                }
            }

            // Character selector row (Left/Right to cycle)
            _items.Add(new HomeItem
            {
                Label = Loc.Get("home_chara_selector", GetCharacterDisplayName(view.crntChara)),
                IsCharaSelector = true
            });

            // Exp-up button (level-up prompt)
            if (statePanel?.expUpButton?.gameObject?.activeInHierarchy == true)
            {
                var btn = statePanel.expUpButton;
                _items.Add(new HomeItem
                {
                    Label = Loc.Get("home_chara_exp_up", GetCharacterDisplayName(view.crntChara)),
                    Activate = () =>
                    {
                        try { statePanel.OnClickExpUp(); }
                        catch (Exception ex)
                        {
                            DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"ExpUp failed: {ex.Message}");
                        }
                    }
                });
            }

            // Deck button
            var deckRoot = view.gameObject.transform.Find("StatePanelBase/HomeDeckPanel(Clone)/DeckIconBg");
            if (deckRoot != null && deckRoot.gameObject.activeInHierarchy)
            {
                var deckBtn = deckRoot.GetComponent<Button>() ?? deckRoot.GetComponentInChildren<Button>(true);
                if (deckBtn != null)
                {
                    _items.Add(new HomeItem
                    {
                        Label = Loc.Get("home_chara_deck", GetDeckDisplayName(view.crntChara)),
                        Activate = () =>
                        {
                            try { view.OnDeck(); }
                            catch (Exception ex)
                            {
                                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"OnDeck failed: {ex.Message}");
                            }
                        }
                    });
                }
            }

            // Change series button
            var seriesRoot = view.gameObject.transform.Find("ChangeSeries");
            if (seriesRoot != null && seriesRoot.gameObject.activeInHierarchy)
            {
                var seriesBtn = seriesRoot.GetComponent<Button>() ?? seriesRoot.GetComponentInChildren<Button>(true);
                if (seriesBtn != null)
                {
                    _items.Add(new HomeItem
                    {
                        Label = Loc.Get("home_chara_change_series"),
                        Activate = () =>
                        {
                            try { view.OnChangeSeries(); }
                            catch (Exception ex)
                            {
                                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"OnChangeSeries failed: {ex.Message}");
                            }
                        }
                    });
                }
            }
        }

        #endregion

        #region Area Navigation

        private void TryMoveArea(int direction)
        {
            var view = FindActiveSingleViewController();
            if (view == null) return;

            if (direction != 0)
            {
                var next = Mathf.Clamp(_mapAreaIndex + direction, 0, 3);
                if (next == _mapAreaIndex) return;
                _mapAreaIndex = next;
                try { view.OnMoveArea(_mapAreaIndex); }
                catch (Exception ex)
                {
                    DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"OnMoveArea failed: {ex.Message}");
                    return;
                }

                // Rebuild map objects for the new area
                var areaLabel = GetAreaLabel();
                if (_items.Count > 0) _items[0].Label = Loc.Get("home_area_selector", areaLabel);
                ScreenReader.Say(Loc.Get("home_area_changed", areaLabel));

                // Rebuild map object entries (area changed, different NPCs)
                StartScan();
            }
            else
            {
                ScreenReader.Say(Loc.Get("home_area_changed", GetAreaLabel()));
            }
        }

        private void SyncMapAreaIndex(SingleViewController view)
        {
            try
            {
                var manager = Single3DManager.Instance;
                if (manager != null)
                    _mapAreaIndex = SingleUtil.getButtonIndexByMapArea(manager.currentArea());
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"SyncMapAreaIndex failed: {ex.Message}");
            }
        }

        private MapArea GetCurrentMapArea()
        {
            try
            {
                var manager = Single3DManager.Instance;
                if (manager != null) return manager.currentArea();
            }
            catch { }
            return SingleUtil.getMapAreaBybuttonIndex(_mapAreaIndex);
        }

        private string GetAreaLabel()
        {
            try
            {
                var area = GetCurrentMapArea();
                var label = area switch
                {
                    MapArea.Street => Loc.Get("map_area_street"),
                    MapArea.Alley  => Loc.Get("map_area_alley"),
                    MapArea.Park   => Loc.Get("map_area_park"),
                    MapArea.Shop   => Loc.Get("map_area_shop"),
                    _              => Loc.Get("home_area_unknown", _mapAreaIndex)
                };
                DebugLogger.Log(LogCategory.Handler, "HomeHandler",
                    $"GetAreaLabel: area={area}, index={_mapAreaIndex}, label={label}");
                return label;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"GetAreaLabel failed: {ex.Message}");
                return Loc.Get("map_area_street");
            }
        }

        #endregion

        #region Character Navigation

        private void TryMoveChara(int direction)
        {
            if (_unlockedCharas == null || _unlockedCharas.Count == 0 || direction == 0) return;
            var view = FindActiveHomeViewController();
            if (view == null) return;

            var next = Mathf.Clamp(_charaListIndex + direction, 0, _unlockedCharas.Count - 1);
            if (next == _charaListIndex) return;

            _charaListIndex = next;
            var cid = _unlockedCharas[_charaListIndex];
            try
            {
                view.OnChangeChara(cid, true);
                var name = GetCharacterDisplayName(cid);
                if (_items.Count > 0) _items[0].Label = Loc.Get("home_chara_selector", name);
                ScreenReader.Say(name);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"OnChangeChara failed cid={cid}: {ex.Message}");
            }
        }

        #endregion

        #region Map Object Helpers

        private string GetMapObjectLabel(MapObjectBase mapObj)
        {
            if (mapObj is BillboardObject billboard)
            {
                var name = TryGetBillboardName(billboard);
                if (!string.IsNullOrWhiteSpace(name)) return name;

                var type = billboard.mapObjectData?.type ?? MapObjectType.None;
                return type switch
                {
                    MapObjectType.NPCChallenge => Loc.Get("map_npc_challenge"),
                    MapObjectType.NPCMob       => Loc.Get("map_npc_standard"),
                    MapObjectType.NPCOrigin    => Loc.Get("map_npc_legendary"),
                    MapObjectType.NPCTrainer   => Loc.Get("map_npc_trainer"),
                    MapObjectType.BonusDuelist => Loc.Get("map_npc_bonus"),
                    MapObjectType.FoundGift    => Loc.Get("map_gift"),
                    _ => Loc.Get("map_npc_standard")
                };
            }

            return mapObj.GetType().Name switch
            {
                "GateTouchObject"     => Loc.Get("home_gate"),
                "GateTouchObjectDSOD" => Loc.Get("home_gate"),
                "ShopObject"          => Loc.Get("map_card_trader"),
                "SchoolObject"        => Loc.Get("map_school"),
                "DuelCenterObject"    => Loc.Get("home_duel_center"),
                _ => mapObj.GetType().Name
            };
        }

        private string TryGetBillboardName(BillboardObject billboard)
        {
            if (billboard == null) return null;

            // Primary: resolve by npcID → CharaUtil
            if (billboard.npcID > 0)
            {
                var name = TryResolveCharacterName(billboard.npcID);
                if (!string.IsNullOrWhiteSpace(name)) return NormalizeName(name);
            }

            // Secondary: look up via getSingleMapChara using MapObjectRoot index
            try
            {
                var go = billboard.gameObject;
                if (go?.name != null && go.name.StartsWith("MapObjectRoot", StringComparison.Ordinal))
                {
                    var suffix = go.name.Substring("MapObjectRoot".Length);
                    if (int.TryParse(suffix, out var index))
                    {
                        var data = Single3DManager.Instance?.getSingleMapChara(index);
                        if (data != null)
                        {
                            var title = SingleUtil.getNpcRewardTitle(data);
                            if (!string.IsNullOrWhiteSpace(title)) return NormalizeName(title);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"getSingleMapChara fallback failed: {ex.Message}");
            }

            return null;
        }

        private string TryResolveCharacterName(int cid)
        {
            if (cid <= 0) return null;
            foreach (var resolver in new Func<int, string>[] {
                CharaUtil.GetNameWithSeries, CharaUtil.GetNameAndSeries, CharaUtil.GetName })
            {
                try
                {
                    var v = resolver(cid);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                catch { }
            }
            return null;
        }

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var s = value.Trim();
            s = Regex.Replace(s, @"\s*LV\s*:\s*\d+.*$", "", RegexOptions.IgnoreCase).Trim();
            s = Regex.Replace(s, @"\s*\(\d+/\d+\).*$", "").Trim();
            if (s.StartsWith("Recompensas de ", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("Recompensas de ".Length).Trim();
            if (s.StartsWith("Rewards for ", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("Rewards for ".Length).Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private void ActivateMapObject(MapObjectBase mapObj)
        {
            try
            {
                var manager = Resources.FindObjectsOfTypeAll<Il2CppYgomSystem.UI.ViewControllerManager>();
                if (manager != null && manager.Length > 0)
                {
                    foreach (var mgr in manager)
                    {
                        if (mgr?.gameObject?.activeInHierarchy == true)
                        {
                            mapObj.TapObject(mgr);
                            return;
                        }
                    }
                }
                // Fallback: pointer event
                var go = mapObj.gameObject;
                var pe = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);
                UnityEngine.EventSystems.ExecuteEvents.Execute(
                    go, pe,
                    UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"ActivateMapObject failed: {ex.Message}");
            }
        }

        private bool HasActiveMapObjectOfType<T>() where T : MapObjectBase
        {
            try
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll<T>())
                    if (obj?.gameObject?.activeInHierarchy == true) return true;
            }
            catch { }
            return false;
        }

        #endregion

        #region Character / Deck Helpers

        private string GetCurrentCharacterName(SingleViewController view)
        {
            try
            {
                // Try the home panel's crntChara if visible
                var homeVc = FindActiveHomeViewController();
                if (homeVc != null) return GetCharacterDisplayName(homeVc.crntChara);
            }
            catch { }
            return Loc.Get("home_character_unknown");
        }

        private string GetCharacterDisplayName(int cid)
        {
            if (cid <= 0) return Loc.Get("home_character_unknown");
            var name = TryResolveCharacterName(cid);
            return string.IsNullOrWhiteSpace(name) ? $"CID {cid}" : name;
        }

        private string GetDeckDisplayName(int cid)
        {
            if (cid <= 0) return Loc.Get("home_character_unknown");
            try
            {
                var name = CharaUtil.GetDeckName(cid);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"GetDeckName failed cid={cid}: {ex.Message}");
            }
            return Loc.Get("home_character_unknown");
        }

        private string GetMissionLabel()
        {
            try
            {
                foreach (var mb in Resources.FindObjectsOfTypeAll<MissionButton>())
                {
                    if (mb?.gameObject?.activeInHierarchy != true) continue;
                    var prefix = mb.StageText?.GetComponent<Text>()?.text?.Trim();
                    var num = mb.StageNumText?.text?.Trim();
                    if (!string.IsNullOrWhiteSpace(prefix) && !string.IsNullOrWhiteSpace(num))
                        return Loc.Get("home_missions_stage", $"{prefix} {num}");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"GetMissionLabel failed: {ex.Message}");
            }
            return Loc.Get("home_missions");
        }

        #endregion

        #region Announcements

        private void AnnounceList()
        {
            if (_items.Count == 0) return;
            ScreenReader.Say(Loc.Get("home_items", _items.Count));
            AnnounceCurrentItem();
        }

        private void AnnounceCurrentItem()
        {
            if (_items.Count == 0) return;
            var item = _items[_focusIndex];

            if (item.IsAreaSelector || item.IsCharaSelector)
                ScreenReader.Say(Loc.Get("home_item", _focusIndex + 1, _items.Count, item.Label));
            else
                ScreenReader.Say(Loc.Get("home_item", _focusIndex + 1, _items.Count, item.Label));
        }

        #endregion

        #region VC Finders

        private static SingleViewController FindActiveSingleViewController()
        {
            try
            {
                foreach (var vc in Resources.FindObjectsOfTypeAll<SingleViewController>())
                    if (vc?.gameObject?.activeInHierarchy == true) return vc;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"FindActiveSingleViewController failed: {ex.Message}");
            }
            return null;
        }

        private static HomeViewController FindActiveHomeViewController()
        {
            try
            {
                foreach (var vc in Resources.FindObjectsOfTypeAll<HomeViewController>())
                    if (vc?.gameObject?.activeInHierarchy == true) return vc;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"FindActiveHomeViewController failed: {ex.Message}");
            }
            return null;
        }

        #endregion

        #region Helpers

        private void AnnounceGemBalance()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return;
                Il2CppYgomSystem.UI.ViewControllerManager baseMgr;
                if (!namedMgr.TryGetValue("base", out baseMgr) || baseMgr == null) return;
                var topVc = baseMgr.GetStackTopViewController();
                if (topVc == null) return;
                var header = topVc.TryCast<Il2CppYgomGame.Menu.HeaderViewController>();
                if (header == null) return;

                var parts = new System.Collections.Generic.List<string>();
                var gems = header.gemNumber?.text?.Trim();
                if (!string.IsNullOrEmpty(gems)) parts.Add(gems + " " + Loc.Get("shop_gems"));
                var cr = header.crNumber?.text?.Trim();
                if (!string.IsNullOrEmpty(cr)) parts.Add(cr + " " + Loc.Get("shop_crystals"));

                if (parts.Count > 0)
                    ScreenReader.Say(string.Join(", ", parts));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "HomeHandler", $"AnnounceGemBalance error: {ex.Message}");
            }
        }

        /// <summary>
        /// Scans active child Text components of a GO for a numeric badge value.
        /// Returns the number string (e.g. "7") or null if none found.
        /// </summary>
        private static string ReadChildBadge(GameObject go)
        {
            if (go == null) return null;
            try
            {
                var texts = go.GetComponentsInChildren<Text>(false);
                if (texts == null) return null;
                foreach (var t in texts)
                {
                    if (t == null || !t.gameObject.activeInHierarchy) continue;
                    string val = t.text?.Trim();
                    if (!string.IsNullOrEmpty(val) && val.Length <= 3
                        && val.All(char.IsDigit) && int.Parse(val) > 0)
                        return val;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Finds items whose labels collide and appends " 1", " 2" etc. so the user
        /// can distinguish them. Only applies to items that share an identical label
        /// — unique labels are left untouched.
        /// </summary>
        private void NumberDuplicateLabels()
        {
            var counts = new Dictionary<string, int>();
            foreach (var item in _items)
            {
                if (!counts.ContainsKey(item.Label)) counts[item.Label] = 0;
                counts[item.Label]++;
            }

            var seen = new Dictionary<string, int>();
            foreach (var item in _items)
            {
                if (counts[item.Label] <= 1) continue;
                if (!seen.ContainsKey(item.Label)) seen[item.Label] = 0;
                seen[item.Label]++;
                item.Label = $"{item.Label} {seen[item.Label]}";
            }
        }

        /// <summary>
        /// Returns true if the string contains hiragana, katakana, or CJK ideographs.
        /// Used to reject button labels that the game stores in Japanese regardless of display language.
        /// </summary>
        private static bool ContainsJapanese(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (char c in s)
            {
                if ((c >= '぀' && c <= 'ヿ') || // Hiragana + Katakana
                    (c >= '一' && c <= '鿿') || // CJK Unified Ideographs
                    (c >= '㐀' && c <= '䶿'))    // CJK Extension A
                    return true;
            }
            return false;
        }

        #endregion
    }
}
