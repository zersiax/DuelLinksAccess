using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace DuelLinksAccess
{
    /// <summary>
    /// Handles keyboard navigation of the duel field: zones, cards, and commands.
    ///
    /// Zone navigation (Tab / Shift+Tab):
    ///   Hand > Your Monsters > Your Spells > Opp Monsters > Opp Spells > Graveyard
    ///   Skips empty zones automatically.
    ///
    /// Card navigation (Left / Right):
    ///   Cycles through occupied slots in the current zone.
    ///
    /// Actions (Enter):
    ///   Queries available commands for the selected card.
    ///   Single command: executes directly. Multiple: opens action menu (Up/Down/Enter/Escape).
    ///
    /// Other keys:
    ///   C — Re-read current card (verbose with stats and available actions)
    ///   F — Field summary (card counts per zone)
    ///   P — Advance to next phase
    ///   Escape — Close action menu or exit navigation
    /// </summary>
    public class DuelFieldNavigator
    {
        #region Constants

        // Zone identifiers for Engine DLL_ functions
        // Discovered empirically via debug scan (NOT standard OCG constants):
        //   locate=2  → Monster Zone (confirmed: opponent's Saggi found here)
        //   locate=13 → Hand (confirmed: drawn Winged Dragon found here)
        //   locate=15 → Deck (confirmed: 19 remaining cards)
        //   locate=3  → Spell/Trap Zone (unconfirmed — needs testing with set spells)
        //   locate=4  → Graveyard (unconfirmed — needs testing with destroyed cards)
        private const int LocateHand = 13;
        private const int LocateMonster = 2;
        private const int LocateSpell = 3;    // TODO: verify when spell/trap cards are set
        private const int LocateGrave = 4;    // TODO: verify when cards are in graveyard

        // Card position bitmasks returned by DLL_DuelGetCardFace
        private const int PosFaceUpAtk = 0x1;
        private const int PosFaceUpDef = 0x4;
        private const int PosFaceUp = PosFaceUpAtk | PosFaceUpDef;  // 0x5
        private const int PosDefense = PosFaceUpDef | 0x8;          // 0xC

        // Speed Duel zone sizes
        private const int MaxMonsterSlots = 3;
        private const int MaxSpellSlots = 3;

        // Player identifiers (matches existing DuelEventAnnouncer convention)
        private const int PlayerMe = 0;
        private const int PlayerOpp = 1;

        #endregion

        #region Types

        /// <summary>Navigable zones on the duel field.</summary>
        public enum Zone
        {
            Hand,
            MyMonster,
            MySpell,
            OppMonster,
            OppSpell,
            MyGrave,
        }


        #endregion

        #region Fields

        private static readonly Zone[] ZoneOrder =
        {
            Zone.Hand,
            Zone.MyMonster,
            Zone.MySpell,
            Zone.OppMonster,
            Zone.OppSpell,
            Zone.MyGrave,
        };

        // Navigation state
        private Zone _currentZone = Zone.Hand;
        private int _navIndex;
        private readonly List<int> _zoneSlots = new();
        private bool _isNavigating;

        // Action menu state
        private bool _inActionMenu;
        private readonly List<CommandInfo> _commands = new();
        private int _cmdIndex;

        // Target selection state (attack targeting)
        private bool _awaitingTarget;
        private int _attackerPlayer;
        private int _attackerLocate;
        private int _attackerSlot;

        // Card selection state (tribute, material, etc.)
        private bool _inCardSelect;
        private readonly List<int> _selectLocations = new();
        private int _selectIndex;

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

        /// <summary>Whether the user has started field navigation.</summary>
        public bool IsNavigating => _isNavigating;

        #endregion

        #region Public Methods

        /// <summary>Resets all navigation state. Call on duel end.</summary>
        public void Reset()
        {
            _currentZone = Zone.Hand;
            _navIndex = 0;
            _zoneSlots.Clear();
            _isNavigating = false;
            _awaitingTarget = false;
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
            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                CycleZone(shift ? -1 : 1);
                return true;
            }

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

            if (InputManager.TryConsumeKeyDown(KeyCode.C))
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
            // Card selection (tribute/material) takes highest priority
            if (_inCardSelect)
                return ProcessCardSelectInput();
            // Check if card selection just opened (even if we weren't in it yet)
            if (CheckForCardSelection())
                return ProcessCardSelectInput();
            if (_awaitingTarget)
                return ProcessTargetSelectionInput();
            if (_inActionMenu)
                return ProcessActionMenuInput();
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
                int myMon = CountOccupiedSlots(PlayerMe, LocateMonster, MaxMonsterSlots);
                int mySp = CountOccupiedSlots(PlayerMe, LocateSpell, MaxSpellSlots);
                int myGr = GetCardCount(PlayerMe, LocateGrave);
                int oppMon = CountOccupiedSlots(PlayerOpp, LocateMonster, MaxMonsterSlots);
                int oppSp = CountOccupiedSlots(PlayerOpp, LocateSpell, MaxSpellSlots);
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

                            // For non-zero counts, also try getting the first card's unique ID
                            try
                            {
                                int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                                    player, loc, 0);
                                uint cardId = uid > 0
                                    ? Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(uid)
                                    : 0;
                                string name = cardId > 0 ? ResolveCardName(cardId) : "(no name)";
                                MelonLoader.MelonLogger.Msg(
                                    $"    -> slot 0: uid={uid}, cardId={cardId}, name={name}");
                            }
                            catch (Exception ex)
                            {
                                MelonLoader.MelonLogger.Msg($"    -> slot 0 error: {ex.Message}");
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
                                MelonLoader.MelonLogger.Msg(
                                    $"  GetCardUniqueID(player={player}, pos={pos} [0x{pos:X2}], slot={slot}) = uid {uid}, cardId={cardId}, name={name}");
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
            // Tab / Shift+Tab: cycle zones
            if (InputManager.TryConsumeKeyDown(KeyCode.Tab))
            {
                bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                CycleZone(shift ? -1 : 1);
                return true;
            }

            // Left/Right: navigate cards within zone
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

            // C: re-read current card (verbose)
            if (InputManager.TryConsumeKeyDown(KeyCode.C))
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

        private void CycleZone(int direction)
        {
            _isNavigating = true;
            int startIdx = Array.IndexOf(ZoneOrder, _currentZone);
            int nextIdx = startIdx;

            // Find next non-empty zone
            for (int i = 0; i < ZoneOrder.Length; i++)
            {
                nextIdx = (nextIdx + direction + ZoneOrder.Length) % ZoneOrder.Length;
                Zone candidate = ZoneOrder[nextIdx];
                RefreshZoneSlots(candidate);
                if (_zoneSlots.Count > 0)
                {
                    _currentZone = candidate;
                    _navIndex = 0;
                    AnnounceZone();
                    ReadCurrentCard(verbose: false, queued: true);
                    return;
                }
            }

            ScreenReader.Say(Loc.Get("duel_no_cards"));
        }

        private void NavigateCard(int direction)
        {
            if (!_isNavigating)
            {
                // First navigation press enters hand by default
                _isNavigating = true;
                _currentZone = Zone.Hand;
                RefreshCurrentZone();
                if (_zoneSlots.Count > 0)
                {
                    _navIndex = 0;
                    AnnounceZone();
                    ReadCurrentCard(verbose: false, queued: true);
                }
                else
                {
                    // Hand empty, try cycling to first non-empty zone
                    CycleZone(1);
                }
                return;
            }

            RefreshCurrentZone();
            if (_zoneSlots.Count == 0)
            {
                ScreenReader.Say(Loc.Get("duel_zone_empty"));
                return;
            }

            _navIndex = (_navIndex + direction + _zoneSlots.Count) % _zoneSlots.Count;
            ReadCurrentCard(verbose: false);
        }

        private void AnnounceZone()
        {
            string zoneName = GetZoneName(_currentZone);
            ScreenReader.Say(Loc.Get("duel_zone_entered", zoneName, _zoneSlots.Count));
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
            int locate = GetZoneLocate(_currentZone);

            try
            {
                int uniqueId = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                    player, locate, slotIndex);

                if (uniqueId <= 0)
                {
                    ScreenReader.Say(Loc.Get("duel_card_position",
                        _navIndex + 1, _zoneSlots.Count, Loc.Get("duel_empty_slot")));
                    return;
                }

                uint cardDbId = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(uniqueId);
                string cardName = ResolveCardName(cardDbId);

                bool isHand = _currentZone == Zone.Hand;
                bool isMyCard = player == PlayerMe;
                bool isMonsterZone = _currentZone == Zone.MyMonster
                    || _currentZone == Zone.OppMonster;

                // --- Build announcement parts ---
                var parts = new List<string>();

                // Face/position info (field cards only)
                if (!isHand)
                {
                    int face = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardFace(
                        player, locate, slotIndex);
                    bool isFaceUp = (face & PosFaceUp) != 0;

                    // Don't reveal opponent's face-down card names
                    if (!isFaceUp && !isMyCard)
                        cardName = Loc.Get("duel_face_down_card");

                    parts.Add(isFaceUp
                        ? Loc.Get("duel_face_up")
                        : Loc.Get("duel_face_down"));

                    if (isMonsterZone)
                    {
                        bool isDefense = (face & PosDefense) != 0;
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

                // Position header: "1 of 3: Blue-Eyes White Dragon"
                string header = Loc.Get("duel_card_position",
                    _navIndex + 1, _zoneSlots.Count, cardName);

                // Combine header with detail parts
                string details = parts.Count > 0 ? string.Join(", ", parts) : "";
                string announcement = details.Length > 0
                    ? $"{header}. {details}"
                    : header;

                // Verbose: show available commands
                if (verbose && isMyCard)
                {
                    uint cmdMask = SafeGetCommandMask(player, locate, slotIndex);
                    if (cmdMask != 0)
                    {
                        string cmds = DescribeCommandMask(cmdMask);
                        announcement += ". " + Loc.Get("duel_available_actions", cmds);
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
            int locate = GetZoneLocate(_currentZone);

            try
            {
                uint cmdMask = SafeGetCommandMask(player, locate, slotIndex);

                if (cmdMask == 0)
                {
                    if (Main.DebugMode)
                        DumpCommandMaskScan(player, locate, slotIndex);

                    // Fallback: if no commands but we're on our own field card,
                    // try tapping it via OnTapLocator. This handles tribute/material
                    // selection where the game waits for taps, not commands.
                    if (_currentZone == Zone.MyMonster || _currentZone == Zone.MySpell)
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
                    // Single command that doesn't need a target: auto-execute
                    var cmdType = _commands[0].Type;
                    string label = _commands[0].Label;
                    ScreenReader.Say(label);

                    TapCardForZone(client, player, locate, slotIndex);
                    MelonCoroutines.Start(AutoClickCommandButton(cmdType));
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
            int locate = GetZoneLocate(_currentZone);

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

            TapCardForZone(client, player, locate, slotIndex);
            MelonCoroutines.Start(AutoClickCommandButton(cmdType));
        }

        /// <summary>
        /// Checks whether a command type requires the user to select a target.
        /// </summary>
        private static bool NeedsTarget(Il2CppYgomGame.Duel.Engine.CommandType cmd)
        {
            return cmd == Il2CppYgomGame.Duel.Engine.CommandType.Attack;
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

            // Switch to opponent's monster zone for target picking
            _currentZone = Zone.OppMonster;
            RefreshCurrentZone();

            if (_zoneSlots.Count > 0)
            {
                _navIndex = 0;
                ScreenReader.Say(Loc.Get("duel_select_target"));
                ReadCurrentCard(verbose: false);
            }
            else
            {
                // No opponent monsters — direct attack
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    "No opponent monsters — attempting direct attack");
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
                ScreenReader.Say(Loc.Get("duel_target_cancelled"));
                return true;
            }
            // C to re-read target card
            if (InputManager.TryConsumeKeyDown(KeyCode.C))
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

            MelonCoroutines.Start(AttackDragSequence(
                _attackerPlayer, _attackerLocate, _attackerSlot,
                directAttack, targetSlot));
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
            bool directAttack, int targetSlot)
        {
            var client = Il2CppYgomGame.Duel.DuelClient.instance;
            var worker = client?.worker2d;
            if (worker == null)
            {
                ScreenReader.Say(Loc.Get("duel_action_error"));
                yield break;
            }

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
                // vs 0x4 (bit 2 = LocateMonster) for targeted attacks.
                // The mask bit positions map to the position parameter in OnSelectAttacked.
                // So direct attack uses position=7, targeted uses position=2.
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

                ScreenReader.Say(Loc.Get("duel_direct_attack"));
            }
            else
            {
                // Targeted attack: select the specific opponent monster
                int tgtSlot = targetSlot;

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"Attack step 2: OnSelectAttacked({PlayerOpp}, {LocateMonster}, {tgtSlot})");
                worker.OnSelectAttacked(PlayerOpp, LocateMonster, tgtSlot);

                LogAttackState(worker, "after OnSelectAttacked");

                for (int i = 0; i < 5; i++)
                    yield return null;

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"Attack step 3: TapUpField({PlayerOpp}, {LocateMonster}, {tgtSlot})");
                worker.OnTapUpField(PlayerOpp, LocateMonster, tgtSlot);

                LogAttackState(worker, "after TapUpField (targeted)");
            }
        }

        /// <summary>Dumps all attack-related worker2d fields for diagnostics.</summary>
        private static void LogAttackState(
            Il2CppYgomGame.Duel.RunEffectWorker2D worker, string label)
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
                        PlayerMe, LocateMonster);
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
            if (InputManager.TryConsumeKeyDown(KeyCode.C))
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

                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"ResolveCard: loc={location} (0x{location:X}) -> player={player} pos={position} (0x{position:X})");

                // Try to get card using position as zone type with slot 0
                // If the location list has multiple entries for the same zone,
                // we try each slot index to find a match
                string name = TryGetCardName(player, position, 0);
                if (name != null) return name;

                // Position might encode zone+slot together.
                // Try common zone types and scan slots.
                int[] zonesToTry = { LocateMonster, LocateSpell, LocateHand, LocateGrave };
                foreach (int zone in zonesToTry)
                {
                    int maxSlots = zone == LocateMonster || zone == LocateSpell
                        ? MaxMonsterSlots : 10;
                    for (int slot = 0; slot < maxSlots; slot++)
                    {
                        name = TryGetCardName(player, zone, slot);
                        if (name != null)
                        {
                            DebugLogger.Log(LogCategory.Game, "FieldNav",
                                $"ResolveCard: found card at player={player} zone={zone} slot={slot}");
                            return name;
                        }
                    }
                }

                // Fallback: describe the position
                string zoneName = position switch
                {
                    2 => player == PlayerMe ? "Your monster zone" : "Opponent monster zone",
                    3 => player == PlayerMe ? "Your spell zone" : "Opponent spell zone",
                    13 => "Hand",
                    4 => player == PlayerMe ? "Your graveyard" : "Opponent graveyard",
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

            // Count ALL monsters in zone 2 across all 3 slots
            try
            {
                MelonLoader.MelonLogger.Msg("  Monster zone scan (3 slots):");
                for (int i = 0; i < MaxMonsterSlots; i++)
                {
                    int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(PlayerMe, LocateMonster, i);
                    uint cardId = uid > 0 ? Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(uid) : 0;
                    MelonLoader.MelonLogger.Msg($"    slot[{i}]: uid={uid} cardId={cardId}");
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

            try
            {
                var inputType = worker.curInputType;
                DebugLogger.Log(LogCategory.Game, "FieldNav",
                    $"curInputType={inputType} before tap");
            }
            catch { }

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"Calling OnTapLocator({player}, {locate}, {slotIndex})");
            worker.OnTapLocator(player, locate, slotIndex);

            DebugLogger.Log(LogCategory.Game, "FieldNav", "OnTapLocator returned");

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
        /// Rebuilds _zoneSlots for the given zone by scanning for occupied slots.
        /// For hand/graveyard: sequential indices 0..count-1.
        /// For monster/spell zones: scans fixed slots and collects occupied ones.
        /// </summary>
        private void RefreshZoneSlots(Zone zone)
        {
            _zoneSlots.Clear();
            int player = GetZonePlayer(zone);
            int locate = GetZoneLocate(zone);

            if (IsFixedSlotZone(zone))
            {
                int maxSlots = GetMaxSlots(zone);
                for (int i = 0; i < maxSlots; i++)
                {
                    try
                    {
                        int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                            player, locate, i);
                        if (uid > 0) _zoneSlots.Add(i);
                    }
                    catch { /* Empty slot or error */ }
                }
            }
            else
            {
                int count = GetCardCount(player, locate);
                for (int i = 0; i < count; i++)
                    _zoneSlots.Add(i);
            }

            DebugLogger.Log(LogCategory.Game, "FieldNav",
                $"RefreshZone {zone}: player={player} locate={locate} found={_zoneSlots.Count} cards");
        }

        private static int CountOccupiedSlots(int player, int locate, int maxSlots)
        {
            int count = 0;
            for (int i = 0; i < maxSlots; i++)
            {
                try
                {
                    int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                        player, locate, i);
                    if (uid > 0) count++;
                }
                catch { }
            }
            return count;
        }

        private static bool IsFixedSlotZone(Zone zone)
        {
            return zone == Zone.MyMonster || zone == Zone.OppMonster
                || zone == Zone.MySpell || zone == Zone.OppSpell;
        }

        private static int GetMaxSlots(Zone zone)
        {
            return zone switch
            {
                Zone.MyMonster or Zone.OppMonster => MaxMonsterSlots,
                Zone.MySpell or Zone.OppSpell => MaxSpellSlots,
                _ => 0
            };
        }

        private static int GetZonePlayer(Zone zone)
        {
            return zone switch
            {
                Zone.OppMonster or Zone.OppSpell => PlayerOpp,
                _ => PlayerMe
            };
        }

        private static int GetZoneLocate(Zone zone)
        {
            return zone switch
            {
                Zone.Hand => LocateHand,
                Zone.MyMonster or Zone.OppMonster => LocateMonster,
                Zone.MySpell or Zone.OppSpell => LocateSpell,
                Zone.MyGrave => LocateGrave,
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
                Zone.OppMonster => Loc.Get("duel_zone_opp_monsters"),
                Zone.OppSpell => Loc.Get("duel_zone_opp_spells"),
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
