using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;
using Il2CppYgomGame.Menu;
using Il2CppYgomGame.Shop;

namespace DuelLinksAccess
{
    /// <summary>
    /// Keyboard-driven shop accessibility handler.
    /// Provides category navigation, item browsing with names and prices,
    /// currency balance reading, and purchase/detail activation.
    /// </summary>
    public class ShopHandler
    {
        #region Types

        private enum Category
        {
            Cards,
            Structure,
            Etc,
            Bundle,
            DPass,
            Accessories,
            HomeBg
        }

        #endregion

        #region Fields

        private ShopViewController2 _vc;
        private bool _wasActive;
        private string _lastVcGoName = "";

        private Category _currentCategory = Category.Cards;
        private int _focusIndex;

        // Cached item list for current category
        private readonly List<MerchViewContentItem> _currentItems = new();

        // Cooldown to prevent rapid-fire activation
        private float _operationCooldown;
        private const float OperationCooldownTime = 0.5f;

        // Delayed initial scan (shop content loads asynchronously)
        private float _initialScanDelay;
        private bool _initialScanDone;
        private int _initialScanAttempts;

        // Available categories (only those with active MerchViews)
        private readonly List<Category> _availableCategories = new();

        #endregion

        #region Properties

        /// <summary>
        /// Whether the handler is actively managing the shop screen.
        /// </summary>
        public bool IsActive { get; private set; }

        #endregion

        #region Public Methods

        /// <summary>
        /// Called each frame from Main.UpdateHandlers().
        /// </summary>
        public void Update()
        {
            if (_operationCooldown > 0f)
                _operationCooldown -= Time.deltaTime;

            if (GameStateTracker.CurrentScreen != GameStateTracker.GameScreen.Shop)
            {
                if (_wasActive) Deactivate();
                return;
            }

            var vc = TryGetShopVC();
            if (vc == null)
            {
                if (_wasActive) Deactivate();
                return;
            }

            string goName = vc.gameObject?.name ?? "";
            if (!_wasActive || goName != _lastVcGoName)
            {
                Activate(vc, goName);
            }

            // Wait for delayed initial scan
            if (!_initialScanDone)
            {
                _initialScanDelay -= Time.deltaTime;
                if (_initialScanDelay <= 0f)
                    DoInitialScan();
                return;
            }

            ProcessInput();
        }

        #endregion

        #region Lifecycle

        private void Activate(ShopViewController2 vc, string goName)
        {
            _vc = vc;
            _lastVcGoName = goName;
            _wasActive = true;
            IsActive = true;
            _focusIndex = 0;
            _initialScanDone = false;
            _initialScanDelay = 1.0f;
            _initialScanAttempts = 0;

            DebugLogger.Log(LogCategory.Handler, "Shop",
                $"Activated, waiting for initial scan...");
        }

        private void DoInitialScan()
        {
            _initialScanAttempts++;

            RefreshAvailableCategories();

            DebugLogger.Log(LogCategory.Handler, "Shop",
                $"Scan attempt {_initialScanAttempts}: {_availableCategories.Count} categories");

            if (_availableCategories.Count == 0 && _initialScanAttempts < 8)
            {
                _initialScanDelay = 0.5f;
                return;
            }

            _initialScanDone = true;

            if (_availableCategories.Count > 0)
                _currentCategory = _availableCategories[0];

            RefreshCurrentItems();

            string balanceText = ReadAllBalances();
            string categoryName = GetCategoryName(_currentCategory);
            int itemCount = _currentItems.Count;

            if (_availableCategories.Count > 0)
            {
                ScreenReader.Say(Loc.Get("shop_entered", balanceText, categoryName, itemCount));
            }
            else
            {
                ScreenReader.Say(Loc.Get("shop_entered_empty", balanceText));
            }

            if (_currentItems.Count > 0)
                AnnounceCurrentItem(queued: true);
        }

        private void Deactivate()
        {
            _wasActive = false;
            IsActive = false;
            _vc = null;
            _currentItems.Clear();
            _availableCategories.Clear();

            DebugLogger.Log(LogCategory.Handler, "Shop", "Deactivated");
        }

        #endregion

        #region VC Detection

        /// <summary>
        /// Attempts to find ShopViewController2 as the top content VC.
        /// </summary>
        private ShopViewController2 TryGetShopVC()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return null;

                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedMgr.TryGetValue("content", out contentMgr) || contentMgr == null)
                    return null;

                var topVc = contentMgr.GetStackTopViewController();
                if (topVc == null) return null;

                return topVc.TryCast<ShopViewController2>();
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"TryGetShopVC error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Category Management

