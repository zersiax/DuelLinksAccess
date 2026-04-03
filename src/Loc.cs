using System.Collections.Generic;

namespace DuelLinksAccess
{
    /// <summary>
    /// Centralized localization for the accessibility mod.
    /// English only for now, but all strings go through here for easy expansion.
    ///
    /// Usage:
    ///   Loc.Get("key")              — get string
    ///   Loc.Get("key", arg1, arg2)  — get string with placeholders {0}, {1}
    /// </summary>
    public static class Loc
    {
        #region Fields

        private static bool _initialized = false;
        private static readonly Dictionary<string, string> _english = new();

        #endregion

        #region Public Methods

        /// <summary>
        /// Initializes localization. Call once at mod startup.
        /// </summary>
        public static void Initialize()
        {
            InitializeStrings();
            _initialized = true;
        }

        /// <summary>
        /// Gets a localized string.
        /// </summary>
        public static string Get(string key)
        {
            if (!_initialized) Initialize();

            if (_english.TryGetValue(key, out string value))
                return value;

            // Fallback: return key itself (helps with debugging)
            return key;
        }

        /// <summary>
        /// Gets a localized string with placeholders.
        /// Uses {0}, {1}, {2} etc. as placeholders.
        /// </summary>
        public static string Get(string key, params object[] args)
        {
            string template = Get(key);
            try
            {
                return string.Format(template, args);
            }
            catch
            {
                return template;
            }
        }

        #endregion

        #region Private Methods

