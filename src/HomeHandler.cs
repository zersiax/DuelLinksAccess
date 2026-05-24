using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;
using Il2CppYgomGame.Menu;
using Il2CppYgomGame.Single;
using Il2CppYgomGame.Utility;
using Il2CppYgomSystem.UI;

namespace DuelLinksAccess
{
    /// <summary>
    /// Curated keyboard navigation for the Duel World / Home screen.
    /// Replaces the generic ScreenButtonHandler 40+ item Selectable scan with
    /// an explicit list of named destinations resolved by transform path and
    /// typed component queries, then filtered to active items only.
    ///
    /// Two view modes:
    ///   1. World map (SingleViewController) — area selector, map NPCs, footer
    ///      buttons, header buttons, missions.
    ///   2. Character panel (HomeViewController) — character selector,
    ///      exp-up prompt, deck, change-series.
    ///
    /// Controls while active:
    ///   Up / Down   — navigate items
    ///   Enter       — activate
    ///   Left / Right — change area (selector row) or character (chara panel)
    ///   Space       — rescan
    ///   Tab         — re-read current item
    ///   G           — gem / crystal balance
    ///   B           — toggle to "browse all" mode (hands the screen back to
    ///                 ScreenButtonHandler so the user can reach raw items not
    ///                 in the curated list). Press B again to return.
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

