using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace DuelLinksAccess
{
    /// <summary>
    /// Handles keyboard navigation of the duel field using a spatial grid model.
    ///
    /// Grid layout (bottom to top):
    ///   Row 0: Hand (variable width)
    ///   Row 1: My Spell/Trap (3 columns)
    ///   Row 2: My Monster (3 columns + Extra Monster Zone)
    ///   Row 3: Opp Monster (3 columns + Extra Monster Zone)
    ///   Row 4: Opp Spell/Trap (3 columns)
    ///
    /// Arrow navigation:
    ///   Up/Down — Move between rows (column preserved). Action menu when open.
    ///   Left/Right — Move between columns within row (wraps).
    ///
    /// Zone hotkeys (Shift = opponent):
    ///   C=Hand, M=Monsters, S=Spells, T=FieldSpell, G=Grave, B=Banished, D=ExtraDeck
    ///   L=LP (read-only), 1-3=Monster slot, 4=Extra Monster Zone
    ///
    /// Actions (Enter):
    ///   Queries available commands for the selected card.
    ///   Single command: executes directly. Multiple: opens action menu (Up/Down/Enter/Escape).
    ///
    /// Other keys:
    ///   V — Re-read current card (verbose with stats and available actions)
    ///   F — Field summary (card counts per zone)
    ///   P — Advance to next phase
    ///   Escape — Close action menu or exit navigation
    /// </summary>
    public class DuelFieldNavigator
    {
        #region Constants

        // Zone identifiers for Engine DLL_ functions
        // Discovered empirically via debug scan (NOT standard OCG constants).
        // Each field SLOT is its own locate value (holds 0 or 1 card).
        // Multi-card zones (hand, grave, deck) use a single locate with slot indices.
        //
        // Confirmed from log analysis:
        //   locate=2,3   → field slots (monsters confirmed here)
        //   locate=13    → Hand (multi-card)
        //   locate=14    → Extra Deck (multi-card, player only; opponent's is hidden)
        //   locate=15    → Deck (multi-card)
        //   locate=16    → Graveyard (multi-card, confirmed: destroyed Doron appeared here)
        //
        // Field slots are scanned as a range and classified by BasicVal.Type:
        //   Type != 0 → monster card
        //   Type == 0 → spell/trap card
        //
        // Confirmed locate assignments (same numbering for both players,
        // differentiated by the player parameter):
        //   2, 3, 4    → Monster zones (3 slots, Speed Duel)
        //   9, 10, 11  → Spell/Trap zones (3 slots; 9 and 10 confirmed from
        //                 CardSet events + RefreshZone scan; 11 presumed)
        //   5-8        → Unknown (possibly field spell, pendulum, extra monster,
        //                 or unused in Speed Duel format)
        private const int LocateHand = 13;
        private const int LocateExtra = 14;
        private const int LocateGrave = 16;
        private const int LocateDeck = 15;
        private const int LocateExtraMonster = 6; // Confirmed via Synchro summon
        private const int LocateFieldSpell = 12;  // Confirmed via field scan (cardId=4341 at loc=12)
        private const int LocateBanished = 17;    // Placeholder

        // Monster zone locates (left to right from player's perspective)
        private static readonly int[] MonsterLocates = { 2, 3, 4, LocateExtraMonster };
        // Spell/Trap zone locates (left to right)
        private static readonly int[] SpellLocates = { 9, 10, 11 };

        // Field slot locate range to scan (each holds 0 or 1 card).
        private const int FieldLocateMin = 1;
        private const int FieldLocateMax = 12;

        // DLL_DuelGetCardFace returns: 0=face-down, 1=face-up (boolean only)
        // ATK/DEF position determined from command mask TurnAtk/TurnDef bits
        private const uint CmdTurnAtk = 0x200;  // Can switch TO ATK → currently in DEF
        private const uint CmdTurnDef = 0x400;  // Can switch TO DEF → currently in ATK

        // Player identifiers (matches existing DuelEventAnnouncer convention)
        private const int PlayerMe = 0;
        private const int PlayerOpp = 1;

        #endregion

        #region Types

        /// <summary>Navigable zones on the duel field.</summary>
        public enum Zone
        {
            // Grid zones (participate in arrow navigation)
            Hand,
            MySpell,
            MyMonster,
            OppMonster,
            OppSpell,
            // Side zones (hotkey-only, not in arrow grid)
            MyFieldSpell,
            OppFieldSpell,
            MyGrave,
            OppGrave,
            MyBanished,
            OppBanished,
            MyExtra,
            OppExtra,
        }


        #endregion

        #region Fields

        /// <summary>Defines one row of the spatial grid.</summary>
        private struct GridRow
        {
            public Zone Zone;
            public int Player;
            public int[] Locates; // locate values per column (null for Hand)
        }

        /// <summary>
        /// Spatial grid rows, bottom to top. Row 0 = Hand, Row 4 = Opp Spells.
        /// Monster rows have 4 columns (3 main + EMZ), spell rows have 3.
        /// </summary>
        private static readonly GridRow[] GridRows =
        {
            new GridRow { Zone = Zone.Hand,       Player = PlayerMe,  Locates = null },
            new GridRow { Zone = Zone.MySpell,    Player = PlayerMe,  Locates = SpellLocates },
            new GridRow { Zone = Zone.MyMonster,  Player = PlayerMe,  Locates = MonsterLocates },
            new GridRow { Zone = Zone.OppMonster, Player = PlayerOpp, Locates = MonsterLocates },
            new GridRow { Zone = Zone.OppSpell,   Player = PlayerOpp, Locates = SpellLocates },
        };

        private const int RowHand = 0;
        private const int RowMySpell = 1;
        private const int RowMyMonster = 2;
        private const int RowOppMonster = 3;
        private const int RowOppSpell = 4;

        /// <summary>Set of grid zones for quick membership checks.</summary>
        private static readonly HashSet<Zone> GridZones = new()
        {
            Zone.Hand, Zone.MySpell, Zone.MyMonster, Zone.OppMonster, Zone.OppSpell
        };

        // Navigation state
        private Zone _currentZone = Zone.Hand;
        private int _currentRow = RowHand;
        private int _currentCol;
        private int _rememberedCol;
        private int _navIndex;
        private readonly List<int> _zoneSlots = new();   // slot index within the locate
        private readonly List<int> _zoneLocates = new(); // actual locate value per entry
        private bool _isNavigating;
        private bool _inSideZone; // true when in a hotkey-only zone (grave, banished, etc.)

        // Action menu state
        private bool _inActionMenu;
        private readonly List<CommandInfo> _commands = new();
        private int _cmdIndex;

        // Target selection state (attack targeting)
        private bool _awaitingTarget;
        private bool _useDirectCommand; // true = skip drag, use OnDoCardCommand(Attack)
        private int _attackerPlayer;
        private int _attackerLocate;
        private int _attackerSlot;

        // Card selection state (tribute, material, etc.)
        private bool _inCardSelect;
        private readonly List<int> _selectLocations = new();
        private int _selectIndex;

        // EmotionalList state (chain, graveyard pick, discard, deck search, etc.)
        private bool _inEmotionalList;
        private int _emoListIndex;
        private int _emoListCount;
        private bool _emoListHandled;

        /// <summary>Stores a command entry for the accessible action menu.</summary>
        private struct CommandInfo
        {
            public Il2CppYgomGame.Duel.Engine.CommandType Type;
            public string Label;
        }

        #endregion

        #region Properties

        /// <summary>Whether the action menu is currently open.</summary>
        public bool InActionMenu => _inActionMenu;

        /// <summary>Whether target selection mode is active (e.g., picking attack target).</summary>
        public bool InTargetMode => _awaitingTarget;

        /// <summary>Whether card selection mode is active (e.g., picking tributes).</summary>
        public bool InCardSelect => _inCardSelect;

        /// <summary>Whether EmotionalList selection is active (chain, grave pick, etc.).</summary>
        public bool InEmotionalList => _inEmotionalList;

        /// <summary>Whether the user has started field navigation.</summary>
        public bool IsNavigating => _isNavigating;

        #endregion

        #region Public Methods

        /// <summary>
        /// Debug: probes DLL_DuelGetCardNum for all locate values 0-25 on both
        /// players and logs any non-zero counts. Use to discover unknown locates
        /// (Extra Deck, banished pile, field spell, etc.) in a live duel.
        /// </summary>
        public static void DumpLocateCounts()
        {
            try
            {
                MelonLoader.MelonLogger.Msg("[FieldNav] === Locate count dump ===");
                for (int player = 0; player <= 1; player++)
                {
                    var found = new System.Text.StringBuilder();
                    for (int locate = 0; locate <= 25; locate++)
                    {
                        int count = 0;
                        try { count = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardNum(player, locate); }
                        catch { continue; }

                        if (count > 0)
                            found.Append($"loc={locate}:{count} ");
                    }
                    MelonLoader.MelonLogger.Msg(
                        $"[FieldNav] Player {player}: {(found.Length > 0 ? found.ToString() : "(none)")}");
                }
                MelonLoader.MelonLogger.Msg("[FieldNav] === End dump ===");
                ScreenReader.Say("Locate counts dumped to log");
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Msg($"[FieldNav] DumpLocateCounts error: {ex.Message}");
            }
        }

        /// <summary>Resets all navigation state. Call on duel end.</summary>
        public void Reset()
        {
            _currentZone = Zone.Hand;
            _currentRow = RowHand;
            _currentCol = 0;
            _rememberedCol = 0;
            _navIndex = 0;
            _zoneSlots.Clear();
            _zoneLocates.Clear();
            _isNavigating = false;
            _inSideZone = false;
            _awaitingTarget = false;
            _useDirectCommand = false;
            _inCardSelect = false;
            _selectLocations.Clear();
            CancelActionMenu();
        }

        /// <summary>Force-closes the action menu (e.g., when a dialog appears).</summary>
        public void CancelActionMenu()
        {
            _inActionMenu = false;
            _commands.Clear();
            _cmdIndex = 0;
        }

        /// <summary>
        /// Processes duel keys that don't conflict with dialog interaction
        /// (Up/Down/Enter/Escape are reserved for dialog navigation).
        /// Called when a dialog is active but field awareness is still needed.
        /// Handles: Tab (zones), P (phase), F (field summary), C (re-read card).
        /// </summary>
        public bool ProcessNonConflictingInput()
        {
            // Zone hotkeys work during dialogs (don't conflict with dialog keys)
            if (ProcessHotkeyInput())
                return true;

            if (InputManager.TryConsumeKeyDown(KeyCode.P))
            {
                AdvancePhase();
                return true;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.F))
            {
                ScreenReader.Say(GetFieldSummary());
                return true;
            }

            if (InputManager.TryConsumeKeyDown(KeyCode.V))
            {
                if (_isNavigating)
                    ReadCurrentCard(verbose: true);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Processes field navigation input. Returns true if a key was consumed.
        /// Called from DuelHandler each frame when no dialog is active.
        /// </summary>
        public bool ProcessInput()
        {
            // EmotionalList already active — handle first
            if (_inEmotionalList)
                return ProcessEmotionalListInput();
            // Card selection (tribute/material) takes highest priority for detection
            if (_inCardSelect)
                return ProcessCardSelectInput();
            if (CheckForCardSelection())
                return ProcessCardSelectInput();
            if (_awaitingTarget)
                return ProcessTargetSelectionInput();
            if (_inActionMenu)
                return ProcessActionMenuInput();
            // Detect EmotionalList only after ruling out other selection modes
            if (CheckForEmotionalList())
                return ProcessEmotionalListInput();
            return ProcessNavigationInput();
        }

        /// <summary>Gets a summary of cards on the field for all zones.</summary>
        public string GetFieldSummary()
        {
            // In debug mode, scan all possible locate values to discover correct constants
            if (Main.DebugMode)
                DumpLocateValues();

            try
            {
                int hand = GetCardCount(PlayerMe, LocateHand);
                CountFieldCards(PlayerMe, out int myMon, out int mySp);
                int myGr = GetCardCount(PlayerMe, LocateGrave);
                CountFieldCards(PlayerOpp, out int oppMon, out int oppSp);
                int oppGr = GetCardCount(PlayerOpp, LocateGrave);

                return Loc.Get("duel_field_summary",
                    hand, myMon, mySp, myGr, oppMon, oppSp, oppGr);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav", $"Summary error: {ex.Message}");
                return Loc.Get("duel_field_summary_error");
            }
        }

        /// <summary>
        /// Debug: scans locate values 0-255 for both players, logs any that return cards.
        /// Also tries DLL_DuelGetCardUniqueID with various position values.
        /// Press F in debug mode during a duel to trigger.
        /// </summary>
        private static void DumpLocateValues()
        {
            MelonLoader.MelonLogger.Msg("=== DuelFieldNavigator: Locate value scan ===");

            // Scan DLL_DuelGetCardNum with different locate values
            for (int player = 0; player <= 1; player++)
            {
                for (int loc = 0; loc <= 255; loc++)
                {
                    try
                    {
                        int count = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardNum(player, loc);
                        if (count > 0)
                        {
                            MelonLoader.MelonLogger.Msg(
                                $"  DLL_DuelGetCardNum(player={player}, locate={loc} [0x{loc:X2}]) = {count}");

                            // For non-zero counts, dump each card's ID, name, and BasicVal.Type
                            for (int s = 0; s < count && s < 5; s++)
                            {
                                try
                                {
                                    int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                                        player, loc, s);
                                    uint cardId = uid > 0
                                        ? Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(uid)
                                        : 0;
                                    string name = cardId > 0 ? ResolveCardName(cardId) : "(no name)";
                                    string typeInfo = "";
                                    try
                                    {
                                        var bv = new Il2CppYgomGame.Duel.Engine.BasicVal();
                                        Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardBasicVal(
                                            player, loc, s, ref bv);
                                        typeInfo = $", Type=0x{bv.Type:X4}, Atk={bv.Atk}, Def={bv.Def}, Lvl={bv.Level}, Rank={bv.Rank}";
                                    }
                                    catch { typeInfo = ", BasicVal=error"; }
                                    MelonLoader.MelonLogger.Msg(
                                        $"    -> slot {s}: uid={uid}, cardId={cardId}, name={name}{typeInfo}");
                                }
                                catch (Exception ex)
                                {
                                    MelonLoader.MelonLogger.Msg($"    -> slot {s} error: {ex.Message}");
                                }
                            }
                        }
                    }
                    catch { /* skip */ }
                }
            }

            // Also try GetCardUniqueID with position values that don't match locate
            MelonLoader.MelonLogger.Msg("--- UniqueID scan with position 0-20, slots 0-4 ---");
            for (int player = 0; player <= 1; player++)
            {
                for (int pos = 0; pos <= 20; pos++)
                {
                    for (int slot = 0; slot < 5; slot++)
                    {
                        try
                        {
                            int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                                player, pos, slot);
                            if (uid > 0)
                            {
                                uint cardId = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(uid);
                                string name = cardId > 0 ? ResolveCardName(cardId) : "(no name)";
                                string typeInfo = "";
                                try
                                {
                                    var bv = new Il2CppYgomGame.Duel.Engine.BasicVal();
                                    Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardBasicVal(
                                        player, pos, slot, ref bv);
                                    typeInfo = $", Type=0x{bv.Type:X4}, Atk={bv.Atk}, Def={bv.Def}, Lvl={bv.Level}, Rank={bv.Rank}";
                                }
                                catch { typeInfo = ", BasicVal=error"; }
                                MelonLoader.MelonLogger.Msg(
                                    $"  GetCardUniqueID(player={player}, pos={pos} [0x{pos:X2}], slot={slot}) = uid {uid}, cardId={cardId}, name={name}{typeInfo}");
                            }
                        }
                        catch { /* skip */ }
                    }
                }
            }

            MelonLoader.MelonLogger.Msg("=== End locate scan ===");
        }

        /// <summary>
        /// Debug: when command mask returns 0, dump TutorialWork state and scan alternatives.
        /// </summary>
        private static void DumpCommandMaskScan(int player, int locate, int slotIndex)
        {
            MelonLoader.MelonLogger.Msg(
                $"=== CommandMask scan: player={player} locate={locate} slot={slotIndex} ===");

            // Get the card's unique ID for reference
            int uid = 0;
            try
            {
                uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(player, locate, slotIndex);
                MelonLoader.MelonLogger.Msg($"  Card uniqueId={uid}");
            }
            catch { }

            // Dump TutorialWork comBit dictionary contents
            try
            {
                var tutWork = Il2CppYgomGame.Duel.DuelClient.GetTutorialWork();
                if (tutWork != null)
                {
                    MelonLoader.MelonLogger.Msg("  TutorialWork found. Dumping comBit dictionary:");
                    var comBit = tutWork.comBit;
                    if (comBit != null)
                    {
                        MelonLoader.MelonLogger.Msg($"    comBit has {comBit.Count} entries");
                        foreach (var kvp in comBit)
                        {
                            MelonLoader.MelonLogger.Msg(
                                $"    key={kvp.Key} (0x{kvp.Key:X}), mask={kvp.Value} (0x{kvp.Value:X})");
                        }
                    }
                    else
                    {
                        MelonLoader.MelonLogger.Msg("    comBit is null");
                    }

                    // Try GetCommandMask with various positions
                    MelonLoader.MelonLogger.Msg("  TutorialWork.GetCommandMask scan:");
                    for (int pos = 0; pos <= 20; pos++)
                    {
                        for (int idx = 0; idx <= 5; idx++)
                        {
                            try
                            {
                                uint tmask = tutWork.GetCommandMask(player, pos, idx);
                                if (tmask != 0)
                                    MelonLoader.MelonLogger.Msg(
                                        $"    GetCommandMask(p={player}, pos={pos}, idx={idx}) = 0x{tmask:X}");
                            }
                            catch { }
                        }
                    }
                }
                else
                {
                    MelonLoader.MelonLogger.Msg("  TutorialWork is null (not a tutorial duel)");
                }
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Msg($"  TutorialWork error: {ex.Message}");
            }

            // Engine.DLL_DuelComGetCommandMask scan
            MelonLoader.MelonLogger.Msg("  Engine.GetCommandMask scan:");
            try
            {
                uint mask = Il2CppYgomGame.Duel.Engine.DLL_DuelComGetCommandMask(
                    player, locate, slotIndex);
                MelonLoader.MelonLogger.Msg(
                    $"    Standard call (p={player}, pos={locate}, idx={slotIndex}) = 0x{mask:X}");
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Msg($"    Standard call THREW: {ex.Message}");
            }

            MelonLoader.MelonLogger.Msg("=== End CommandMask scan ===");
        }

        #endregion

        #region Navigation Input

        private bool ProcessNavigationInput()
        {
            // Zone hotkeys (C/M/S/T/G/B/D/L + Shift, 1-4)
            if (ProcessHotkeyInput())
                return true;

            // Up/Down: move between grid rows
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.UpArrow))
            {
                NavigateRow(1); // Up = toward opponent
                return true;
            }
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.DownArrow))
            {
                NavigateRow(-1); // Down = toward player
                return true;
            }

            // Left/Right: navigate within zone
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.LeftArrow))
            {
                NavigateCard(-1);
                return true;
            }
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.RightArrow))
            {
                NavigateCard(1);
                return true;
            }

            // Enter: open actions for selected card
            if (InputManager.TryConsumeKeyDown(KeyCode.Return))
            {
                if (_isNavigating)
                    OpenActions();
                else
                    ScreenReader.Say(Loc.Get("duel_nav_hint"));
                return true;
            }

            // V: re-read current card (verbose)
            if (InputManager.TryConsumeKeyDown(KeyCode.V))
            {
                if (_isNavigating)
                    ReadCurrentCard(verbose: true);
                else
                    ScreenReader.Say(Loc.Get("duel_nav_hint"));
                return true;
            }

            // F: field summary
            if (InputManager.TryConsumeKeyDown(KeyCode.F))
            {
                ScreenReader.Say(GetFieldSummary());
                return true;
            }

            // P: advance phase
            if (InputManager.TryConsumeKeyDown(KeyCode.P))
            {
                AdvancePhase();
                return true;
            }

            // Escape: exit navigation mode
            if (_isNavigating && InputManager.TryConsumeKeyDown(KeyCode.Escape))
            {
                _isNavigating = false;
                ScreenReader.Say(Loc.Get("duel_nav_closed"));
                return true;
            }

            return false;
        }

        #endregion

        #region Zone Navigation

        /// <summary>
        /// Moves cursor to a grid row (Up/Down). Preserves column via _rememberedCol.
        /// direction: +1 = Up (toward opponent), -1 = Down (toward player).
        /// </summary>
        private void NavigateRow(int direction)
        {
            if (!_isNavigating)
            {
                // First press: enter hand
                EnterGridRow(RowHand, 0);
                return;
            }

            if (_inSideZone)
            {
                // Return to last grid position from side zone
                _inSideZone = false;
                EnterGridRow(_currentRow, _currentCol);
                return;
            }

            int targetRow = _currentRow + direction;
            if (targetRow < 0 || targetRow >= GridRows.Length)
            {
                ScreenReader.Say(Loc.Get(direction > 0
                    ? "duel_grid_edge_top" : "duel_grid_edge_bottom"));
                return;
            }

            // Resolve column for target row
            int col = _rememberedCol;
            if (targetRow == RowHand)
            {
                // Clamp to hand card count
                RefreshZoneSlots(Zone.Hand);
                int handCount = _zoneSlots.Count;
                col = handCount > 0 ? Math.Min(col, handCount - 1) : 0;
            }
            else
            {
                int maxCol = GridRows[targetRow].Locates.Length - 1;
                col = Math.Min(col, maxCol);
            }

            EnterGridRow(targetRow, col);
        }

        /// <summary>Moves to a specific grid row and column, announces zone and card.</summary>
        private void EnterGridRow(int row, int col)
        {
            _isNavigating = true;
            _inSideZone = false;
            _currentRow = row;
            _currentZone = GridRows[row].Zone;

            RefreshCurrentZone();

            if (_currentZone == Zone.Hand)
            {
                // Hand: clamp col to card count
                _currentCol = _zoneSlots.Count > 0 ? Math.Min(col, _zoneSlots.Count - 1) : 0;
            }
            else
            {
                _currentCol = Math.Min(col, GridRows[row].Locates.Length - 1);
            }
            _navIndex = _currentCol;

            AnnounceZone();
            ReadCurrentCard(verbose: false, queued: true);
        }

        /// <summary>
        /// Moves cursor Left/Right within the current zone.
        /// Grid rows: columns wrap at edges. Side/stack zones: card index wraps.
        /// </summary>
        private void NavigateCard(int direction)
        {
            if (!_isNavigating)
            {
                // First press: enter hand
                EnterGridRow(RowHand, 0);
                return;
            }

            if (_inSideZone)
            {
                // Side zone: navigate card index within the stack
                RefreshCurrentZone();
                if (_zoneSlots.Count == 0)
                {
                    ScreenReader.Say(Loc.Get("duel_zone_empty"));
                    return;
                }
                _navIndex = (_navIndex + direction + _zoneSlots.Count) % _zoneSlots.Count;
                ReadCurrentCard(verbose: false);
                return;
            }

            RefreshCurrentZone();

            if (_currentZone == Zone.Hand)
            {
                // Hand: wrap through cards
                if (_zoneSlots.Count == 0)
                {
                    ScreenReader.Say(Loc.Get("duel_zone_empty"));
                    return;
                }
                _currentCol = (_currentCol + direction + _zoneSlots.Count) % _zoneSlots.Count;
                _navIndex = _currentCol;
                _rememberedCol = _currentCol;
                ReadCurrentCard(verbose: false);
            }
            else
            {
                // Grid row: wrap through columns
                int colCount = GridRows[_currentRow].Locates.Length;
                _currentCol = (_currentCol + direction + colCount) % colCount;
                _navIndex = _currentCol;
                _rememberedCol = _currentCol;
                ReadCurrentCard(verbose: false);
            }
        }

        /// <summary>Enters a side zone (hotkey-only, not in grid). Preserves grid position.</summary>
        private void EnterSideZone(Zone zone)
        {
            _isNavigating = true;
            _inSideZone = true;
            _currentZone = zone;
            _navIndex = 0;

            RefreshCurrentZone();
            AnnounceZone();
            if (_zoneSlots.Count > 0)
                ReadCurrentCard(verbose: false, queued: true);
        }

        private void AnnounceZone()
        {
            string zoneName = GetZoneName(_currentZone);
            int cardCount;
            if (IsFieldSlotZone(_currentZone))
            {
                // Count only occupied slots for the announcement
                int player = GetZonePlayer(_currentZone);
                cardCount = 0;
                foreach (int loc in _zoneLocates)
                {
                    try
                    {
                        int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                            player, loc, 0);
                        if (uid > 0) cardCount++;
                    }
                    catch { /* skip */ }
                }
            }
            else
            {
                cardCount = _zoneSlots.Count;
            }
            ScreenReader.Say(Loc.Get("duel_zone_entered", zoneName, cardCount));
        }

        /// <summary>Processes zone hotkeys. Returns true if a key was consumed.</summary>
        private bool ProcessHotkeyInput()
        {
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);

            // Zone jump hotkeys
            if (InputManager.TryConsumeKeyDown(KeyCode.C))
            {
                JumpToZone(Zone.Hand);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.M))
            {
                JumpToZone(shift ? Zone.OppMonster : Zone.MyMonster);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.S))
            {
                JumpToZone(shift ? Zone.OppSpell : Zone.MySpell);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.T))
            {
                JumpToZone(shift ? Zone.OppFieldSpell : Zone.MyFieldSpell);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.G))
            {
                JumpToZone(shift ? Zone.OppGrave : Zone.MyGrave);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.B))
            {
                JumpToZone(shift ? Zone.OppBanished : Zone.MyBanished);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.D))
            {
                JumpToZone(shift ? Zone.OppExtra : Zone.MyExtra);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.L))
            {
                ReadLP();
                return true;
            }

            // Number keys: direct monster slot access
            if (InputManager.TryConsumeKeyDown(KeyCode.Alpha1))
            {
                JumpToMonsterSlot(0);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Alpha2))
            {
                JumpToMonsterSlot(1);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Alpha3))
            {
                JumpToMonsterSlot(2);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Alpha4))
            {
                JumpToMonsterSlot(3); // Extra Monster Zone
                return true;
            }

            return false;
        }

        /// <summary>Jumps cursor to a zone. Grid zones set row/col; side zones preserve grid pos.</summary>
        private void JumpToZone(Zone zone)
        {
            if (GridZones.Contains(zone))
            {
                // Find the grid row for this zone
                for (int r = 0; r < GridRows.Length; r++)
                {
                    if (GridRows[r].Zone == zone)
                    {
                        int col = (zone == Zone.Hand) ? 0 : Math.Min(_rememberedCol,
                            GridRows[r].Locates.Length - 1);
                        EnterGridRow(r, col);
                        return;
                    }
                }
            }
            else
            {
                EnterSideZone(zone);
            }
        }

        /// <summary>Jumps to a specific column in the My Monster row.</summary>
        private void JumpToMonsterSlot(int col)
        {
            col = Math.Min(col, MonsterLocates.Length - 1);
            _rememberedCol = col;
            EnterGridRow(RowMyMonster, col);
        }

        /// <summary>Reads LP for both players without moving the cursor.</summary>
        private void ReadLP()
        {
            try
            {
                int myLP = Il2CppYgomGame.Duel.Engine.DLL_DuelGetLP(PlayerMe);
                int oppLP = Il2CppYgomGame.Duel.Engine.DLL_DuelGetLP(PlayerOpp);
                ScreenReader.Say(Loc.Get("duel_lp_read", myLP, oppLP));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav", $"ReadLP error: {ex.Message}");
                ScreenReader.Say(Loc.Get("duel_lp_read", 0, 0));
            }
        }

        #endregion

        #region Card Reading

        private void ReadCurrentCard(bool verbose, bool queued = false)
        {
            RefreshCurrentZone();
            if (_zoneSlots.Count == 0)
            {
                ScreenReader.Say(Loc.Get("duel_zone_empty"));
                return;
            }

            // Clamp index if zone shrank (card was destroyed during navigation)
            if (_navIndex >= _zoneSlots.Count)
                _navIndex = _zoneSlots.Count - 1;

            int slotIndex = _zoneSlots[_navIndex];
            int player = GetZonePlayer(_currentZone);
            int locate = _zoneLocates[_navIndex];

            try
            {
                int uniqueId = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                    player, locate, slotIndex);

                if (uniqueId <= 0)
                {
                    // Grid zone: announce "Slot N: Empty"
                    string emptyText = IsFieldSlotZone(_currentZone)
                        ? Loc.Get("duel_empty_slot_named", _navIndex + 1)
                        : Loc.Get("duel_card_position",
                            _navIndex + 1, _zoneSlots.Count, Loc.Get("duel_empty_slot"));
                    if (queued) ScreenReader.SayQueued(emptyText);
                    else ScreenReader.Say(emptyText);
                    return;
                }

                uint cardDbId = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(uniqueId);
                string cardName = ResolveCardName(cardDbId);

                bool isHand = _currentZone == Zone.Hand;
                bool isExtra = _currentZone == Zone.MyExtra || _currentZone == Zone.OppExtra;
                // Hand and Extra Deck share read semantics: no face, Content-DB stats.
                bool isContentView = isHand || isExtra;
                bool isMyCard = player == PlayerMe;
                bool isFieldCard = IsFieldSlotZone(_currentZone);

                // Determine actual card type from BasicVal (not zone label)
                bool isMonster = false;
                if (isFieldCard)
                {
                    try
                    {
                        var bv = new Il2CppYgomGame.Duel.Engine.BasicVal();
                        Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardBasicVal(
                            player, locate, slotIndex, ref bv);
                        isMonster = bv.Type != 0;
                    }
                    catch { isMonster = true; }
                }

                // --- Build announcement parts ---
                var parts = new List<string>();

                // Face/position info (field + grave; not hand/extra)
                if (!isContentView)
                {
                    // DLL_DuelGetCardFace: 0=face-down, 1=face-up (boolean)
                    int face = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardFace(
                        player, locate, slotIndex);
                    bool isFaceUp = face != 0;

                    // Don't reveal opponent's face-down card names
                    if (!isFaceUp && !isMyCard)
                        cardName = Loc.Get("duel_face_down_card");

                    parts.Add(isFaceUp
                        ? Loc.Get("duel_face_up")
                        : Loc.Get("duel_face_down"));

                    if (isMonster)
                    {
                        // Determine ATK/DEF position from command mask:
                        // TurnAtk available → currently DEF, TurnDef available → currently ATK.
                        // Face-down monsters are always in DEF (Yu-Gi-Oh rule).
                        bool isDefense = !isFaceUp; // face-down = always DEF
                        if (isFaceUp)
                        {
                            try
                            {
                                uint cmdMask = SafeGetCommandMask(player, locate, slotIndex);
                                if ((cmdMask & CmdTurnAtk) != 0)
                                    isDefense = true;  // can switch TO ATK → in DEF
                                // If TurnDef is set, isDefense stays false (in ATK)
                                // If neither, default to ATK (just summoned/already switched)
                            }
                            catch { }
                        }

                        parts.Add(isDefense
                            ? Loc.Get("duel_defense_position")
                            : Loc.Get("duel_attack_position"));

                        // Stats for face-up or own monsters
                        if (isFaceUp || isMyCard)
                        {
                            string stats = GetCardStatsText(player, locate, slotIndex, verbose);
                            if (stats != null)
                                parts.Add(stats);
                        }
                    }
                }
                else
                {
                    // Hand / Extra Deck cards: read level, ATK, DEF from Content database
                    string stats = GetHandCardStats(cardDbId);
                    if (stats != null)
                        parts.Add(stats);
                }

                // Position header: "1 of 3: Blue-Eyes White Dragon"
                string header = Loc.Get("duel_card_position",
                    _navIndex + 1, _zoneSlots.Count, cardName);

                // Combine header with detail parts
                string details = parts.Count > 0 ? string.Join(", ", parts) : "";
                string announcement = details.Length > 0
                    ? $"{header}. {details}"
                    : header;

                // Verbose: show card type, description, and available commands
                if (verbose)
                {
                    try
                    {
                        var content = Il2CppYgomGame.Card.Content.Instance;
                        if (content != null)
                        {
                            int mrk = (int)cardDbId;
                            var kind = content.GetKind(mrk);

                            // Show card type (spell/trap with icon, or monster kind)
                            string kindText = content.GetKindText(kind);
                            if (kind == Il2CppYgomGame.Card.Content.Kind.Magic
                                || kind == Il2CppYgomGame.Card.Content.Kind.Trap)
                            {
                                var icon = content.GetIcon(mrk);
                                if (icon != Il2CppYgomGame.Card.Content.Icon.Null)
                                    announcement += ". " + content.GetIconText(icon) + " " + kindText;
                                else
                                    announcement += ". " + kindText;
                            }
                            else
                            {
                                var attr = content.GetAttr(mrk);
                                var type = content.GetType(mrk);
                                announcement += ". " + content.GetAttributeText(attr)
                                    + " " + content.GetTypeText(type)
                                    + " " + kindText;
                            }

                            string desc = content.GetDesc(mrk);
                            if (!string.IsNullOrEmpty(desc))
                                announcement += ". " + desc;
                        }
                    }
                    catch { }

                    if (isMyCard)
                    {
                        uint cmdMask = SafeGetCommandMask(player, locate, slotIndex);
                        if (cmdMask != 0)
                        {
                            string cmds = DescribeCommandMask(cmdMask);
                            announcement += ". " + Loc.Get("duel_available_actions", cmds);
                        }
                    }
                }

                if (queued)
                    ScreenReader.SayQueued(announcement);
                else
                    ScreenReader.Say(announcement);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"ReadCard error zone={_currentZone} slot={slotIndex}: {ex.Message}");
                ScreenReader.Say(Loc.Get("duel_card_read_error"));
            }
        }

        private string GetCardStatsText(int player, int locate, int slotIndex, bool verbose)
        {
            try
            {
                var basicVal = new Il2CppYgomGame.Duel.Engine.BasicVal();
                Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardBasicVal(
                    player, locate, slotIndex, ref basicVal);

                string stats = Loc.Get("duel_card_stats", basicVal.Atk, basicVal.Def);

                if (verbose && (basicVal.Atk != basicVal.OrgAtk || basicVal.Def != basicVal.OrgDef))
                {
                    stats += ". " + Loc.Get("duel_card_original_stats",
                        basicVal.OrgAtk, basicVal.OrgDef);
                }

                return stats;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"GetCardStats error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Gets level, ATK, DEF for a hand card using the Content database.
        /// Returns null for spells/traps (no ATK/DEF).
        /// </summary>
        private string GetHandCardStats(uint cardDbId)
        {
            try
            {
                var content = Il2CppYgomGame.Card.Content.Instance;
                if (content == null) return null;

                int mrk = (int)cardDbId;
                var kind = content.GetKind(mrk);

                // Only monsters have ATK/DEF
                if (kind == Il2CppYgomGame.Card.Content.Kind.Magic
                    || kind == Il2CppYgomGame.Card.Content.Kind.Trap)
                    return null;

                int level = content.GetLevel(mrk);
                int atk = content.GetAtk(mrk);
                int def = content.GetDef2(mrk);

                return $"Level {level}, " + Loc.Get("duel_card_stats", atk, def);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"GetHandCardStats error: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Actions

        /// <summary>
        /// Simulates tapping the selected card via OnTapLocator.
        /// The game opens its native CardCommand popup (handled by DialogHandler
        /// or ScreenButtonHandler) or auto-executes single commands.
        /// </summary>
        private void OpenActions()
        {
            if (_zoneSlots.Count == 0 || _navIndex >= _zoneSlots.Count)
            {
                ScreenReader.Say(Loc.Get("duel_no_card_selected"));
                return;
            }

            int slotIndex = _zoneSlots[_navIndex];
            int player = GetZonePlayer(_currentZone);
            int locate = _zoneLocates[_navIndex];

            // Guard: check if slot is actually occupied (grid rows include empty slots)
            try
            {
                int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                    player, locate, slotIndex);
                if (uid <= 0)
                {
                    ScreenReader.Say(Loc.Get("duel_empty_slot_named", _navIndex + 1));
                    return;
                }
            }
            catch { /* proceed to command mask check */ }

            try
            {
                uint cmdMask = SafeGetCommandMask(player, locate, slotIndex);

                // Always log attack diagnostics for field monsters
                if (_currentZone == Zone.MyMonster)
                {
                    string inputType = "?";
                    int attackMask = 0;
                    try { inputType = Il2CppYgomGame.Duel.DuelClient.instance?.worker2d?
                        .curInputType.ToString() ?? "?"; } catch { }
                    try { attackMask = Il2CppYgomGame.Duel.Engine
                        .DLL_DuelGetAttackTargetMask(player, locate); } catch { }
                    MelonLogger.Msg($"[FieldNav][AttackDiag] zone={_currentZone} loc={locate} " +
                        $"slot={slotIndex} cmdMask=0x{cmdMask:X} attackTargetMask=0x{attackMask:X} " +
                        $"inputType={inputType}");
                }

                if (cmdMask == 0)
                {
                    if (Main.DebugMode)
                        DumpCommandMaskScan(player, locate, slotIndex);

                    // cmdMask=0 is normal for Defense Position monsters (can't attack)
                    // and for monsters that already attacked this turn.

                    // Fallback: if no commands but we're on our own field/extra card,
                    // try tapping it via OnTapLocator. This handles tribute/material
                    // selection where the game waits for taps, not commands, and also
                    // extra deck summon initiation (materials picked after tap).
                    if (_currentZone == Zone.MyMonster || _currentZone == Zone.MySpell
                        || _currentZone == Zone.MyExtra)
                    {
                        var duelClient = Il2CppYgomGame.Duel.DuelClient.instance;
                        if (duelClient != null)
                        {
                            DebugLogger.Log(LogCategory.Game, "FieldNav",
                                $"No commands — fallback tap: OnTapLocator({player}, {locate}, {slotIndex})");
                            TapFieldCard(duelClient, player, locate, slotIndex);

                            // Debug: dump SelectCardLocation state and scan for tribute UI
                            if (Main.DebugMode)
                                DumpCardSelectionState();
                            return;
                        }
                    }

                    ScreenReader.Say(Loc.Get("duel_no_actions"));
                    return;
                }

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"OpenActions: player={player} locate=0x{locate:X} slot={slotIndex} cmdMask=0x{cmdMask:X}");

                // Build the list of available commands
                BuildCommandList(cmdMask);

                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                if (client == null)
                {
                    ScreenReader.Say(Loc.Get("duel_action_error"));
                    return;
                }

                if (_commands.Count == 1 && !NeedsTarget(_commands[0].Type))
                {
                    // Single command that doesn't need a target: auto-execute.
                    // Must mirror ExecuteSelectedCommand's dispatch to handle
                    // field Action / direct commands correctly.
                    var cmdType = _commands[0].Type;
                    string label = _commands[0].Label;
                    ScreenReader.Say(label);

                    bool isFieldAction = cmdType == Il2CppYgomGame.Duel.Engine.CommandType.Action
                        && _currentZone != Zone.Hand;

                    if (IsTapOnlyCommand(cmdType))
                    {
                        // Decide/Select: just tap the card — no CardCommand popup
                        if (Main.DebugMode)
                            DumpCardSelectionState();
                        TapFieldCard(client, player, locate, slotIndex);
                    }
                    else if (IsDirectCommand(cmdType) || isFieldAction)
                    {
                        // Position changes / flip / field activation: OnDoCardCommand
                        // directly. The CardCommand popup path fails for these.
                        var worker = client.worker2d;
                        if (worker != null)
                        {
                            DebugLogger.Log(LogCategory.Game, "FieldNav",
                                $"Direct command (auto): OnDoCardCommand({cmdType}) for ({player}, {locate}, {slotIndex})");
                            worker.OnDoCardCommand(player, locate, slotIndex, cmdType);
                        }
                    }
                    else
                    {
                        TapCardForZone(client, player, locate, slotIndex);
                        MelonCoroutines.Start(AutoClickCommandButton(cmdType));
                    }
                }
                else
                {
                    // Multiple commands, or single command needing a target:
                    // enter accessible action menu so user can confirm
                    _inActionMenu = true;
                    _cmdIndex = 0;
                    AnnounceCommand();
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"OpenActions error: {ex.Message}");
                ScreenReader.Say(Loc.Get("duel_action_error"));
            }
        }

        /// <summary>
        /// Processes input while the action menu is open.
        /// Up/Down to navigate, Enter to execute, Escape to cancel.
        /// </summary>
        private bool ProcessActionMenuInput()
        {
            if (_commands.Count == 0)
            {
                _inActionMenu = false;
                ScreenReader.Say(Loc.Get("duel_no_actions"));
                return true;
            }

            if (InputManager.IsKeyDownOrRepeat(KeyCode.UpArrow))
            {
                _cmdIndex = (_cmdIndex - 1 + _commands.Count) % _commands.Count;
                AnnounceCommand();
                return true;
            }
            if (InputManager.IsKeyDownOrRepeat(KeyCode.DownArrow))
            {
                _cmdIndex = (_cmdIndex + 1) % _commands.Count;
                AnnounceCommand();
                return true;
            }
            if (InputManager.IsKeyDown(KeyCode.Return))
            {
                ExecuteSelectedCommand();
                return true;
            }
            if (InputManager.IsKeyDown(KeyCode.Escape))
            {
                CancelActionMenu();
                ScreenReader.Say(Loc.Get("duel_nav_closed"));
                return true;
            }
            return false;
        }

        /// <summary>Announces the currently highlighted command in the action menu.</summary>
        private void AnnounceCommand()
        {
            if (_cmdIndex < 0 || _cmdIndex >= _commands.Count) return;
            var cmd = _commands[_cmdIndex];
            ScreenReader.Say(Loc.Get("duel_card_position",
                _cmdIndex + 1, _commands.Count, cmd.Label));
        }

        /// <summary>Executes the currently selected command from the action menu.</summary>
        private void ExecuteSelectedCommand()
        {
            if (_cmdIndex < 0 || _cmdIndex >= _commands.Count) return;

            var cmdType = _commands[_cmdIndex].Type;
            int slotIndex = _zoneSlots[_navIndex];
            int player = GetZonePlayer(_currentZone);
            int locate = _zoneLocates[_navIndex];

            CancelActionMenu();

            if (NeedsTarget(cmdType))
            {
                // Enter target selection mode
                BeginTargetSelection(player, locate, slotIndex, cmdType);
                return;
            }

            var client = Il2CppYgomGame.Duel.DuelClient.instance;
            if (client == null)
            {
                ScreenReader.Say(Loc.Get("duel_action_error"));
                return;
            }

            // Action on field cards (monster effects) fails the OnTapLocator +
            // CardCommand popup flow — the popup never appears. Route through
            // OnDoCardCommand directly, like TurnAtk/TurnDef/Reverse. Hand cards
            // continue using the tap flow which works via HandCards.TapCard.
            bool isFieldAction = cmdType == Il2CppYgomGame.Duel.Engine.CommandType.Action
                && _currentZone != Zone.Hand;

            if (IsTapOnlyCommand(cmdType))
            {
                // Decide/Select: just tap the card — no CardCommand popup
                if (Main.DebugMode)
                    DumpCardSelectionState();
                TapFieldCard(client, player, locate, slotIndex);
            }
            else if (IsDirectCommand(cmdType) || isFieldAction)
            {
                // Position changes / flip / field activation: execute via
                // OnDoCardCommand directly.
                var worker = client.worker2d;
                if (worker != null)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"Direct command: OnDoCardCommand({cmdType}) for ({player}, {locate}, {slotIndex})");
                    worker.OnDoCardCommand(player, locate, slotIndex, cmdType);
                }
            }
            else
            {
                // During CheckChain, OnTapLocator doesn't create a CardCommand popup.
                // Use OnDoCardCommand directly to execute the selected action.
                var worker = client.worker2d;
                var inputType = Il2CppYgomGame.Duel.Engine.MenuActType.Null;
                try { inputType = worker?.curInputType ?? inputType; } catch { }

                if (inputType == Il2CppYgomGame.Duel.Engine.MenuActType.CheckChain)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"CheckChain: using OnDoCardCommand({cmdType}) for ({player}, {locate}, {slotIndex})");
                    worker.OnDoCardCommand(player, locate, slotIndex, cmdType);
                }
                else
                {
                    TapCardForZone(client, player, locate, slotIndex);
                    MelonCoroutines.Start(AutoClickCommandButton(cmdType));
                }
            }
        }

        /// <summary>
        /// Checks whether a command type requires the user to select a target.
        /// </summary>
        private static bool NeedsTarget(Il2CppYgomGame.Duel.Engine.CommandType cmd)
        {
            return cmd == Il2CppYgomGame.Duel.Engine.CommandType.Attack;
        }

        /// <summary>
        /// Checks whether a command should just tap the card directly via OnTapLocator
        /// without opening the CardCommand popup. Used for tribute material selection
        /// (Decide) where the game expects a direct field tap.
        /// </summary>
        private static bool IsTapOnlyCommand(Il2CppYgomGame.Duel.Engine.CommandType cmd)
        {
            return cmd == Il2CppYgomGame.Duel.Engine.CommandType.Decide;
        }

        /// <summary>
        /// Commands that can be executed directly via OnDoCardCommand without
        /// needing the CardCommand popup UI (position changes, flip summon, etc.).
        /// OnTapLocator + CardCommand popup doesn't register for these on face-down
        /// monsters — the popup never appears, so we bypass it entirely.
        /// </summary>
        private static bool IsDirectCommand(Il2CppYgomGame.Duel.Engine.CommandType cmd)
        {
            return cmd == Il2CppYgomGame.Duel.Engine.CommandType.TurnAtk
                || cmd == Il2CppYgomGame.Duel.Engine.CommandType.TurnDef
                || cmd == Il2CppYgomGame.Duel.Engine.CommandType.Reverse;
        }

        /// <summary>
        /// Enters target selection mode: stores attacker info, taps the attacker
        /// to initiate the command, then switches to opponent's monster zone
        /// for the user to pick a target.
        /// </summary>
        private void BeginTargetSelection(int player, int locate, int slotIndex,
            Il2CppYgomGame.Duel.Engine.CommandType cmdType)
        {
            _attackerPlayer = player;
            _attackerLocate = locate;
            _attackerSlot = slotIndex;
            _awaitingTarget = true;

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"BeginTargetSelection: attacker p={player} loc={locate} slot={slotIndex} cmd={cmdType}");

            // Don't call any game methods yet — wait for the user to pick a target.
            // The actual attack is a drag gesture: TapDown on attacker → TapUp on target.

            // Check if opponent has any monsters before entering target selection
            _currentZone = Zone.OppMonster;
            _currentRow = RowOppMonster;
            _currentCol = 0;
            _inSideZone = false;
            RefreshCurrentZone();

            // Count actual monsters (grid rows include empty slots)
            int oppMonsterCount = 0;
            int player2 = PlayerOpp;
            foreach (int loc in MonsterLocates)
            {
                try
                {
                    int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                        player2, loc, 0);
                    if (uid > 0) oppMonsterCount++;
                }
                catch { /* skip */ }
            }

            if (oppMonsterCount > 0)
            {
                _navIndex = 0;
                ScreenReader.Say(Loc.Get("duel_select_target"));
                ReadCurrentCard(verbose: false);
            }
            else
            {
                // No opponent monsters — direct attack automatically
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    "No opponent monsters — direct attack");
                ConfirmTargetSelection(directAttack: true);
            }
        }

        /// <summary>
        /// Processes input during target selection mode.
        /// Left/Right to pick target, Enter to confirm, Escape to cancel.
        /// </summary>
        private bool ProcessTargetSelectionInput()
        {
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.LeftArrow))
            {
                NavigateCard(-1);
                return true;
            }
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.RightArrow))
            {
                NavigateCard(1);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Return))
            {
                ConfirmTargetSelection(directAttack: false);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Escape))
            {
                _awaitingTarget = false;
                // Return cursor to attacker's position
                _currentZone = Zone.MyMonster;
                _currentRow = RowMyMonster;
                int atkCol = Array.IndexOf(MonsterLocates, _attackerLocate);
                _currentCol = atkCol >= 0 ? atkCol : 0;
                _rememberedCol = _currentCol;
                _navIndex = _currentCol;
                _inSideZone = false;
                ScreenReader.Say(Loc.Get("duel_target_cancelled"));
                return true;
            }
            // V to re-read target card
            if (InputManager.TryConsumeKeyDown(KeyCode.V))
            {
                ReadCurrentCard(verbose: true);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Confirms the attack by simulating a drag gesture via coroutine:
        /// OnTapDownField on attacker, wait frames, then OnTapUpField on target.
        /// </summary>
        private void ConfirmTargetSelection(bool directAttack)
        {
            _awaitingTarget = false;

            int targetSlot = (!directAttack && _zoneSlots.Count > 0)
                ? _zoneSlots[_navIndex] : 0;
            int targetLocate = (!directAttack && _zoneLocates.Count > 0)
                ? _zoneLocates[_navIndex] : 0;

            // Return cursor to player's monster zone after attack
            _currentZone = Zone.MyMonster;
            _currentRow = RowMyMonster;
            // Restore to attacker's column
            int atkCol = Array.IndexOf(MonsterLocates, _attackerLocate);
            _currentCol = atkCol >= 0 ? atkCol : 0;
            _rememberedCol = _currentCol;
            _navIndex = _currentCol;
            _inSideZone = false;

            if (_useDirectCommand)
            {
                // OnDoCardCommand path: declare attack, then select target.
                // OnDoCardCommand triggers BattleAttack + WaitInput(8) for target selection.
                // We then tap the target via OnTapLocator to confirm.
                _useDirectCommand = false;
                int tgtLoc = directAttack ? 0 : targetLocate;
                int tgtSlot = directAttack ? 0 : targetSlot;
                MelonCoroutines.Start(DirectCommandAttackSequence(
                    _attackerPlayer, _attackerLocate, _attackerSlot,
                    directAttack, tgtLoc, tgtSlot));
                return;
            }

            MelonCoroutines.Start(AttackDragSequence(
                _attackerPlayer, _attackerLocate, _attackerSlot,
                directAttack, targetLocate, targetSlot));
        }

        /// <summary>
        /// Coroutine: simulates attack drag gesture.
        /// For targeted attacks:
        ///   1. TapDownField on attacker → 2. OnSelectAttacked on target → 3. TapUpField
        /// For direct attacks:
        ///   Uses OnDoCardCommand to let the game engine resolve the attack as direct.
        /// </summary>
        private static IEnumerator AttackDragSequence(
            int atkPlayer, int atkLocate, int atkSlot,
            bool directAttack, int targetLocate, int targetSlot)
        {
            var client = Il2CppYgomGame.Duel.DuelClient.instance;
            var worker = client?.worker2d;
            if (worker == null)
            {
                ScreenReader.Say(Loc.Get("duel_action_error"));
                yield break;
            }

            // Clean stale attack state before starting a new attack
            worker.selectAttacked = false;
            worker.startTargeting = false;

            // Dump attack state before anything
            LogAttackState(worker, "before TapDownField");

            // Step 1: TapDown on attacker (sets attackingMonster)
            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"Attack step 1: TapDownField({atkPlayer}, {atkLocate}, {atkSlot})");
            worker.OnTapDownField(atkPlayer, atkLocate, atkSlot);

            // Force targeting flags — tutorial system prevents natural activation
            worker.selectAttacked = true;
            worker.startTargeting = true;

            LogAttackState(worker, "after TapDownField + force flags");

            for (int i = 0; i < 5; i++)
                yield return null;

            if (directAttack)
            {
                // Direct attack: no opponent monsters to target.
                // DLL_DuelGetAttackTargetMask returns 0x80 (bit 7) for direct attacks,
                // vs 0x4 (bit 2) for targeted attacks.
                // The mask bit positions map to the position parameter in OnSelectAttacked.
                // So direct attack uses position=7, targeted uses the target's locate value.
                const int DirectAttackPosition = 7;

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"Attack step 2 (direct): OnSelectAttacked({PlayerOpp}, {DirectAttackPosition}, 0)");
                worker.OnSelectAttacked(PlayerOpp, DirectAttackPosition, 0);

                LogAttackState(worker, "after OnSelectAttacked (direct pos=7)");

                for (int i = 0; i < 5; i++)
                    yield return null;

                LogAttackState(worker, "after wait (direct)");

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"Attack step 3 (direct): TapUpField({PlayerOpp}, {DirectAttackPosition}, 0)");
                worker.OnTapUpField(PlayerOpp, DirectAttackPosition, 0);

                LogAttackState(worker, "after TapUpField (direct)");

                // Fallback: if drag sequence didn't register, try OnDoCardCommand
                bool directDragWorked = false;
                try { directDragWorked = worker.autoAttack; } catch { }
                if (!directDragWorked)
                {
                    MelonLogger.Msg("[FieldNav] Direct drag failed (autoAttack=false), " +
                        $"trying OnDoCardCommand(Attack) for loc={atkLocate}");
                    try
                    {
                        worker.OnDoCardCommand(atkPlayer, atkLocate, atkSlot,
                            Il2CppYgomGame.Duel.Engine.CommandType.Attack);
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Msg($"[FieldNav] OnDoCardCommand(Attack) error: {ex.Message}");
                    }
                }

                ScreenReader.Say(Loc.Get("duel_direct_attack"));
            }
            else
            {
                // Targeted attack: select the specific opponent monster
                int tgtSlot = targetSlot;

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"Attack step 2: OnSelectAttacked({PlayerOpp}, {targetLocate}, {tgtSlot})");
                worker.OnSelectAttacked(PlayerOpp, targetLocate, tgtSlot);

                LogAttackState(worker, "after OnSelectAttacked");

                for (int i = 0; i < 5; i++)
                    yield return null;

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"Attack step 3: TapUpField({PlayerOpp}, {targetLocate}, {tgtSlot})");
                worker.OnTapUpField(PlayerOpp, targetLocate, tgtSlot);

                LogAttackState(worker, "after TapUpField (targeted)");

                // Fallback: if drag sequence didn't register (autoAttack still false),
                // try OnDoCardCommand(Attack). This handles cases where the engine
                // gates OnSelectAttacked (e.g. equipped monsters breaking cmdMask).
                bool dragWorked = false;
                try { dragWorked = worker.autoAttack; } catch { }
                if (!dragWorked)
                {
                    MelonLogger.Msg("[FieldNav] Drag sequence failed (autoAttack=false), " +
                        $"trying OnDoCardCommand(Attack) for loc={atkLocate}");
                    try
                    {
                        worker.OnDoCardCommand(atkPlayer, atkLocate, atkSlot,
                            Il2CppYgomGame.Duel.Engine.CommandType.Attack);
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Msg($"[FieldNav] OnDoCardCommand(Attack) error: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Coroutine: attack via OnDoCardCommand for when drag sequence fails
        /// (e.g. equipped monsters breaking cmdMask). Declares the attack, waits
        /// for the engine to enter target selection, then taps the target.
        /// </summary>
        private static IEnumerator DirectCommandAttackSequence(
            int atkPlayer, int atkLocate, int atkSlot,
            bool directAttack, int targetLocate, int targetSlot)
        {
            var client = Il2CppYgomGame.Duel.DuelClient.instance;
            var worker = client?.worker2d;
            if (worker == null)
            {
                ScreenReader.Say(Loc.Get("duel_action_error"));
                yield break;
            }

            // Force all worker2d state that TapDownField normally sets.
            // TapDownField fails when cmdMask is broken (equipped monsters),
            // but OnSelectAttacked needs this state to commit the battle.
            worker.attackingMonster = atkLocate;
            worker.selectAttacked = true;
            worker.startTargeting = true;
            worker.isTapDowned = true;
            worker.attackDrag = true;

            LogAttackState(worker, "after manual flag setup");

            for (int i = 0; i < 5; i++)
                yield return null;

            if (directAttack)
            {
                MelonLogger.Msg($"[FieldNav] DirectCommand: OnSelectAttacked({PlayerOpp}, 7, 0)");
                worker.OnSelectAttacked(PlayerOpp, 7, 0);
                LogAttackState(worker, "after OnSelectAttacked (direct)");

                for (int i = 0; i < 5; i++)
                    yield return null;

                worker.OnTapUpField(PlayerOpp, 7, 0);
            }
            else
            {
                MelonLogger.Msg($"[FieldNav] DirectCommand: " +
                    $"OnSelectAttacked({PlayerOpp}, {targetLocate}, {targetSlot})");
                worker.OnSelectAttacked(PlayerOpp, targetLocate, targetSlot);
                LogAttackState(worker, "after OnSelectAttacked (targeted)");

                for (int i = 0; i < 5; i++)
                    yield return null;

                worker.OnTapUpField(PlayerOpp, targetLocate, targetSlot);
            }

            LogAttackState(worker, "after TapUpField");
        }

        /// <summary>Dumps all attack-related worker2d fields for diagnostics.</summary>
        private static void LogAttackState(
            Il2CppYgomGame.Duel.RunEffectWorker2D worker, string label,
            int atkLocate = 0)
        {
            try
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"=== AttackState ({label}) ===");
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"  attackingMonster={worker.attackingMonster}");
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"  attackedMonster={worker.attackedMonster}");
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"  selectAttacked={worker.selectAttacked}");
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"  startTargeting={worker.startTargeting}");
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"  autoAttack={worker.autoAttack}");
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"  attackDrag={worker.attackDrag}");
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"  isTapDowned={worker.isTapDowned}");
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"  curInputType={worker.curInputType}");

                // Query engine for valid attack targets (bitmask)
                try
                {
                    int targetMask = Il2CppYgomGame.Duel.Engine.DLL_DuelGetAttackTargetMask(
                        PlayerMe, atkLocate);
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"  attackTargetMask={targetMask} (0x{targetMask:X})");
                }
                catch { /* DLL method may not be available in all states */ }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"  LogAttackState error: {ex.Message}");
            }
        }

        #endregion

        #region EmotionalList (Chain / Graveyard / Discard / Deck Search)

        /// <summary>
        /// Checks whether the game's EmotionalList is actively presenting a card selection.
        /// </summary>
        public bool CheckForEmotionalList()
        {
            if (_inEmotionalList) return true;

            try
            {
                var emoList = Il2CppYgomGame.Duel.EmotionalList.Instance;
                if (emoList == null) return false;

                bool goActive = false;
                bool isClosing = false;
                int selectMax = 0;

                try { var go = emoList.gameObject; goActive = go != null && go.activeInHierarchy; } catch { }
                try { isClosing = emoList.isClosing; } catch { }
                try { selectMax = emoList.selectMaxNum; } catch { }

                // List inactive — reset handled flag so next activation is detected
                if (!goActive || isClosing || selectMax <= 0)
                {
                    _emoListHandled = false;
                    return false;
                }

                // Don't re-enter if we already handled this active list instance
                if (_emoListHandled) return false;

                var items = emoList.itemList;
                if (items == null || items.Count == 0) return false;

                EnterEmotionalList(emoList);
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"EmotionalList check error: {ex.Message}");
                return false;
            }
        }

        private void EnterEmotionalList(Il2CppYgomGame.Duel.EmotionalList emoList)
        {
            _inEmotionalList = true;
            _emoListIndex = 0;

            var items = emoList.itemList;
            _emoListCount = items?.Count ?? 0;

            int selectMax = 0;
            try { selectMax = emoList.selectMaxNum; } catch { }

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"EmotionalList: entered with {_emoListCount} items, selectMax={selectMax}");

            string prompt = selectMax > 1
                ? Loc.Get("duel_emo_list_multi", _emoListCount, selectMax)
                : Loc.Get("duel_emo_list_single", _emoListCount);

            ScreenReader.Say(prompt);

            if (_emoListCount > 0)
                ReadEmotionalListCard(0);
        }

        private bool ProcessEmotionalListInput()
        {
            int selectMax = 0;
            try
            {
                var emoList = Il2CppYgomGame.Duel.EmotionalList.Instance;
                if (emoList == null)
                {
                    _inEmotionalList = false;
                    return false;
                }

                bool goActive = false;
                bool isClosing = false;
                try { var go = emoList.gameObject; goActive = go != null && go.activeInHierarchy; } catch { }
                try { isClosing = emoList.isClosing; } catch { }
                try { selectMax = emoList.selectMaxNum; } catch { }

                if (!goActive || isClosing || selectMax <= 0)
                {
                    _inEmotionalList = false;
                    _emoListHandled = false;
                    return false;
                }

                _emoListCount = emoList.itemList?.Count ?? 0;
            }
            catch
            {
                _inEmotionalList = false;
                return false;
            }

            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.LeftArrow))
            {
                if (_emoListCount > 0)
                {
                    _emoListIndex = (_emoListIndex - 1 + _emoListCount) % _emoListCount;
                    // Multi-select: scroll only (don't toggle). Single-select: SelectIndex moves highlight.
                    if (selectMax > 1)
                        ScrollEmotionalList(_emoListIndex);
                    else
                        SelectEmotionalListCard(_emoListIndex);
                    ReadEmotionalListCard(_emoListIndex);
                }
                return true;
            }
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.RightArrow))
            {
                if (_emoListCount > 0)
                {
                    _emoListIndex = (_emoListIndex + 1) % _emoListCount;
                    if (selectMax > 1)
                        ScrollEmotionalList(_emoListIndex);
                    else
                        SelectEmotionalListCard(_emoListIndex);
                    ReadEmotionalListCard(_emoListIndex);
                }
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Return))
            {
                if (selectMax > 1)
                    ToggleEmotionalListCard();
                else
                    ConfirmEmotionalList();
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Space))
            {
                if (selectMax > 1)
                    ConfirmEmotionalList();
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Escape))
            {
                CancelEmotionalList();
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.V))
            {
                if (_emoListCount > 0)
                    ReadEmotionalListCard(_emoListIndex, verbose: true);
                return true;
            }
            return false;
        }

        /// <summary>Scrolls the EmotionalList to show the card at index without toggling selection.</summary>
        private void ScrollEmotionalList(int index)
        {
            try
            {
                var emoList = Il2CppYgomGame.Duel.EmotionalList.Instance;
                emoList?.ScrollCardList(index, true);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"ScrollEmotionalList error: {ex.Message}");
            }
        }

        private void SelectEmotionalListCard(int index)
        {
            try
            {
                var emoList = Il2CppYgomGame.Duel.EmotionalList.Instance;
                emoList?.SelectIndex(index, false);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"SelectEmotionalListCard error: {ex.Message}");
            }
        }

        private void ReadEmotionalListCard(int index, bool verbose = false)
        {
            if (index < 0 || index >= _emoListCount) return;

            try
            {
                var emoList = Il2CppYgomGame.Duel.EmotionalList.Instance;
                if (emoList == null) return;

                var items = emoList.itemList;
                if (items == null || index >= items.Count) return;

                var item = items[index];
                if (item == null)
                {
                    ScreenReader.Say(Loc.Get("duel_card_select_item",
                        index + 1, _emoListCount, Loc.Get("duel_unknown_card")));
                    return;
                }

                int mixedId = item.mixedId;
                int uid = 0;
                int cid = 0;
                try { uid = item.uniqueId; } catch { }
                try { cid = item.cid; } catch { }

                // Resolve card DB ID. Field cards (tribute material) store the
                // runtime uniqueId in mixedId; hand/grave cards often store the
                // card DB ID directly. Try uniqueId lookup first, then fall back
                // to treating mixedId as a card DB ID.
                int cardDbId = 0;
                if (uid > 0)
                {
                    try
                    {
                        uint resolved = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(uid);
                        if (resolved > 0 && resolved < 100000)
                            cardDbId = (int)resolved;
                    }
                    catch { }
                }
                // Try mixedId as a uniqueId (field cards in tribute/material selection)
                if (cardDbId == 0 && mixedId > 0)
                {
                    try
                    {
                        uint resolved = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(mixedId);
                        if (resolved > 0 && resolved < 100000)
                            cardDbId = (int)resolved;
                    }
                    catch { }
                }
                if (cardDbId == 0 && cid > 0 && cid < 100000)
                    cardDbId = cid;
                // Last resort: mixedId as a direct card DB ID (hand/grave discard lists)
                if (cardDbId == 0 && mixedId > 0 && mixedId < 100000)
                    cardDbId = mixedId;

                MelonLoader.MelonLogger.Msg(
                    $"[FieldNav] EmoCard[{index}]: mixedId={mixedId} uniqueId={uid} cid={cid} resolved={cardDbId}");

                string info = cardDbId > 0
                    ? (verbose ? CardFormatter.FormatVerbose(cardDbId) : CardFormatter.FormatCompact(cardDbId))
                    : Loc.Get("duel_unknown_card");

                ScreenReader.Say(Loc.Get("duel_card_select_item",
                    index + 1, _emoListCount, info));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"ReadEmotionalListCard error: {ex.Message}");
                ScreenReader.Say(Loc.Get("duel_card_select_item",
                    index + 1, _emoListCount, Loc.Get("duel_unknown_card")));
            }
        }

        /// <summary>
        /// Toggles selection on the current card for multi-select EmotionalList.
        /// SelectIndex toggles the card in the game's selectedList.
        /// </summary>
        private void ToggleEmotionalListCard()
        {
            try
            {
                var emoList = Il2CppYgomGame.Duel.EmotionalList.Instance;
                if (emoList == null) return;

                emoList.SelectIndex(_emoListIndex, false);

                int selectedCount = 0;
                int selectMax = 0;
                try { selectedCount = emoList.selectedList?.Count ?? 0; } catch { }
                try { selectMax = emoList.selectMaxNum; } catch { }

                ScreenReader.Say(Loc.Get("duel_emo_list_toggled", selectedCount, selectMax));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"ToggleEmotionalListCard error: {ex.Message}");
            }
        }

        private void ConfirmEmotionalList()
        {
            try
            {
                var emoList = Il2CppYgomGame.Duel.EmotionalList.Instance;
                if (emoList == null)
                {
                    ScreenReader.Say(Loc.Get("duel_action_error"));
                    _inEmotionalList = false;
                    return;
                }

                // For multi-select, check minimum selection requirement
                int selectMax = 0;
                int selectMin = 0;
                try { selectMax = emoList.selectMaxNum; } catch { }
                try { selectMin = emoList.selectMinNum; } catch { }

                if (selectMax > 1)
                {
                    int selectedCount = 0;
                    try { selectedCount = emoList.selectedList?.Count ?? 0; } catch { }
                    if (selectedCount < selectMin)
                    {
                        ScreenReader.Say(Loc.Get("duel_emo_list_need_more", selectMin));
                        return;
                    }
                }
                else
                {
                    // Single select: previous approach set selectedList=[idx] and
                    // called OnDecide, but OnDecide doesn't read selectedList alone
                    // — it uses the internal state updated by OnClickCard. Route
                    // through OnClickCard with the ListCard at our index, which is
                    // exactly what the game invokes on a physical tap.
                    try
                    {
                        var cards = emoList.cardList;
                        if (cards != null && _emoListIndex < cards.Count)
                        {
                            var listCard = cards[_emoListIndex];
                            MelonLoader.MelonLogger.Msg(
                                $"[FieldNav] Confirm: _emoListIndex={_emoListIndex}, " +
                                $"calling OnClickCard(cardList[{_emoListIndex}])");
                            emoList.OnClickCard(listCard);
                        }
                        else
                        {
                            MelonLoader.MelonLogger.Msg(
                                $"[FieldNav] Confirm: cardList null or index out of range " +
                                $"(_emoListIndex={_emoListIndex} count={cards?.Count ?? 0})");
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLoader.MelonLogger.Msg(
                            $"[FieldNav] OnClickCard error: {ex.Message}");
                    }
                }

                // Announce what's being picked (with card name)
                string pickedName = GetEmoCardName(_emoListIndex);
                ScreenReader.Say(Loc.Get("duel_card_picked", pickedName));

                // For multi-select, OnDecide is still required. For single-select,
                // OnClickCard may auto-confirm — but OnDecide is idempotent/safe.
                emoList.OnDecide();

                _inEmotionalList = false;
                _emoListHandled = true;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"ConfirmEmotionalList error: {ex.Message}");
                ScreenReader.Say(Loc.Get("duel_action_error"));
            }
        }

        /// <summary>Resolves the display name of the EmotionalList card at the given index.</summary>
        private string GetEmoCardName(int index)
        {
            try
            {
                var emoList = Il2CppYgomGame.Duel.EmotionalList.Instance;
                var items = emoList?.itemList;
                if (items == null || index < 0 || index >= items.Count)
                    return Loc.Get("duel_unknown_card");

                var item = items[index];
                if (item == null) return Loc.Get("duel_unknown_card");

                int mixedId = item.mixedId;
                int uid = 0, cid = 0;
                try { uid = item.uniqueId; } catch { }
                try { cid = item.cid; } catch { }

                int cardDbId = 0;
                if (uid > 0)
                {
                    try
                    {
                        uint r = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(uid);
                        if (r > 0 && r < 100000) cardDbId = (int)r;
                    }
                    catch { }
                }
                if (cardDbId == 0 && mixedId > 0)
                {
                    try
                    {
                        uint r = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(mixedId);
                        if (r > 0 && r < 100000) cardDbId = (int)r;
                    }
                    catch { }
                }
                if (cardDbId == 0 && cid > 0 && cid < 100000) cardDbId = cid;
                if (cardDbId == 0 && mixedId > 0 && mixedId < 100000) cardDbId = mixedId;

                if (cardDbId > 0)
                    return CardFormatter.GetName(cardDbId);
            }
            catch { }
            return Loc.Get("duel_unknown_card");
        }

        private void CancelEmotionalList()
        {
            try
            {
                var emoList = Il2CppYgomGame.Duel.EmotionalList.Instance;
                if (emoList != null && emoList.cancelable)
                {
                    emoList.OnCancel();
                    ScreenReader.Say(Loc.Get("duel_action_cancelled"));
                }
                else
                {
                    ScreenReader.Say(Loc.Get("duel_cannot_cancel"));
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"CancelEmotionalList error: {ex.Message}");
            }

            _inEmotionalList = false;
            _emoListHandled = true;
        }

        #endregion

        #region Card Selection (Tribute / Material)

        /// <summary>
        /// Checks whether the game's SelectCardLocation UI has opened (tribute selection,
        /// material selection, etc.). If so, enters card selection mode.
        /// </summary>
        public bool CheckForCardSelection()
        {
            if (_inCardSelect) return true;

            try
            {
                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                var worker = client?.worker2d;
                if (worker == null) return false;

                // Try multiple detection methods:
                // 1. DuelClient static method (global check)
                bool dcIsOpen = false;
                try { dcIsOpen = Il2CppYgomGame.Duel.DuelClient.IsOpenSelectCardLocation(); }
                catch { }

                // 2. Worker2D instance method
                bool w2dIsOpen = false;
                try { w2dIsOpen = worker.IsOpenSelectCardLocation(); }
                catch { }

                // 3. Direct component check
                var scl = worker.selectCardLocation;
                bool sclIsOpen = false;
                bool sclIsActive = false;
                try
                {
                    if (scl != null)
                    {
                        sclIsOpen = scl.IsOpen;
                        sclIsActive = scl.IsActive;
                    }
                }
                catch { }

                // 4. Try finding the component in the scene if not on worker
                if (scl == null)
                {
                    try
                    {
                        scl = UnityEngine.Object.FindObjectOfType<
                            Il2CppYgomGame.Duel.SelectCardLocation>();
                    }
                    catch { }

                    if (scl != null)
                    {
                        try
                        {
                            sclIsOpen = scl.IsOpen;
                            sclIsActive = scl.IsActive;
                        }
                        catch { }
                    }
                }

                bool anyOpen = dcIsOpen || w2dIsOpen || sclIsOpen;

                if (Main.DebugMode && (anyOpen || sclIsActive))
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"CardSelect state: DC.IsOpen={dcIsOpen} w2d.IsOpen={w2dIsOpen}" +
                        $" scl.IsOpen={sclIsOpen} scl.IsActive={sclIsActive}" +
                        $" scl null={scl == null}");
                }

                if (!anyOpen) return false;
                if (scl == null) return false;

                EnterCardSelectMode(scl);
                return true;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"CardSelect check error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Enters card selection mode: reads the selectable locations from the
        /// game's SelectCardLocation component, announces the prompt, and
        /// lets the user navigate and pick.
        /// </summary>
        private void EnterCardSelectMode(Il2CppYgomGame.Duel.SelectCardLocation scl)
        {
            _inCardSelect = true;
            _inActionMenu = false;
            _awaitingTarget = false;
            _selectLocations.Clear();
            _selectIndex = 0;

            var locations = scl.selectLocationList;
            if (locations != null)
            {
                for (int i = 0; i < locations.Count; i++)
                    _selectLocations.Add(locations[i]);
            }

            string infoText = "";
            try { infoText = scl.GetInfoText() ?? ""; } catch { }

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"CardSelect: entered with {_selectLocations.Count} locations, info='{infoText}'");

            // Log each location for debugging the encoding
            foreach (var loc in _selectLocations)
            {
                try
                {
                    int p = Il2CppYgomGame.Duel.SelectCardLocation.LocationToPlayer(loc);
                    int pos = Il2CppYgomGame.Duel.SelectCardLocation.LocationToPosition(loc);
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"  Location {loc} (0x{loc:X}): player={p} position={pos} (0x{pos:X})");
                }
                catch (Exception ex)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"  Location {loc}: decode error: {ex.Message}");
                }
            }

            string prompt = string.IsNullOrEmpty(infoText)
                ? Loc.Get("duel_select_card")
                : infoText;

            ScreenReader.Say(Loc.Get("duel_card_select_prompt",
                prompt, _selectLocations.Count));

            if (_selectLocations.Count > 0)
                ReadSelectableCard(_selectIndex);
        }

        /// <summary>
        /// Processes input during card selection mode.
        /// Left/Right to navigate choices, Enter to select, Escape to cancel.
        /// </summary>
        private bool ProcessCardSelectInput()
        {
            // Check if selection UI is still open
            try
            {
                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                var worker = client?.worker2d;
                if (worker == null || !worker.IsOpenSelectCardLocation())
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        "CardSelect: selection closed externally");
                    _inCardSelect = false;
                    return false;
                }
            }
            catch
            {
                _inCardSelect = false;
                return false;
            }

            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.LeftArrow))
            {
                if (_selectLocations.Count > 0)
                {
                    _selectIndex = (_selectIndex - 1 + _selectLocations.Count)
                        % _selectLocations.Count;
                    ReadSelectableCard(_selectIndex);
                }
                return true;
            }
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.RightArrow))
            {
                if (_selectLocations.Count > 0)
                {
                    _selectIndex = (_selectIndex + 1) % _selectLocations.Count;
                    ReadSelectableCard(_selectIndex);
                }
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Return))
            {
                ConfirmCardSelect();
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Escape))
            {
                CancelCardSelect();
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.V))
            {
                if (_selectLocations.Count > 0)
                    ReadSelectableCard(_selectIndex);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Reads the card at the given index in the selectable locations list.
        /// Tries to resolve the card name from the encoded location.
        /// </summary>
        private void ReadSelectableCard(int index)
        {
            if (index < 0 || index >= _selectLocations.Count) return;

            int loc = _selectLocations[index];
            string cardInfo = ResolveCardAtLocation(loc);

            ScreenReader.Say(Loc.Get("duel_card_select_item",
                index + 1, _selectLocations.Count, cardInfo));
        }

        /// <summary>
        /// Resolves a card name and info from a SelectCardLocation location int.
        /// Uses LocationToPlayer/LocationToPosition to decode, then queries Engine.
        /// </summary>
        private static string ResolveCardAtLocation(int location)
        {
            try
            {
                int player = Il2CppYgomGame.Duel.SelectCardLocation.LocationToPlayer(location);
                int position = Il2CppYgomGame.Duel.SelectCardLocation.LocationToPosition(location);

                // Always log raw values for debugging tribute/selection issues
                MelonLoader.MelonLogger.Msg(
                    $"[FieldNav] ResolveCard: loc={location} (0x{location:X}) -> player={player} pos={position} (0x{position:X})");

                // Try position as direct field locate with slot 0
                string name = TryGetCardName(player, position, 0);
                if (name != null) return name;

                // Position might encode a slot index for multi-card zones (hand, grave).
                // For hand cards: locate=13, slot=position value
                // For field cards: the locate IS the position (2,3,4 for monsters, 9,10,11 for spells)
                if (position >= 0 && position < 10)
                {
                    // Try as hand slot
                    name = TryGetCardName(player, LocateHand, position);
                    if (name != null) return name;
                }

                // Scan field monster and spell zones
                for (int loc = FieldLocateMin; loc <= FieldLocateMax; loc++)
                {
                    name = TryGetCardName(player, loc, 0);
                    if (name != null) return name;
                }

                // Fallback: describe the position
                string zoneName = position switch
                {
                    >= FieldLocateMin and <= FieldLocateMax =>
                        player == PlayerMe ? "Your field" : "Opponent field",
                    13 => "Hand",
                    15 => "Deck",
                    16 => player == PlayerMe ? "Your graveyard" : "Opponent graveyard",
                    _ => $"Position {position}"
                };
                return zoneName;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"ResolveCard error: {ex.Message}");
                return $"Card {location}";
            }
        }

        /// <summary>
        /// Tries to get a card name at the given player/zone/slot. Returns null if none found.
        /// </summary>
        private static string TryGetCardName(int player, int zone, int slot)
        {
            try
            {
                int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(player, zone, slot);
                if (uid <= 0) return null;

                uint cardId = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(uid);
                if (cardId == 0) return null;

                string name = ResolveCardName(cardId);
                if (string.IsNullOrEmpty(name)) return null;

                // Try to get stats for monster cards
                try
                {
                    var bv = new Il2CppYgomGame.Duel.Engine.BasicVal();
                    Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardBasicVal(
                        player, zone, slot, ref bv);
                    if (bv.Atk >= 0)
                        return $"{name}, ATK {bv.Atk}, DEF {bv.Def}";
                }
                catch { }

                return name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Confirms the currently highlighted card selection via OnClickZoneDirect.
        /// Does not exit card select mode — the game may require multiple selections
        /// (e.g., 2 tributes for a level 7+ monster).
        /// </summary>
        private void ConfirmCardSelect()
        {
            if (_selectLocations.Count == 0 || _selectIndex >= _selectLocations.Count)
            {
                ScreenReader.Say(Loc.Get("duel_action_error"));
                return;
            }

            int loc = _selectLocations[_selectIndex];

            try
            {
                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                var worker = client?.worker2d;
                var scl = worker?.selectCardLocation;

                if (scl == null)
                {
                    ScreenReader.Say(Loc.Get("duel_action_error"));
                    _inCardSelect = false;
                    return;
                }

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"CardSelect: selecting location {loc} (0x{loc:X})");

                bool result = scl.OnClickZoneDirect(loc);
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"CardSelect: OnClickZoneDirect returned {result}");

                ScreenReader.Say(Loc.Get("duel_card_selected"));

                // Remove the selected location from the list so the user
                // doesn't try to select the same card twice
                _selectLocations.RemoveAt(_selectIndex);
                if (_selectIndex >= _selectLocations.Count && _selectLocations.Count > 0)
                    _selectIndex = 0;

                // If more selections needed, the game keeps SelectCardLocation open
                // and ProcessCardSelectInput will continue handling it.
                // If no more selections needed, it closes and we auto-exit.
                if (_selectLocations.Count == 0)
                    _inCardSelect = false;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"CardSelect error: {ex.Message}");
                ScreenReader.Say(Loc.Get("duel_action_error"));
            }
        }

        /// <summary>
        /// Cancels the card selection if the game allows it.
        /// </summary>
        private void CancelCardSelect()
        {
            _inCardSelect = false;
            _selectLocations.Clear();

            try
            {
                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                var worker = client?.worker2d;
                var scl = worker?.selectCardLocation;
                if (scl != null && scl.cancelable)
                {
                    scl.OnCancel();
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        "CardSelect: cancelled via OnCancel");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"CardSelect cancel error: {ex.Message}");
            }

            ScreenReader.Say(Loc.Get("duel_card_select_cancelled"));
        }

        /// <summary>
        /// Debug: dumps the state of SelectCardLocation and scans for tribute UI elements.
        /// Called when fallback tap is used and debug mode is on.
        /// </summary>
        private static void DumpCardSelectionState()
        {
            MelonLoader.MelonLogger.Msg("=== DumpCardSelectionState ===");

            var client = Il2CppYgomGame.Duel.DuelClient.instance;
            var worker = client?.worker2d;

            // Log curInputType — controls what input the game accepts
            if (worker != null)
            {
                try
                {
                    var inputType = worker.curInputType;
                    MelonLoader.MelonLogger.Msg($"  curInputType = {inputType} ({(int)inputType})");
                }
                catch (Exception ex)
                {
                    MelonLoader.MelonLogger.Msg($"  curInputType THREW: {ex.Message}");
                }

                try
                {
                    MelonLoader.MelonLogger.Msg($"  isTapDowned = {worker.isTapDowned}");
                }
                catch { }

                try
                {
                    MelonLoader.MelonLogger.Msg($"  worker.totalLevel = {worker.totalLevel}");
                }
                catch { }
            }

            // Check SelectCardLocation
            try
            {
                bool dcOpen = Il2CppYgomGame.Duel.DuelClient.IsOpenSelectCardLocation();
                MelonLoader.MelonLogger.Msg($"  DuelClient.IsOpenSelectCardLocation() = {dcOpen}");
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Msg($"  DuelClient.IsOpenSelectCardLocation() THREW: {ex.Message}");
            }

            if (worker != null)
            {
                try
                {
                    bool w2dOpen = worker.IsOpenSelectCardLocation();
                    MelonLoader.MelonLogger.Msg($"  worker2d.IsOpenSelectCardLocation() = {w2dOpen}");
                }
                catch { }

                try
                {
                    var scl = worker.selectCardLocation;
                    MelonLoader.MelonLogger.Msg($"  selectCardLocation null = {scl == null}");
                    if (scl != null)
                    {
                        MelonLoader.MelonLogger.Msg($"    IsOpen={scl.IsOpen} IsActive={scl.IsActive}");
                        MelonLoader.MelonLogger.Msg($"    cancelable={scl.cancelable} isClosing={scl.isClosing}");
                        var locList = scl.selectLocationList;
                        MelonLoader.MelonLogger.Msg($"    selectLocationList null={locList == null}" +
                            $" count={locList?.Count ?? -1}");
                    }
                }
                catch { }
            }

            // Check DLL_DuelList — engine's card selection list
            try
            {
                int itemMax = Il2CppYgomGame.Duel.Engine.DLL_DuelListGetItemMax();
                int selMax = Il2CppYgomGame.Duel.Engine.DLL_DuelListGetSelectMax();
                int selMin = Il2CppYgomGame.Duel.Engine.DLL_DuelListGetSelectMin();
                int multi = Il2CppYgomGame.Duel.Engine.DLL_DuelListIsMultiMode();
                MelonLoader.MelonLogger.Msg(
                    $"  DLL_DuelList: itemMax={itemMax} selMax={selMax} selMin={selMin} multiMode={multi}");

                for (int i = 0; i < itemMax && i < 10; i++)
                {
                    int cardId = Il2CppYgomGame.Duel.Engine.DLL_DuelListGetItemID(i);
                    int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelListGetItemUniqueID(i);
                    int from = Il2CppYgomGame.Duel.Engine.DLL_DuelListGetItemFrom(i);
                    MelonLoader.MelonLogger.Msg(
                        $"    list[{i}]: cardId={cardId} uid={uid} from=0x{from:X}");
                }
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.Msg($"  DLL_DuelList THREW: {ex.Message}");
            }

            // Check DLL_DuelDlg — dialog selection state
            try
            {
                int dlgItems = Il2CppYgomGame.Duel.Engine.DLL_DuelDlgGetSelectItemNum();
                MelonLoader.MelonLogger.Msg($"  DLL_DuelDlgGetSelectItemNum = {dlgItems}");
            }
            catch { }

            // Scan all field locate values for cards
            try
            {
                MelonLoader.MelonLogger.Msg("  Field locate scan:");
                for (int loc = FieldLocateMin; loc <= FieldLocateMax; loc++)
                {
                    int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(PlayerMe, loc, 0);
                    uint cardId = uid > 0 ? Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(uid) : 0;
                    if (uid > 0) MelonLoader.MelonLogger.Msg($"    locate[{loc}]: uid={uid} cardId={cardId}");
                }
            }
            catch { }

            MelonLoader.MelonLogger.Msg("=== End DumpCardSelectionState ===");
        }

        /// <summary>Taps the card in the current zone to open the command popup.</summary>
        private void TapCardForZone(Il2CppYgomGame.Duel.DuelClient client,
            int player, int locate, int slotIndex)
        {
            if (_currentZone == Zone.Hand)
                TapHandCard(client, slotIndex);
            else
                TapFieldCard(client, player, locate, slotIndex);
        }

        /// <summary>
        /// Coroutine: waits a few frames for the CardCommand popup to initialize,
        /// then finds the matching command button and clicks it via OnCommand.
        /// </summary>
        private IEnumerator AutoClickCommandButton(
            Il2CppYgomGame.Duel.Engine.CommandType targetCmd)
        {
            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"AutoClick: waiting for CardCommand popup, target={targetCmd}");

            // Wait up to ~30 frames for CardCommand to appear
            Il2CppYgomGame.Duel.CardCommand cardCom = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                yield return null;

                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                if (client == null) continue;
                var worker = client.worker2d;
                if (worker == null) continue;

                cardCom = worker.cardCom;
                if (cardCom != null && cardCom.myGameObject != null
                    && cardCom.myGameObject.activeSelf)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"AutoClick: CardCommand found after {attempt + 1} frames");
                    break;
                }
                cardCom = null;
            }

            if (cardCom == null)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    "AutoClick: CardCommand not found after 30 frames");
                ScreenReader.Say(Loc.Get("duel_action_error"));
                yield break;
            }

            // Find the button matching our target command
            var buttons = cardCom.commands;
            if (buttons == null)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    "AutoClick: cardCom.commands is null");
                ScreenReader.Say(Loc.Get("duel_action_error"));
                yield break;
            }

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"AutoClick: scanning {buttons.Count} command buttons");

            for (int i = 0; i < buttons.Count; i++)
            {
                var btn = buttons[i];
                if (btn == null) continue;

                bool active = btn.gameObject != null && btn.gameObject.activeSelf;
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"AutoClick: button[{i}] cmd={btn.cmd} active={active}");

                if (btn.cmd == targetCmd && active)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"AutoClick: clicking {targetCmd} button");
                    cardCom.OnCommand(btn);
                    yield break;
                }
            }

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"AutoClick: no active button found for {targetCmd}");
            ScreenReader.Say(Loc.Get("duel_action_error"));
        }

        /// <summary>Populates _commands from a command bitmask.</summary>
        private void BuildCommandList(uint cmdMask)
        {
            _commands.Clear();

            void TryAdd(uint bit, Il2CppYgomGame.Duel.Engine.CommandType type, string locKey)
            {
                if ((cmdMask & bit) != 0)
                    _commands.Add(new CommandInfo { Type = type, Label = Loc.Get(locKey) });
            }

            TryAdd(0x10, Il2CppYgomGame.Duel.Engine.CommandType.Summon, "duel_cmd_summon");
            TryAdd(0x04, Il2CppYgomGame.Duel.Engine.CommandType.SummonSp, "duel_cmd_special_summon");
            TryAdd(0x40, Il2CppYgomGame.Duel.Engine.CommandType.SetMonst, "duel_cmd_set_monster");
            TryAdd(0x80, Il2CppYgomGame.Duel.Engine.CommandType.Set, "duel_cmd_set");
            TryAdd(0x08, Il2CppYgomGame.Duel.Engine.CommandType.Action, "duel_cmd_activate");
            TryAdd(0x100, Il2CppYgomGame.Duel.Engine.CommandType.Pendulum, "duel_cmd_pendulum");
            TryAdd(0x01, Il2CppYgomGame.Duel.Engine.CommandType.Attack, "duel_cmd_attack");
            TryAdd(0x20, Il2CppYgomGame.Duel.Engine.CommandType.Reverse, "duel_cmd_flip_summon");
            TryAdd(0x200, Il2CppYgomGame.Duel.Engine.CommandType.TurnAtk, "duel_cmd_to_attack");
            TryAdd(0x400, Il2CppYgomGame.Duel.Engine.CommandType.TurnDef, "duel_cmd_to_defense");
            TryAdd(0x1000, Il2CppYgomGame.Duel.Engine.CommandType.Decide, "duel_cmd_select");
        }

        /// <summary>
        /// Taps a hand card by getting its actual GameObject from HandCards.m_InfoList
        /// and calling HandCards.TapCard — the game's own tap handler.
        /// </summary>
        private void TapHandCard(Il2CppYgomGame.Duel.DuelClient client, int slotIndex)
        {
            var hud = client.duelHUD;
            if (hud == null)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav", "duelHUD is null");
                ScreenReader.Say(Loc.Get("duel_action_error"));
                return;
            }

            var handCards = hud.nearHandCard;
            if (handCards == null)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav", "nearHandCard is null");
                ScreenReader.Say(Loc.Get("duel_action_error"));
                return;
            }

            // Diagnostic: log HandCards state flags
            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"HandCards state: EnableControle={handCards.EnableControle}" +
                $" m_EnableTapCallback={handCards.m_EnableTapCallback}" +
                $" m_EnableSelect={handCards.m_EnableSelect}" +
                $" m_IsBusy={handCards.m_IsBusy}" +
                $" m_TouchMode={handCards.m_TouchMode}");

            // Get the card's actual GameObject from m_InfoList
            var infoList = handCards.m_InfoList;
            if (infoList == null || slotIndex >= infoList.Count)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"m_InfoList null or index {slotIndex} out of range (count={infoList?.Count ?? -1})");
                ScreenReader.Say(Loc.Get("duel_action_error"));
                return;
            }

            var handInfo = infoList[slotIndex];
            if (handInfo == null)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav", $"HandInfo at index {slotIndex} is null");
                ScreenReader.Say(Loc.Get("duel_action_error"));
                return;
            }

            var cardGO = handInfo.m_CardPrefab;
            if (cardGO == null)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav", $"m_CardPrefab at index {slotIndex} is null");
                ScreenReader.Say(Loc.Get("duel_action_error"));
                return;
            }

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"HandInfo[{slotIndex}]: uid={handInfo.m_UniqueId} hide={handInfo.m_IsHide}" +
                $" highlight={handInfo.m_IsHighlight} GO={cardGO.name}");

            // Ensure m_EnableSelect is true — tutorial mode sets this to false,
            // which causes TapCard to silently return without doing anything
            bool wasSelectEnabled = handCards.m_EnableSelect;
            if (!wasSelectEnabled)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    "m_EnableSelect was false, enabling for TapCard");
                handCards.m_EnableSelect = true;
            }

            // First TapCard: selects/highlights the card (game's "first tap" behavior).
            // Second TapCard: opens the command popup (game's "tap selected card" behavior).
            // Both calls are needed — the game uses a two-tap flow.
            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"Calling HandCards.TapCard({cardGO.name}, touch=false) x2");
            handCards.TapCard(cardGO, false);
            handCards.TapCard(cardGO, false);
            DebugLogger.Log(LogCategory.Game, "FieldNav", "TapCard x2 returned");
        }

        /// <summary>
        /// Taps a field card. When a "pointing" TutorialArrow is active,
        /// routes the tap through the TutorialArrow's pointer handlers
        /// (which pass-through to underlying game objects). Otherwise falls
        /// back to OnTapLocator on RunEffectWorker2D.
        /// </summary>
        private void TapFieldCard(Il2CppYgomGame.Duel.DuelClient client,
            int player, int locate, int slotIndex)
        {
            var worker = client.worker2d;
            if (worker == null)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav", "worker2d is null");
                ScreenReader.Say(Loc.Get("duel_action_error"));
                return;
            }

            // When a "pointing" TutorialArrow is active, OnTapLocator won't work
            // (curInputType=Null, comBit empty). Try routing through the arrow's
            // pointer handlers or the full TapDown/TapUp sequence instead.
            if (IsTutorialArrowPointing())
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    "Pointing TutorialArrow active — using tutorial tap path");
                TapFieldCardViaTutorial(worker, player, locate, slotIndex);
                return;
            }

            var inputType = Il2CppYgomGame.Duel.Engine.MenuActType.Null;
            try { inputType = worker.curInputType; }
            catch { }

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"curInputType={inputType} before tap");

            // In LockOn/Selection modes (spell targeting, tribute selection),
            // OnTapLocator doesn't register. Use OnDoCardCommand with Decide
            // to directly tell the engine to select this card.
            if (inputType == Il2CppYgomGame.Duel.Engine.MenuActType.LockOn
                || inputType == Il2CppYgomGame.Duel.Engine.MenuActType.Selection)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"Using OnDoCardCommand(Decide) for {inputType}: ({player}, {locate}, {slotIndex})");
                worker.OnDoCardCommand(player, locate, slotIndex,
                    Il2CppYgomGame.Duel.Engine.CommandType.Decide);
            }
            else
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"Calling OnTapLocator({player}, {locate}, {slotIndex})");
                worker.OnTapLocator(player, locate, slotIndex);
            }

            DebugLogger.Log(LogCategory.Game, "FieldNav", "Tap returned");

            try
            {
                var afterType = worker.curInputType;
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"curInputType={afterType} after tap");
            }
            catch { }
        }

        /// <summary>
        /// Checks if a "pointing" TutorialArrow is active (one that stays on
        /// screen and points at game elements, rather than click-to-continue).
        /// </summary>
        private static bool IsTutorialArrowPointing()
        {
            try
            {
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) return false;

                Il2CppYgomSystem.UI.ViewControllerManager mgr;
                if (!namedManager.TryGetValue("dialog", out mgr)) return false;

                var topVc = mgr?.GetStackTopViewController();
                if (topVc?.gameObject == null) return false;

                return topVc.gameObject.name == "TutorialArrow";
            }
            catch { return false; }
        }

        /// <summary>
        /// Handles card interaction when a pointing TutorialArrow is active.
        /// Finds ListCard objects in the scene (tribute/material selection UI)
        /// and clicks the first selectable one via OnClick().
        /// </summary>
        private void TapFieldCardViaTutorial(
            Il2CppYgomGame.Duel.RunEffectWorker2D worker,
            int player, int locate, int slotIndex)
        {
            try
            {
                // Get the TutorialArrow's ipclick handlers — these are the
                // YgomButtons the game routes clicks through
                var namedManager = Il2CppYgomSystem.UI.ViewControllerManager.namedManager;
                if (namedManager == null) { FallbackTap(worker, player, locate, slotIndex); return; }
                if (!namedManager.TryGetValue("dialog", out var mgr)) { FallbackTap(worker, player, locate, slotIndex); return; }
                var topVc = mgr?.GetStackTopViewController();
                if (topVc?.gameObject == null || topVc.gameObject.name != "TutorialArrow")
                {
                    FallbackTap(worker, player, locate, slotIndex);
                    return;
                }

                var arrowVc = topVc.TryCast<
                    Il2CppYgomGame.Menu.TutorialArrowViewController>();
                if (arrowVc == null) { FallbackTap(worker, player, locate, slotIndex); return; }

                // Get ipclick handlers — these point to YgomButtons on DuelListCards
                var ipclick = arrowVc.ipclick;
                if (ipclick == null || ipclick.Length == 0)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        "TutorialArrow has no ipclick handlers");
                    FallbackTap(worker, player, locate, slotIndex);
                    return;
                }

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"TutorialArrow ipclick count={ipclick.Length}");

                // Create pointer event data for the click
                var pointerData = new UnityEngine.EventSystems.PointerEventData(
                    UnityEngine.EventSystems.EventSystem.current)
                {
                    position = new UnityEngine.Vector2(
                        UnityEngine.Screen.width / 2f,
                        UnityEngine.Screen.height / 2f)
                };

                // Check if ipclick target is already selected
                bool ipclickSelected = false;
                try
                {
                    var mb = ipclick[0]?.TryCast<UnityEngine.MonoBehaviour>();
                    var go = mb?.gameObject;
                    if (go != null)
                    {
                        var lc = go.GetComponent<Il2CppYgomGame.Duel.ListCard>();
                        if (lc != null) ipclickSelected = lc.Selected;
                    }
                }
                catch { }

                if (!ipclickSelected)
                {
                    // Select the card via YgomButton click
                    var button = ipclick[0]?.TryCast<Il2CppYgomSystem.UI.YgomButton>();
                    if (button != null)
                    {
                        DebugLogger.Log(LogCategory.Game, "FieldNav",
                            "Clicking ipclick YgomButton + OnDecide (select+confirm)");
                        button.OnPointerClick(pointerData);

                        // Immediately confirm via EmotionalList.OnDecide
                        try
                        {
                            var eList = UnityEngine.Object.FindObjectOfType<
                                Il2CppYgomGame.Duel.EmotionalList>();
                            if (eList != null)
                            {
                                DebugLogger.Log(LogCategory.Game, "FieldNav",
                                    "Calling OnDecide() to confirm selection");
                                eList.OnDecide();
                            }
                        }
                        catch (Exception ex)
                        {
                            DebugLogger.Log(LogCategory.Game, "FieldNav",
                                $"OnDecide error: {ex.Message}");
                        }
                        return;
                    }
                }
                else
                {
                    // Card already selected — just confirm
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        "Card already selected — calling OnDecide to confirm");
                    try
                    {
                        var eList = UnityEngine.Object.FindObjectOfType<
                            Il2CppYgomGame.Duel.EmotionalList>();
                        if (eList != null)
                        {
                            eList.OnDecide();
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Game, "FieldNav",
                            $"OnDecide error: {ex.Message}");
                    }
                }

                FallbackTap(worker, player, locate, slotIndex);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"TapFieldCardViaTutorial error: {ex.Message}");
            }
        }

        private static void FallbackTap(
            Il2CppYgomGame.Duel.RunEffectWorker2D worker,
            int player, int locate, int slotIndex)
        {
            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"FallbackTap: OnTapLocator({player}, {locate}, {slotIndex})");
            worker.OnTapLocator(player, locate, slotIndex);
        }

        #endregion

        #region Phase Advancement

        private void AdvancePhase()
        {
            try
            {
                uint movable = Il2CppYgomGame.Duel.Engine.DLL_DuelComGetMovablePhase();
                if (movable == 0)
                {
                    // Engine API locked (tutorial mode) — call game methods directly
                    AdvancePhaseDirect();
                    return;
                }

                uint currentPhaseVal = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCurrentPhase();
                int current = (int)currentPhaseVal;

                // Try phases in forward order after current
                Il2CppYgomGame.Duel.Engine.Phase[] phaseOrder =
                {
                    Il2CppYgomGame.Duel.Engine.Phase.Main1,
                    Il2CppYgomGame.Duel.Engine.Phase.Battle,
                    Il2CppYgomGame.Duel.Engine.Phase.Main2,
                    Il2CppYgomGame.Duel.Engine.Phase.End,
                };

                foreach (var phase in phaseOrder)
                {
                    if ((int)phase > current && (movable & (1u << (int)phase)) != 0)
                    {
                        Il2CppYgomGame.Duel.Engine.DLL_DuelComMovePhase((int)phase);
                        string phaseName = DuelEventAnnouncer.GetPhaseName(phase);
                        ScreenReader.Say(Loc.Get("duel_advancing_phase", phaseName));
                        return;
                    }
                }

                // Nothing after current available — try End Phase as fallback
                if ((movable & (1u << (int)Il2CppYgomGame.Duel.Engine.Phase.End)) != 0)
                {
                    Il2CppYgomGame.Duel.Engine.DLL_DuelComMovePhase(
                        (int)Il2CppYgomGame.Duel.Engine.Phase.End);
                    string endName = DuelEventAnnouncer.GetPhaseName(
                        Il2CppYgomGame.Duel.Engine.Phase.End);
                    ScreenReader.Say(Loc.Get("duel_advancing_phase", endName));
                    return;
                }

                ScreenReader.Say(Loc.Get("duel_cant_advance_phase"));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"AdvancePhase error: {ex.Message}");
                ScreenReader.Say(Loc.Get("duel_phase_error"));
            }
        }

        /// <summary>
        /// Advances phase by calling game methods directly (bypasses UI/tutorial locks).
        /// Tries EmotionalCommand.OnBattlePhase/OnEndPhase first, then DuelMenu.ChangePhase.
        /// </summary>
        private static void AdvancePhaseDirect()
        {
            try
            {
                uint currentPhaseVal = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCurrentPhase();
                var currentPhase = (Il2CppYgomGame.Duel.Engine.Phase)(int)currentPhaseVal;

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"AdvancePhaseDirect: currentPhase={currentPhase} ({currentPhaseVal})");

                // Try EmotionalCommand first — has direct OnBattlePhase/OnEndPhase
                var emCmd = UnityEngine.Object.FindObjectOfType<
                    Il2CppYgomGame.Duel.EmotionalCommand>();
                if (emCmd != null)
                {
                    if (currentPhase == Il2CppYgomGame.Duel.Engine.Phase.Main1
                        || currentPhase == Il2CppYgomGame.Duel.Engine.Phase.Main2)
                    {
                        DebugLogger.Log(LogCategory.Game, "FieldNav",
                            "Calling EmotionalCommand.OnBattlePhase()");
                        emCmd.OnBattlePhase();
                        ScreenReader.Say(Loc.Get("duel_advancing_phase",
                            DuelEventAnnouncer.GetPhaseName(
                                Il2CppYgomGame.Duel.Engine.Phase.Battle)));
                        return;
                    }
                    if (currentPhase == Il2CppYgomGame.Duel.Engine.Phase.Battle)
                    {
                        DebugLogger.Log(LogCategory.Game, "FieldNav",
                            "Calling EmotionalCommand.OnEndPhase()");
                        emCmd.OnEndPhase();
                        ScreenReader.Say(Loc.Get("duel_advancing_phase",
                            DuelEventAnnouncer.GetPhaseName(
                                Il2CppYgomGame.Duel.Engine.Phase.End)));
                        return;
                    }
                }
                else
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        "EmotionalCommand not found");
                }

                // Fallback: DuelMenu.ChangePhase directly
                var duelMenu = UnityEngine.Object.FindObjectOfType<
                    Il2CppYgomGame.Duel.DuelMenu>();
                if (duelMenu != null)
                {
                    var targetPhase = currentPhase == Il2CppYgomGame.Duel.Engine.Phase.Battle
                        ? Il2CppYgomGame.Duel.Engine.Phase.End
                        : Il2CppYgomGame.Duel.Engine.Phase.Battle;

                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"Calling DuelMenu.ChangePhase({targetPhase})");
                    duelMenu.ChangePhase(targetPhase);
                    ScreenReader.Say(Loc.Get("duel_advancing_phase",
                        DuelEventAnnouncer.GetPhaseName(targetPhase)));
                    return;
                }

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    "Neither EmotionalCommand nor DuelMenu found");
                ScreenReader.Say(Loc.Get("duel_cant_advance_phase"));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"AdvancePhaseDirect error: {ex.Message}");
                ScreenReader.Say(Loc.Get("duel_phase_error"));
            }
        }

        #endregion

        #region Command Helpers

        private static string DescribeCommandMask(uint cmdMask)
        {
            var names = new List<string>();

            if ((cmdMask & 0x10) != 0) names.Add(Loc.Get("duel_cmd_summon"));
            if ((cmdMask & 0x04) != 0) names.Add(Loc.Get("duel_cmd_special_summon"));
            if ((cmdMask & 0x40) != 0) names.Add(Loc.Get("duel_cmd_set_monster"));
            if ((cmdMask & 0x80) != 0) names.Add(Loc.Get("duel_cmd_set"));
            if ((cmdMask & 0x08) != 0) names.Add(Loc.Get("duel_cmd_activate"));
            if ((cmdMask & 0x100) != 0) names.Add(Loc.Get("duel_cmd_pendulum"));
            if ((cmdMask & 0x01) != 0) names.Add(Loc.Get("duel_cmd_attack"));
            if ((cmdMask & 0x20) != 0) names.Add(Loc.Get("duel_cmd_flip_summon"));
            if ((cmdMask & 0x200) != 0) names.Add(Loc.Get("duel_cmd_to_attack"));
            if ((cmdMask & 0x400) != 0) names.Add(Loc.Get("duel_cmd_to_defense"));

            return string.Join(", ", names);
        }

        #endregion

        #region Zone Helpers

        private void RefreshCurrentZone()
        {
            RefreshZoneSlots(_currentZone);
        }

        /// <summary>
        /// Rebuilds _zoneSlots and _zoneLocates for the given zone.
        /// Grid zones: populates ALL columns (including empty) for spatial awareness.
        /// Side zones: sequential indices at fixed locate.
        /// </summary>
        private void RefreshZoneSlots(Zone zone)
        {
            _zoneSlots.Clear();
            _zoneLocates.Clear();
            int player = GetZonePlayer(zone);

            if (IsFieldSlotZone(zone))
            {
                // Grid zone: use explicit locate arrays, include ALL columns
                int[] locates = (zone == Zone.MyMonster || zone == Zone.OppMonster)
                    ? MonsterLocates : SpellLocates;
                for (int col = 0; col < locates.Length; col++)
                {
                    _zoneSlots.Add(0);   // field slots always use index 0
                    _zoneLocates.Add(locates[col]);
                }
            }
            else if (IsSingleSlotZone(zone))
            {
                // Field spell: single locate, only add if occupied
                int locate = GetSideZoneLocate(zone);
                try
                {
                    int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                        player, locate, 0);
                    if (uid > 0)
                    {
                        _zoneSlots.Add(0);
                        _zoneLocates.Add(locate);
                    }
                }
                catch { /* empty */ }
            }
            else
            {
                // Stack zone (hand, grave, banished, extra deck)
                int locate = GetSideZoneLocate(zone);
                int count = GetCardCount(player, locate);
                for (int i = 0; i < count; i++)
                {
                    _zoneSlots.Add(i);
                    _zoneLocates.Add(locate);
                }
            }

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"RefreshZone {zone}: player={player} found={_zoneSlots.Count} entries" +
                (_zoneLocates.Count > 0 ? $" at locates=[{string.Join(",", _zoneLocates)}]" : ""));
        }

        /// <summary>
        /// Counts monsters and spells on field using the known locate arrays.
        /// </summary>
        private static void CountFieldCards(int player, out int monsters, out int spells)
        {
            monsters = 0;
            spells = 0;
            foreach (int loc in MonsterLocates)
            {
                try
                {
                    int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                        player, loc, 0);
                    if (uid > 0) monsters++;
                }
                catch { /* skip */ }
            }
            foreach (int loc in SpellLocates)
            {
                try
                {
                    int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                        player, loc, 0);
                    if (uid > 0) spells++;
                }
                catch { /* skip */ }
            }
        }

        /// <summary>Whether this zone is a grid zone with fixed field slot columns.</summary>
        private static bool IsFieldSlotZone(Zone zone)
        {
            return zone == Zone.MyMonster || zone == Zone.OppMonster
                || zone == Zone.MySpell || zone == Zone.OppSpell;
        }

        /// <summary>Whether this zone is a single-card field slot (field spell).</summary>
        private static bool IsSingleSlotZone(Zone zone)
        {
            return zone == Zone.MyFieldSpell || zone == Zone.OppFieldSpell;
        }

        private static int GetZonePlayer(Zone zone)
        {
            return zone switch
            {
                Zone.OppMonster or Zone.OppSpell or Zone.OppFieldSpell
                    or Zone.OppGrave or Zone.OppBanished or Zone.OppExtra => PlayerOpp,
                _ => PlayerMe
            };
        }

        /// <summary>Gets the fixed locate value for stack and side zones.</summary>
        private static int GetSideZoneLocate(Zone zone)
        {
            return zone switch
            {
                Zone.Hand => LocateHand,
                Zone.MyGrave or Zone.OppGrave => LocateGrave,
                Zone.MyExtra or Zone.OppExtra => LocateExtra,
                Zone.MyBanished or Zone.OppBanished => LocateBanished,
                Zone.MyFieldSpell or Zone.OppFieldSpell => LocateFieldSpell,
                _ => LocateHand
            };
        }

        private static string GetZoneName(Zone zone)
        {
            return zone switch
            {
                Zone.Hand => Loc.Get("duel_zone_hand"),
                Zone.MyMonster => Loc.Get("duel_zone_my_monsters"),
                Zone.MySpell => Loc.Get("duel_zone_my_spells"),
                Zone.MyGrave => Loc.Get("duel_zone_my_grave"),
                Zone.MyExtra => Loc.Get("duel_zone_my_extra"),
                Zone.OppMonster => Loc.Get("duel_zone_opp_monsters"),
                Zone.OppSpell => Loc.Get("duel_zone_opp_spells"),
                Zone.OppGrave => Loc.Get("duel_zone_opp_grave"),
                Zone.OppExtra => Loc.Get("duel_zone_opp_extra"),
                Zone.MyFieldSpell => Loc.Get("duel_zone_my_field_spell"),
                Zone.OppFieldSpell => Loc.Get("duel_zone_opp_field_spell"),
                Zone.MyBanished => Loc.Get("duel_zone_my_banished"),
                Zone.OppBanished => Loc.Get("duel_zone_opp_banished"),
                _ => "Unknown"
            };
        }

        #endregion

        #region Card Helpers

        private static int GetCardCount(int player, int locate)
        {
            try
            {
                return Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardNum(player, locate);
            }
            catch { return 0; }
        }

        private static string ResolveCardName(uint cardDbId)
        {
            if (cardDbId == 0 || cardDbId > 100000)
                return Loc.Get("duel_unknown_card");

            try
            {
                var content = Il2CppYgomGame.Card.Content.Instance;
                if (content == null) return Loc.Get("duel_unknown_card");

                string name = content.GetName((int)cardDbId);
                return string.IsNullOrEmpty(name) ? Loc.Get("duel_unknown_card") : name;
            }
            catch { return Loc.Get("duel_unknown_card"); }
        }

        private static uint SafeGetCommandMask(int player, int locate, int slotIndex)
        {
            // Tutorial duels use TutorialWork.GetCommandMask (backed by comBit dictionary)
            // instead of Engine.DLL_DuelComGetCommandMask
            try
            {
                var tutWork = Il2CppYgomGame.Duel.DuelClient.GetTutorialWork();
                if (tutWork != null)
                {
                    uint tutMask = tutWork.GetCommandMask(player, locate, slotIndex);
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"TutorialWork.GetCommandMask(p={player}, pos={locate}, idx={slotIndex}) = 0x{tutMask:X}");
                    if (tutMask != 0) return tutMask;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"TutorialWork.GetCommandMask error: {ex.Message}");
            }

            // Normal duels: query engine directly
            try
            {
                return Il2CppYgomGame.Duel.Engine.DLL_DuelComGetCommandMask(
                    player, locate, slotIndex);
            }
            catch { return 0; }
        }

        #endregion
    }
}
