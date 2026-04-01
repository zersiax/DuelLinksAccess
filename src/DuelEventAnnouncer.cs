using System;
using MelonLoader;

namespace DuelLinksAccess
{
    /// <summary>
    /// Processes duel events from DuelClient.RunEffect and generates announcements.
    /// Translates Engine.ViewType events into human-readable screen reader output.
    /// </summary>
    public static class DuelEventAnnouncer
    {
        #region Fields

        /// <summary>Fired when an event should be announced and logged.</summary>
        public static event Action<string> OnAnnouncement;

        /// <summary>Whether a duel is currently in progress.</summary>
        public static bool InDuel { get; private set; }

        /// <summary>Whether the duel has ended but we're still on the result screen.</summary>
        public static bool DuelEnded { get; private set; }

        // Throttle duplicate messages
        private static string _lastMessage = "";
        private static float _lastMessageTime;
        private const float ThrottleSeconds = 0.3f;

        // Track state to detect changes and compute deltas
        private static int _lastTurnPlayer = -1;
        private static int _lastPhase = -1;
        private static int _lastMyLP = -1;
        private static int _lastOppLP = -1;

        // Deferred announcements — engine hasn't updated state yet when
        // RunEffect fires, so we read values next frame
        private static bool _pendingPhaseAnnouncement;
        private static int _pendingLPDamagePlayer = -1;
        private static float _pendingLPDamageWait = 0f;
        private const float LPDamageMaxWait = 1.5f; // timeout fallback

        #endregion

        #region Public Methods

        /// <summary>
        /// Called each frame from Main.OnUpdate(). Handles deferred announcements
        /// that need the engine to finish updating before we read state.
        /// </summary>
        public static void Update()
        {
            if (_pendingPhaseAnnouncement)
            {
                _pendingPhaseAnnouncement = false;
                AnnouncePhaseChange();
            }

            if (_pendingLPDamagePlayer >= 0)
            {
                // Poll until DLL_DuelGetLP returns a different value from our
                // last tracked LP — the engine updates after the damage animation
                // plays, not when LifeDamage fires. Timeout after LPDamageMaxWait.
                _pendingLPDamageWait += UnityEngine.Time.deltaTime;
                int player = _pendingLPDamagePlayer;
                try
                {
                    int currentLP = Il2CppYgomGame.Duel.Engine.DLL_DuelGetLP(player);
                    int lastLP = player == 0 ? _lastMyLP : _lastOppLP;
                    bool changed = lastLP >= 0 && currentLP != lastLP;

                    if (changed || _pendingLPDamageWait >= LPDamageMaxWait)
                    {
                        _pendingLPDamagePlayer = -1;
                        AnnounceLPDamage(player);
                    }
                }
                catch
                {
                    // Engine not available — announce with whatever we can read
                    _pendingLPDamagePlayer = -1;
                    AnnounceLPDamage(player);
                }
            }
        }