        /// <summary>
        /// All translations defined here.
        /// Convention: [handler]_[action] for handler-specific strings.
        /// </summary>
        private static void InitializeStrings()
        {
            // ===== GENERAL =====
            _english["mod_loaded"] = "Duel Links Access loaded. F1 for help.";
            _english["help_text"] = "Key bindings: Up down arrows navigate items. Enter activate. Escape or Backspace go back. Space rescan screen. Tab re-read current item. During duel: Tab and Shift Tab cycle zones. Left right navigate cards. Enter open actions. C re-read card. F field summary. P advance phase. S status. L event log. F1 Help. F12 Toggle debug mode. Control R Repeat last announcement. Control F11 Mod settings.";
            _english["debug_mode"] = "Debug mode {0}";

            // ===== SETTINGS =====
            _english["settings_opened"] = "Mod settings opened";
            _english["settings_closed"] = "Mod settings closed and saved";
            _english["settings_item"] = "{0} of {1}: {2}, {3}. Left right to change.";

            // ===== SCREEN CHANGES =====
            _english["screen_home"] = "Home screen";
            _english["screen_title"] = "Title screen";
            _english["title_press_enter"] = "Title screen. Press Enter to continue.";
            _english["screen_duel"] = "";
            _english["screen_deck"] = "Deck editor";
            _english["screen_shop"] = "Shop";
            _english["screen_dialog"] = "Dialog";
            _english["screen_card_detail"] = "Card detail";
            _english["screen_gate"] = "Gate";
            _english["screen_store"] = "Store";
            _english["screen_notices"] = "Notices";
            _english["no_repeat"] = "Nothing to repeat";

            // ===== DIALOGS =====
            _english["dialog_buttons"] = "{0} items. Up down to navigate, Enter to press, Left right for sliders.";
            _english["dialog_button_item"] = "{0} of {1}: {2}";
            _english["dialog_slider_item"] = "{0} of {1}: Slider, value {2}, range {3} to {4}. Left right to adjust.";
            _english["dialog_slider"] = "Slider: {0}";
            _english["dialog_no_buttons"] = "No interactive items found. Press Space to re-scan.";
            _english["dialog_click_error"] = "Could not press button";
            _english["dialog_text_mode"] = "Press Enter or Space to continue.";

            // ===== AGE VERIFICATION =====
            _english["ageverify_prompt"] = "Age verification. Select your birth year and month. Year: {0}. Month: {1}.";
            _english["ageverify_year"] = "Year: {0}";
            _english["ageverify_month"] = "Month: {0}";

            // ===== SCREEN BUTTONS =====
            _english["screen_buttons"] = "{0} items. Up down to navigate, Enter to press.";
            _english["screen_button_item"] = "{0} of {1}: {2}";
            _english["screen_slider_item"] = "{0} of {1}: {2}, value {3}, range {4} to {5}. Left right to adjust.";
            _english["screen_slider"] = "Slider: {0}";
            _english["screen_rescan"] = "Rescanning";
            _english["screen_click_error"] = "Could not press button";
            _english["screen_back"] = "Back";
            _english["screen_cutscene"] = "Cutscene. Press Enter or Space to advance.";

            // ===== DUEL WORLD MAP =====
            _english["map_area_street"] = "Street";
            _english["map_area_alley"] = "Alley";
            _english["map_area_park"] = "Park";
            _english["map_area_shop"] = "Shop";
            _english["map_npc_challenge"] = "Challenge Duelist";
            _english["map_npc_standard"] = "Standard Duelist";
            _english["map_npc_legendary"] = "Legendary Duelist";
            _english["map_gift"] = "Gift";
            _english["map_card_trader"] = "Card Trader";
            _english["map_npc_trainer"] = "Trainer";
            _english["map_npc_bonus"] = "Bonus Duelist";

            // ===== DUEL: GENERAL =====
            _english["duel_started"] = "Duel started";
            _english["duel_ended"] = "Duel ended";
            _english["duel_result_button"] = "Press Enter: {0}";
            _english["duel_result_screen"] = "{0}. Press Enter to continue";
            _english["duel_result_win"] = "You win";
            _english["duel_result_lose"] = "You lose";
            _english["duel_result_draw"] = "Draw";
            _english["duel_result_turns"] = "{0} turns";
            _english["duel_result_lp"] = "Your LP: {0}, Opponent LP: {1}";
            _english["duel_result_finisher"] = "Finished by {0}";
            _english["duel_not_in_duel"] = "Not in a duel";
            _english["duel_status"] = "Turn {0}, {1}. {2}. Your LP: {3}. Opponent LP: {4}";
            _english["duel_status_error"] = "Could not read duel status";

            // ===== DUEL: TURNS AND PHASES =====
            _english["duel_turn"] = "Turn {0}. {1}";
            _english["duel_your_turn"] = "Your turn";
            _english["duel_opponent_turn"] = "Opponent's turn";
            _english["duel_phase_draw"] = "Draw Phase";
            _english["duel_phase_standby"] = "Standby Phase";
            _english["duel_phase_main1"] = "Main Phase";
            _english["duel_phase_battle"] = "Battle Phase";
            _english["duel_phase_main2"] = "Main Phase 2";
            _english["duel_phase_end"] = "End Phase";
            _english["duel_phase_unknown"] = "Unknown Phase";
            _english["duel_phase_cutscene"] = "Cutscene";

            // ===== DUEL: LIFE POINTS =====
            _english["duel_you"] = "You";
            _english["duel_opponent"] = "Opponent";
            _english["duel_lp_damage"] = "{0} took {1} damage. LP: {2}";
            _english["duel_lp_recover"] = "{0} recovered {1} LP. LP: {2}";
            _english["duel_lp_update"] = "{0} LP: {1}";

            // ===== DUEL: CARD EVENTS =====
            _english["duel_a_card"] = "A card";
            _english["duel_a_monster"] = "A monster";
            _english["duel_summoned"] = "{0} summoned";
            _english["duel_sp_summoned"] = "{0} special summoned";
            _english["duel_card_set"] = "A card was set";
            _english["duel_activated"] = "{0} activated";
            _english["duel_destroyed"] = "{0} destroyed";
            _english["duel_flipped"] = "{0} flipped";
            _english["duel_drew_card"] = "Drew a card";
            _english["duel_opponent_drew"] = "Opponent drew a card";
            _english["duel_fusion"] = "Fusion summon";

            // ===== DUEL: ACTIONS =====
            _english["duel_your_move"] = "Your move";
            _english["duel_opponent_thinking"] = "Opponent is thinking";
            _english["duel_attack"] = "Attack declared";
            _english["duel_command_available"] = "Select an action";
            _english["duel_dialog"] = "Duel prompt";
            _english["duel_select_card"] = "Select a card";
            _english["duel_chain_link"] = "Chain link {0}";

            // ===== DUEL: FIELD NAVIGATION =====
            _english["duel_nav_hint"] = "Press Tab to start navigating the field";
            _english["duel_nav_closed"] = "Navigation closed";
            _english["duel_no_cards"] = "No cards on the field";
            _english["duel_zone_empty"] = "Zone is empty";
            _english["duel_zone_entered"] = "{0}, {1} cards";
            _english["duel_zone_hand"] = "Hand";
            _english["duel_zone_my_monsters"] = "Your monsters";
            _english["duel_zone_my_spells"] = "Your spells and traps";
            _english["duel_zone_my_grave"] = "Your graveyard";
            _english["duel_zone_opp_monsters"] = "Opponent monsters";
            _english["duel_zone_opp_spells"] = "Opponent spells and traps";
            _english["duel_card_position"] = "{0} of {1}: {2}";
            _english["duel_empty_slot"] = "Empty";
            _english["duel_face_up"] = "Face up";
            _english["duel_face_down"] = "Face down";
            _english["duel_face_down_card"] = "Face-down card";
            _english["duel_attack_position"] = "Attack position";
            _english["duel_defense_position"] = "Defense position";
            _english["duel_card_stats"] = "ATK {0}, DEF {1}";
            _english["duel_card_original_stats"] = "Original ATK {0}, DEF {1}";
            _english["duel_available_actions"] = "Actions: {0}";
            _english["duel_unknown_card"] = "Unknown card";
            _english["duel_card_read_error"] = "Could not read card";

            // ===== DUEL: FIELD SUMMARY =====
            _english["duel_field_summary"] = "Hand {0}. Your field: {1} monsters, {2} spells. Your grave {3}. Opponent: {4} monsters, {5} spells. Opponent grave {6}.";
            _english["duel_field_summary_error"] = "Could not read field";

            // ===== DUEL: CARD ACTIONS =====
            _english["duel_no_card_selected"] = "No card selected";
            _english["duel_no_actions"] = "No actions available";
            _english["duel_opening_actions"] = "Actions: {0}";
            _english["duel_action_error"] = "Action failed";
            _english["duel_cmd_summon"] = "Summon";
            _english["duel_cmd_special_summon"] = "Special summon";
            _english["duel_cmd_set_monster"] = "Set monster";
            _english["duel_cmd_set"] = "Set";
            _english["duel_cmd_activate"] = "Activate";
            _english["duel_cmd_attack"] = "Attack";
            _english["duel_cmd_flip_summon"] = "Flip summon";
            _english["duel_cmd_to_attack"] = "Change to attack";
            _english["duel_cmd_to_defense"] = "Change to defense";
            _english["duel_cmd_pendulum"] = "Pendulum summon";
            _english["duel_cmd_select"] = "Select";

            // ===== DUEL: TARGET SELECTION =====
            _english["duel_select_target"] = "Select attack target";
            _english["duel_target_cancelled"] = "Attack cancelled";
            _english["duel_direct_attack"] = "Direct attack";

            // ===== DUEL: CARD SELECTION (tribute, material, etc.) =====
            _english["duel_card_select_prompt"] = "{0}. {1} choices. Left right to navigate, Enter to select.";
            _english["duel_card_select_item"] = "{0} of {1}: {2}";
            _english["duel_card_selected"] = "Selected";
            _english["duel_card_select_cancelled"] = "Selection cancelled";

            // ===== DUEL: YES/NO DIALOG =====
            _english["duel_yesno_prompt"] = "{0} Enter for Yes, Escape for No.";
            _english["duel_yesno_generic"] = "Confirm? Enter for Yes, Escape for No.";
            _english["duel_yes"] = "Yes";
            _english["duel_no"] = "No";

            // ===== DUEL: PHASE ADVANCEMENT =====
            _english["duel_advancing_phase"] = "Moving to {0}";
            _english["duel_cant_advance_phase"] = "Cannot advance phase";
            _english["duel_phase_error"] = "Phase change failed";

            // ===== DUEL: TUTORIAL =====
            _english["duel_tutorial_arrow"] = "Press Space to continue tutorial";
            _english["duel_tutorial_arrow_pointing"] = "Tutorial arrow. Navigate the field and press Enter to interact.";

            // ===== DUEL: EVENT LOG =====
            _english["duel_log_entry"] = "{0} of {1}: {2}";
            _english["duel_log_empty"] = "Event log is empty";
            _english["duel_log_opened"] = "Event log. {0} entries. Up down to browse, Escape to close.";
            _english["duel_log_closed"] = "Event log closed";
        }

        #endregion
    }
}
