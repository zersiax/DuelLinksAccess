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
    ///   Row 2: My Monster (3 columns)
    ///   Row 3: Opp Monster (3 columns)
    ///   Row 4: Opp Spell/Trap (3 columns)
    ///
    /// Speed Duel uses 3 main monster zones. The Extra Monster Zone (used by
    /// Synchro/Xyz/Link/Fusion summons) is folded into the monster count in
    /// the field summary (F key) and announced via summon events; it does not
    /// have its own grid column or hotkey.
    ///
    /// Arrow navigation:
    ///   Up/Down — Move between rows (column preserved). Action menu when open.
    ///   Left/Right — Move between columns within row (wraps).
    ///
    /// Zone hotkeys (Shift = opponent):
    ///   C=Hand, M=Monsters, S=Spells, T=FieldSpell, G=Grave, B=Banished, D=ExtraDeck
    ///   L=LP (read-only), 1-3=Monster slot
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
        //   1, 2, 3    → Monster zones (3 slots, Speed Duel — game v10.7.0).
        //                 Confirmed empirically via CardMove p3 decode
        //                 ((locate << 1) | player): a third Normal Summon
        //                 lands at loc=1 (e.g. p3=0x4002), with first/second
        //                 at loc=2/loc=3. loc=4 is never occupied in Speed
        //                 Duel and was previously assumed to be a slot.
        //   8, 9, 10   → Spell/Trap zones (3 slots, Speed Duel). Confirmed
        //                 2026-05-01 via SpellProbe diagnostic: a duel with
        //                 3 set spells produced occupied locates {8, 9, 10}
        //                 (cardIds 12470, 5538, 12458). First spell set lands
        //                 at loc=9 (middle), second at loc=10 (right), third
        //                 at loc=8 (left) — same middle/right/left fill order
        //                 as monsters. loc=11 is never occupied and was
        //                 previously assumed to be a slot.
        //   5-7        → Unknown (possibly pendulum or unused in Speed Duel)
        private const int LocateHand = 13;
        private const int LocateExtra = 14;
        private const int LocateGrave = 16;
        private const int LocateDeck = 15;
        // Extra Monster Zones — shared between players, positional left/right.
        // BlindDuel (Master Duel) treats EMZ as PosExLMonster / PosExRMonster
        // (positional), not summon-type-segregated. Loc=5 confirmed via player-0
        // Link/Fusion (2026-05-14 PvP), loc=6 via earlier Synchro. Mapping left=5,
        // right=6 is a positional guess; if testing reveals it reversed, flip
        // these two constants.
        private const int LocateEMZLeft = 5;
        private const int LocateEMZRight = 6;
        private const int LocateFieldSpell = 12;  // Confirmed via field scan (cardId=4341 at loc=12)
        private const int LocateBanished = 17;    // Placeholder

        // Monster zone locates ordered by the engine's auto-placement sequence
        // for Speed Duel: first Normal Summon lands at loc=2, second at loc=3,
        // third at loc=1 (decoded from CardMove p3 as (locate << 1) | player).
        // Listing {2, 3, 1} instead of {1, 2, 3} keeps the announcement aligned
        // with summon order: a single monster reads "1 of 3: <name>" rather
        // than "2 of 3: <name>" with an empty leftmost slot the player can't
        // do anything with. Trade-off: column index no longer matches the
        // spatial layout 1→2→3, but Speed Duel never displays "loc 1" labels
        // anywhere visible to the player anyway.
        // EMZ (loc=6, Synchro/Xyz/Link/Fusion) is folded into CountFieldCards
        // for the F-key summary but is NOT a navigable column.
        private static readonly int[] MonsterLocates = { 2, 3, 1 };
        // Spell/Trap zone locates ordered by the engine's auto-placement
        // sequence (middle, right, left) — see comment block above. Listing
        // {9, 10, 8} instead of {8, 9, 10} keeps the announcement aligned
        // with set order: a single set spell reads "1 of 3" rather than
        // "2 of 3" with an empty leftmost slot.
        private static readonly int[] SpellLocates = { 9, 10, 8 };
        // Extra Monster Zone locates ordered left-to-right (loc=5 left, loc=6
        // right — positional guess matching BlindDuel's PosExLMonster /
        // PosExRMonster convention; flip if testing reveals reversed).
        private static readonly int[] EMZLocates = { LocateEMZLeft, LocateEMZRight };

        // Field slot locate range to scan (each holds 0 or 1 card).
        private const int FieldLocateMin = 1;
        private const int FieldLocateMax = 12;

        // DLL_DuelGetCardFace returns: 0=face-down, 1=face-up (boolean only)
        // ATK/DEF position determined from command mask TurnAtk/TurnDef bits
        private const uint CmdTurnAtk = 0x200;  // Can switch TO ATK → currently in DEF
        private const uint CmdTurnDef = 0x400;  // Can switch TO DEF → currently in ATK

        // Player identifiers — the engine's seat number for "me" (0 or 1).
        // Single-player is always 0; PvP/Ranked matchmaking assigns either
        // seat, so we delegate to DuelEventAnnouncer.MyPlayerNum() (cached
        // per-frame). All engine queries below go through GetZonePlayer(zone)
        // which routes "MyXxx" zones to PlayerMe and "OppXxx" to PlayerOpp,
        // so updating these two accessors is enough to PvP-correct the entire
        // navigator. GridRow.Player captures the value at static-init time
        // but is never read, so the init-time fallback to 0 is harmless.
        private static int PlayerMe => DuelEventAnnouncer.MyPlayerNum();
        private static int PlayerOpp => 1 - DuelEventAnnouncer.MyPlayerNum();

        #endregion

        #region Types

        /// <summary>Navigable zones on the duel field.</summary>
        public enum Zone
        {
            // Grid zones (participate in arrow navigation)
            Hand,
            MySpell,
            MyMonster,
            // Extra Monster Zones — shared row between the two players
            // (2 slots, loc=5 left / loc=6 right). Owner is resolved per slot
            // via GetEMZOwner. Sits between MyMonster and OppMonster so Up
            // from MyMonster lands on EMZ before reaching OppMonster, matching
            // the physical TCG layout where EMZ slots sit between the players.
            ExtraMonster,
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
        /// Spatial grid rows, bottom to top. Hand at the bottom, Opp Spells at
        /// the top. The Extra Monster Zone row sits between MyMonster and
        /// OppMonster (positionally between the two players, matching the
        /// TCG layout where EMZ is shared). Monster and spell rows are 3 cols;
        /// EMZ row is 2 cols. The Player field on the EMZ row is a placeholder
        /// (PlayerMe default) — actual ownership is resolved per locate via
        /// GetEMZOwner in ReadCurrentCard / RefreshZoneSlots.
        /// </summary>
        private static readonly GridRow[] GridRows =
        {
            new GridRow { Zone = Zone.Hand,         Player = PlayerMe,  Locates = null },
            new GridRow { Zone = Zone.MySpell,      Player = PlayerMe,  Locates = SpellLocates },
            new GridRow { Zone = Zone.MyMonster,    Player = PlayerMe,  Locates = MonsterLocates },
            new GridRow { Zone = Zone.ExtraMonster, Player = PlayerMe,  Locates = EMZLocates },
            new GridRow { Zone = Zone.OppMonster,   Player = PlayerOpp, Locates = MonsterLocates },
            new GridRow { Zone = Zone.OppSpell,     Player = PlayerOpp, Locates = SpellLocates },
        };

        private const int RowHand = 0;
        private const int RowMySpell = 1;
        private const int RowMyMonster = 2;
        private const int RowExtraMonster = 3;
        private const int RowOppMonster = 4;
        private const int RowOppSpell = 5;

        /// <summary>Set of grid zones for quick membership checks.</summary>
        private static readonly HashSet<Zone> GridZones = new()
        {
            Zone.Hand, Zone.MySpell, Zone.MyMonster, Zone.ExtraMonster,
            Zone.OppMonster, Zone.OppSpell
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

        // PvP hand-play: when the engine doesn't expose hand commands
        // (DLL_DuelComGetCommandMask returns 0 for hand slots in PvP/Ranked),
        // we tap the card to open the game's native CardCommand popup, scan
        // its buttons, and present an accessible action menu. This field
        // holds the popup reference so ExecuteSelectedCommand can fire
        // OnCommand directly instead of re-tapping (which would close it).
        private Il2CppYgomGame.Duel.CardCommand _pendingCardCom;

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
        // Wall-clock deadline (Time.unscaledTime) after which _emoListHandled
        // is force-cleared on the next CheckForEmotionalList. Without this,
        // sequential pickers (XYZ second material; pick-N tributes) get
        // missed because the goActive=false → reset transition never fires
        // when the game opens prompt 2 in the same frame it closes prompt 1.
        private float _emoListHandledUntil;
        private const float EmoListHandledTimeout = 0.4f;

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

            // PvP hand-play: if the popup was opened by TapHandAndReadCommands
            // but the user backed out, close the game's CardCommand popup too —
            // otherwise it stays visually open and blocks further input.
            if (_pendingCardCom != null)
            {
                try { _pendingCardCom.Close(); } catch { }
                _pendingCardCom = null;
            }
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
                int hand = DuelState.GetCardCount(PlayerMe, LocateHand);
                DuelState.CountFieldCards(PlayerMe, out int myMon, out int mySp);
                int myGr = DuelState.GetCardCount(PlayerMe, LocateGrave);
                DuelState.CountFieldCards(PlayerOpp, out int oppMon, out int oppSp);
                int oppGr = DuelState.GetCardCount(PlayerOpp, LocateGrave);

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
                // Count only occupied slots for the announcement. Route through
                // DuelState.GetFieldCard which is hollow-engine-safe in PvP;
                // the old DLL_DuelGetCardUniqueID(player, loc, 0) probe returns
                // 0 for everything in PvP, so we'd always misannounce "0 cards".
                // EMZ is shared between players — check both sides per slot so
                // an opponent-owned fusion/synchro/xyz/link in the shared zone
                // still counts.
                bool isShared = _currentZone == Zone.ExtraMonster;
                int player = GetZonePlayer(_currentZone);
                cardCount = 0;
                foreach (int loc in _zoneLocates)
                {
                    try
                    {
                        if (DuelState.GetFieldCard(player, loc, 0) != null)
                        {
                            cardCount++;
                        }
                        else if (isShared
                            && DuelState.GetFieldCard(1 - player, loc, 0) != null)
                        {
                            cardCount++;
                        }
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

            // Number keys: direct monster slot access (3 main zones, Speed Duel)
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
            // Alpha4 / Alpha5: Extra Monster Zones (shared row, no shift).
            // Direct slot access into the EMZ grid row, complementing Up/Down
            // arrow navigation through MyMonster → EMZ → OppMonster.
            if (InputManager.TryConsumeKeyDown(KeyCode.Alpha4))
            {
                JumpToEMZSlot(0);
                return true;
            }
            if (InputManager.TryConsumeKeyDown(KeyCode.Alpha5))
            {
                JumpToEMZSlot(1);
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

        /// <summary>Jumps to a specific column in the Extra Monster Zone row.</summary>
        private void JumpToEMZSlot(int col)
        {
            col = Math.Min(col, EMZLocates.Length - 1);
            _rememberedCol = col;
            EnterGridRow(RowExtraMonster, col);
        }

        /// <summary>Reads LP for both players without moving the cursor.</summary>
        private void ReadLP()
        {
            try
            {
                int myLP = DuelState.GetLP(PlayerMe);
                int oppLP = DuelState.GetLP(PlayerOpp);
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
            int locate = _zoneLocates[_navIndex];
            // EMZ ownership is per-slot, not per-zone. Probe the actual owner
            // for EMZ locates; otherwise use the row-level player.
            int player = _currentZone == Zone.ExtraMonster
                ? GetEMZOwner(locate)
                : GetZonePlayer(_currentZone);

            try
            {
                // Single read path: DuelState backs everything by the visual
                // layer (CardRoot for field, HandCardManager for hand) plus
                // event-tracked counters. Works uniformly in single-player
                // and PvP — the engine's DLL_Duel* card queries are hollow
                // in PvP but the visual layer is fully populated in both.
                CardSnapshot? snap;
                if (_currentZone == Zone.Hand)
                    snap = DuelState.GetHandCard(player, slotIndex);
                else
                    snap = DuelState.GetFieldCard(player, locate, slotIndex);

                if (snap == null)
                {
                    string emptyText = IsFieldSlotZone(_currentZone)
                        ? Loc.Get("duel_empty_slot_named", _navIndex + 1)
                        : Loc.Get("duel_card_position",
                            _navIndex + 1, _zoneSlots.Count, Loc.Get("duel_empty_slot"));
                    if (queued) ScreenReader.SayQueued(emptyText);
                    else ScreenReader.Say(emptyText);
                    return;
                }

                var card = snap.Value;
                uint cardDbId = (uint)card.CardDbId;
                string cardName = card.Name ?? Loc.Get("duel_unknown_card");

                bool isHand = _currentZone == Zone.Hand;
                bool isExtra = _currentZone == Zone.MyExtra || _currentZone == Zone.OppExtra;
                bool isEMZ = _currentZone == Zone.ExtraMonster;
                // Hand and Extra Deck share read semantics: no face, Content-DB stats.
                bool isContentView = isHand || isExtra;
                bool isMyCard = player == PlayerMe;
                bool isFieldCard = IsFieldSlotZone(_currentZone);

                // EMZ is shared — annotate the name with "opponent's" when the
                // slot's resolved owner is the opponent, so the user knows whose
                // card they're on (the zone name itself is neutral).
                if (isEMZ && !isMyCard)
                    cardName = Loc.Get("duel_card_opponent_owned", cardName);

                // Determine card kind from Content database — uniform across modes.
                bool isMonster = true;
                if (isFieldCard && cardDbId > 0)
                {
                    try
                    {
                        var content = Il2CppYgomGame.Card.Content.Instance;
                        if (content != null)
                        {
                            var kind = content.GetKind((int)cardDbId);
                            isMonster = kind != Il2CppYgomGame.Card.Content.Kind.Magic
                                     && kind != Il2CppYgomGame.Card.Content.Kind.Trap;
                        }
                    }
                    catch { }
                }

                // --- Build announcement parts ---
                var parts = new List<string>();

                // Face/position info (field + grave; not hand/extra)
                if (!isContentView)
                {
                    bool isFaceUp = card.IsFaceUp;

                    // Don't reveal opponent's face-down card names
                    if (!isFaceUp && !isMyCard)
                        cardName = Loc.Get("duel_face_down_card");

                    parts.Add(isFaceUp
                        ? Loc.Get("duel_face_up")
                        : Loc.Get("duel_face_down"));

                    if (isMonster)
                    {
                        // Face-down is always DEF; face-up uses CardRoot.isAttack
                        // (live visual state — already in card.IsAttack).
                        bool isDefense = isFaceUp ? !card.IsAttack : true;

                        parts.Add(isDefense
                            ? Loc.Get("duel_defense_position")
                            : Loc.Get("duel_attack_position"));

                        // Stats for face-up or own monsters — DuelState reads
                        // atk/def off CardRoot (live, including effect modifiers).
                        if (isFaceUp || isMyCard)
                        {
                            string stats = Loc.Get("duel_card_stats", card.Atk, card.Def);
                            parts.Add(stats);
                        }
                    }
                }
                else
                {
                    // Hand / Extra Deck cards: level + ATK/DEF for monsters
                    string stats = GetHandCardStats(cardDbId);
                    if (stats != null)
                        parts.Add(stats);
                }

                // Position header: "1 of 3: Blue-Eyes White Dragon" for
                // navigation reads. For verbose (V key) the user is
                // re-reading the same card they already navigated to, so
                // the "X of Y" prefix is redundant noise — drop it.
                string header = verbose
                    ? cardName
                    : Loc.Get("duel_card_position",
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

            // Guard: skip empty grid slots. DuelState's GetFieldCard /
            // GetHandCard return null when the slot is empty, sourced from
            // the visual layer (works uniformly in single-player and PvP).
            try
            {
                CardSnapshot? snap = _currentZone == Zone.Hand
                    ? DuelState.GetHandCard(player, slotIndex)
                    : DuelState.GetFieldCard(player, locate, slotIndex);

                if (snap == null)
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

                    // PvP hand cards: the engine doesn't expose hand commands
                    // (cmdMask is always 0), so tap to open the game's CardCommand
                    // popup and read its buttons directly. The coroutine fills
                    // _commands and either auto-executes or enters the action menu.
                    if (_currentZone == Zone.Hand)
                    {
                        DebugLogger.Log(LogCategory.Game, "FieldNav",
                            "Hand cmdMask=0 — opening popup via TapHandCard to read commands");
                        MelonCoroutines.Start(TapHandAndReadCommands());
                        return;
                    }

                    // PvP attack short-circuit: in PvP the engine's cmdMask
                    // is always 0 even for valid attackers, but
                    // DLL_DuelGetAttackTargetMask still returns the real
                    // bitmask. Skip the popup-tap dance and go straight to
                    // target selection. Only fires when the engine is hollow
                    // — in single-player a cmdMask=0 on a monster genuinely
                    // means "can't attack" (DEF position / already attacked)
                    // and we must NOT inject Attack there.
                    if (_currentZone == Zone.MyMonster && DuelState.IsEngineHollow)
                    {
                        int atkMask = 0;
                        try
                        {
                            atkMask = Il2CppYgomGame.Duel.Engine
                                .DLL_DuelGetAttackTargetMask(player, locate);
                        }
                        catch { }
                        if (atkMask != 0)
                        {
                            DebugLogger.Log(LogCategory.Game, "FieldNav",
                                $"PvP attack: cmdMask=0 but attackTargetMask=0x{atkMask:X} — " +
                                $"injecting Attack and entering target selection");
                            ScreenReader.Say(Loc.Get("duel_cmd_attack"));
                            BeginTargetSelection(player, locate, slotIndex,
                                Il2CppYgomGame.Duel.Engine.CommandType.Attack);
                            return;
                        }
                    }

                    // Own field zones: same popup-reading flow as hand. In PvP
                    // this is the only way to get the action menu (Activate /
                    // Change to defense / etc.); in single-player it also
                    // handles the case where cmdMask is 0 but the popup still
                    // has actions (DEF monsters with Activate).
                    if (_currentZone == Zone.MyMonster || _currentZone == Zone.MySpell
                        || _currentZone == Zone.MyExtra)
                    {
                        DebugLogger.Log(LogCategory.Game, "FieldNav",
                            $"Field cmdMask=0 — opening popup via TapFieldCard at ({player}, {locate}, {slotIndex})");
                        MelonCoroutines.Start(TapFieldAndReadCommands());

                        // Debug: dump SelectCardLocation state and scan for tribute UI
                        if (Main.DebugMode)
                            DumpCardSelectionState();
                        return;
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
                    else if (IsDirectCommand(cmdType, locate) || isFieldAction)
                    {
                        // Position changes / flip / field activation / extra-deck
                        // SummonSp: OnDoCardCommand directly. The CardCommand popup
                        // path fails for these (popup never appears).
                        var worker = client.worker2d;
                        if (worker != null)
                        {
                            DebugLogger.Log(LogCategory.Game, "FieldNav",
                                $"Direct command (auto): OnDoCardCommand({cmdType}) for ({player}, {locate}, {slotIndex})");
                            if (cmdType == Il2CppYgomGame.Duel.Engine.CommandType.SummonSp)
                                DuelEventAnnouncer.ArmLocalSeatCapture();
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

            // Capture the PvP-popup ref before CancelActionMenu clears it.
            // We'll need it below to fire OnCommand directly.
            var pendingCardCom = _pendingCardCom;
            _pendingCardCom = null;

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

            // PvP hand-play path: the CardCommand popup is already open (from
            // TapHandAndReadCommands). Fire OnCommand on the matching button
            // directly — re-tapping would close the popup instead of executing.
            if (pendingCardCom != null)
            {
                var btn = FindCardComButton(pendingCardCom, cmdType);
                if (btn != null)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"PvP hand: CardCommand.OnCommand({cmdType})");
                    // The resulting CutinSummon/CutinSet's p1 IS our server seat.
                    DuelEventAnnouncer.ArmLocalSeatCapture();
                    pendingCardCom.OnCommand(btn);
                }
                else
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"PvP hand: no active button matching {cmdType}");
                    ScreenReader.Say(Loc.Get("duel_action_error"));
                }
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
            else if (IsDirectCommand(cmdType, locate) || isFieldAction)
            {
                // Position changes / flip / field activation / extra-deck SummonSp:
                // OnDoCardCommand directly. The CardCommand popup path fails for these.
                var worker = client.worker2d;
                if (worker != null)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"Direct command: OnDoCardCommand({cmdType}) for ({player}, {locate}, {slotIndex})");
                    if (cmdType == Il2CppYgomGame.Duel.Engine.CommandType.SummonSp)
                        DuelEventAnnouncer.ArmLocalSeatCapture();
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
        /// SummonSp on Extra Deck (locate=14) is in the same family: OnTapLocator
        /// won't open the popup for Extra Deck SummonSp either. Hand-card SummonSp
        /// (rare; e.g. Cyber Dragon SS from hand) still uses the popup flow.
        /// </summary>
        private static bool IsDirectCommand(Il2CppYgomGame.Duel.Engine.CommandType cmd, int locate)
        {
            return cmd == Il2CppYgomGame.Duel.Engine.CommandType.TurnAtk
                || cmd == Il2CppYgomGame.Duel.Engine.CommandType.TurnDef
                || cmd == Il2CppYgomGame.Duel.Engine.CommandType.Reverse
                || (cmd == Il2CppYgomGame.Duel.Engine.CommandType.SummonSp
                    && locate == LocateExtra);
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

            // Filter the navigable target list to only OCCUPIED opp slots so the
            // user can't navigate to (or accidentally Enter on) "Slot N: Empty".
            // Without this, pressing Enter on an empty slot fires
            // OnSelectAttacked(opp, emptyLocate, 0); the drag fails and the
            // OnDoCardCommand(Attack) fallback lets the game route the attack
            // to the only legal target — which sounds like a direct attack to
            // the player even though the engine treated it as a battle.
            int player2 = PlayerOpp;
            int oppMonsterCount = 0;
            for (int i = _zoneSlots.Count - 1; i >= 0; i--)
            {
                int loc = _zoneLocates[i];
                // Route through DuelState (hollow-engine-safe in PvP). The old
                // DLL_DuelGetCardUniqueID probe returned 0 for EVERY locate in
                // PvP, leaving oppMonsterCount=0 even when opp had monsters —
                // we'd then misannounce a targeted attack as a direct attack.
                bool occupied = false;
                try { occupied = DuelState.GetFieldCard(player2, loc, 0) != null; }
                catch { occupied = false; }

                if (occupied)
                    oppMonsterCount++;
                else
                {
                    _zoneSlots.RemoveAt(i);
                    _zoneLocates.RemoveAt(i);
                }
            }

            // Also include opponent-owned EMZ slots as attack targets. EMZ is
            // shared, so loc=5/6 may host an opp Synchro/Xyz/Fusion/Link that
            // sits outside the main monster row but is a legal attack target.
            // Probe via DuelState (hollow-engine-safe in PvP). DLL_DuelGetCardUniqueID
            // returns 0 in PvP everywhere, so we can't use the uid probe here.
            foreach (int emzLoc in new[] { LocateEMZLeft, LocateEMZRight })
            {
                try
                {
                    if (DuelState.GetFieldCard(player2, emzLoc, 0) != null)
                    {
                        _zoneSlots.Add(0);
                        _zoneLocates.Add(emzLoc);
                        oppMonsterCount++;
                    }
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
                NavigateTarget(-1);
                return true;
            }
            if (InputManager.TryConsumeKeyDownOrRepeat(KeyCode.RightArrow))
            {
                NavigateTarget(1);
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
        /// Wraps the cursor through only the occupied opp slots filled in by
        /// BeginTargetSelection. Using the standard NavigateCard would walk
        /// the full grid-row width (including the empty columns we filtered
        /// out), letting the user re-encounter the "Slot N: Empty" announcement
        /// the filter was supposed to hide.
        /// </summary>
        private void NavigateTarget(int direction)
        {
            int count = _zoneSlots.Count;
            if (count <= 0)
            {
                ScreenReader.Say(Loc.Get("duel_zone_empty"));
                return;
            }
            _navIndex = (_navIndex + direction + count) % count;
            _currentCol = _navIndex;
            ReadCurrentCard(verbose: false);
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

                // Force-clear the handled flag once the post-confirm timeout
                // elapses, so back-to-back pickers (XYZ second material; multi-
                // tribute) are detected when the game opens prompt 2 fast enough
                // that we never see goActive=false in between.
                if (_emoListHandled
                    && UnityEngine.Time.unscaledTime > _emoListHandledUntil)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        "EmotionalList: post-confirm timeout — clearing handled flag");
                    _emoListHandled = false;
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
            int selectMin = 0;
            try { selectMax = emoList.selectMaxNum; } catch { }
            try { selectMin = emoList.selectMinNum; } catch { }

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"EmotionalList: entered with {_emoListCount} items, " +
                $"selectMax={selectMax} selectMin={selectMin}");

            // Diagnostic dump for material-picker debugging: log each visual
            // card's resolved itemList uniqueId so re-prompt loops (see
            // project_followups.md "Tribute summon EmotionalList re-prompts")
            // are easy to classify. Debug logger only — no Tolk output.
            try
            {
                var cards = emoList.cardList;
                int n = cards?.Count ?? 0;
                for (int i = 0; i < n; i++)
                {
                    int itemIdx = ResolveItemIndex(emoList, i);
                    int uid = 0;
                    int mixedId = 0;
                    try
                    {
                        if (items != null && itemIdx >= 0 && itemIdx < items.Count)
                        {
                            var it = items[itemIdx];
                            if (it != null)
                            {
                                try { uid = it.uniqueId; } catch { }
                                try { mixedId = it.mixedId; } catch { }
                            }
                        }
                    }
                    catch { }
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"EmoList[visual={i}]: itemIdx={itemIdx} uid={uid} mixedId={mixedId}");
                }
            }
            catch { }

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

                int itemIdx = ResolveItemIndex(emoList, index);
                var items = emoList.itemList;
                if (items == null || itemIdx < 0 || itemIdx >= items.Count) return;

                var item = items[itemIdx];
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
                // For field tribute/material selection, mixedId is the
                // CardRoot's uniqueId. Don't go through
                // DLL_DuelGetCardIDByUniqueID2 — that API is hollow in PvP
                // (returns 0 even for live uids) and historically poisons
                // unrelated low integers ("Pierce!" leak). Instead walk
                // cardRoots directly: any matching CardRoot has the cardId on
                // its CardPlane.CardModel even if root.cardId is 0 (face-down
                // rendering), and MakeSnapshot already handles that fallback
                // behind the privacy gate.
                if (cardDbId == 0 && mixedId > 0)
                {
                    var byUid = FindCardRootByUniqueId(mixedId);
                    if (byUid != null)
                    {
                        int recovered = ExtractCardId(byUid);
                        if (recovered > 0 && recovered < 100000) cardDbId = recovered;
                    }
                }
                if (cardDbId == 0 && cid > 0 && cid < 100000)
                    cardDbId = cid;
                // Last resort: mixedId as a direct card DB ID. Real cardDbIds
                // are typically in the thousands; values < 1000 are almost
                // certainly slot indices or uids the previous walk already
                // rejected, so don't fall through to them.
                if (cardDbId == 0 && mixedId >= 1000 && mixedId < 100000)
                    cardDbId = mixedId;

                MelonLoader.MelonLogger.Msg(
                    $"[FieldNav] EmoCard[visual={index}, itemIdx={itemIdx}]: " +
                    $"mixedId={mixedId} uniqueId={uid} cid={cid} resolved={cardDbId}");

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
        /// Walks goManager.cardRoots looking for a CardRoot whose uniqueId
        /// matches. Used to resolve tribute/material selection items whose
        /// `mixedId` is the CardRoot's uid — bypasses the unreliable
        /// DLL_DuelGetCardIDByUniqueID2 (hollow in PvP, name-poisoning in
        /// general).
        /// </summary>
        private static Il2CppYgomGame.Duel.CardRoot FindCardRootByUniqueId(int uid)
        {
            if (uid <= 0) return null;
            try
            {
                var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                if (roots == null) return null;
                for (int i = 0; i < roots.Count; i++)
                {
                    var r = roots[i];
                    if (r != null && r.uniqueId == uid) return r;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Pulls a cardId from a CardRoot, preferring root.cardId and falling
        /// back to root.cardPlane.cardModel.cardId when the root reports 0
        /// (face-down stack/set rendering). Mirror of the same fallback in
        /// DuelState.MakeSnapshot. Used by EmotionalList readers to resolve
        /// tribute/material names without the engine name-resolution API.
        /// </summary>
        private static int ExtractCardId(Il2CppYgomGame.Duel.CardRoot root)
        {
            if (root == null) return 0;
            int id = 0;
            try { id = root.cardId; } catch { }
            if (id > 0) return id;
            try
            {
                var model = root.cardPlane?.cardModel;
                if (model != null) id = model.cardId;
            }
            catch { }
            return id;
        }

        /// <summary>
        /// Maps a visual position in cardList to the corresponding itemList index
        /// using ListCard.Index. cardList is the rendered visual order; itemList
        /// is the underlying data. They are not guaranteed to share an ordering
        /// (the game may sort the visuals), so reading itemList[i] while clicking
        /// cardList[i] can target a different card than the user heard.
        /// Falls back to the visual index when ListCard.Index is unavailable.
        /// </summary>
        private static int ResolveItemIndex(
            Il2CppYgomGame.Duel.EmotionalList emoList, int visualIndex)
        {
            if (emoList == null || visualIndex < 0) return visualIndex;
            try
            {
                var cards = emoList.cardList;
                if (cards == null || visualIndex >= cards.Count) return visualIndex;
                var listCard = cards[visualIndex];
                if (listCard == null) return visualIndex;
                int idx = listCard.Index;
                return idx >= 0 ? idx : visualIndex;
            }
            catch
            {
                return visualIndex;
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

                // Multi-select: fire the decideButton's onClick handler (the
                // same path a mouse click takes). Direct OnDecide() alone
                // doesn't trigger the engine's confirm flow for multi-select —
                // observed in XYZ material picker (selectMax=2): Space ran
                // OnDecide but the game waited for a physical Confirm button
                // click before resolving the summon. Calling onClick.Invoke
                // covers the Htjson-runtime listener that does the real work.
                // For single-select, OnClickCard above already advanced state;
                // OnDecide remains as a defensive call.
                if (selectMax > 1)
                {
                    try
                    {
                        var decideBtn = emoList.decideButton;
                        if (decideBtn != null)
                        {
                            DebugLogger.Log(LogCategory.Game, "FieldNav",
                                "Multi-select: firing decideButton.onClick.Invoke");
                            decideBtn.onClick.Invoke();
                        }
                        else
                        {
                            DebugLogger.Log(LogCategory.Game, "FieldNav",
                                "Multi-select: decideButton null — falling back to OnDecide");
                            emoList.OnDecide();
                        }
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Game, "FieldNav",
                            $"decideButton.onClick.Invoke error: {ex.Message} — falling back");
                        emoList.OnDecide();
                    }
                }
                else
                {
                    emoList.OnDecide();
                }

                _inEmotionalList = false;
                _emoListHandled = true;
                _emoListHandledUntil = UnityEngine.Time.unscaledTime
                    + EmoListHandledTimeout;
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
                if (emoList == null) return Loc.Get("duel_unknown_card");

                int itemIdx = ResolveItemIndex(emoList, index);
                var items = emoList.itemList;
                if (items == null || itemIdx < 0 || itemIdx >= items.Count)
                    return Loc.Get("duel_unknown_card");

                var item = items[itemIdx];
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
                    var byUid = FindCardRootByUniqueId(mixedId);
                    if (byUid != null)
                    {
                        int recovered = ExtractCardId(byUid);
                        if (recovered > 0 && recovered < 100000) cardDbId = recovered;
                    }
                }
                if (cardDbId == 0 && cid > 0 && cid < 100000) cardDbId = cid;
                if (cardDbId == 0 && mixedId >= 1000 && mixedId < 100000) cardDbId = mixedId;

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
        /// PvP hand-play: taps the selected hand card, waits for the
        /// CardCommand popup to populate, then either auto-executes a
        /// single command or fills _commands and enters the action menu.
        /// _pendingCardCom holds the popup ref so ExecuteSelectedCommand
        /// can fire OnCommand directly without re-tapping.
        /// </summary>
        private IEnumerator TapHandAndReadCommands()
        {
            var client = Il2CppYgomGame.Duel.DuelClient.instance;
            if (client == null)
            {
                ScreenReader.Say(Loc.Get("duel_action_error"));
                yield break;
            }

            int slotIndex = _zoneSlots[_navIndex];
            TapHandCard(client, slotIndex);
            yield return ReadPopupAndPresentCommands();
        }

        /// <summary>
        /// PvP field-play: same flow as hand, but taps a field card. Used
        /// when cmdMask is 0 for MyMonster/MySpell/MyExtra zones (i.e., in
        /// PvP where the engine has no command-mask data — we tap to open
        /// the game's own CardCommand popup and scan its buttons).
        /// </summary>
        private IEnumerator TapFieldAndReadCommands()
        {
            var client = Il2CppYgomGame.Duel.DuelClient.instance;
            if (client == null)
            {
                ScreenReader.Say(Loc.Get("duel_action_error"));
                yield break;
            }

            int slotIndex = _zoneSlots[_navIndex];
            int player = GetZonePlayer(_currentZone);
            int locate = _zoneLocates[_navIndex];

            // Extra Deck: OnTapLocator(player, 14, slot) doesn't produce a
            // CardCommand popup (same family as TurnAtk/TurnDef/Reverse). Skip
            // the 30-frame popup wait and fire SummonSp directly. Material
            // selection happens downstream via EmotionalList.
            if (locate == LocateExtra)
            {
                var worker = client.worker2d;
                if (worker != null)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"Extra Deck: OnDoCardCommand(SummonSp) for ({player}, {locate}, {slotIndex})");
                    ScreenReader.Say(Loc.Get("duel_cmd_special_summon"));
                    DuelEventAnnouncer.ArmLocalSeatCapture();
                    worker.OnDoCardCommand(player, locate, slotIndex,
                        Il2CppYgomGame.Duel.Engine.CommandType.SummonSp);
                }
                yield break;
            }

            TapFieldCard(client, player, locate, slotIndex);
            yield return ReadPopupAndPresentCommands();
        }

        /// <summary>
        /// Shared coroutine body for the PvP popup-reading flow. After the
        /// caller has fired its tap, waits for the CardCommand popup, scans
        /// active buttons, and either auto-executes a single command or
        /// enters the accessible action menu. Caller fires
        /// ArmLocalSeatCapture before the final OnCommand so the seat is
        /// captured from the resulting Cutin event.
        /// </summary>
        private IEnumerator ReadPopupAndPresentCommands()
        {
            var client = Il2CppYgomGame.Duel.DuelClient.instance;
            if (client == null) yield break;

            Il2CppYgomGame.Duel.CardCommand cardCom = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                yield return null;

                var worker = client.worker2d;
                cardCom = worker?.cardCom;
                if (cardCom == null || cardCom.myGameObject == null
                    || !cardCom.myGameObject.activeSelf)
                {
                    cardCom = null;
                    continue;
                }

                var probe = cardCom.commands;
                if (probe == null) continue;

                bool anyActive = false;
                for (int i = 0; i < probe.Length; i++)
                {
                    var b = probe[i];
                    if (b != null && b.gameObject != null && b.gameObject.activeSelf)
                    {
                        anyActive = true;
                        break;
                    }
                }
                if (anyActive) break;
                cardCom = null;
            }

            if (cardCom == null)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    "PvP popup: CardCommand never appeared with active buttons");

                // Diagnostic for the "Enter on no-action card leaves the duel
                // wedged" symptom: dump the input/selection state we *exit* in
                // so we can see whether the failed tap left curInputType or
                // HandCards.m_EnableSelect in a state that blocks subsequent
                // P-key phase advance. If a difference shows up between
                // before-tap and after-bail, the cleanup step is "restore
                // those flags / fire a deselect tap" before yield break.
                try
                {
                    var worker = client.worker2d;
                    var hud = client.duelHUD;
                    var handCards = hud?.nearHandCard;
                    string inputType = "?";
                    try { inputType = worker?.curInputType.ToString() ?? "?"; } catch { }
                    string handState = "n/a";
                    if (handCards != null)
                    {
                        try
                        {
                            handState = $"EnableControle={handCards.EnableControle}"
                                + $" m_EnableSelect={handCards.m_EnableSelect}"
                                + $" m_IsBusy={handCards.m_IsBusy}"
                                + $" m_TouchMode={handCards.m_TouchMode}";
                        }
                        catch { }
                    }
                    bool pendingCardCom = _pendingCardCom != null;
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"PvP popup bail: curInputType={inputType} pendingCardCom={pendingCardCom} hand=[{handState}]");
                }
                catch { }

                ScreenReader.Say(Loc.Get("duel_no_actions"));
                yield break;
            }

            _commands.Clear();
            var buttons = cardCom.commands;
            for (int i = 0; i < buttons.Length; i++)
            {
                var btn = buttons[i];
                if (btn == null || btn.gameObject == null || !btn.gameObject.activeSelf)
                    continue;
                _commands.Add(new CommandInfo
                {
                    Type = btn.cmd,
                    Label = Loc.Get(CommandLocKey(btn.cmd)),
                });
            }

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"PvP popup: {_commands.Count} active commands in CardCommand popup");

            if (_commands.Count == 0)
            {
                try { cardCom.Close(); } catch { }
                ScreenReader.Say(Loc.Get("duel_no_actions"));
                yield break;
            }

            _pendingCardCom = cardCom;

            if (_commands.Count == 1 && !NeedsTarget(_commands[0].Type))
            {
                // Single command: auto-execute via OnCommand
                var btn = FindCardComButton(cardCom, _commands[0].Type);
                if (btn != null)
                {
                    ScreenReader.Say(_commands[0].Label);
                    // The resulting CutinSummon/CutinSet's p1 IS our server seat.
                    DuelEventAnnouncer.ArmLocalSeatCapture();
                    cardCom.OnCommand(btn);
                }
                _pendingCardCom = null;
                _commands.Clear();
            }
            else
            {
                _inActionMenu = true;
                _cmdIndex = 0;
                AnnounceCommand();
            }
        }

        /// <summary>Finds the active CardCommand button matching the given command type.</summary>
        private static Il2CppYgomGame.Duel.DuelCommandButton FindCardComButton(
            Il2CppYgomGame.Duel.CardCommand cardCom,
            Il2CppYgomGame.Duel.Engine.CommandType cmd)
        {
            if (cardCom == null) return null;
            var buttons = cardCom.commands;
            if (buttons == null) return null;
            for (int i = 0; i < buttons.Length; i++)
            {
                var btn = buttons[i];
                if (btn == null || btn.gameObject == null || !btn.gameObject.activeSelf)
                    continue;
                if (btn.cmd == cmd) return btn;
            }
            return null;
        }

        /// <summary>Maps a CommandType to its localization key (shared with BuildCommandList).</summary>
        private static string CommandLocKey(Il2CppYgomGame.Duel.Engine.CommandType cmd)
        {
            return cmd switch
            {
                Il2CppYgomGame.Duel.Engine.CommandType.Summon => "duel_cmd_summon",
                Il2CppYgomGame.Duel.Engine.CommandType.SummonSp => "duel_cmd_special_summon",
                Il2CppYgomGame.Duel.Engine.CommandType.SetMonst => "duel_cmd_set_monster",
                Il2CppYgomGame.Duel.Engine.CommandType.Set => "duel_cmd_set",
                Il2CppYgomGame.Duel.Engine.CommandType.Action => "duel_cmd_activate",
                Il2CppYgomGame.Duel.Engine.CommandType.Pendulum => "duel_cmd_pendulum",
                Il2CppYgomGame.Duel.Engine.CommandType.Attack => "duel_cmd_attack",
                Il2CppYgomGame.Duel.Engine.CommandType.Reverse => "duel_cmd_flip_summon",
                Il2CppYgomGame.Duel.Engine.CommandType.TurnAtk => "duel_cmd_to_attack",
                Il2CppYgomGame.Duel.Engine.CommandType.TurnDef => "duel_cmd_to_defense",
                Il2CppYgomGame.Duel.Engine.CommandType.Decide => "duel_cmd_select",
                _ => "duel_cmd_unknown",
            };
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
        /// Retry-escalation state for AdvancePhaseDirect. Static because the
        /// caller is also static and the duel is a singleton flow. Used to
        /// detect the "user pressed P twice from the same Main Phase with no
        /// phase change in between" pattern and pivot the second attempt to
        /// End Phase — handles Duel Links Speed Duel's first-player-turn-1
        /// no-Battle-Phase rule without needing a reliable up-front "is BP
        /// legal?" probe (which we don't have in PvP).
        /// </summary>
        private static float _lastPhaseAttemptTime;
        private static Il2CppYgomGame.Duel.Engine.Phase _lastPhaseAttemptTarget
            = Il2CppYgomGame.Duel.Engine.Phase.Null;
        private static Il2CppYgomGame.Duel.Engine.Phase _lastPhaseAttemptFromPhase
            = Il2CppYgomGame.Duel.Engine.Phase.Null;

        /// <summary>
        /// Advances phase by calling game methods directly (bypasses UI/tutorial locks).
        /// PvP order: PhaseButtonViewController.OnClickPhase() (knows its own
        /// nextPhase and routes through the server). Single-player fallback:
        /// EmotionalCommand.OnBattlePhase/OnEndPhase, then DuelMenu.ChangePhase.
        /// </summary>
        private static void AdvancePhaseDirect()
        {
            try
            {
                // DLL_DuelGetCurrentPhase isn't authoritative in PvP — the
                // server runs the phase machine and the local engine's value
                // stays stuck at Draw=0. DuelState tracks phase from
                // PhaseChange events, which works in both modes.
                var currentPhase = DuelState.CurrentPhase
                    != Il2CppYgomGame.Duel.Engine.Phase.Null
                    ? DuelState.CurrentPhase
                    : (Il2CppYgomGame.Duel.Engine.Phase)
                        (int)Il2CppYgomGame.Duel.Engine.DLL_DuelGetCurrentPhase();
                uint currentPhaseVal = (uint)currentPhase;

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"AdvancePhaseDirect: currentPhase={currentPhase} ({currentPhaseVal})");

                // Diagnostic: capture input/selection state at P-press time.
                // If the user just bailed out of a "no actions available" tap
                // and the duel is wedged, we expect to see curInputType != the
                // expected MainPhase / BattlePhase value here, or HandCards
                // still in a selected state.
                try
                {
                    var client = Il2CppYgomGame.Duel.DuelClient.instance;
                    var worker = client?.worker2d;
                    var handCards = client?.duelHUD?.nearHandCard;
                    string inputType = "?";
                    try { inputType = worker?.curInputType.ToString() ?? "?"; } catch { }
                    string handState = "n/a";
                    if (handCards != null)
                    {
                        try
                        {
                            handState = $"EnableControle={handCards.EnableControle}"
                                + $" m_EnableSelect={handCards.m_EnableSelect}"
                                + $" m_IsBusy={handCards.m_IsBusy}"
                                + $" m_TouchMode={handCards.m_TouchMode}";
                        }
                        catch { }
                    }
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"AdvancePhaseDirect state: curInputType={inputType} hand=[{handState}]");
                }
                catch { }

                // First try: write a phase-move request to the engine's
                // command queue. In single-player this works whenever the
                // phase is movable. In PvP the engine forwards the request
                // to the server, which then sends back a PhaseChange event.
                // DLL_DuelComGetMovablePhase returns 0 in PvP but the move
                // call itself can still succeed — the bitmask check in
                // AdvancePhase() is too conservative for PvP.
                Il2CppYgomGame.Duel.Engine.Phase target
                    = Il2CppYgomGame.Duel.Engine.Phase.Null;
                if (currentPhase == Il2CppYgomGame.Duel.Engine.Phase.Main1
                    || currentPhase == Il2CppYgomGame.Duel.Engine.Phase.Main2)
                {
                    target = Il2CppYgomGame.Duel.Engine.Phase.Battle;
                }
                else if (currentPhase == Il2CppYgomGame.Duel.Engine.Phase.Battle)
                {
                    target = Il2CppYgomGame.Duel.Engine.Phase.End;
                }

                // Retry-escalation for the first-player-turn-1 lockup:
                // dirBpButton.activeInHierarchy is unreliable as a "BP legal?"
                // probe because the button lives inside a collapsed phase
                // menu — it's almost always inactive, including during normal
                // turns when BP IS legal. So we can't tell up-front whether
                // the server will accept Battle.
                //
                // Instead: if the previous P press also asked for Battle from
                // the SAME currentPhase and the phase still hasn't moved,
                // escalate this attempt to End Phase. Effect: turn-1 first-
                // player presses P once, hears "Moving to Battle Phase" but
                // nothing happens, presses P again, hears "Moving to End
                // Phase" and the server accepts. Normal turns transition on
                // the first press so the escalation never triggers.
                if (target == Il2CppYgomGame.Duel.Engine.Phase.Battle
                    && _lastPhaseAttemptTarget == Il2CppYgomGame.Duel.Engine.Phase.Battle
                    && _lastPhaseAttemptFromPhase == currentPhase
                    && (UnityEngine.Time.unscaledTime - _lastPhaseAttemptTime) < 10f)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        "  Previous Battle Phase attempt didn't advance — " +
                        "escalating retry to End Phase");
                    target = Il2CppYgomGame.Duel.Engine.Phase.End;
                }
                _lastPhaseAttemptTime = UnityEngine.Time.unscaledTime;
                _lastPhaseAttemptTarget = target;
                _lastPhaseAttemptFromPhase = currentPhase;

                if (target != Il2CppYgomGame.Duel.Engine.Phase.Null)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"  Trying DLL_DuelComMovePhase({target} = {(int)target})");
                    try
                    {
                        Il2CppYgomGame.Duel.Engine.DLL_DuelComMovePhase((int)target);
                    }
                    catch (Exception ex)
                    {
                        DebugLogger.Log(LogCategory.Game, "FieldNav",
                            $"  DLL_DuelComMovePhase threw: {ex.Message}");
                    }

                    // Scene scan: log every ACTIVE UI.Button whose name or
                    // ancestor name hints at phase control. This is the
                    // user's "what would a mouse click do?" lookup — once
                    // we identify the right Button by name, we can target
                    // it directly via onClick.Invoke().
                    DumpPhaseRelatedButtons();
                }

                // PvP-aware path: the game's own PhaseButtonViewController knows
                // its nextPhase and OnClickPhase() routes through the server.
                // DLL_DuelGetCurrentPhase isn't authoritative in PvP (server runs
                // the phase machine), so prefer this over heuristics on the
                // cached engine phase.
                var phaseBtn = UnityEngine.Object.FindObjectOfType<
                    Il2CppYgomGame.Duel.PhaseButtonViewController>();
                if (phaseBtn != null)
                {
                    var next = phaseBtn.nextPhase;
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"Calling PhaseButtonViewController.OnClickPhase() (nextPhase={next})");
                    phaseBtn.OnClickPhase();
                    ScreenReader.Say(Loc.Get("duel_advancing_phase",
                        DuelEventAnnouncer.GetPhaseName(next)));
                    return;
                }

                // EmotionalCommand owns the in-game phase buttons. The
                // direct method calls (OnBattlePhase / OnEndPhase) are no-ops
                // in PvP — the server doesn't see them as a phase request.
                // The actual UI button click fires the right server message,
                // so we invoke onClick on the Button. There can be multiple
                // EmotionalCommand instances (one per player or per
                // perspective); iterate and try each, ignoring the
                // interactable flag (onClick.Invoke bypasses it anyway).
                //
                // Derive isBattleAdvance / isEndAdvance from `target` (post-
                // pivot) rather than currentPhase so that when we've decided
                // to skip BP and request End, the EmCmd loop targets
                // dirEpButton accordingly.
                bool isBattleAdvance =
                    target == Il2CppYgomGame.Duel.Engine.Phase.Battle;
                bool isEndAdvance =
                    target == Il2CppYgomGame.Duel.Engine.Phase.End;

                if (isBattleAdvance || isEndAdvance)
                {
                    var targetPhase = isBattleAdvance
                        ? Il2CppYgomGame.Duel.Engine.Phase.Battle
                        : Il2CppYgomGame.Duel.Engine.Phase.End;
                    string btnName = isBattleAdvance ? "dirBpButton" : "dirEpButton";

                    var allEm = UnityEngine.Object.FindObjectsOfType<
                        Il2CppYgomGame.Duel.EmotionalCommand>(includeInactive: true);
                    int emCount = allEm?.Length ?? 0;
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"AdvancePhaseDirect: found {emCount} EmotionalCommand instance(s)");

                    bool fired = false;
                    if (allEm != null)
                    {
                        for (int i = 0; i < allEm.Length; i++)
                        {
                            var em = allEm[i];
                            if (em == null) continue;

                            UnityEngine.UI.Button btn = null;
                            try
                            {
                                btn = isBattleAdvance ? em.dirBpButton : em.dirEpButton;
                            }
                            catch { }
                            if (btn == null) continue;

                            bool active = false, interactable = false;
                            try { active = btn.gameObject != null
                                && btn.gameObject.activeInHierarchy; } catch { }
                            try { interactable = btn.interactable; } catch { }

                            DebugLogger.Log(LogCategory.Game, "FieldNav",
                                $"  EmCmd[{i}].{btnName}: active={active} interactable={interactable} " +
                                $"hasOnClick={btn.onClick != null}");

                            // Prefer active+interactable, but if none such found
                            // after the loop we fall back to firing on the first
                            // available below.
                            if (active && interactable && btn.onClick != null)
                            {
                                DebugLogger.Log(LogCategory.Game, "FieldNav",
                                    $"  -> Firing onClick.Invoke() on EmCmd[{i}].{btnName}");
                                try { btn.onClick.Invoke(); fired = true; break; }
                                catch (Exception ex)
                                {
                                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                                        $"  onClick.Invoke threw: {ex.Message}");
                                }
                            }
                        }

                        // No active+interactable button found — try invoking
                        // any non-null one anyway (onClick.Invoke bypasses
                        // interactable). The button being inactive usually
                        // means the wrong EmCmd instance, but the listener
                        // might still route correctly. Single-player works
                        // through this path even when the button is inactive.
                        if (!fired)
                        {
                            for (int i = 0; i < allEm.Length && !fired; i++)
                            {
                                var em = allEm[i];
                                if (em == null) continue;
                                UnityEngine.UI.Button btn = null;
                                try { btn = isBattleAdvance ? em.dirBpButton : em.dirEpButton; }
                                catch { }
                                if (btn == null || btn.onClick == null) continue;
                                DebugLogger.Log(LogCategory.Game, "FieldNav",
                                    $"  Fallback: firing onClick.Invoke() on inactive EmCmd[{i}].{btnName}");
                                try { btn.onClick.Invoke(); fired = true; }
                                catch (Exception ex)
                                {
                                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                                        $"  onClick.Invoke threw: {ex.Message}");
                                }
                            }
                        }

                        // Last resort: direct method call on the first instance
                        if (!fired && allEm.Length > 0 && allEm[0] != null)
                        {
                            DebugLogger.Log(LogCategory.Game, "FieldNav",
                                "  Last resort: direct EmotionalCommand method call");
                            try
                            {
                                if (isBattleAdvance) allEm[0].OnBattlePhase();
                                else allEm[0].OnEndPhase();
                                fired = true;
                            }
                            catch (Exception ex)
                            {
                                DebugLogger.Log(LogCategory.Game, "FieldNav",
                                    $"  Direct method threw: {ex.Message}");
                            }
                        }
                    }

                    if (fired)
                    {
                        ScreenReader.Say(Loc.Get("duel_advancing_phase",
                            DuelEventAnnouncer.GetPhaseName(targetPhase)));
                        return;
                    }
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        "AdvancePhaseDirect: no EmotionalCommand button worked");
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

        /// <summary>
        /// Diagnostic: log every active UI.Button in the scene whose name or
        /// any ancestor's name hints at phase control. The user's "what
        /// would a mouse click do?" lookup — once we see what's actually in
        /// the scene during BP/MainPhase, we can target the right Button
        /// by name and invoke its onClick directly.
        /// </summary>
        private static void DumpPhaseRelatedButtons()
        {
            try
            {
                var buttons = UnityEngine.Object.FindObjectsOfType<UnityEngine.UI.Button>();
                int hits = 0;
                for (int i = 0; i < buttons.Length; i++)
                {
                    var btn = buttons[i];
                    if (btn == null) continue;
                    var go = btn.gameObject;
                    if (go == null) continue;

                    // Walk up the parent chain, collect path; check for hints
                    string path = go.name;
                    bool hint = NameHintsPhase(go.name);
                    var t = go.transform?.parent;
                    int depth = 0;
                    while (t != null && depth < 6)
                    {
                        path = t.name + "/" + path;
                        if (NameHintsPhase(t.name)) hint = true;
                        t = t.parent;
                        depth++;
                    }

                    if (!hint) continue;

                    bool active = false, interactable = false;
                    int listenerCount = 0;
                    try { active = go.activeInHierarchy; } catch { }
                    try { interactable = btn.interactable; } catch { }
                    try { listenerCount = btn.onClick?.GetPersistentEventCount() ?? 0; } catch { }

                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        $"  PhaseBtn[{hits}]: path='{path}' active={active} " +
                        $"interactable={interactable} persistentListeners={listenerCount}");
                    hits++;
                }

                if (hits == 0)
                {
                    DebugLogger.Log(LogCategory.Game, "FieldNav",
                        "  No phase-hinted UI.Button found in scene.");
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"DumpPhaseRelatedButtons error: {ex.Message}");
            }
        }

        private static bool NameHintsPhase(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            string l = n.ToLowerInvariant();
            return l.Contains("phase") || l.Contains("bp") || l.Contains("ep")
                || l.Contains("battle") || l.Contains("end");
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
                int[] locates = zone switch
                {
                    Zone.MyMonster or Zone.OppMonster => MonsterLocates,
                    Zone.MySpell or Zone.OppSpell => SpellLocates,
                    Zone.ExtraMonster => EMZLocates,
                    _ => MonsterLocates
                };
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
                if (DuelState.GetFieldCard(player, locate, 0) != null)
                {
                    _zoneSlots.Add(0);
                    _zoneLocates.Add(locate);
                }
            }
            else
            {
                // Stack zone (hand, grave, banished, extra deck) — DuelState
                // routes hand counts through the visual layer (HandCardManager)
                // and other zones through the engine with a visual-layer
                // fallback, so this works uniformly in single-player and PvP.
                int locate = GetSideZoneLocate(zone);
                int count = DuelState.GetCardCount(player, locate);
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

        // CountFieldCards moved to DuelState — uses visual layer for
        // uniform single-player / PvP behavior.

        /// <summary>Whether this zone is a grid zone with fixed field slot columns.</summary>
        private static bool IsFieldSlotZone(Zone zone)
        {
            return zone == Zone.MyMonster || zone == Zone.OppMonster
                || zone == Zone.MySpell || zone == Zone.OppSpell
                || zone == Zone.ExtraMonster;
        }

        /// <summary>Whether this zone is a single-card field slot (field spell).</summary>
        private static bool IsSingleSlotZone(Zone zone)
        {
            return zone == Zone.MyFieldSpell || zone == Zone.OppFieldSpell;
        }

        /// <summary>
        /// Resolves the player whose card occupies the given EMZ slot. EMZ is
        /// shared — the slot belongs to whichever player summoned there. Falls
        /// back to PlayerMe when the slot is empty (so Enter on empty EMZ tries
        /// the user's command path and produces a benign "no actions" instead
        /// of mis-routing to the opponent).
        /// </summary>
        private static int GetEMZOwner(int locate)
        {
            try
            {
                if (DuelState.GetFieldCard(PlayerMe, locate, 0) != null) return PlayerMe;
                if (DuelState.GetFieldCard(PlayerOpp, locate, 0) != null) return PlayerOpp;
            }
            catch { /* fall through */ }
            return PlayerMe;
        }

        private static int GetZonePlayer(Zone zone)
        {
            return zone switch
            {
                Zone.OppMonster or Zone.OppSpell or Zone.OppFieldSpell
                    or Zone.OppGrave or Zone.OppBanished or Zone.OppExtra => PlayerOpp,
                // ExtraMonster is shared; default to PlayerMe at the row level.
                // Per-slot ownership is resolved via GetEMZOwner(locate) in
                // ReadCurrentCard and anywhere else slot owner matters.
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
                Zone.ExtraMonster => Loc.Get("duel_zone_emz"),
                _ => "Unknown"
            };
        }

        #endregion

        #region Card Helpers

        // Field-card lookup, hand-card access, generic GetCardCount, and
        // per-zone resolvers all moved to DuelState — single source of truth
        // backed by the visual layer (worker3d.goManager.cardRoots,
        // HandCardManager) plus event-tracked counters. This keeps the
        // navigator focused on input/UI logic.

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
