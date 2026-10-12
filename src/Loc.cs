using System.Collections.Generic;

namespace DuelLinksAccess
{
    /// <summary>
    /// Centralized localization for the accessibility mod.
    /// English only for now, but all strings go through here for easy expansion.
    ///
    /// Usage:
    ///   Loc.Get("key")              â€” get string
    ///   Loc.Get("key", arg1, arg2)  â€” get string with placeholders {0}, {1}
    /// </summary>
    public static class Loc
    {
        #region Fields

        private static volatile bool _initialized = false;
        private static readonly object _initializeLock = new();
        private static readonly Dictionary<string, string> _english = new();

        #endregion

        #region Public Methods

        /// <summary>
        /// Initializes localization. Call once at mod startup.
        /// </summary>
        public static void Initialize()
        {
            if (_initialized) return;
            lock (_initializeLock)
            {
                if (_initialized) return;
                InitializeStrings();
                _initialized = true;
            }
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
        /// Gets a localized label for a shortcut panel key.
        /// Falls back to a cleaned-up version of the key if not mapped.
        /// </summary>
        public static string GetShortcutLabel(string key)
        {
            string locKey = "shortcut_" + key;
            if (!_initialized) Initialize();
            if (_english.TryGetValue(locKey, out string value))
                return value;
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
            _english["mod_loaded"] = "决斗链接无障碍模组已加载。按 F1 查看按键帮助。";
            _english["help_text"] = "按键操作指南：方向键上下浏览项目，回车键激活或确认，Esc 或退格键返回，空格键重新扫描当前屏幕，Tab 键重复朗读当前项。在决斗中：方向键上下切换场地行，左右切换卡槽位置。区域快捷键：C 键手牌，M 键怪兽区，S 键魔法陷阱区，T 键场地魔法区，G 键墓地，B 键除外区，D 键额外卡组。按住 Shift 键配合上述快捷键查看对方对应区域。数字键 1、2、3 切换对应怪兽卡槽。数字键 4、5 切换左右额外怪兽区（双方共用行，无需按住 Shift，也可在己方怪兽行按上键进入）。L 键播报生命值。回车键打开操作菜单。V 键朗读当前卡片详细效果。F 键播报全场战局概况。P 键推进决斗阶段。抽卡自动进行，抽卡阶段若有延迟可按空格键重试。卡片召唤、效果演出与动画播放期间，按空格键可点击屏幕快进（PvP 天梯倒计时在动画期间仍会消耗，请及时按空格跳过）。I 键查看当前状态。J 键查看决斗日志。Ctrl+Q 投降，并在弹出的对话框中确认。Ctrl+E 或 F8 导出您拥有的全部卡片清单及具体效果文本到文件。卡组编辑界面：Tab 键切换区域，左右方向键浏览卡片，回车键添加或移出卡片，V 键朗读卡片效果详情，I 键查看卡组数量统计，S 键播报当前装备技能，K 键更换技能，U 键设为当前使用卡组，A 键设置卡组配件（卡套和卡垫），Ctrl+S 保存卡组。商店界面：Tab 键切换分类，左右方向键浏览商品，回车键购买，G 键播报宝石余额，C 键或 I 键查看商品详情。兑换券界面：左右方向键浏览卡片，回车键选择卡片，空格键确认兑换，G 键播报剩余兑换券数量。卡片商人界面：左右方向键浏览交易项，回车键选择并打开交易确认框，随后在对话框中确认，C 键或 I 键查看交易详情，G 键播报金币余额，F 键列出商人功能标签页并可按数字键打开相应功能（如卡片转换、特殊加工等），空格键确认批量转换。主界面/决斗世界：上下方向键浏览目的地，左右方向键切换区域（或在角色面板上切换角色），回车键进入，G 键播报宝石余额，B 键切换全元素浏览模式与精简模式。F1 随时查看帮助。F11 强制激活教程引导箭头（仅在方向键无法选中时使用）。F12 切换调试模式。Ctrl+R 重复上一条播报。";
            _english["debug_mode"] = "调试模式：{0}";
            _english["tutorial_arrow_activated"] = "教程引导箭头已激活";
            _english["tutorial_arrow_no_arrow"] = "当前无活动教程引导箭头";
            _english["tutorial_arrow_no_target"] = "教程箭头未指定目标";
            _english["hardware_click_no_window"] = "硬件模拟点击失败：未找到游戏窗口";

            // ===== SETTINGS =====

            // ===== SCREEN CHANGES =====
            _english["screen_home"] = "主界面";
            _english["screen_title"] = "标题画面";
            _english["title_press_enter"] = "标题画面。按回车键继续。";
            _english["screen_duel"] = "决斗进行中";
            _english["screen_deck"] = "卡组编辑";
            _english["screen_shop"] = "商店";
            _english["screen_dialog"] = "对话框";
            _english["screen_card_detail"] = "卡片详情";
            _english["screen_gate"] = "传送门";
            _english["screen_store"] = "商店";
            _english["screen_notices"] = "公告通知";
            _english["screen_duel_trials"] = "决斗残局测试";
            _english["duel_trials_quiz"] = "残局决斗测试 第 {0} 关";
            _english["no_repeat"] = "没有可重复的语音内容";

            // ===== DIALOGS =====
            _english["dialog_buttons"] = "共 {0} 个项目。上下键浏览，回车键按下，左右键调节滑块。";
            _english["dialog_button_item"] = "{2}，第 {0} 项，共 {1} 项";
            _english["dialog_slider_item"] = "滑块，当前值 {2}，第 {0} 项共 {1} 项，调节范围 {3} 至 {4}。左右键调节。";
            _english["dialog_slider"] = "滑块：{0}";
            _english["dialog_no_buttons"] = "未找到交互组件。按空格键重新扫描。";
            _english["dialog_click_error"] = "无法点击按钮";
            _english["dialog_row_info"] = "{0}，详细信息";
            _english["dialog_text_mode"] = "按回车键或空格键继续。";

            // ===== AGE VERIFICATION =====
            _english["ageverify_prompt"] = "年龄确认。请输入您的出生年月后按回车提交。";
            _english["ageverify_year"] = "出生年份：{0}";
            _english["ageverify_month"] = "出生月份：{0}";

            // ===== SCREEN BUTTONS =====
            _english["screen_buttons"] = "共 {0} 个项目。上下键浏览，回车键按下。";
            _english["screen_button_item"] = "{2}，第 {0} 项，共 {1} 项";
            _english["screen_slider_item"] = "{2}，当前值 {3}，第 {0} 项共 {1} 项，调节范围 {4} 至 {5}。左右键调节。";
            _english["screen_slider"] = "滑块：{0}";
            _english["screen_rescan"] = "正在重新扫描界面";
            _english["screen_click_error"] = "无法点击按钮";
            _english["screen_back"] = "返回";
            _english["transfer_entered"] = "卡片批量转移。当前选中 {0} 张卡片。按空格键切换选中，按回车键执行转移。";
            _english["transfer_entered_info"] = "{0}。左右键浏览，回车键选择卡片，T 键执行交易，Esc 键取消。";
            _english["transfer_trading"] = "正在交易。";
            _english["transfer_pos"] = "{0}，第 {1} 张共 {2} 张";
            _english["transfer_state_selected"] = "已选";
            _english["transfer_state_unavailable"] = "不可用";
            _english["transfer_chose"] = "已选中。当前已选 {0} 张。";
            _english["transfer_removed"] = "已移出。当前已选 {0} 张。";
            _english["transfer_not_selectable"] = "该卡片无法用于交换。";
            _english["transfer_need_selection"] = "请先至少选择一张要交换的卡片。";
            _english["transfer_empty"] = "没有可用于交换的卡片。";
            _english["transfer_loading"] = "正在加载交换卡片列表，请稍候。";
            _english["transfer_load_failed"] = "交换列表加载失败。请按 Esc 返回后重试。";
            _english["transfer_cost_shortage"] = "你持有 {0} 共 {1} 张，但此项交换需要 {2} 张。无法完成交易。请按 Esc 返回。";
            _english["screen_cutscene"] = "剧情对话与过场。按回车键或空格键推进。";
            _english["screen_no_text"] = "页面暂无文本";

            // ===== DUEL WORLD MAP =====
            _english["map_area_street"] = "街道";
            _english["map_area_alley"] = "小巷";
            _english["map_area_park"] = "公园";
            _english["map_area_shop"] = "商店街";
            _english["map_npc_challenge"] = "挑战决斗者";
            _english["map_npc_standard"] = "标准决斗者：{0}";
            _english["map_npc_legendary"] = "传奇决斗者：{0}";
            _english["map_gift"] = "礼物";
            _english["map_card_trader"] = "卡片商人";
            _english["map_npc_trainer"] = "决斗导师";
            _english["map_character"] = "决斗世界角色";
            _english["map_event_object"] = "活动互动项";
            _english["map_event_ranked"] = "天梯排位决斗";
            _english["map_event_bingo"] = "宾果挑战活动";
            _english["map_event_bingo_missions"] = "宾果任务";
            _english["map_event_scramble"] = "争夺战活动";
            _english["map_event_chara_deck"] = "角色卡组决斗";
            _english["map_event_replays"] = "精彩决斗回放";
            _english["map_event_named"] = "{0} 活动";

            // ===== CARD TRADER =====
            _english["trader_entered"] = "卡片商人。共有 {0} 项可兑换内容。左右方向键浏览，回车键选择。";
            _english["trader_items"] = "卡片商人：共 {0} 项";
            _english["trader_no_items"] = "卡片商人处暂无兑换项目";
            _english["trader_selected"] = "已选择：{0}";
            _english["trader_can_trade"] = "可兑换";
            _english["trader_cannot_trade"] = "不可兑换";
            _english["trader_opens_picker"] = "以此卡交换此项";
            _english["trader_changecard_ok"] = "提供 {0} {1}，当前持有 {2}";
            _english["trader_changecard_short"] = "需要 {0} {1}，当前持有 {2}，物资不足无法兑换";
            _english["card_rush_tag"] = "超速决斗卡";
            _english["trader_cost_gold"] = "{0} 金币";
            _english["trader_cost_gold_poss"] = "{0} 金币（持有 {1}）";
            _english["trader_cost_card"] = "{1} ×{0}";
            _english["trader_cost_card_poss"] = "{1} ×{0}（持有 {2}）";
            _english["trader_cost_item"] = "{0} ×{1}";
            _english["trader_cost_item_poss"] = "{0} ×{1}（持有 {2}）";
            _english["trader_stock"] = "库存：{0}";
            _english["trader_gold"] = "金币：{0}";
            _english["trader_gold_unknown"] = "金币余额无法获取";
            _english["trader_goto_catalog"] = "正在打开卡片转换目录";
            _english["trader_rarity_list"] = "{0} 稀有度卡片列表";
            _english["trader_chroniclizer"] = "卡片年代记加工";
            _english["trader_process"] = "卡片特殊加工";
            _english["trader_change_skill"] = "更换技能";
            _english["trader_sold_out"] = "已售罄";
            _english["trader_skill_ticket"] = "技能兑换券";
            _english["trader_ex_item"] = "特殊道具";
            _english["trader_pack"] = "卡包";
            _english["trader_box_chip"] = "卡盒筹码";
            _english["trader_respect_orb"] = "敬意宝珠";
            _english["trader_exchange_for"] = "使用 {0} 张卡片交换此项";
            _english["trader_press_space_to_trade"] = "按空格键进行兑换";
            _english["trader_confirm_prompt"] = "确定兑换 {0} 吗？再次按回车确认，或按方向键取消。";
            _english["trader_trading"] = "正在兑换 {0}";
            _english["trader_actual_selected"] = "商人已选定：{0}";
            _english["trader_drift_abort"] = "兑换已中止：选择项已从 {0} 漂移切换为 {1}。请重试。";
            _english["trader_drift_warn"] = "注意：选择项已从 {0} 切换为 {1}。请在确认前仔细核对对话框。";
            _english["trader_footer_tabs"] = "商人功能标签页：{0}。按对应数字键可直接打开。";
            _english["trader_footer_none"] = "暂无商人功能标签页";
            _english["trader_footer_activated"] = "正在打开 {0}";
            _english["trader_footer_invalid"] = "该标签页不存在";
            _english["trader_footer_unnamed"] = "标签页 {0}";
            _english["trader_select_failed"] = "无法选择 {0}。请重试。";
            _english["trader_unknown_item"] = "未知道具";
            _english["map_npc_bonus"] = "奖励决斗者";
            _english["map_school"] = "决斗残局测试";

            // ===== HOME / DUEL WORLD HANDLER =====
            _english["home_items"] = "主界面：共 {0} 项";
            // Label first â€” position info is secondary for navigation efficiency
            _english["home_item"] = "{2}，第 {0} 项，共 {1} 项";
            _english["home_area_selector"] = "区域：{0}";
            _english["home_area_changed"] = "已切换至 {0}";
            _english["home_area_left"] = "上一个区域";
            _english["home_area_right"] = "下一个区域";
            _english["home_area_unknown"] = "区域 {0}";
            _english["home_gate"] = "传送门";
            _english["home_shop"] = "商店";
            _english["home_colosseum"] = "竞技场";
            _english["home_labo"] = "卡片工作室";
            _english["home_events"] = "活动";
            _english["home_gifts"] = "礼品盒";
            _english["home_info"] = "通知与公告";
            _english["home_user_info"] = "决斗者信息";
            _english["home_option"] = "选项设置";
            _english["home_neuron_code"] = "Neuron 联动码";
            _english["home_character"] = "当前角色：{0}";
            _english["home_character_unknown"] = "未知角色";
            _english["home_deck_select"] = "选择卡组";
            _english["home_deck_edit"] = "编辑卡组";
            _english["home_missions"] = "任务";
            _english["home_missions_stage"] = "任务：{0}";
            _english["home_duel_center"] = "决斗中心";
            _english["home_chara_selector"] = "角色：{0}";
            _english["home_chara_exp_up"] = "为 {0} 升级";
            _english["home_chara_deck"] = "{0} 的卡组";
            _english["home_chara_change_series"] = "切换系列世界";
            _english["home_chara_change_style"] = "切换角色形态";
            _english["home_chara_style"] = "形态 {0}";
            _english["home_chara_list"] = "打开角色列表";
            _english["home_chara_mission"] = "角色任务";
            _english["home_browse_all"] = "全元素浏览模式。已显示所有界面组件。";
            _english["home_curated"] = "精选导航模式。仅显示主要目的地。";
            _english["home_shortcut"] = "快捷面板";
            _english["home_menu_settings"] = "菜单与设置";
            _english["home_character_button"] = "打开角色面板";
            _english["home_duel_shortcut"] = "快速决斗";
            _english["home_series"] = "切换决斗世界";
            _english["home_series_option"] = "世界：{0}";
            _english["home_series_cancel"] = "取消世界切换";
            _english["home_series_rush"] = "超速决斗世界";
            _english["home_series_current"] = "当前世界";
            _english["home_series_locked"] = "未解锁";
            _english["map_card_portal"] = "卡片传送门";
            _english["map_alley_gomibako"] = "垃圾桶";
            _english["map_event_panel"] = "活动公告";
            _english["map_treasure_chest"] = "宝箱";

            // ===== SHORTCUT PANEL =====
            _english["shortcut_trader"] = "卡片商人";
            _english["shortcut_duelchallenge"] = "决斗测试挑战";
            _english["shortcut_traderEx"] = "卡片商人 EX";
            _english["shortcut_replay"] = "决斗录像回放";
            _english["shortcut_cardlist"] = "卡片列表";
            _english["shortcut_cardchronicle"] = "卡片年代史记";
            _english["shortcut_skilllist"] = "技能列表";
            _english["shortcut_ranking"] = "天梯排名榜";
            _english["shortcut_eve1004"] = "活动专属入口";

            // ===== DUEL: GENERAL =====
            _english["duel_started"] = "决斗开始";
            _english["duel_ended"] = "决斗结束";
            _english["duel_result_button"] = "按回车键：{0}";
            _english["duel_result_screen"] = "{0}。按回车键继续";
            _english["duel_result_win"] = "决斗胜利";
            _english["duel_result_lose"] = "决斗失败";
            _english["duel_result_draw"] = "平局";
            _english["duel_result_turns"] = "历经 {0} 回合";
            _english["duel_result_lp"] = "己方生命值：{0}，对方生命值：{1}";
            _english["duel_result_finisher"] = "决胜卡片：{0}";
            _english["duel_not_in_duel"] = "当前不在决斗中";
            _english["duel_status"] = "第 {0} 回合，{1}。{2}。己方生命值：{3}。对方生命值：{4}";
            _english["duel_status_error"] = "无法读取决斗状态";

            // ===== DUEL: TURNS AND PHASES =====
            _english["duel_turn"] = "第 {0} 回合。{1}";
            _english["duel_your_turn"] = "你的回合";
            _english["duel_opponent_turn"] = "对方的回合";
            _english["surrender_opening"] = "正在发起投降。请在对话框中确认。";
            _english["surrender_unavailable"] = "当前无法投降。";
            _english["surrender_error"] = "投降失败。";
            _english["duel_time_low"] = "剩余时间：{0} 秒";
            _english["duel_abandon_warning"] = "挂机警告！请按空格键继续决斗。";
            _english["duel_abandon_warning_timed"] = "挂机警告！请在 {0} 秒内按空格键继续决斗。";
            _english["duel_abandon_cleared"] = "挂机警告已解除。";
            _english["duel_phase_draw"] = "抽卡阶段";
            _english["duel_phase_standby"] = "准备阶段";
            _english["duel_phase_main1"] = "主要阶段";
            _english["duel_phase_battle"] = "战斗阶段";
            _english["duel_phase_main2"] = "主要阶段2";
            _english["duel_phase_end"] = "结束阶段";
            _english["duel_phase_unknown"] = "未知阶段";
            _english["duel_phase_cutscene"] = "剧情演出";

            // ===== DUEL: LIFE POINTS =====
            _english["duel_you"] = "己方";
            _english["duel_opponent"] = "对方";
            _english["duel_lp_damage"] = "{0} 受到 {1} 点伤害。剩余生命值：{2}";
            _english["duel_lp_recover"] = "{0} 回复 {1} 点生命值。剩余生命值：{2}";
            _english["duel_lp_update"] = "{0} 生命值：{1}";

            // ===== DUEL: CARD EVENTS =====
            _english["duel_a_card"] = "一张卡片";
            _english["duel_a_monster"] = "一只怪兽";
            _english["duel_summoned"] = "{0} 召唤";
            _english["duel_sp_summoned"] = "{0} 特殊召唤";
            _english["duel_card_set"] = "盖伏了一张卡片";
            _english["duel_activated"] = "{0} 发动";
            _english["duel_destroyed"] = "{0} 被破坏";
            _english["duel_flipped"] = "{0} 反转";
            _english["duel_drew_card"] = "抽了一张卡";
            _english["duel_opponent_drew"] = "对方抽了一张卡";
            _english["duel_fusion"] = "融合召唤";

            // ===== DUEL: ACTIONS =====
            _english["duel_your_move"] = "轮到你行动";
            _english["duel_opponent_thinking"] = "对方正在思考";
            _english["duel_attack"] = "宣告攻击";
            _english["duel_command_available"] = "请选择操作";
            _english["duel_dialog"] = "决斗提示";
            _english["duel_select_card"] = "请选择卡片";
            _english["duel_chain_link"] = "连锁 {0}";

            // ===== DUEL: FIELD NAVIGATION =====
            _english["duel_nav_hint"] = "按方向键或区域快捷键开始浏览场地";
            _english["duel_nav_closed"] = "退出场地浏览";
            _english["duel_no_cards"] = "场上没有卡片";
            _english["duel_zone_empty"] = "该区域为空";
            _english["duel_zone_entered"] = "{0}，共 {1} 张卡";
            _english["duel_zone_hand"] = "手牌";
            _english["duel_zone_my_monsters"] = "己方怪兽区";
            _english["duel_zone_my_spells"] = "己方魔法陷阱区";
            _english["duel_zone_my_grave"] = "己方墓地";
            _english["duel_zone_my_extra"] = "己方额外卡组";
            _english["duel_zone_opp_monsters"] = "对方怪兽区";
            _english["duel_zone_opp_spells"] = "对方魔法陷阱区";
            _english["duel_zone_opp_grave"] = "对方墓地";
            _english["duel_zone_opp_extra"] = "对方额外卡组";
            _english["duel_zone_my_field_spell"] = "己方场地魔法区";
            _english["duel_zone_opp_field_spell"] = "对方场地魔法区";
            _english["duel_zone_my_banished"] = "己方除外区";
            _english["duel_zone_opp_banished"] = "对方除外区";
            _english["duel_zone_emz"] = "额外怪兽区";
            _english["duel_card_opponent_owned"] = "{0}，对方的卡";
            _english["duel_position_changed"] = "切换为 {0}：{1}";
            _english["duel_position_changed_opponent"] = "对方切换为 {0}：{1}";
            _english["duel_position_changed_generic"] = "表示形式改变：{0}";
            _english["duel_position_changed_opponent_generic"] = "对方改变了表示形式：{0}";
            _english["duel_lp_read"] = "你的生命值：{0}。对方生命值：{1}";
            _english["duel_empty_slot_named"] = "卡槽 {0}：空";
            _english["duel_grid_edge_top"] = "场地最顶端";
            _english["duel_grid_edge_bottom"] = "场地最底端";
            _english["duel_card_position"] = "{2}，第 {0} 张，共 {1} 张";
            _english["duel_empty_slot"] = "空";
            _english["duel_face_up"] = "表侧表示";
            _english["duel_face_down"] = "里侧表示";
            _english["duel_face_down_card"] = "盖放的卡片";
            _english["duel_attack_position"] = "攻击表示";
            _english["duel_defense_position"] = "守备表示";
            _english["duel_card_stats"] = "攻击力 {0}，守备力 {1}";
            _english["duel_card_original_stats"] = "原本攻击力 {0}，原本守备力 {1}";
            _english["duel_available_actions"] = "可选操作：{0}";
            _english["duel_unknown_card"] = "未知卡片";
            _english["duel_card_read_error"] = "无法读取卡片信息";

            // ===== DUEL: FIELD SUMMARY =====
            _english["duel_field_summary"] = "手牌 {0} 张。己方场上：怪兽 {1} 只，魔陷 {2} 张，墓地 {3} 张。对方场上：怪兽 {4} 只，魔陷 {5} 张，墓地 {6} 张。";
            _english["duel_field_summary_error"] = "无法读取场地信息";

            // ===== DUEL: CARD ACTIONS =====
            _english["duel_no_card_selected"] = "未选择卡片";
            _english["duel_no_actions"] = "无可用操作";
            _english["duel_opening_actions"] = "可选操作：{0}";
            _english["duel_action_error"] = "操作失败";
            _english["duel_cmd_summon"] = "通常召唤";
            _english["duel_cmd_special_summon"] = "特殊召唤";
            _english["duel_cmd_set_monster"] = "放置怪兽";
            _english["duel_cmd_set"] = "盖伏";
            _english["duel_cmd_activate"] = "发动";
            _english["duel_cmd_attack"] = "攻击";
            _english["duel_cmd_flip_summon"] = "反转召唤";
            _english["duel_cmd_to_attack"] = "变为攻击表示";
            _english["duel_cmd_to_defense"] = "变为守备表示";
            _english["duel_cmd_pendulum"] = "灵摆召唤";
            _english["duel_cmd_select"] = "选择";
            _english["duel_cmd_unknown"] = "操作";

            // ===== DUEL: TARGET SELECTION =====
            _english["duel_select_target"] = "请选择攻击目标";
            _english["duel_target_cancelled"] = "攻击已取消";
            _english["duel_direct_attack"] = "直接攻击";

            // ===== DUEL: CARD SELECTION (tribute, material, etc.) =====
            _english["duel_card_select_prompt"] = "{0}。共 {1} 个选项。左右键移动，回车键选择。";
            _english["duel_card_select_item"] = "{2}，第 {0} 项，共 {1} 项";
            _english["duel_card_selected"] = "已选择";
            _english["duel_card_picked"] = "已选中：{0}";
            _english["duel_card_select_cancelled"] = "选择已取消";

            // ===== DUEL: EMOTIONAL LIST (card list selection) =====
            _english["duel_emo_list_single"] = "请选择一张卡片。共 {0} 个选项。左右键移动，回车键选择。";
            _english["duel_emo_list_multi"] = "请选择卡片。共 {0} 个选项，最多可选 {1} 张。左右键移动，回车键勾选，空格键确认。";
            _english["duel_emo_list_view"] = "卡片列表，共 {0} 张卡。左右键查看，回车键选择要加入的卡，空格键继续。";
            _english["duel_emo_list_confirmed"] = "已确认";
            _english["duel_emo_list_toggled"] = "已选 {0} 张，共需 {1} 张";
            _english["duel_emo_list_need_more"] = "至少需要选择 {0} 张";
            _english["duel_action_cancelled"] = "操作已取消";
            _english["duel_cannot_cancel"] = "无法取消";

            // ===== DUEL: BATTLE POSITION =====
            _english["duel_battle_position"] = "请选择表示形式：按回车或 1 选攻击表示，按 2 选守备表示，按 Esc 取消。";
            _english["duel_atk_position"] = "攻击表示";
            _english["duel_def_position"] = "守备表示";

            // ===== DUEL: YES/NO DIALOG =====
            _english["duel_yesno_prompt"] = "{0} 按回车确认“是”，按 Esc 确认“否”。";
            _english["duel_yesno_generic"] = "确认操作？按回车确认“是”，按 Esc 确认“否”。";
            _english["duel_yes"] = "是";
            _english["duel_no"] = "否";

            // ===== DUEL: SELECT-EFFECT DIALOG (multi-effect picker) =====
            _english["duel_effect_intro"] = "{0} 效果 {1}，共 {2} 个效果。{3} 上下键选择，回车键确认，Esc 键取消。";
            _english["duel_effect_item"] = "效果 {0}，共 {1} 个效果。{2}";
            _english["duel_effect_single"] = "{0} {1} 回车键确认，Esc 键取消。";
            _english["duel_effect_confirmed"] = "效果已确认";
            _english["duel_effect_cancelled"] = "已取消";

            // ===== DUEL: PHASE ADVANCEMENT =====
            _english["duel_advancing_phase"] = "正在进入 {0}";
            _english["duel_cant_advance_phase"] = "无法推进阶段";
            _english["duel_phase_error"] = "阶段切换失败";

            // ===== DUEL: TUTORIAL =====
            _english["duel_tutorial_arrow"] = "按空格键继续教程";
            _english["duel_tutorial_arrow_pointing"] = "教程引导箭头激活。按方向键浏览至目标并按回车确认，或按 F11 激活不可达目标。";
            _english["tutorial_arrow_target_named"] = "新手教程指引：请按 F11 激活 {0}，或通过方向键浏览并按回车确认";
            _english["tutorial_arrow_back"] = "新手教程指引：请返回上一层以寻找目标按钮。";
            _english["tutorial_arrow_back_named"] = "新手教程指引：请按 Esc 返回，然后选择 {0}。";

            // ===== DUEL: SKILL ACTIVATION =====
            _english["duel_skill_opening"] = "正在打开技能确认...";
            _english["duel_skill_not_available"] = "当前不可发动技能或未满足条件";
            _english["duel_skill_not_in_duel"] = "当前不在决斗中";
            _english["duel_skill_error"] = "技能操作失败";
            _english["duel_skill_not_your_turn"] = "当前是对手回合，无法发动技能";
            _english["duel_skill_activating"] = "正在发动技能...";
            _english["duel_skill_btn_activate"] = "发动技能";
            _english["duel_skill_btn_close"] = "取消";

            // ===== DECK EDITOR =====
            _english["deck_edit_entered"] = "卡组编辑器。主卡组 {0} 张，额外卡组 {1} 张，卡片收藏库 {2} 张。按 Tab 键切换区域。快捷键：F 筛选，Ctrl+F 搜索，B 自动构筑，A 配件设置，K 技能，Ctrl+S 保存。";
            _english["deck_zone"] = "{0}，共 {1} 张卡";
            _english["deck_zone_main_name"] = "主卡组";
            _english["deck_zone_extra_name"] = "额外卡组";
            _english["deck_zone_collection_name"] = "卡片收藏库";
            _english["deck_zone_toolbar_name"] = "操作菜单";
            _english["deck_zone_toolbar"] = "操作菜单。共 {0} 个功能项目。按方向键浏览，回车键激活。按 Tab 键返回卡组。";
            _english["deck_toolbar_item"] = "{0}，第 {1} 项，共 {2} 项";
            _english["deck_btn_auto_build"] = "自动构筑卡组 (快捷键 B)";
            _english["deck_btn_recipe"] = "推荐卡组与配方";
            _english["deck_btn_skill"] = "技能选择：{0} (快捷键 K)";
            _english["deck_btn_accessories"] = "卡组配件与设置 (快捷键 A)";
            _english["deck_btn_menu"] = "卡组菜单";
            _english["deck_btn_use_deck"] = "设为当前主力卡组 (快捷键 U)";
            _english["deck_btn_save"] = "保存卡组 (快捷键 Ctrl+S)";
            _english["deck_btn_clear"] = "清空卡组";
            _english["deck_btn_copy"] = "复制卡组";
            _english["deck_btn_paste"] = "粘贴卡组代码";
            _english["deck_btn_filter"] = "卡片详细筛选";
            _english["deck_btn_search"] = "卡片名称搜索";
            _english["deck_btn_stats"] = "卡组统计信息 (快捷键 I)";
            _english["deck_zone_empty"] = "该区域没有卡片";
            _english["deck_card_position"] = "{2}，第 {0} 张，共 {1} 张";
            _english["deck_card_added"] = "已将 {0} 加入卡组";
            _english["deck_card_added_zone_count"] = "已加入 {0}。{1}：当前 {2} 张";
            _english["deck_card_removed"] = "已将 {0} 移出卡组";
            _english["deck_card_removed_zone_count"] = "已移出 {0}。{1}：当前 {2} 张";
            _english["deck_card_not_addible"] = "无法加入 {0}";
            _english["deck_card_not_addible_reason"] = "无法加入 {0}：{1}";
            _english["deck_card_in_deck"] = "卡组中已有 {0} 张";
            _english["deck_stats"] = "主卡组：{0} / {1} 张。额外卡组：{2} / {3} 张。";
            _english["deck_edit_owner"] = "该卡组归属于角色：{0}。";
            _english["deck_chara_group"] = "{0} 的卡组分类";
            _english["deck_slot_owner"] = "{0}：{1}";
            _english["deck_slot_empty"] = "空白卡组槽位";
            _english["deck_saved"] = "卡组保存成功";
            _english["deck_operation_error"] = "卡组操作失败";
            _english["deck_level"] = "等级";
            _english["deck_rank"] = "阶级";
            _english["deck_link"] = "连接标记";
            _english["deck_skill"] = "装备技能：{0}。按 K 键可更换技能。";
            _english["deck_no_skill"] = "未装备技能。按 K 键选择技能。";
            _english["deck_use_deck_pressed"] = "正在设为当前主力卡组";
            _english["deck_already_active"] = "该卡组已是当前主力卡组";
            _english["deck_reason_full"] = "卡组已满，上限 {0} 张";
            _english["deck_reason_limit"] = "卡组中已有该卡的上限 {0} 张";
            _english["deck_reason_no_copies"] = "没有更多可用的卡片存量";
            _english["deck_reason_unknown"] = "规则不允许加入";

            // ===== AUTO DECK BUILD =====
            _english["deck_auto_opening"] = "正在打开自动构筑功能";
            _english["deck_auto_selected"] = "{0}，已选中";
            _english["deck_auto_not_selected"] = "{0}，未选中";
            _english["deck_auto_unavailable"] = "{0}。{1}";
            _english["deck_auto_expanded"] = "{0}，已展开";
            _english["deck_auto_collapsed"] = "{0}，已折叠";
            _english["deck_auto_advanced"] = "高级选项";
            _english["deck_auto_size"] = "卡组容量：{0} 张，范围 {1} 至 {2} 张";

            // ===== DECK MENU =====
            _english["deck_menu_draw_test"] = "起手抽卡测试 (试抽5张)";
            _english["deck_menu_history"] = "卡组修改历史记录";
            _english["deck_menu_deep_link"] = "生成并复制卡组分享链接";
            _english["deck_menu_distribute"] = "官方精选卡组配方";
            _english["deck_menu_contribute"] = "玩家投稿卡组配方";
            _english["deck_menu_popular_card"] = "热门卡片排行";
            _english["deck_menu_popular_skill"] = "热门技能排行";
            _english["deck_menu_sel_kira"] = "优先加工罕贵度设置：{0}";

            // ===== DOWNLOAD SETTINGS =====
            _english["download_start"] = "开始下载 (所需空间：{0})";
            _english["download_cancel"] = "稍后下载";
            _english["download_card_data"] = "卡片高画质数据下载，复选框 {0}";
            _english["download_bg_data"] = "后台下载，复选框 {0}";

            // ===== DRAW TEST =====
            _english["draw_test_redraw"] = "重新洗牌试抽";
            _english["draw_test_add_card"] = "再抽一张";
            _english["draw_test_hand_card"] = "第 {0} 张手牌：{1}";

            // ===== SKILL SELECT =====
            _english["skill_equipped"] = "{0}，已装备技能";
            _english["skill_unequipped"] = "{0}，未装备技能";
            _english["skill_locked"] = "{0}，未解锁技能";

            // ===== DECK ACCESSORIES (sleeve / mat / ace card) =====
            _english["deck_edit_accessories_opening"] = "正在打开卡组配件设置";
            _english["accessory_sleeve"] = "卡套：{0}";
            _english["accessory_mat"] = "决斗垫：{0}";
            _english["accessory_ace"] = "王牌怪兽演出：{0}";
            _english["accessory_none"] = "无";
            _english["accessory_preview"] = "{0}，预览";
            _english["accessory_current"] = "{0}，当前佩戴";
            _english["accessory_new"] = "{0}，新获得";
            _english["accessory_confirm_sleeve_toggle"] = "应用此卡套，复选框 {0}";
            _english["accessory_confirm_mat_toggle"] = "应用此决斗垫，复选框 {0}";
            _english["accessory_checked"] = "已勾选";
            _english["accessory_unchecked"] = "未勾选";
            _english["accessory_confirm_yes"] = "确定";
            _english["accessory_confirm_nochange"] = "不更改";

            // ===== DUEL: EVENT LOG =====
            _english["duel_log_entry"] = "{2}，第 {0} 条，共 {1} 条";
            _english["duel_log_empty"] = "事件日志为空";
            _english["duel_log_opened"] = "事件日志。共 {0} 条记录。上下键浏览，Esc 键关闭。";
            _english["duel_log_closed"] = "事件日志已关闭";

            // ===== SHOP =====
            _english["shop_entered"] = "商店。{0}。{1}，共 {2} 个商品。按 Tab 键切换分类。";
            _english["shop_entered_empty"] = "商店。{0}。正在加载商品列表。";
            _english["shop_category"] = "{0}，共 {1} 个商品";
            _english["shop_item_position"] = "{2}，第 {0} 个，共 {1} 个";
            _english["shop_gems"] = "宝石";
            _english["shop_gold"] = "金币";
            _english["shop_crystals"] = "充值晶石";
            _english["shop_balance_unknown"] = "余额未知";
            _english["shop_bonus"] = "附赠特惠";
            _english["shop_no_items"] = "该分类暂无商品";
            _english["shop_unknown_item"] = "未知商品";
            _english["shop_item_loading"] = "正在加载";
            _english["shop_item_sold_out"] = "已售罄";
            _english["shop_item_sale"] = "限时特惠中";
            _english["shop_item_sale_date"] = "特惠截止时间：{0}";
            _english["shop_item_limit"] = "限购数量：{0}";
            _english["shop_activate_error"] = "无法购买或激活该商品";
            _english["shop_card_pack"] = "卡包";
            _english["shop_remaining"] = "剩余 {0} 包";
            _english["shop_packs_label"] = "包";
            _english["shop_contains_cards"] = "内含 {0} 张卡片";
            _english["shop_category_cards"] = "卡片卡包";
            _english["shop_category_structure"] = "预组构筑卡组";
            _english["shop_category_etc"] = "道具与其他";
            _english["shop_category_bundle"] = "特惠礼包";
            _english["shop_category_dpass"] = "决斗通行证";
            _english["shop_category_accessories"] = "个性化配件";
            _english["shop_category_homebg"] = "主界面背景壁纸";
            _english["shop_reset_box"] = "重置卡盒";
            _english["shop_regulation"] = "卡牌禁限规制说明";
            _english["shop_box_chip"] = "卡盒兑换芯片";
            _english["shop_errata"] = "效果变更说明";
            _english["shop_buy_packs"] = "购买 {0} 包";

            // ===== CARD CATALOG (CONVERSION) =====
            _english["catalog_entered"] = "卡片目录。左右方向键浏览，C 键查看描述，Tab 键筛选与菜单，B 键打开批量转换。";
            _english["catalog_zone_menu"] = "菜单。包含筛选、排序与搜索。按 Tab 键返回卡片列表。";
            _english["catalog_card_count"] = "目录中共有 {0} 张卡片";
            _english["catalog_no_cards"] = "卡片目录中没有卡片";
            _english["catalog_own_count"] = "当前持有 {0} 张";
            _english["catalog_convertible"] = "可进行转换";
            _english["catalog_not_convertible"] = "不可转换";
            _english["catalog_detail_mode"] = "{0}：正在获取转换详情。按 Tab 重新朗读，按 Esc 取消。";
            _english["catalog_detail_ready"] = "可进行转换。按回车键确认。";
            _english["catalog_detail_not_available"] = "该卡片不支持单卡转换。请按 B 键使用批量转换。";
            _english["catalog_detail_not_convertible"] = "{0}：不可转换。按 Esc 返回。";
            _english["catalog_converting"] = "正在转换卡片";
            _english["catalog_converting_not_ready"] = "卡片转换尚未就绪，请稍后重试。";
            _english["catalog_not_convertible_card"] = "该卡片无法转换。";
            _english["catalog_changecard_pending"] = "暂不支持在此处选卡。";
            _english["catalog_selectable"] = "可使用";
            _english["catalog_not_selectable"] = "不可使用";
            _english["catalog_card_selected"] = "已选择 {0}";
            _english["catalog_card_not_selectable"] = "该卡片无法在此处使用。";
            _english["catalog_process_selected"] = "已选定 {0}。再次按回车确认。";
            _english["catalog_process_confirming"] = "正在确认中。";
            _english["catalog_trade_confirm_opening"] = "正在打开 {0} 的兑换确认界面。";
            _english["catalog_batch_convert"] = "正在打开批量转换";
            _english["catalog_batch_not_available"] = "当前不支持批量转换";

            // ===== TICKET EXCHANGE =====
            _english["screen_ticket_exchange"] = "兑换券兑换";
            _english["ticket_entered"] = "兑换券界面。共有 {0} 张可选卡片。左右方向键浏览，回车键选择。";
            _english["ticket_card_position"] = "{2}，第 {0} 张，共 {1} 张";
            _english["ticket_count"] = "剩余 {0} 张兑换券";
            _english["ticket_no_cards"] = "暂无可选兑换卡片";
            _english["ticket_selected"] = "已选择 {0}";
            _english["ticket_deselected"] = "已取消选择 {0}";
            _english["ticket_exchange_confirm"] = "兑换已确认";
            _english["ticket_activate_error"] = "无法完成兑换操作";
            _english["ticket_nothing_to_exchange"] = "尚未选择卡片，无法确认兑换。请先选择卡片。";

            // ===== DOWNLOAD & VOICE SETTINGS =====
            _english["download_voice_setting"] = "语音设置：{0}{1}（按回车更改）";
            _english["download_voice_off"] = "语音设置：已关闭（按回车开启）";
            _english["download_voice_select_prompt"] = "打开语音语言选择列表";
            _english["settings_talk_toggle"] = "对战中的台词：{0}";
            _english["settings_voice_slider"] = "语音音量：{0}%";
            _english["settings_voice_mute"] = "语音静音：{0}";
            _english["settings_voice_data_btn"] = "语音数据下载设置";
            _english["settings_voice_lang"] = "当前语音语言：{0}";
        }

        #endregion
    }
}