        /// <summary>
        /// Discovers which shop categories have active MerchViews.
        /// </summary>
        private void RefreshAvailableCategories()
        {
            _availableCategories.Clear();

            try
            {
                if (_vc == null) return;

                if (IsMerchViewListActive(_vc.cardMerchViews))
                    _availableCategories.Add(Category.Cards);

                if (IsMerchViewActive(_vc.etcMerchView2))
                    _availableCategories.Add(Category.Etc);

                if (IsMerchViewActive(_vc.bundleMerchView2))
                    _availableCategories.Add(Category.Bundle);

                if (IsMerchViewActive(_vc.dpassMerchView))
                    _availableCategories.Add(Category.DPass);

                if (IsMerchViewActive(_vc.accsMerchView))
                    _availableCategories.Add(Category.Accessories);

                if (IsMerchViewActive(_vc.homeBgMerchView))
                    _availableCategories.Add(Category.HomeBg);

                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"Available categories: {string.Join(", ", _availableCategories)}");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"RefreshAvailableCategories error: {ex.Message}");
            }
        }

        private bool IsMerchViewActive(MerchViewBase view)
        {
            try
            {
                if (view == null) return false;
                var go = view.gameObject;
                return go != null && go.activeInHierarchy;
            }
            catch { return false; }
        }

        private bool IsMerchViewListActive(Il2CppSystem.Collections.Generic.List<MerchViewBase> views)
        {
            try
            {
                if (views == null || views.Count == 0) return false;
                for (int i = 0; i < views.Count; i++)
                {
                    if (IsMerchViewActive(views[i]))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// Gets the active MerchView for the current category.
        /// </summary>
        private MerchViewBase GetActiveMerchView()
        {
            try
            {
                if (_vc == null) return null;

                return _currentCategory switch
                {
                    Category.Cards => GetFirstActiveMerchView(_vc.cardMerchViews),
                    Category.Etc => _vc.etcMerchView2,
                    Category.Bundle => _vc.bundleMerchView2,
                    Category.DPass => _vc.dpassMerchView,
                    Category.Accessories => _vc.accsMerchView,
                    Category.HomeBg => _vc.homeBgMerchView,
                    _ => null
                };
            }
            catch { return null; }
        }

        private MerchViewBase GetFirstActiveMerchView(Il2CppSystem.Collections.Generic.List<MerchViewBase> views)
        {
            try
            {
                if (views == null) return null;
                for (int i = 0; i < views.Count; i++)
                {
                    if (IsMerchViewActive(views[i]))
                        return views[i];
                }
            }
            catch { }
            return null;
        }

        #endregion

        #region Data Access

        /// <summary>
        /// Refreshes the item list from the current category's MerchView.
        /// </summary>
        private void RefreshCurrentItems()
        {
            _currentItems.Clear();

            try
            {
                var merchView = GetActiveMerchView();
                if (merchView == null) return;

                var items = merchView.contentItems;
                if (items == null) return;

                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    if (item != null && item.gameObject != null && item.gameObject.activeInHierarchy)
                        _currentItems.Add(item);
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"RefreshCurrentItems error: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads the gem balance from ShopViewController2.
        /// </summary>
        private string ReadGemBalance()
        {
            try
            {
                var gemText = _vc?.gemBalance;
                if (gemText != null)
                {
                    string text = gemText.text;
                    if (!string.IsNullOrEmpty(text))
                        return text;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"ReadGemBalance error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Reads the crystal balance from HeaderViewController.crNumber.
        /// </summary>
        private string ReadCrystalBalance()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return null;

                Il2CppYgomSystem.UI.ViewControllerManager baseMgr;
                if (!namedMgr.TryGetValue("base", out baseMgr) || baseMgr == null)
                    return null;

                var topVc = baseMgr.GetStackTopViewController();
                if (topVc == null) return null;

                var header = topVc.TryCast<HeaderViewController>();
                if (header == null) return null;

                var crText = header.crNumber;
                if (crText != null)
                {
                    string text = crText.text;
                    if (!string.IsNullOrEmpty(text) && text.Trim().Length > 0)
                        return text.Trim();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"ReadCrystalBalance error: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Reads all available currency balances and formats them.
        /// </summary>
        private string ReadAllBalances()
        {
            var parts = new List<string>();

            string gems = ReadGemBalance();
            if (!string.IsNullOrEmpty(gems))
                parts.Add(gems + " " + Loc.Get("shop_gems"));

            string crystals = ReadCrystalBalance();
            if (!string.IsNullOrEmpty(crystals))
                parts.Add(crystals + " " + Loc.Get("shop_crystals"));

            if (parts.Count == 0)
                return Loc.Get("shop_balance_unknown");

            return string.Join(", ", parts);
        }

        #endregion

        #region Input Processing

        private void ProcessInput()
        {
            // Tab / Shift+Tab — switch category
            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
                    SwitchCategoryPrev();
                else
                    SwitchCategoryNext();
                return;
            }

            // Left / Right — navigate items
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

            // Up / Down — page jump (5 items)
            if (InputManager.TryConsumeKeyDown(KeyCode.UpArrow))
            {
                NavigateBy(-5);
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.DownArrow))
            {
                NavigateBy(5);
                return;
            }

            // Home / End
            if (InputManager.TryConsumeKeyDown(KeyCode.Home))
            {
                if (_currentItems.Count > 0)
                {
                    _focusIndex = 0;
                    AnnounceCurrentItem();
                }
                return;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.End))
            {
                if (_currentItems.Count > 0)
                {
                    _focusIndex = _currentItems.Count - 1;
                    AnnounceCurrentItem();
                }
                return;
            }

            // Enter — activate current item (open detail / purchase)
            if (InputManager.TryConsumeKeyDown(KeyCode.Return))
            {
                ActivateCurrentItem();
                return;
            }

            // G — currency balances
            if (InputManager.TryConsumeKeyDown(KeyCode.G))
            {
                string balances = ReadAllBalances();
                ScreenReader.Say(balances);
                return;
            }

            // C or I — verbose item details
            if (InputManager.TryConsumeKeyDown(KeyCode.C) || InputManager.TryConsumeKeyDown(KeyCode.I))
            {
                AnnounceCurrentItem(verbose: true);
                return;
            }

            // Space — rescan items
            if (InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                RefreshAvailableCategories();
                RefreshCurrentItems();
                string categoryName = GetCategoryName(_currentCategory);
                ScreenReader.Say(Loc.Get("shop_category", categoryName, _currentItems.Count));
                if (_currentItems.Count > 0)
                    AnnounceCurrentItem(queued: true);
                return;
            }

            // F3 — debug dump current item (debug mode only)
            if (Main.DebugMode && InputManager.TryConsumeKeyDown(KeyCode.F3))
            {
                DumpCurrentItem();
                return;
            }

            // Escape / Backspace — go back
            if (InputManager.TryConsumeKeyDown(KeyCode.Escape)
                || InputManager.TryConsumeKeyDown(KeyCode.Backspace))
            {
                GoBack();
                return;
            }
        }

        #endregion

        #region Navigation

        private void SwitchCategoryNext()
        {
            if (_availableCategories.Count == 0) return;

            int idx = _availableCategories.IndexOf(_currentCategory);
            idx = (idx + 1) % _availableCategories.Count;
            _currentCategory = _availableCategories[idx];
            OnCategoryChanged();
        }

        private void SwitchCategoryPrev()
        {
            if (_availableCategories.Count == 0) return;

            int idx = _availableCategories.IndexOf(_currentCategory);
            idx = (idx - 1 + _availableCategories.Count) % _availableCategories.Count;
            _currentCategory = _availableCategories[idx];
            OnCategoryChanged();
        }

        private void OnCategoryChanged()
        {
            RefreshCurrentItems();
            _focusIndex = 0;

            string categoryName = GetCategoryName(_currentCategory);
            ScreenReader.Say(Loc.Get("shop_category", categoryName, _currentItems.Count));

            if (_currentItems.Count > 0)
                AnnounceCurrentItem(queued: true);

            DebugLogger.Log(LogCategory.Handler, "Shop",
                $"Category: {_currentCategory}, {_currentItems.Count} items");
        }

        private void NavigateBy(int delta)
        {
            if (_currentItems.Count == 0)
            {
                ScreenReader.Say(Loc.Get("shop_no_items"));
                return;
            }

            _focusIndex += delta;
            if (_focusIndex < 0) _focusIndex = 0;
            if (_focusIndex >= _currentItems.Count) _focusIndex = _currentItems.Count - 1;

            AnnounceCurrentItem();
        }

        #endregion

        #region Item Activation

        /// <summary>
        /// Activates the current item. Behavior varies by item type:
        /// ArtItem/ArtItemBox → OnClickInfo() to open pack details.
        /// BundleLineupItem → OnInformation() if available, else first purchase button.
        /// StandardItem/Trainer2Item → OnPurchase()/OnClickPurchase().
        /// Fallback → PurchaseButton.OnTouch() or Button.onClick.
        /// </summary>
        private void ActivateCurrentItem()
        {
            if (_operationCooldown > 0f) return;
            if (_currentItems.Count == 0 || _focusIndex < 0 || _focusIndex >= _currentItems.Count) return;

            var item = _currentItems[_focusIndex];
            if (item == null) return;

            try
            {
                // ArtItem (card packs/boxes) — open detail screen
                var artItem = item.TryCast<ArtItem>();
                if (artItem != null)
                {
                    artItem.OnClickInfo();
                    DebugLogger.Log(LogCategory.Handler, "Shop",
                        $"Called OnClickInfo on ArtItem at index {_focusIndex}");
                    _operationCooldown = OperationCooldownTime;
                    return;
                }

                // BundleLineupItem — open information or first purchase button
                var bundleItem = item.TryCast<BundleLineupItem>();
                if (bundleItem != null)
                {
                    bundleItem.OnInformation();
                    DebugLogger.Log(LogCategory.Handler, "Shop",
                        $"Called OnInformation on BundleLineupItem at index {_focusIndex}");
                    _operationCooldown = OperationCooldownTime;
                    return;
                }

                // Trainer2Item
                var trainerItem = item.TryCast<Trainer2Item>();
                if (trainerItem != null)
                {
                    trainerItem.OnClickPurchase();
                    DebugLogger.Log(LogCategory.Handler, "Shop",
                        $"Called OnClickPurchase on Trainer2Item at index {_focusIndex}");
                    _operationCooldown = OperationCooldownTime;
                    return;
                }

                // StandardItem
                var standardItem = item.TryCast<StandardItem>();
                if (standardItem != null)
                {
                    standardItem.OnPurchase();
                    DebugLogger.Log(LogCategory.Handler, "Shop",
                        $"Called OnPurchase on StandardItem at index {_focusIndex}");
                    _operationCooldown = OperationCooldownTime;
                    return;
                }

                // Fallback: find a PurchaseButton in the item's children
                var purchaseBtn = item.gameObject.GetComponentInChildren<PurchaseButton>();
                if (purchaseBtn != null)
                {
                    purchaseBtn.OnTouch();
                    DebugLogger.Log(LogCategory.Handler, "Shop",
                        $"Called OnTouch on PurchaseButton at index {_focusIndex}");
                    _operationCooldown = OperationCooldownTime;
                    return;
                }

                // Last fallback: Unity Button
                var button = item.gameObject.GetComponentInChildren<Button>();
                if (button != null)
                {
                    button.onClick.Invoke();
                    DebugLogger.Log(LogCategory.Handler, "Shop",
                        $"Called onClick on Button at index {_focusIndex}");
                    _operationCooldown = OperationCooldownTime;
                    return;
                }

                ScreenReader.Say(Loc.Get("shop_activate_error"));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"ActivateCurrentItem error: {ex.Message}");
                ScreenReader.Say(Loc.Get("shop_activate_error"));
            }
        }

        private void GoBack()
        {
            try
            {
                var namedMgr = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedMgr == null) return;

                Il2CppYgomSystem.UI.ViewControllerManager contentMgr;
                if (!namedMgr.TryGetValue("content", out contentMgr) || contentMgr == null)
                    return;

                var topVc = contentMgr.GetStackTopViewController();
                topVc?.SendBack();
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"GoBack error: {ex.Message}");
            }
        }

        #endregion

        #region Announcements

        private void AnnounceCurrentItem(bool verbose = false, bool queued = false)
        {
            if (_currentItems.Count == 0)
            {
                ScreenReader.Say(Loc.Get("shop_no_items"));
                return;
            }

            if (_focusIndex < 0 || _focusIndex >= _currentItems.Count)
                _focusIndex = 0;

            var item = _currentItems[_focusIndex];
            if (item == null)
            {
                ScreenReader.Say(Loc.Get("shop_no_items"));
                return;
            }

            int pos = _focusIndex + 1;
            int total = _currentItems.Count;
            string itemText = verbose ? FormatItemVerbose(item) : FormatItemCompact(item);
            string announcement = Loc.Get("shop_item_position", pos, total, itemText);

            if (queued)
                ScreenReader.SayQueued(announcement);
            else
                ScreenReader.Say(announcement);
        }

        /// <summary>
        /// Compact item format: name + price/stock info.
        /// Dispatches to type-specific formatters.
        /// </summary>
        private string FormatItemCompact(MerchViewContentItem item)
        {
            try
            {
                if (!item.isReady)
                    return Loc.Get("shop_item_loading");

                // ArtItemBox (check before ArtItem — it's a subclass)
                var artItemBox = item.TryCast<ArtItemBox>();
                if (artItemBox != null)
                    return FormatArtItemBoxCompact(artItemBox);

                // ArtItem (card packs)
                var artItem = item.TryCast<ArtItem>();
                if (artItem != null)
                    return FormatArtItemCompact(artItem);

                // BundleLineupItem
                var bundleItem = item.TryCast<BundleLineupItem>();
                if (bundleItem != null)
                    return FormatBundleItemCompact(bundleItem);

                // Trainer2Item
                var trainerItem = item.TryCast<Trainer2Item>();
                if (trainerItem != null)
                    return FormatTrainerItemCompact(trainerItem);

                // StandardItem
                var standardItem = item.TryCast<StandardItem>();
                if (standardItem != null)
                    return FormatStandardItemCompact(standardItem);

                // Fallback
                return ExtractItemTextFallback(item);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"FormatItemCompact error: {ex.Message}");
                return Loc.Get("shop_unknown_item");
            }
        }

        /// <summary>
        /// Verbose item format: compact info + description + limit + sale info.
        /// </summary>
        private string FormatItemVerbose(MerchViewContentItem item)
        {
            try
            {
                if (!item.isReady)
                    return Loc.Get("shop_item_loading");

                var artItemBox = item.TryCast<ArtItemBox>();
                if (artItemBox != null)
                    return FormatArtItemBoxVerbose(artItemBox);

                var artItem = item.TryCast<ArtItem>();
                if (artItem != null)
                    return FormatArtItemVerbose(artItem);

                var bundleItem = item.TryCast<BundleLineupItem>();
                if (bundleItem != null)
                    return FormatBundleItemVerbose(bundleItem);

                var trainerItem = item.TryCast<Trainer2Item>();
                if (trainerItem != null)
                    return FormatTrainerItemVerbose(trainerItem);

                var standardItem = item.TryCast<StandardItem>();
                if (standardItem != null)
                    return FormatStandardItemVerbose(standardItem);

                return ExtractItemTextFallback(item);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"FormatItemVerbose error: {ex.Message}");
                return Loc.Get("shop_unknown_item");
            }
        }

        #endregion

        #region ArtItem Formatting

        /// <summary>
        /// Extracts the name of a card pack from its dataSource dictionary or child text.
        /// ArtItem titles are images, so we try dataSource keys then child text scan.
        /// </summary>
        private string GetArtItemName(ArtItem artItem)
        {
            // Try dataSource dictionary for a name key
            try
            {
                var ds = artItem.dataSource;
                if (ds != null)
                {
                    // Common keys: "name", "title", "packName"
                    foreach (var key in new[] { "name", "title", "packName", "nameText" })
                    {
                        Il2CppSystem.Object val;
                        if (ds.TryGetValue(key, out val) && val != null)
                        {
                            string name = val.ToString();
                            if (!string.IsNullOrEmpty(name))
                                return LabelExtractor.StripRichText(name);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"GetArtItemName dataSource error: {ex.Message}");
            }

            // Fallback: scan child Text components
            return ExtractChildTexts(artItem.gameObject);
        }

        private string FormatArtItemCompact(ArtItem artItem)
        {
            var parts = new List<string>();

            string name = GetArtItemName(artItem);
            if (!string.IsNullOrEmpty(name))
                parts.Add(name);
            else
                parts.Add(Loc.Get("shop_card_pack"));

            // Scan for purchase buttons and prices within the item
            string prices = GetAllPricesFromGameObject(artItem.gameObject);
            if (!string.IsNullOrEmpty(prices))
                parts.Add(prices);

            return parts.Count > 0 ? string.Join(", ", parts) : Loc.Get("shop_unknown_item");
        }

        private string FormatArtItemVerbose(ArtItem artItem)
        {
            var parts = new List<string>();
            parts.Add(FormatArtItemCompact(artItem));

            string packIdStr = "Pack " + artItem.packId;
            parts.Add(packIdStr);

            return string.Join(". ", parts);
        }

        private string FormatArtItemBoxCompact(ArtItemBox artItemBox)
        {
            var parts = new List<string>();

            string name = GetArtItemName(artItemBox);
            if (!string.IsNullOrEmpty(name))
                parts.Add(name);
            else
                parts.Add(Loc.Get("shop_card_pack"));

            // Stock counter (packs remaining in box)
            string stock = SafeReadText(artItemBox.stockCounter);
            if (!string.IsNullOrEmpty(stock))
                parts.Add(Loc.Get("shop_remaining", stock));

            // Pack count
            string packsText = SafeReadText(artItemBox.packs);
            if (!string.IsNullOrEmpty(packsText))
                parts.Add(packsText + " " + Loc.Get("shop_packs_label"));

            // Prices
            string prices = GetAllPricesFromGameObject(artItemBox.gameObject);
            if (!string.IsNullOrEmpty(prices))
                parts.Add(prices);

            return parts.Count > 0 ? string.Join(", ", parts) : Loc.Get("shop_unknown_item");
        }

        private string FormatArtItemBoxVerbose(ArtItemBox artItemBox)
        {
            var parts = new List<string>();
            parts.Add(FormatArtItemBoxCompact(artItemBox));

            string packIdStr = "Pack " + artItemBox.packId;
            parts.Add(packIdStr);

            return string.Join(". ", parts);
        }

        #endregion

        #region BundleLineupItem Formatting

        private string FormatBundleItemCompact(BundleLineupItem bundleItem)
        {
            var parts = new List<string>();

            string title = SafeReadText(bundleItem.titleLabel);
            if (!string.IsNullOrEmpty(title))
                parts.Add(title);

            // Stock info
            try
            {
                int stock = bundleItem.Stock;
                if (stock > 0)
                    parts.Add(Loc.Get("shop_remaining", stock.ToString()));
            }
            catch { }

            // Get prices from purchase buttons
            string prices = GetAllPricesFromGameObject(bundleItem.gameObject);
            if (!string.IsNullOrEmpty(prices))
                parts.Add(prices);

            if (parts.Count == 0)
                return ExtractItemTextFallback(bundleItem);

            return string.Join(", ", parts);
        }

        private string FormatBundleItemVerbose(BundleLineupItem bundleItem)
        {
            var parts = new List<string>();
            parts.Add(FormatBundleItemCompact(bundleItem));

            // Card count
            try
            {
                var cards = bundleItem.Cards;
                if (cards != null && cards.Count > 0)
                    parts.Add(Loc.Get("shop_contains_cards", cards.Count));
            }
            catch { }

            return string.Join(". ", parts);
        }

        #endregion

        #region Trainer2Item Formatting

        private string FormatTrainerItemCompact(Trainer2Item trainerItem)
        {
            var parts = new List<string>();

            string title = SafeReadText(trainerItem.titleLabel);
            if (!string.IsNullOrEmpty(title))
                parts.Add(title);

            string price = SafeReadText(trainerItem.priceTag);
            if (!string.IsNullOrEmpty(price))
                parts.Add(price);

            try
            {
                if (trainerItem.isSale)
                    parts.Add(Loc.Get("shop_item_sale"));
            }
            catch { }

            if (parts.Count == 0)
                return ExtractItemTextFallback(trainerItem);

            return string.Join(", ", parts);
        }

        private string FormatTrainerItemVerbose(Trainer2Item trainerItem)
        {
            var parts = new List<string>();
            parts.Add(FormatTrainerItemCompact(trainerItem));

            string caption = SafeReadText(trainerItem.caption);
            if (!string.IsNullOrEmpty(caption))
                parts.Add(caption);

            string limit = SafeReadText(trainerItem.limit);
            if (!string.IsNullOrEmpty(limit))
                parts.Add(Loc.Get("shop_item_limit", limit));

            try
            {
                if (trainerItem.isSale)
                {
                    string saleDate = SafeReadText(trainerItem.saleDate);
                    if (!string.IsNullOrEmpty(saleDate))
                        parts.Add(Loc.Get("shop_item_sale_date", saleDate));
                }
            }
            catch { }

            return string.Join(". ", parts);
        }

        #endregion

        #region StandardItem Formatting

        private string FormatStandardItemCompact(StandardItem item)
        {
            var parts = new List<string>();

            string title = SafeReadText(item.titleLabel);
            if (!string.IsNullOrEmpty(title))
                parts.Add(title);

            string price = GetItemPrice(item);
            if (!string.IsNullOrEmpty(price))
                parts.Add(price);

            try
            {
                if (item.isSale)
                    parts.Add(Loc.Get("shop_item_sale"));
            }
            catch { }

            if (parts.Count == 0)
                return ExtractItemTextFallback(item);

            return string.Join(", ", parts);
        }

        private string FormatStandardItemVerbose(StandardItem item)
        {
            var parts = new List<string>();
            parts.Add(FormatStandardItemCompact(item));

            string caption = SafeReadText(item.caption);
            if (!string.IsNullOrEmpty(caption))
                parts.Add(caption);

            string limit = SafeReadText(item.limit);
            if (!string.IsNullOrEmpty(limit))
                parts.Add(Loc.Get("shop_item_limit", limit));

            try
            {
                if (item.isSale)
                {
                    string saleDate = SafeReadText(item.saleDate);
                    if (!string.IsNullOrEmpty(saleDate))
                        parts.Add(Loc.Get("shop_item_sale_date", saleDate));
                }
            }
            catch { }

            return string.Join(". ", parts);
        }

        /// <summary>
        /// Gets the price text from a StandardItem.
        /// </summary>
        private string GetItemPrice(StandardItem item)
        {
            try
            {
                string priceTag = SafeReadText(item.priceTag);
                if (!string.IsNullOrEmpty(priceTag))
                    return priceTag;

                return GetAllPricesFromGameObject(item.purchaseButton) ?? GetAllPricesFromGameObject(item.gameObject);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"GetItemPrice error: {ex.Message}");
            }
            return null;
        }

        #endregion

        #region Price Extraction

        /// <summary>
        /// Scans a GameObject for all PurchaseButton variants and extracts price info.
        /// Returns a consolidated string like "50 Gems" or "$4.99, 50 Gems".
        /// </summary>
        private string GetAllPricesFromGameObject(GameObject go)
        {
            if (go == null) return null;

            try
            {
                var priceParts = new List<string>();

                var purchaseButtons = go.GetComponentsInChildren<PurchaseButton>(false);
                if (purchaseButtons != null)
                {
                    for (int i = 0; i < purchaseButtons.Count; i++)
                    {
                        var btn = purchaseButtons[i];
                        if (btn == null) continue;

                        string price = ExtractPriceFromButton(btn);
                        if (!string.IsNullOrEmpty(price) && !priceParts.Contains(price))
                            priceParts.Add(price);
                    }
                }

                return priceParts.Count > 0 ? string.Join(" / ", priceParts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Extracts price text from a single PurchaseButton, checking all variants.
        /// </summary>
        private string ExtractPriceFromButton(PurchaseButton btn)
        {
            try
            {
                // Check if sold out
                var soldOut = btn.TryCast<PurchaseButtonSoldOut>();
                if (soldOut != null)
                    return Loc.Get("shop_item_sold_out");

                // PurchaseButtonGem — gem prices
                var gemBtn = btn.TryCast<PurchaseButtonGem>();
                if (gemBtn != null)
                {
                    string gemPrice = SafeReadText(gemBtn.GemPrice);
                    if (!string.IsNullOrEmpty(gemPrice))
                    {
                        string result = gemPrice + " " + Loc.Get("shop_gems");
                        // Append limit if present
                        string limit = SafeReadText(gemBtn._limit);
                        string limitNum = SafeReadText(gemBtn._limitnum);
                        if (!string.IsNullOrEmpty(limitNum))
                            result += " (" + Loc.Get("shop_item_limit", limitNum) + ")";
                        return result;
                    }
                }

                // PurchaseButtonSale — real money / crystal prices
                var saleBtn = btn.TryCast<PurchaseButtonSale>();
                if (saleBtn != null)
                {
                    // Real money price
                    string rmPrice = SafeReadText(saleBtn.RMPrice);
                    if (!string.IsNullOrEmpty(rmPrice))
                    {
                        // Check for bonus
                        string bonus = SafeReadText(saleBtn.BonusSum);
                        if (!string.IsNullOrEmpty(bonus))
                            return rmPrice + " (+" + bonus + " " + Loc.Get("shop_bonus") + ")";
                        return rmPrice;
                    }

                    // Crystal price
                    var crstGo = saleBtn.crstPrice;
                    if (crstGo != null && crstGo.activeInHierarchy)
                    {
                        var crstText = crstGo.GetComponentInChildren<Text>();
                        if (crstText != null)
                        {
                            string crstVal = SafeReadText(crstText);
                            if (!string.IsNullOrEmpty(crstVal))
                                return crstVal + " " + Loc.Get("shop_crystals");
                        }
                    }

                    // Limit info
                    string saleLimit = SafeReadText(saleBtn._limit);
                    string saleLimitNum = SafeReadText(saleBtn._limitnum);
                    if (!string.IsNullOrEmpty(saleLimitNum))
                        return Loc.Get("shop_item_limit", saleLimitNum);
                }

                // Base PurchaseButton — generic label
                if (btn.isGemPurchase)
                {
                    string label = SafeReadText(btn.Label);
                    if (!string.IsNullOrEmpty(label))
                        return label + " " + Loc.Get("shop_gems");
                }
                else
                {
                    string label = SafeReadText(btn.Label);
                    if (!string.IsNullOrEmpty(label))
                        return label;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Handler, "Shop",
                    $"ExtractPriceFromButton error: {ex.Message}");
            }
            return null;
        }

        #endregion

        #region Text Extraction Fallback

        /// <summary>
        /// Extracts readable text from child Text components.
        /// </summary>
        private string ExtractChildTexts(GameObject go)
        {
            if (go == null) return null;

            try
            {
                var texts = go.GetComponentsInChildren<Text>(false);
                if (texts == null) return null;

                var parts = new List<string>();
                for (int i = 0; i < texts.Count; i++)
                {
                    var text = texts[i];
                    if (text == null) continue;
                    string val = text.text;
                    if (string.IsNullOrEmpty(val) || val.Trim().Length == 0) continue;

                    string clean = LabelExtractor.StripRichText(val.Trim());
                    if (!string.IsNullOrEmpty(clean) && !parts.Contains(clean))
                        parts.Add(clean);
                }
                return parts.Count > 0 ? string.Join(", ", parts) : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Fallback: extract text from item using LabelExtractor then child Text scan.
        /// </summary>
        private string ExtractItemTextFallback(MerchViewContentItem item)
        {
            try
            {
                var go = item.gameObject;
                if (go == null) return Loc.Get("shop_unknown_item");

                string label = LabelExtractor.GetLabel(go);
                if (!string.IsNullOrEmpty(label) && label != "Button")
                    return label;

                string childTexts = ExtractChildTexts(go);
                if (!string.IsNullOrEmpty(childTexts))
                    return childTexts;

                return Loc.Get("shop_unknown_item");
            }
            catch
            {
                return Loc.Get("shop_unknown_item");
            }
        }

        #endregion

        #region Debug

        /// <summary>
        /// Dumps detailed information about the current item to the debug log and screen reader.
        /// </summary>
        private void DumpCurrentItem()
        {
            if (_currentItems.Count == 0 || _focusIndex < 0 || _focusIndex >= _currentItems.Count)
            {
                ScreenReader.Say("No item to dump.");
                return;
            }

            var item = _currentItems[_focusIndex];
            if (item == null)
            {
                ScreenReader.Say("Item is null.");
                return;
            }

            var lines = new List<string>();
            string typeName = item.GetIl2CppType()?.Name ?? "Unknown";
            lines.Add($"Type: {typeName}");
            lines.Add($"GO: {item.gameObject?.name ?? "null"}");
            lines.Add($"isReady: {item.isReady}");

            // ArtItem fields
            var artItem = item.TryCast<ArtItem>();
            if (artItem != null)
            {
                lines.Add($"packId: {artItem.packId}");
                lines.Add($"titlePath: {artItem.titlePath ?? "null"}");
                lines.Add($"mainPath: {artItem.mainPath ?? "null"}");
                lines.Add($"infoUrl: {artItem.infoUrl ?? "null"}");

                // Dump dataSource keys
                try
                {
                    var ds = artItem.dataSource;
                    if (ds != null)
                    {
                        var enumerator = ds.GetEnumerator();
                        while (enumerator.MoveNext())
                        {
                            var kv = enumerator.Current;
                            string key = kv.Key;
                            string val = kv.Value?.ToString() ?? "null";
                            if (val.Length > 80) val = val.Substring(0, 80) + "...";
                            lines.Add($"  ds[{key}] = {val}");
                        }
                    }
                    else
                    {
                        lines.Add("dataSource: null");
                    }
                }
                catch (Exception ex)
                {
                    lines.Add($"dataSource error: {ex.Message}");
                }
            }

            // ArtItemBox extra fields
            var artItemBox = item.TryCast<ArtItemBox>();
            if (artItemBox != null)
            {
                lines.Add($"stockCounter: {SafeReadText(artItemBox.stockCounter) ?? "null"}");
                lines.Add($"packs: {SafeReadText(artItemBox.packs) ?? "null"}");
            }

            // BundleLineupItem fields
            var bundleItem = item.TryCast<BundleLineupItem>();
            if (bundleItem != null)
            {
                lines.Add($"titleLabel: {SafeReadText(bundleItem.titleLabel) ?? "null"}");
                try { lines.Add($"Stock: {bundleItem.Stock}"); } catch { }
                try
                {
                    var cards = bundleItem.Cards;
                    lines.Add($"Cards: {(cards != null ? cards.Count.ToString() : "null")}");
                }
                catch { }
            }

            // StandardItem fields
            var stdItem = item.TryCast<StandardItem>();
            if (stdItem != null)
            {
                lines.Add($"titleLabel: {SafeReadText(stdItem.titleLabel) ?? "null"}");
                lines.Add($"priceTag: {SafeReadText(stdItem.priceTag) ?? "null"}");
                lines.Add($"caption: {SafeReadText(stdItem.caption) ?? "null"}");
                lines.Add($"limit: {SafeReadText(stdItem.limit) ?? "null"}");
                lines.Add($"isSale: {stdItem.isSale}");
            }

            // Dump all PurchaseButton components
            var purchaseButtons = item.gameObject.GetComponentsInChildren<PurchaseButton>(true);
            if (purchaseButtons != null)
            {
                for (int i = 0; i < purchaseButtons.Count; i++)
                {
                    var btn = purchaseButtons[i];
                    if (btn == null) continue;
                    string btnType = btn.GetIl2CppType()?.Name ?? "Unknown";
                    string btnLabel = SafeReadText(btn.Label) ?? "null";
                    lines.Add($"PurchaseBtn[{i}]: {btnType}, label={btnLabel}, gem={btn.isGemPurchase}");

                    var gemBtn = btn.TryCast<PurchaseButtonGem>();
                    if (gemBtn != null)
                        lines.Add($"  GemPrice: {SafeReadText(gemBtn.GemPrice) ?? "null"}");

                    var saleBtn = btn.TryCast<PurchaseButtonSale>();
                    if (saleBtn != null)
                    {
                        lines.Add($"  RMPrice: {SafeReadText(saleBtn.RMPrice) ?? "null"}");
                        lines.Add($"  BonusSum: {SafeReadText(saleBtn.BonusSum) ?? "null"}");
                    }
                }
            }

            // Child text dump
            lines.Add("--- Child Texts ---");
            var allTexts = item.gameObject.GetComponentsInChildren<Text>(true);
            if (allTexts != null)
            {
                for (int i = 0; i < allTexts.Count; i++)
                {
                    var t = allTexts[i];
                    if (t == null) continue;
                    string tVal = t.text;
                    if (!string.IsNullOrEmpty(tVal) && tVal.Trim().Length > 0)
                    {
                        string goPath = t.gameObject?.name ?? "?";
                        lines.Add($"  [{goPath}]: {tVal.Trim()}");
                    }
                }
            }

            string dump = string.Join("\n", lines);
            DebugLogger.Log(LogCategory.Handler, "Shop", $"ITEM DUMP [{_focusIndex}]:\n{dump}");
            ScreenReader.Say($"Dumped {typeName} to log. {lines.Count} lines.");
        }

        #endregion

        #region Utilities

        private string SafeReadText(Text textComponent)
        {
            try
            {
                if (textComponent == null) return null;
                string text = textComponent.text;
                if (string.IsNullOrEmpty(text)) return null;
                return LabelExtractor.StripRichText(text.Trim());
            }
            catch { return null; }
        }

        private string GetCategoryName(Category category)
        {
            return category switch
            {
                Category.Cards => Loc.Get("shop_category_cards"),
                Category.Structure => Loc.Get("shop_category_structure"),
                Category.Etc => Loc.Get("shop_category_etc"),
                Category.Bundle => Loc.Get("shop_category_bundle"),
                Category.DPass => Loc.Get("shop_category_dpass"),
                Category.Accessories => Loc.Get("shop_category_accessories"),
                Category.HomeBg => Loc.Get("shop_category_homebg"),
                _ => "Unknown"
            };
        }

        #endregion
    }
}