            /// <summary>
            /// Underlying scene GameObject for this item (the Button GO, the
            /// MapObject GO, the panel GO). Used by ActivateCurrent to detect
            /// when a TutorialArrow's ipclick handler targets this item, so
            /// the arrow's tutorial-observed click channel fires alongside
            /// Button.onClick. Null for synthetic items (area / chara
            /// selectors) which never participate in tutorials.
            /// </summary>
            public GameObject Go;
        }

        #endregion

        #region Fields

        private readonly List<HomeItem> _items = new();
        private int _focusIndex;

        private string _lastVcName = "";

        // Map / character selector tracking
        private int _mapAreaIndex;
        private int _charaListIndex;
        private List<int> _unlockedCharas;

        // Delayed initial scan — SingleViewController content loads async
        private bool _scanDone;
        private float _scanDelay;

        // Cooldown between activations to avoid double-click on Enter spam
        private float _operationCooldown;
        private const float OperationCooldownTime = 0.35f;

        // Browse-all fallback mode: when true, HomeHandler steps aside and
        // ScreenButtonHandler takes over until the user toggles back or leaves
        // the Home screen.
        private bool _sbhFallback;
        private bool _wasActive;

        #endregion

        #region Properties

        /// <summary>
        /// True while the curated home screen is the active input handler.
        /// False during browse-all fallback or when no items are built yet.
        /// Main.UpdateHandlers uses this to gate ScreenButtonHandler.
        /// </summary>
        public bool IsActive { get; private set; }

        #endregion

        #region Update

        /// <summary>Called every frame by Main.UpdateHandlers().</summary>
        public void Update()
        {
            if (_operationCooldown > 0f)
                _operationCooldown -= Time.deltaTime;

            if (GameStateTracker.CurrentScreen != GameStateTracker.GameScreen.Home)
            {
                if (_wasActive) Deactivate();
                return;
            }

            // Tutorial-arrow policy. In the character panel
            // (HomeViewController) the curated list now exposes every
            // documented arrow target (Cambiar estilo, alternate styles,
            // deck, change-series, mission, character selector), so the
            // user can just navigate to the target and press Enter — no
            // F11 or "complete tutorial" hotkey needed. On the World map
            // (SingleViewController) we still suspend, because tutorials
            // there can point at MapObjects or other GOs the curated list
            // does not cover, and ScreenButtonHandler's raw scan is the
            // safety net.
            if (IsTutorialArrowActive())
            {
                bool inCharaPanel = FindActiveViewController<HomeViewController>() != null;
                if (!inCharaPanel)
                {
                    if (_wasActive || IsActive || _items.Count > 0)
                    {
                        IsActive = false;
                        _wasActive = false;
                        _items.Clear();
                        _scanDone = false;
                        _lastVcName = "";
                        AccessStateManager.Exit(AccessStateManager.State.Home);
                        DebugLogger.Log(LogCategory.Handler, "Home",
                            "Tutorial arrow on World map — suspending curated mode");
                    }
                    return;
                }
                // Chara panel: fall through and keep curating.
            }

            // VC changed — rebuild list (e.g. transitioning between SingleVc
            // and HomeVc, or returning from a duel)
            string vcName = GameStateTracker.LastViewControllerName;
            if (vcName != _lastVcName)
            {
                _lastVcName = vcName;
                StartScan();
            }

            // Delayed scan — keep retrying until the view supplies content.
            if (!_scanDone)
            {
                _scanDelay -= Time.deltaTime;
                if (_scanDelay <= 0f)
                {
                    if (TryBuildItems())
                    {
                        _scanDone = true;
                        if (!_sbhFallback) Activate();
                    }
                    else
                    {
                        _scanDelay = 0.5f;
                    }
                }
                return;
            }

            // B toggles between curated and browse-all mode at any time.
            // Consume early so SBH never sees it while we're curating.
            if (InputManager.TryConsumeKeyDown(KeyCode.B))
            {
                ToggleSbhFallback();
                return;
            }

            if (_sbhFallback) return;

            if (!IsActive) return;

            ProcessInput();
        }

        #endregion

        #region Activation

        private void StartScan()
        {
            _items.Clear();
            _focusIndex = 0;
            _scanDone = false;
            _scanDelay = 0.6f;
        }

        private void Activate()
        {
            IsActive = true;
            _wasActive = true;
            AccessStateManager.TryEnter(AccessStateManager.State.Home);
            AnnounceList();
            DebugLogger.Log(LogCategory.Handler, "Home",
                $"Activated curated mode with {_items.Count} items");
        }

        private void Deactivate()
        {
            IsActive = false;
            _wasActive = false;
            _sbhFallback = false;
            _items.Clear();
            _lastVcName = "";
            _scanDone = false;
            AccessStateManager.Exit(AccessStateManager.State.Home);
            DebugLogger.Log(LogCategory.Handler, "Home", "Deactivated");
        }

        private void ToggleSbhFallback()
        {
            _sbhFallback = !_sbhFallback;
            if (_sbhFallback)
            {
                IsActive = false;
                AccessStateManager.Exit(AccessStateManager.State.Home);
                ScreenReader.Say(Loc.Get("home_browse_all"));
                DebugLogger.Log(LogCategory.Handler, "Home", "Switched to browse-all (SBH fallback)");
            }
            else
            {
                ScreenReader.Say(Loc.Get("home_curated"));
                DebugLogger.Log(LogCategory.Handler, "Home", "Switched back to curated mode");
                StartScan();
            }
        }

        #endregion

        #region Input

        private void ProcessInput()
        {
            if (InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                ScreenReader.Say(Loc.Get("screen_rescan"));
                StartScan();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                AnnounceCurrentItem();
                return;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.G))
            {
                AnnounceGemBalance();
                return;
            }

            if (_items.Count == 0) return;

            var item = _items[_focusIndex];

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

            if (InputManager.TryConsumeKeyDown(KeyCode.Return)
                || InputManager.TryConsumeKeyDown(KeyCode.KeypadEnter))
            {
                if (_operationCooldown > 0f) return;
                ActivateCurrent(item);
                return;
            }
        }

        private void ActivateCurrent(HomeItem item)
        {
            if (item.IsAreaSelector)
            {
                ScreenReader.Say(Loc.Get("home_area_changed", GetAreaLabel()));
                return;
            }

            if (item.Activate == null) return;

            _operationCooldown = OperationCooldownTime;
            ScreenReader.Say(item.Label);

            // Tutorial-arrow routing. If a TutorialArrow has its ipclick
            // handler registered on this item's GO (or any ancestor /
            // descendant), the tutorial system observes that specific
            // OnPointerClick channel — Button.onClick.Invoke alone does
            // NOT satisfy the tutorial condition (game-api.md:1010-1014).
            // Dispatching ipclick first + the normal click action after is
            // explicitly safe (game-api.md:815, "always firing both is
            // universally safe"). Empirically validated by the Yami-Yugi
            // style tutorial: onClick fires the style swap but the arrow
            // never advances unless ipclick also fires.
            TryFireArrowIpclickFor(item);

            try { item.Activate(); }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"Activate failed for '{item.Label}': {ex.Message}");
            }
        }

        /// <summary>
        /// If an active TutorialArrow has an ipclick handler whose GO is
        /// the item's GO, an ancestor, or a descendant (generous match —
        /// some buttons attach the IPointerClickHandler to a child like a
        /// Frame / Icon GO), dispatch the arrow's ipclick. No-op when no
        /// arrow is up or no handler matches.
        /// </summary>
        private static void TryFireArrowIpclickFor(HomeItem item)
        {
            if (item?.Go == null) return;
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return;

                if (!GameStateTracker.TryFindArrowAcrossManagers(
                        namedManager, out var arrowVc, out _, out _))
                {
                    return;
                }

                var ipclick = arrowVc?.ipclick;
                if (ipclick == null || ipclick.Length == 0) return;

                bool matches = false;
                for (int i = 0; i < ipclick.Length; i++)
                {
                    var mb = ipclick[i]?.TryCast<MonoBehaviour>();
                    var handlerGo = mb?.gameObject;
                    if (handlerGo == null) continue;
                    if (IsSameOrRelated(handlerGo, item.Go))
                    {
                        matches = true;
                        break;
                    }
                }

                if (!matches) return;

                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"TutorialArrow targets '{item.Label}' " +
                    $"({item.Go.name}) — dispatching ipclick");
                Main.InvokeArrowIpclickDirect(arrowVc);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"TryFireArrowIpclickFor error: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns true if <paramref name="a"/> is the same GO as
        /// <paramref name="b"/>, an ancestor, or a descendant. Used to
        /// match a TutorialArrow's ipclick handler GO against a curated
        /// item's GO when the relationship may be slightly off (handler
        /// on a child Frame, or curated item on the wrapper).
        /// </summary>
        private static bool IsSameOrRelated(GameObject a, GameObject b)
        {
            if (a == null || b == null) return false;
            if (a == b) return true;

            // a is ancestor of b
            var t = b.transform.parent;
            while (t != null)
            {
                if (t.gameObject == a) return true;
                t = t.parent;
            }

            // b is ancestor of a
            t = a.transform.parent;
            while (t != null)
            {
                if (t.gameObject == b) return true;
                t = t.parent;
            }

            return false;
        }

        #endregion

        #region Item Building

        private bool TryBuildItems()
        {
            _items.Clear();
            _focusIndex = 0;

            // Character detail panel takes priority over world map when both exist
            var homeVc = FindActiveViewController<HomeViewController>();
            if (homeVc != null)
            {
                BuildHomeViewItems(homeVc);
                return _items.Count > 0;
            }

            var singleVc = FindActiveViewController<SingleViewController>();
            if (singleVc?.gameObject == null) return false;

            BuildSingleViewItems(singleVc);
            return _items.Count > 0;
        }

        // ── World map (SingleViewController) ─────────────────────────────────

        private void BuildSingleViewItems(SingleViewController view)
        {
            SyncMapAreaIndex();

            _items.Add(new HomeItem
            {
                Label = Loc.Get("home_area_selector", GetAreaLabel()),
                IsAreaSelector = true
            });

            AddMapObjectItems();

            // Footer destination buttons. Try the known path first, then a
            // typed-name fallback within the SingleVC subtree — survives layer
            // renames between game patches.
            bool gateOnMap = HasActiveMapObjectOfType<GateTouchObject>()
                || HasActiveMapObjectOfType<GateTouchObjectDSOD>();
            if (!gateOnMap)
                AddButton(view.gameObject,
                    "Layer4/SingleFooter/AreaButtons/Gate/YgomButton",
                    "Gate", Loc.Get("home_gate"));

            AddButton(view.gameObject,
                "Layer4/SingleFooter/AreaButtons/Colosseum/YgomButton",
                "Colosseum", Loc.Get("home_colosseum"));
            AddButton(view.gameObject,
                "Layer4/SingleFooter/AreaButtons/Shop/YgomButton",
                "Shop", Loc.Get("home_shop"));
            AddButton(view.gameObject,
                "Layer4/SingleFooter/AreaButtons/Labo/YgomButton",
                "Labo", Loc.Get("home_labo"));

            // Quick-duel shortcuts on the home sidebar (DuelShortcut0..3).
            // Two of them are typically duplicates of the other two (one
            // visible per side); NumberDuplicateLabels disambiguates if
            // labels collide. Uses the rendered text directly.
            AddButtonByName(view.gameObject, "DuelShortcut0", Loc.Get("home_duel_shortcut"));
            AddButtonByName(view.gameObject, "DuelShortcut1", Loc.Get("home_duel_shortcut"));
            AddButtonByName(view.gameObject, "DuelShortcut2", Loc.Get("home_duel_shortcut"));
            AddButtonByName(view.gameObject, "DuelShortcut3", Loc.Get("home_duel_shortcut"));

            AddDirectButton(view.ArrowLButton, Loc.Get("home_area_left"));
            AddDirectButton(view.ArrowRButton, Loc.Get("home_area_right"));

            // Header buttons. Events button shows a badge count when active
            // events / notices are present.
            string eventsLabel = Loc.Get("home_events");
            string eventsBadge = ReadChildBadge(view.generalInfomationButton);
            if (!string.IsNullOrEmpty(eventsBadge))
                eventsLabel = $"{eventsLabel} ({eventsBadge})";
            AddButton(view.gameObject,
                "Layer4/SingleHeaderBG/GeneralInfomationButton/YgomButton",
                "GeneralInfomationButton", eventsLabel);
            AddButton(view.gameObject,
                "Layer4/SingleHeaderBG/HeaderButtons/PresentButton",
                "PresentButton", Loc.Get("home_gifts"));
            AddButton(view.gameObject,
                "Layer4/SingleHeaderBG/HeaderButtons/InfoButton",
                "InfoButton", Loc.Get("home_info"));
            AddButton(view.gameObject,
                "Layer4/SingleHeaderBG/HeaderButtons/UserInfoButton",
                "UserInfoButton", Loc.Get("home_user_info"));
            AddButton(view.gameObject,
                "Layer4/SingleHeaderBG/HeaderButtons/OptionButton",
                "OptionButton", Loc.Get("home_option"));
            AddButton(view.gameObject,
                "Layer4/SingleHeaderBG/NeuronCode(Clone)",
                "NeuronCode", Loc.Get("home_neuron_code"));

            // Menu / shortcut / settings cluster — opens secondary panels.
            // ShortCut surfaces Card Trader EX, Replay, Card List, Card
            // Chronicle, Skill List, Ranking, current Event.
            AddButtonByName(view.gameObject, "ShortCutButton", Loc.Get("home_shortcut"));
            AddButtonByName(view.gameObject, "MenuSettingsButton", Loc.Get("home_menu_settings"));

            // Character button opens the HomeViewController character
            // detail panel (level, exp, chara list, change-series).
            AddButtonByName(view.gameObject, "CharacterButton",
                Loc.Get("home_character_button"),
                () => { try { view.OnCharacterButton(); } catch { } });

            // Footer right side — current character + deck shortcuts
            AddButton(view.gameObject,
                "Layer4/SingleFooter/MenuRoot/MenuRightBase/Mask/SelectChara",
                "SelectChara", Loc.Get("home_character", GetCurrentCharacterName()));
            AddButton(view.gameObject,
                "Layer4/SingleFooter/MenuRoot/MenuRightBase/SelectDeck",
                "SelectDeck", Loc.Get("home_deck_select"));
            AddButton(view.gameObject,
                "Layer4/SingleFooter/MenuRoot/MenuRightBase/EditDeck",
                "EditDeck", Loc.Get("home_deck_edit"));

            // Series / world switcher (DM / GX / 5Ds / Zexal / Arc-V /
            // VRAINS). Renders the active world's name as the label when
            // possible, otherwise falls back to "Change duel world". Click
            // opens the series-change panel.
            AddButtonByName(view.gameObject, "SeriesLogo",
                Loc.Get("home_series"),
                () => { try { view.OnOpenSeriesChangePanel(); } catch { } });

            // Footer left — missions, with live stage label when available
            AddButton(view.gameObject,
                "Layer4/SingleFooter/MenuRoot/MenuLeftBase/Mission",
                "Mission", GetMissionLabel());

            NumberDuplicateLabels();
        }

        private void AddMapObjectItems()
        {
            var area = GetCurrentMapArea();
            // Billboards (characters) — filter to current area so we don't
            // surface NPCs from other neighborhoods.
            AddMapObjects<BillboardObject>(area);
            // Permanent destinations (no area filter — only one of each)
            AddMapObjects<GateTouchObject>(null);
            AddMapObjects<GateTouchObjectDSOD>(null);
            AddMapObjects<ShopObject>(null);
            AddMapObjects<SchoolObject>(null);
            AddMapObjects<DuelCenterObject>(null);
            // Event-time / story interactables (only present when active in
            // the current world / area; activeInHierarchy filter handles the
            // off-state automatically). All extend MapObjectBase so they
            // share the same activation path (TapObject + ViewControllerManager).
            AddMapObjects<CardPortalObject>(null);
            AddMapObjects<AlleyGomibakoObject>(area);
            AddMapObjects<GomiBakoObjectDSOD>(area);

            // Event banners and chest panels — MonoBehaviour, not MapObjectBase.
            // Separate walker invokes the panel's own click method.
            AddPanels<EventPanel>(p => p.OnPanel(), "map_event_panel");
            AddPanels<DropTheTreasureChest>(p => p.OnIcon(), "map_treasure_chest");
        }

        /// <summary>
        /// Adds active MonoBehaviour-rooted panels with a custom click action.
        /// Used for event banners and similar overlays that don't extend
        /// MapObjectBase (so the typed map-object walker doesn't catch them).
        /// </summary>
        private void AddPanels<T>(Action<T> click, string fallbackLabelKey)
            where T : MonoBehaviour
        {
            T[] found;
            try { found = Resources.FindObjectsOfTypeAll<T>(); }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"FindObjectsOfTypeAll<{typeof(T).Name}> failed: {ex.Message}");
                return;
            }

            if (found == null) return;
            foreach (var panel in found)
            {
                if (panel?.gameObject == null) continue;
                if (!panel.gameObject.activeInHierarchy) continue;

                string gameLabel = LabelExtractor.GetLabel(panel.gameObject);
                bool junk = string.IsNullOrEmpty(gameLabel)
                    || gameLabel == "Button"
                    || gameLabel == LabelExtractor.CleanGoName(panel.gameObject.name)
                    || ContainsJapanese(gameLabel);
                string label = junk ? Loc.Get(fallbackLabelKey) : gameLabel;

                var captured = panel;
                _items.Add(new HomeItem
                {
                    Label = label,
                    Go = panel.gameObject,
                    Activate = () =>
                    {
                        try { click(captured); }
                        catch (Exception ex)
                        {
                            DebugLogger.Log(LogCategory.Handler, "Home",
                                $"Panel click failed type={typeof(T).Name}: {ex.Message}");
                        }
                    }
                });
            }
        }

        private void AddMapObjects<T>(MapArea? requiredArea) where T : MapObjectBase
        {
            T[] found;
            try { found = Resources.FindObjectsOfTypeAll<T>(); }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"FindObjectsOfTypeAll<{typeof(T).Name}> failed: {ex.Message}");
                return;
            }

            if (found == null) return;
            foreach (var mapObj in found)
            {
                if (mapObj?.gameObject == null) continue;
                if (!mapObj.gameObject.activeInHierarchy) continue;

                if (requiredArea.HasValue && mapObj.mapObjectData != null)
                {
                    var objArea = (MapArea)mapObj.mapObjectData.area;
                    if (objArea != requiredArea.Value) continue;
                }

                var label = GetMapObjectLabel(mapObj);

                // Skip billboards we couldn't resolve at all — these are
                // typically MapObjectRoot slots with no npcID and no
                // getSingleMapChara data (decorative or off-screen NPCs).
                // GetMapObjectLabel returns null in that case for billboards.
                if (label == null) continue;

                var captured = mapObj;
                _items.Add(new HomeItem
                {
                    Label = label,
                    Go = mapObj.gameObject,
                    Activate = () => ActivateMapObject(captured)
                });
            }
        }

        // ── Character panel (HomeViewController) ─────────────────────────────

        private void BuildHomeViewItems(HomeViewController view)
        {
            _unlockedCharas = new List<int>();
            _charaListIndex = 0;

            var statePanel = view.crntStatePanel;
            if (statePanel?.unlockedCharas != null)
            {
                foreach (var cid in statePanel.unlockedCharas)
                    _unlockedCharas.Add(cid);

                for (int i = 0; i < _unlockedCharas.Count; i++)
                {
                    if (_unlockedCharas[i] == view.crntChara)
                    {
                        _charaListIndex = i;
                        break;
                    }
                }
            }

            _items.Add(new HomeItem
            {
                Label = Loc.Get("home_chara_selector", GetCharacterDisplayName(view.crntChara)),
                IsCharaSelector = true
            });

            // Full chara list — opens the dialog with every unlocked character.
            // Distinct from the inline Left/Right cycler above: lets the user
            // jump directly to a chara instead of stepping through one by one.
            var charaListBtn = statePanel?.charaListBtn;
            if (charaListBtn?.gameObject?.activeInHierarchy == true)
            {
                _items.Add(new HomeItem
                {
                    Label = Loc.Get("home_chara_list"),
                    Go = charaListBtn.gameObject,
                    Activate = () =>
                    {
                        try { statePanel.OnCharaListClick(); }
                        catch (Exception ex)
                        {
                            DebugLogger.Log(LogCategory.Handler, "Home",
                                $"OnCharaListClick failed: {ex.Message}");
                        }
                    }
                });
            }

            // Level-up prompt — only present when the character has banked exp
            var expUp = statePanel?.expUpButton;
            if (expUp?.gameObject?.activeInHierarchy == true)
            {
                _items.Add(new HomeItem
                {
                    Label = Loc.Get("home_chara_exp_up", GetCharacterDisplayName(view.crntChara)),
                    Go = expUp.gameObject,
                    Activate = () =>
                    {
                        try { statePanel.OnClickExpUp(); }
                        catch (Exception ex)
                        {
                            DebugLogger.Log(LogCategory.Handler, "Home",
                                $"OnClickExpUp failed: {ex.Message}");
                        }
                    }
                });
            }

            // Deck panel
            var deckRoot = view.gameObject.transform.Find(
                "StatePanelBase/HomeDeckPanel(Clone)/DeckIconBg");
            if (deckRoot?.gameObject?.activeInHierarchy == true)
            {
                _items.Add(new HomeItem
                {
                    Label = Loc.Get("home_chara_deck", GetDeckDisplayName(view.crntChara)),
                    Go = deckRoot.gameObject,
                    Activate = () =>
                    {
                        try { view.OnDeck(); }
                        catch (Exception ex)
                        {
                            DebugLogger.Log(LogCategory.Handler, "Home",
                                $"OnDeck failed: {ex.Message}");
                        }
                    }
                });
            }

            // Change-series button
            var seriesRoot = view.gameObject.transform.Find("ChangeSeries");
            if (seriesRoot?.gameObject?.activeInHierarchy == true)
            {
                _items.Add(new HomeItem
                {
                    Label = Loc.Get("home_chara_change_series"),
                    Go = seriesRoot.gameObject,
                    Activate = () =>
                    {
                        try { view.OnChangeSeries(); }
                        catch (Exception ex)
                        {
                            DebugLogger.Log(LogCategory.Handler, "Home",
                                $"OnChangeSeries failed: {ex.Message}");
                        }
                    }
                });
            }

            // Per-character mission badge. Distinct from the World-map
            // "Missions" footer entry — this one is wired to the active
            // character's daily / weekly missions.
            AddButtonByName(view.gameObject, "mission",
                Loc.Get("home_chara_mission"));

            // Alternate-style switcher. "Cambiar estilo" (GO name "switch")
            // opens the style picker; the picker's children are bare digit
            // names ("1", "2", ...). ScreenButtonHandler filters digit-only
            // labels as badge noise, which would otherwise leave the
            // Yami-Yugi-style tutorial step (and any future alt-style step)
            // unreachable. Walk 1..9 generously — GetStyles returns any
            // subset and AddButtonByName's activeInHierarchy guard skips
            // missing entries.
            AddButtonByName(view.gameObject, "switch",
                Loc.Get("home_chara_change_style"));
            for (int i = 1; i <= 9; i++)
            {
                string num = i.ToString();
                AddButtonByName(view.gameObject, num,
                    Loc.Get("home_chara_style", num));
            }
        }

        // ── Button helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Adds a button by transform path with a typed-name fallback. If the
        /// exact path is missing (game patch renamed a layer), walks the
        /// SingleVC subtree looking for a Button on a GO matching the
        /// fallback name. This keeps the curated list intact across small
        /// game updates that move things around.
        /// </summary>
        private void AddButton(GameObject root, string relativePath,
            string fallbackGoName, string fallbackLabel)
        {
            if (root == null) return;

            Button btn = ResolveButton(root, relativePath, fallbackGoName);
            if (btn == null || !btn.gameObject.activeInHierarchy) return;

            // Prefer the game's own localized label unless it returns junk
            // (cleaned GO name, generic "Button", or Japanese hardcoded text
            // — some headers are stored in JP regardless of game language).
            string gameLabel = LabelExtractor.GetLabel(btn.gameObject);
            bool junk = string.IsNullOrEmpty(gameLabel)
                || gameLabel == "Button"
                || gameLabel == LabelExtractor.CleanGoName(btn.gameObject.name)
                || ContainsJapanese(gameLabel);
            string label = junk ? fallbackLabel : gameLabel;

            var captured = btn;
            _items.Add(new HomeItem
            {
                Label = label,
                Go = btn.gameObject,
                Activate = () =>
                {
                    try { captured.onClick?.Invoke(); }
                    catch (Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Handler, "Home",
                            $"onClick failed path={relativePath}: {ex.Message}");
                    }
                }
            });
        }

        /// <summary>
        /// Adds a button found purely by GO name within the active subtree.
        /// Useful when no stable transform path is documented (e.g. shortcut
        /// / menu / duel-shortcut buttons that hang off different parents
        /// depending on layout state). Optional customClick lets callers
        /// route activation through a typed VC method (preferred when the
        /// button's onClick listener doesn't fire the right side-effect in
        /// IL2CPP).
        /// </summary>
        private void AddButtonByName(GameObject root, string goName,
            string fallbackLabel, Action customClick = null)
        {
            if (root == null) return;
            var btn = ResolveButtonByName(root, goName);
            if (btn == null || !btn.gameObject.activeInHierarchy) return;

            string gameLabel = LabelExtractor.GetLabel(btn.gameObject);
            bool junk = string.IsNullOrEmpty(gameLabel)
                || gameLabel == "Button"
                || gameLabel == LabelExtractor.CleanGoName(btn.gameObject.name)
                || ContainsJapanese(gameLabel);
            string label = junk ? fallbackLabel : gameLabel;

            var captured = btn;
            var capturedClick = customClick;
            _items.Add(new HomeItem
            {
                Label = label,
                Go = btn.gameObject,
                Activate = () =>
                {
                    try
                    {
                        if (capturedClick != null) capturedClick();
                        else captured.onClick?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Handler, "Home",
                            $"AddButtonByName click failed goName={goName}: {ex.Message}");
                    }
                }
            });
        }

        private static Button ResolveButtonByName(GameObject root, string goName)
        {
            if (root == null || string.IsNullOrEmpty(goName)) return null;
            try
            {
                var buttons = root.GetComponentsInChildren<Button>(true);
                if (buttons == null) return null;
                foreach (var b in buttons)
                {
                    if (b?.gameObject == null) continue;
                    if (!b.gameObject.activeInHierarchy) continue;
                    if (b.gameObject.name == goName) return b;
                    var parent = b.gameObject.transform.parent;
                    if (parent != null && parent.name == goName) return b;
                }
            }
            catch { }
            return null;
        }

        private static Button ResolveButton(GameObject root, string relativePath, string fallbackGoName)
        {
            // 1. Exact transform path
            var t = root.transform.Find(relativePath);
            if (t != null)
            {
                var btn = t.GetComponent<Button>() ?? t.GetComponentInChildren<Button>(true);
                if (btn != null) return btn;
            }

            // 2. Typed fallback: any Button on a GO whose name matches fallbackGoName.
            // Walks the active subtree only so we don't pick up unrelated screens.
            if (string.IsNullOrEmpty(fallbackGoName)) return null;
            try
            {
                var buttons = root.GetComponentsInChildren<Button>(true);
                if (buttons == null) return null;
                foreach (var b in buttons)
                {
                    if (b == null) continue;
                    var go = b.gameObject;
                    if (go == null || !go.activeInHierarchy) continue;
                    if (go.name == fallbackGoName) return b;
                    // Also accept the immediate parent's name (some buttons
                    // live under a wrapper GO).
                    var parent = go.transform.parent;
                    if (parent != null && parent.name == fallbackGoName) return b;
                }
            }
            catch { }
            return null;
        }

        private void AddDirectButton(YgomButton btn, string label)
        {
            if (btn?.gameObject == null || !btn.gameObject.activeInHierarchy) return;
            var captured = btn;
            _items.Add(new HomeItem
            {
                Label = label,
                Go = btn.gameObject,
                Activate = () =>
                {
                    try { captured.OnPointerClick(new UnityEngine.EventSystems.PointerEventData(
                        UnityEngine.EventSystems.EventSystem.current)); }
                    catch (Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Handler, "Home",
                            $"YgomButton click failed label={label}: {ex.Message}");
                    }
                }
            });
        }

        #endregion

        #region Area navigation

        private void TryMoveArea(int direction)
        {
            var view = FindActiveViewController<SingleViewController>();
            if (view == null) return;

            // Use the in-game arrow handlers (the same code path the sighted
            // user's UI arrows hit). Empirical: the previous
            // OnMoveArea(absoluteIndex) call left Shop unreachable — likely
            // because the button-index ⇄ MapArea mapping isn't a clean
            // 0..3 / Street..Shop in this world, so Clamp(0,3) +
            // OnMoveArea(3) didn't transition. OnArrowR / onArrowL handle
            // whatever cycle (and Shop inclusion) the game actually
            // implements, including any wrap-around at the ends.
            try
            {
                if (direction > 0) view.OnArrowR();
                else view.onArrowL();
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"Area arrow {(direction > 0 ? "R" : "L")} failed: " +
                    $"{ex.Message}");
                return;
            }

            // Re-sync from the game manager — currentArea() is the source
            // of truth once the arrow handler ran.
            SyncMapAreaIndex();
            string label = GetAreaLabel();
            if (_items.Count > 0)
                _items[0].Label = Loc.Get("home_area_selector", label);
            ScreenReader.Say(Loc.Get("home_area_changed", label));

            // Different NPCs for the new area — rebuild
            StartScan();
        }

        private void SyncMapAreaIndex()
        {
            try
            {
                var mgr = Single3DManager.Instance;
                if (mgr != null)
                    _mapAreaIndex = SingleUtil.getButtonIndexByMapArea(mgr.currentArea());
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"SyncMapAreaIndex failed: {ex.Message}");
            }
        }

        private MapArea GetCurrentMapArea()
        {
            try
            {
                var mgr = Single3DManager.Instance;
                if (mgr != null) return mgr.currentArea();
            }
            catch { }
            try { return SingleUtil.getMapAreaBybuttonIndex(_mapAreaIndex); }
            catch { return MapArea.Street; }
        }

        private string GetAreaLabel()
        {
            return GetCurrentMapArea() switch
            {
                MapArea.Street => Loc.Get("map_area_street"),
                MapArea.Alley  => Loc.Get("map_area_alley"),
                MapArea.Park   => Loc.Get("map_area_park"),
                MapArea.Shop   => Loc.Get("map_area_shop"),
                _              => Loc.Get("home_area_unknown", _mapAreaIndex)
            };
        }

        #endregion

        #region Character navigation

        private void TryMoveChara(int direction)
        {
            if (_unlockedCharas == null || _unlockedCharas.Count == 0) return;
            var view = FindActiveViewController<HomeViewController>();
            if (view == null) return;

            int next = Mathf.Clamp(_charaListIndex + direction, 0, _unlockedCharas.Count - 1);
            if (next == _charaListIndex) return;

            _charaListIndex = next;
            int cid = _unlockedCharas[_charaListIndex];
            try
            {
                view.OnChangeChara(cid, true);
                string name = GetCharacterDisplayName(cid);
                if (_items.Count > 0)
                    _items[0].Label = Loc.Get("home_chara_selector", name);
                ScreenReader.Say(name);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"OnChangeChara failed cid={cid}: {ex.Message}");
            }
        }

        #endregion

        #region Map object helpers

        private string GetMapObjectLabel(MapObjectBase mapObj)
        {
            if (mapObj is BillboardObject billboard)
            {
                string name = TryGetBillboardName(billboard);
                if (!string.IsNullOrWhiteSpace(name)) return name;

                // No name AND no MapObjectType data → decorative slot or
                // unhydrated billboard. Return null so the caller filters
                // it out instead of surfacing "Standard Duelist" for an
                // empty MapObjectRoot.
                var t = billboard.mapObjectData?.type ?? MapObjectType.None;
                if (t == MapObjectType.None && (billboard.npcID <= 0)) return null;

                return t switch
                {
                    MapObjectType.NPCChallenge => Loc.Get("map_npc_challenge"),
                    MapObjectType.NPCMob       => Loc.Get("map_npc_standard"),
                    MapObjectType.NPCOrigin    => Loc.Get("map_npc_legendary"),
                    MapObjectType.NPCTrainer   => Loc.Get("map_npc_trainer"),
                    MapObjectType.BonusDuelist => Loc.Get("map_npc_bonus"),
                    MapObjectType.FoundGift    => Loc.Get("map_gift"),
                    _                          => Loc.Get("map_npc_standard")
                };
            }

            return mapObj.GetType().Name switch
            {
                "GateTouchObject"      => Loc.Get("home_gate"),
                "GateTouchObjectDSOD"  => Loc.Get("home_gate"),
                "ShopObject"           => Loc.Get("map_card_trader"),
                "SchoolObject"         => Loc.Get("map_school"),
                "DuelCenterObject"     => Loc.Get("home_duel_center"),
                "CardPortalObject"     => Loc.Get("map_card_portal"),
                "AlleyGomibakoObject"  => Loc.Get("map_alley_gomibako"),
                "GomiBakoObjectDSOD"   => Loc.Get("map_alley_gomibako"),
                _                      => LabelExtractor.CleanGoName(mapObj.GetType().Name)
            };
        }

        private string TryGetBillboardName(BillboardObject billboard)
        {
            if (billboard == null) return null;

            // Primary: npcID resolved via CharaUtil
            if (billboard.npcID > 0)
            {
                string name = TryResolveCharacterName(billboard.npcID);
                if (!string.IsNullOrWhiteSpace(name)) return NormalizeName(name);
            }

            // Secondary: pull through Single3DManager.getSingleMapChara by
            // MapObjectRoot index — billboards with no npcID still have
            // server data keyed by their root index.
            try
            {
                var go = billboard.gameObject;
                if (go?.name != null
                    && go.name.StartsWith("MapObjectRoot", StringComparison.Ordinal))
                {
                    string suffix = go.name.Substring("MapObjectRoot".Length);
                    if (int.TryParse(suffix, out var idx))
                    {
                        var data = Single3DManager.Instance?.getSingleMapChara(idx);
                        if (data != null)
                        {
                            string title = SingleUtil.getNpcRewardTitle(data);
                            if (!string.IsNullOrWhiteSpace(title))
                                return NormalizeName(title);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"getSingleMapChara fallback failed: {ex.Message}");
            }

            return null;
        }

        private static string TryResolveCharacterName(int cid)
        {
            if (cid <= 0) return null;
            foreach (var resolver in new Func<int, string>[]
            {
                CharaUtil.GetNameWithSeries,
                CharaUtil.GetNameAndSeries,
                CharaUtil.GetName
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

        private static string NormalizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            string s = value.Trim();
            // Strip "LV: 5", "(0/3)", "Rewards for ..." prefixes used by
            // getNpcRewardTitle so the name lands as a clean character handle.
            s = Regex.Replace(s, @"\s*LV\s*:\s*\d+.*$", "", RegexOptions.IgnoreCase).Trim();
            s = Regex.Replace(s, @"\s*\(\d+/\d+\).*$", "").Trim();
            if (s.StartsWith("Rewards for ", StringComparison.OrdinalIgnoreCase))
                s = s.Substring("Rewards for ".Length).Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        }

        private static void ActivateMapObject(MapObjectBase mapObj)
        {
            try
            {
                var managers = Resources.FindObjectsOfTypeAll<
                    Il2CppYgomSystem.UI.ViewControllerManager>();
                if (managers != null)
                {
                    foreach (var mgr in managers)
                    {
                        if (mgr?.gameObject?.activeInHierarchy == true)
                        {
                            mapObj.TapObject(mgr);
                            return;
                        }
                    }
                }

                // Fallback — pointer click on the GO itself
                var pe = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current);
                UnityEngine.EventSystems.ExecuteEvents.Execute(
                    mapObj.gameObject, pe,
                    UnityEngine.EventSystems.ExecuteEvents.pointerClickHandler);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"ActivateMapObject failed: {ex.Message}");
            }
        }

        private static bool HasActiveMapObjectOfType<T>() where T : MapObjectBase
        {
            try
            {
                var found = Resources.FindObjectsOfTypeAll<T>();
                if (found == null) return false;
                foreach (var obj in found)
                    if (obj?.gameObject?.activeInHierarchy == true) return true;
            }
            catch { }
            return false;
        }

        #endregion

        #region Character / deck label helpers

        private string GetCurrentCharacterName()
        {
            // Prefer the live HomeViewController field when the chara panel
            // is open — it's the freshest snapshot of the user's pick.
            try
            {
                var homeVc = FindActiveViewController<HomeViewController>();
                if (homeVc != null && homeVc.crntChara > 0)
                    return GetCharacterDisplayName(homeVc.crntChara);
            }
            catch { }

            // Fallback for the World map: HomeViewController is not loaded
            // there, so the previous code always landed in "Unknown
            // character". CharaUtil.currentCharaId is the static global the
            // game itself reads — updated whenever the user picks a chara,
            // valid in all screens.
            try
            {
                int cid = CharaUtil.currentCharaId;
                if (cid > 0) return GetCharacterDisplayName(cid);
            }
            catch { }

            return Loc.Get("home_character_unknown");
        }

        private static string GetCharacterDisplayName(int cid)
        {
            if (cid <= 0) return Loc.Get("home_character_unknown");
            string name = TryResolveCharacterName(cid);
            return string.IsNullOrWhiteSpace(name) ? $"CID {cid}" : name;
        }

        private static string GetDeckDisplayName(int cid)
        {
            if (cid <= 0) return Loc.Get("home_character_unknown");
            try
            {
                string name = CharaUtil.GetDeckName(cid);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"GetDeckName failed cid={cid}: {ex.Message}");
            }
            return Loc.Get("home_character_unknown");
        }

        private static string GetMissionLabel()
        {
            try
            {
                var buttons = Resources.FindObjectsOfTypeAll<MissionButton>();
                if (buttons != null)
                {
                    foreach (var mb in buttons)
                    {
                        if (mb?.gameObject?.activeInHierarchy != true) continue;
                        string prefix = mb.StageText?.GetComponent<Text>()?.text?.Trim();
                        string num = mb.StageNumText?.text?.Trim();
                        if (!string.IsNullOrWhiteSpace(prefix)
                            && !string.IsNullOrWhiteSpace(num))
                            return Loc.Get("home_missions_stage", $"{prefix} {num}");
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"GetMissionLabel failed: {ex.Message}");
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
            ScreenReader.Say(Loc.Get("home_item",
                _focusIndex + 1, _items.Count, item.Label));
        }

        private static void AnnounceGemBalance()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return;
                if (!namedMgr.TryGetValue("base", out var baseMgr) || baseMgr == null) return;
                var topVc = baseMgr.GetStackTopViewController();
                var header = topVc?.TryCast<HeaderViewController>();
                if (header == null) return;

                var parts = new List<string>();
                string gems = header.gemNumber?.text?.Trim();
                if (!string.IsNullOrEmpty(gems)) parts.Add($"{gems} {Loc.Get("shop_gems")}");
                string cr = header.crNumber?.text?.Trim();
                if (!string.IsNullOrEmpty(cr)) parts.Add($"{cr} {Loc.Get("shop_crystals")}");

                if (parts.Count > 0)
                    ScreenReader.Say(string.Join(", ", parts));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"AnnounceGemBalance error: {ex.Message}");
            }
        }

        #endregion

        #region Misc helpers

        private static T FindActiveViewController<T>()
            where T : Il2CppYgomSystem.UI.ViewController
        {
            try
            {
                var all = Resources.FindObjectsOfTypeAll<T>();
                if (all == null) return null;
                foreach (var vc in all)
                    if (vc?.gameObject?.activeInHierarchy == true) return vc;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Home",
                    $"FindActiveViewController<{typeof(T).Name}> failed: {ex.Message}");
            }
            return null;
        }

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
                    if (string.IsNullOrEmpty(val) || val.Length > 3) continue;
                    bool allDigits = true;
                    foreach (char c in val)
                        if (!char.IsDigit(c)) { allDigits = false; break; }
                    if (allDigits && int.Parse(val) > 0) return val;
                }
            }
            catch { }
            return null;
        }

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
        /// Returns true when a TutorialArrowViewController is on top of any
        /// named ViewControllerManager stack. While true HomeHandler stays
        /// silent so the tutorial-arrow scaffolding in Main + SBH owns input.
        /// </summary>
        private static bool IsTutorialArrowActive()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return false;
                return GameStateTracker.TryFindArrowAcrossManagers(
                    namedManager, out _, out _, out _);
            }
            catch
            {
                return false;
            }
        }

        private static bool ContainsJapanese(string s)
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

        #endregion
    }
}