        /// <summary>
        /// Called from Harmony postfix on DuelClient.RunEffect.
        /// Translates the raw event into an announcement.
        /// </summary>
        public static void OnRunEffect(int id, int param1, int param2, int param3)
        {
            var viewType = (Il2CppYgomGame.Duel.Engine.ViewType)id;

            DebugLogger.Log(LogCategory.Game, "DuelEvent",
                $"{viewType} ({id}): p1={param1}, p2={param2}, p3={param3}");

            switch (viewType)
            {
                case Il2CppYgomGame.Duel.Engine.ViewType.DuelStart:
                    InDuel = true;
                    DuelEnded = false;
                    _lastTurnPlayer = -1;
                    _lastPhase = -1;
                    _lastMyLP = -1;
                    _lastOppLP = -1;
                    Announce(Loc.Get("duel_started"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.DuelEnd:
                    Announce(Loc.Get("duel_ended"));
                    InDuel = false;
                    DuelEnded = true;
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.TurnChange:
                    AnnounceTurnChange();
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.PhaseChange:
                    // Defer to next frame — engine hasn't updated the phase yet
                    // when RunEffect fires, causing an off-by-one announcement
                    _pendingPhaseAnnouncement = true;
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.LifeDamage:
                    // Defer — poll each frame until DLL_DuelGetLP changes.
                    // Engine updates LP during the damage animation, not immediately.
                    _pendingLPDamagePlayer = param1;
                    _pendingLPDamageWait = 0f;
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.LifeSet:
                    TrackLP(param1);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.WaitInput:
                    Announce(Loc.Get("duel_your_move"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CpuThinking:
                    Announce(Loc.Get("duel_opponent_thinking"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CutinDraw:
                    AnnounceDraw(param1);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.RunSummon:
                    AnnounceCardEvent("duel_summoned", param1, param2, param3);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.RunSpSummon:
                    AnnounceCardEvent("duel_sp_summoned", param1, param2, param3);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CutinActivate:
                    AnnounceCardEvent("duel_activated", param1, param2, param3);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CardBreak:
                case Il2CppYgomGame.Duel.Engine.ViewType.CardExplosion:
                    AnnounceCardEvent("duel_destroyed", param1, param2, param3);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.BattleAttack:
                    Announce(Loc.Get("duel_attack"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.RunCommand:
                    // Suppressed — fires multiple times per turn and is noisy.
                    // WaitInput ("Your move") is the meaningful signal.
                    DebugLogger.Log(LogCategory.Game, "DuelEvent",
                        $"RunCommand: p2=0x{param2:X}, p3=0x{param3:X}");
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.RunDialog:
                    Announce(Loc.Get("duel_dialog"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.RunList:
                    Announce(Loc.Get("duel_select_card"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.ChainSet:
                    Announce(Loc.Get("duel_chain_link", param3));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.RunFusion:
                    Announce(Loc.Get("duel_fusion"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CardSet:
                    Announce(Loc.Get("duel_card_set"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CardFlipTurn:
                    AnnounceCardEvent("duel_flipped", param1, param2, param3);
                    break;
            }
        }

        /// <summary>
        /// Gets current duel status as a readable string.
        /// </summary>
        public static string GetStatusText()
        {
            if (!InDuel) return Loc.Get("duel_not_in_duel");

            try
            {
                int myLP = Il2CppYgomGame.Duel.Engine.DLL_DuelGetLP(0);
                int oppLP = Il2CppYgomGame.Duel.Engine.DLL_DuelGetLP(1);
                uint phaseVal = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCurrentPhase();
                uint turnNum = Il2CppYgomGame.Duel.Engine.DLL_DuelGetTurnNum();
                int turnPlayer = Il2CppYgomGame.Duel.Engine.DLL_DuelWhichTurnNow();

                var phaseEnum = (Il2CppYgomGame.Duel.Engine.Phase)phaseVal;
                string phase = phaseEnum == Il2CppYgomGame.Duel.Engine.Phase.Null
                    ? Loc.Get("duel_phase_cutscene")
                    : GetPhaseName(phaseEnum);
                string whose = turnPlayer == 0
                    ? Loc.Get("duel_your_turn")
                    : Loc.Get("duel_opponent_turn");

                return Loc.Get("duel_status", turnNum, whose, phase, myLP, oppLP);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelStatus", $"Error: {ex.Message}");
                return Loc.Get("duel_status_error");
            }
        }

        /// <summary>Resets all tracked state.</summary>
        public static void Reset()
        {
            InDuel = false;
            DuelEnded = false;
            _lastMessage = "";
            _lastTurnPlayer = -1;
            _lastPhase = -1;
            _lastMyLP = -1;
            _lastOppLP = -1;
            _pendingPhaseAnnouncement = false;
            _pendingLPDamagePlayer = -1;
            _pendingLPDamageWait = 0f;
        }

        #endregion

        #region Private Methods

        private static void Announce(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            float time = UnityEngine.Time.unscaledTime;
            if (message == _lastMessage && time - _lastMessageTime < ThrottleSeconds)
                return;

            _lastMessage = message;
            _lastMessageTime = time;

            OnAnnouncement?.Invoke(message);
        }

        private static void AnnounceTurnChange()
        {
            try
            {
                int turnPlayer = Il2CppYgomGame.Duel.Engine.DLL_DuelWhichTurnNow();
                uint turnNum = Il2CppYgomGame.Duel.Engine.DLL_DuelGetTurnNum();

                if (turnPlayer == _lastTurnPlayer) return;
                _lastTurnPlayer = turnPlayer;

                string whose = turnPlayer == 0
                    ? Loc.Get("duel_your_turn")
                    : Loc.Get("duel_opponent_turn");

                Announce(Loc.Get("duel_turn", turnNum, whose));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelTurn", $"Error: {ex.Message}");
            }
        }

        private static void AnnouncePhaseChange()
        {
            try
            {
                uint phaseVal = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCurrentPhase();

                if ((int)phaseVal == _lastPhase) return;
                _lastPhase = (int)phaseVal;

                var phase = (Il2CppYgomGame.Duel.Engine.Phase)phaseVal;

                // Skip announcing Null/unknown phases (cutscenes, automated sequences)
                if (phase == Il2CppYgomGame.Duel.Engine.Phase.Null) return;

                string phaseName = GetPhaseName(phase);
                Announce(phaseName);
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelPhase", $"Error: {ex.Message}");
            }
        }

        private static void AnnounceLPDamage(int player)
        {
            try
            {
                int lp = Il2CppYgomGame.Duel.Engine.DLL_DuelGetLP(player);
                string who = player == 0 ? Loc.Get("duel_you") : Loc.Get("duel_opponent");

                int lastLP = player == 0 ? _lastMyLP : _lastOppLP;
                if (lastLP >= 0 && lastLP != lp)
                {
                    int diff = lastLP - lp;
                    if (diff > 0)
                        Announce(Loc.Get("duel_lp_damage", who, diff, lp));
                    else
                        Announce(Loc.Get("duel_lp_recover", who, -diff, lp));
                }
                else
                {
                    Announce(Loc.Get("duel_lp_update", who, lp));
                }

                if (player == 0) _lastMyLP = lp;
                else _lastOppLP = lp;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelLP", $"Error: {ex.Message}");
            }
        }

        /// <summary>Silently tracks LP on LifeSet without announcing (avoids noise).</summary>
        private static void TrackLP(int player)
        {
            try
            {
                int lp = Il2CppYgomGame.Duel.Engine.DLL_DuelGetLP(player);
                if (player == 0) _lastMyLP = lp;
                else _lastOppLP = lp;
            }
            catch { }
        }

        private static void AnnounceDraw(int player)
        {
            if (player == 0)
                Announce(Loc.Get("duel_drew_card"));
            else
                Announce(Loc.Get("duel_opponent_drew"));
        }

        /// <summary>
        /// Announces a card-related event, attempting to resolve the card name
        /// from the event parameters.
        /// </summary>
        private static void AnnounceCardEvent(string locKey, int param1, int param2, int param3)
        {
            string cardName = TryGetCardName(param2) ?? TryGetCardName(param3);
            string name = cardName ?? Loc.Get("duel_a_card");
            Announce(Loc.Get(locKey, name));
        }

        /// <summary>
        /// Attempts to resolve a card name from a possible unique ID.
        /// Returns null if the ID is invalid or card cannot be resolved.
        /// </summary>
        private static string TryGetCardName(int possibleUniqueId)
        {
            if (possibleUniqueId <= 0) return null;
            try
            {
                uint cardId = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(possibleUniqueId);
                if (cardId == 0 || cardId > 100000) return null;

                var content = Il2CppYgomGame.Card.Content.Instance;
                if (content == null) return null;

                string name = content.GetName((int)cardId);
                if (string.IsNullOrEmpty(name)) return null;
                return name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>Converts a Phase enum to a localized display name.</summary>
        internal static string GetPhaseName(Il2CppYgomGame.Duel.Engine.Phase phase)
        {
            return phase switch
            {
                Il2CppYgomGame.Duel.Engine.Phase.Draw => Loc.Get("duel_phase_draw"),
                Il2CppYgomGame.Duel.Engine.Phase.Standby => Loc.Get("duel_phase_standby"),
                Il2CppYgomGame.Duel.Engine.Phase.Main1 => Loc.Get("duel_phase_main1"),
                Il2CppYgomGame.Duel.Engine.Phase.Battle => Loc.Get("duel_phase_battle"),
                Il2CppYgomGame.Duel.Engine.Phase.Main2 => Loc.Get("duel_phase_main2"),
                Il2CppYgomGame.Duel.Engine.Phase.End => Loc.Get("duel_phase_end"),
                _ => Loc.Get("duel_phase_unknown")
            };
        }

        #endregion
    }
}
