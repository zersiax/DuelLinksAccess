using System;
using System.Collections.Generic;
using MelonLoader;

namespace DuelLinksAccess
{
    /// <summary>
    /// Tracks the ATK/DEF battle position of monsters by their runtime uniqueId.
    /// The command-mask heuristic (TurnAtk/TurnDef bits) only works the turn AFTER
    /// a position change — once a monster has changed position this turn, both bits
    /// drop and the heuristic falls back to ATK regardless of actual orientation.
    /// CutinTurn / CutinReverse events fire in real time when the engine animates
    /// a position change, so this cache is the authoritative source.
    /// </summary>
    public static class DuelPositionTracker
    {
        // uniqueId -> true if currently in defense position
        private static readonly Dictionary<int, bool> _isDefense = new();

        public static void Reset() => _isDefense.Clear();

        public static void SetAttack(int uniqueId)
        {
            if (uniqueId > 0) _isDefense[uniqueId] = false;
        }

        public static void SetDefense(int uniqueId)
        {
            if (uniqueId > 0) _isDefense[uniqueId] = true;
        }

        public static void Toggle(int uniqueId)
        {
            if (uniqueId <= 0) return;
            _isDefense[uniqueId] = !(_isDefense.TryGetValue(uniqueId, out bool cur) && cur);
        }

        public static void Forget(int uniqueId)
        {
            if (uniqueId > 0) _isDefense.Remove(uniqueId);
        }

        /// <summary>Returns null if the uid has no cached position.</summary>
        public static bool? IsDefense(int uniqueId)
        {
            if (uniqueId <= 0) return null;
            return _isDefense.TryGetValue(uniqueId, out bool isDef) ? (bool?)isDef : null;
        }
    }

    /// <summary>
    /// Processes duel events from DuelClient.RunEffect and generates announcements.
    /// Translates Engine.ViewType events into human-readable screen reader output.
    /// </summary>
    public static class DuelEventAnnouncer
    {
        #region Fields

        /// <summary>Fired when an event should be announced and logged.</summary>
        public static event Action<string> OnAnnouncement;

        /// <summary>Whether a duel is currently in progress. Backed by DuelState.</summary>
        public static bool InDuel => DuelState.InDuel;

        /// <summary>Whether the duel has ended but we're still on the result screen.</summary>
        public static bool DuelEnded => DuelState.DuelEnded;

        // Throttle duplicate messages
        private static string _lastMessage = "";
        private static float _lastMessageTime;
        private const float ThrottleSeconds = 0.3f;

        // Last-announced state — used purely for de-duplication so we don't
        // re-announce the same phase/turn back-to-back. Actual state lives
        // in DuelState (LP, phase, turn player, etc.).
        private static int _lastAnnouncedTurnPlayer = -1;
        private static int _lastAnnouncedPhase = -1;

        // Phase announcements debounce so the rapid Draw → Standby → Main
        // burst at turn start collapses to a single "Main Phase" — Tolk's
        // interrupt=true would otherwise chop each one off.
        private static bool _pendingPhaseAnnouncement;
        private static float _pendingPhaseDeadline = -1f;
        private const float PhaseDebounceSeconds = 0.30f;

        // Deferred dialog text — rapid-fire RunDialog events (multiple per
        // phase transition) replace each other so only the last one speaks
        private static string _pendingDialogText;

        #endregion

        #region Public Methods

        /// <summary>
        /// Server seat of the local player. Delegates to DuelState, which
        /// detects from the visual layer (nearHandCard's CardPlace.team) —
        /// reliable in single-player and PvP/Ranked alike, and works at
        /// duel start before any user action.
        /// </summary>
        public static int MyPlayerNum() => DuelState.MyPlayerNum();

        /// <summary>
        /// Kept for compatibility with PvP hand-play / field-play coroutines
        /// that arm seat capture before firing OnCommand. DuelState detects
        /// the seat from the visual layer at duel start, so this is now a
        /// no-op — left in place so existing call sites don't need surgery.
        /// </summary>
        public static void ArmLocalSeatCapture()
        {
            // Intentionally empty — DuelState.MyPlayerNum() reads from the
            // visual layer and doesn't need an event-action capture trigger.
        }

        /// <summary>Diagnostic: dumps engine seat / player-type info to the log once.</summary>
        public static void DumpPlayerSeats()
        {
            int rawGet = -99, rawMyself = -99, rawRival = -99;
            int type0 = -99, type1 = -99;
            int isMyself0 = -99, isMyself1 = -99, isRival0 = -99, isRival1 = -99;
            int battlePos = -99, tagFaced0 = -99, tagFaced1 = -99;
            try { rawGet = Il2CppYgomGame.Duel.Engine.DLL_DuelGetMyPlayerNum(); } catch { }
            try { rawMyself = Il2CppYgomGame.Duel.Engine.DLL_DuelMyself(); } catch { }
            try { rawRival = Il2CppYgomGame.Duel.Engine.DLL_DuelRival(); } catch { }
            try { type0 = Il2CppYgomGame.Duel.Engine.DLL_DuelGetPlayerType(0); } catch { }
            try { type1 = Il2CppYgomGame.Duel.Engine.DLL_DuelGetPlayerType(1); } catch { }
            try { isMyself0 = Il2CppYgomGame.Duel.Engine.DLL_DuelIsMyself(0); } catch { }
            try { isMyself1 = Il2CppYgomGame.Duel.Engine.DLL_DuelIsMyself(1); } catch { }
            try { isRival0 = Il2CppYgomGame.Duel.Engine.DLL_DuelIsRival(0); } catch { }
            try { isRival1 = Il2CppYgomGame.Duel.Engine.DLL_DuelIsRival(1); } catch { }
            try { battlePos = Il2CppYgomGame.Duel.Engine.DLL_DuelGetBattlePlayerPos(); } catch { }
            try { tagFaced0 = Il2CppYgomGame.Duel.Engine.DLL_DuelGetTagPlayerFaced(0); } catch { }
            try { tagFaced1 = Il2CppYgomGame.Duel.Engine.DLL_DuelGetTagPlayerFaced(1); } catch { }

            DebugLogger.Log(LogCategory.Game, "DuelPlayer",
                $"Seats: GetMyPlayerNum={rawGet}, Myself={rawMyself}, Rival={rawRival}, " +
                $"PlayerType(0)={type0}, PlayerType(1)={type1}, " +
                $"IsMyself(0)={isMyself0}, IsMyself(1)={isMyself1}, " +
                $"IsRival(0)={isRival0}, IsRival(1)={isRival1}, " +
                $"BattlePlayerPos={battlePos}, TagFaced(0)={tagFaced0}, TagFaced(1)={tagFaced1}");
        }

        /// <summary>
        /// Called each frame from Main.OnUpdate(). Handles deferred announcements
        /// that need the engine to finish updating before we read state.
        /// </summary>
        public static void Update()
        {
            // Phase announcements are debounced: wait until the deadline has
            // passed with no further PhaseChange events before speaking, so
            // the rapid Draw → Standby → Main burst at turn start collapses
            // to a single "Main Phase" announcement instead of being chopped
            // up by Tolk's interrupt behavior.
            if (_pendingPhaseAnnouncement
                && UnityEngine.Time.unscaledTime >= _pendingPhaseDeadline)
            {
                _pendingPhaseAnnouncement = false;
                _pendingPhaseDeadline = -1f;
                AnnouncePhaseChange();
            }

            if (_pendingDialogText != null)
            {
                // Drop the deferred dialog if a card-selection picker has
                // become active in the meantime — the picker prompt is the
                // relevant cue. Engine fires RunDialog "You succeeded in
                // Special Summoning a monster. Check the field?" with p3=2
                // BEFORE opening the material EmotionalList; without this
                // suppression the user hears a misleading completion message
                // before the actual material picker reads its options.
                if (IsEmotionalListActive())
                {
                    _pendingDialogText = null;
                }
                else
                {
                    Announce(_pendingDialogText);
                    _pendingDialogText = null;
                }
            }
        }

        /// <summary>
        /// Called from Harmony postfix on DuelClient.RunEffect.
        /// Translates the raw event into an announcement.
        /// </summary>
        public static void OnRunEffect(int id, int param1, int param2, int param3)
        {
            // Update the central duel-state adapter first so any consumer
            // that reads DuelState during this event cycle (announcer below,
            // field navigator, status hotkey) sees the fresh values.
            DuelState.OnRunEffect(id, param1, param2, param3);

            var viewType = (Il2CppYgomGame.Duel.Engine.ViewType)id;

            DebugLogger.Log(LogCategory.Game, "DuelEvent",
                $"{viewType} ({id}): p1={param1}, p2={param2}, p3={param3}");

            switch (viewType)
            {
                case Il2CppYgomGame.Duel.Engine.ViewType.DuelStart:
                    // DuelState handles InDuel/DuelEnded flags and clears its
                    // own state. We just reset our announcement-dedup vars
                    // and the position tracker, then announce.
                    _lastAnnouncedTurnPlayer = -1;
                    _lastAnnouncedPhase = -1;
                    DuelPositionTracker.Reset();
                    DumpPlayerSeats();
                    Announce(Loc.Get("duel_started"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.DuelEnd:
                    Announce(Loc.Get("duel_ended"));
                    DuelPositionTracker.Reset();
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.TurnChange:
                    AnnounceTurnChange();
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.PhaseChange:
                    // DuelState already captured the phase value from p2.
                    // We only need to schedule the debounced announcement.
                    _pendingPhaseDeadline = UnityEngine.Time.unscaledTime + PhaseDebounceSeconds;
                    _pendingPhaseAnnouncement = true;
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.LifeDamage:
                    // LifeDamage fires twice per damage event (p3=257 then p3=256).
                    // Only announce on first fire (p3 bit 0 set). p2 carries the
                    // exact damage amount (negative = took damage, positive = gain).
                    // DuelState has already applied the delta when we get here.
                    if ((param3 & 1) == 1)
                        AnnounceLPDamage(param1, param2);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.LifeSet:
                    // DuelState tracks; nothing to do here for announcements
                    // (the initial 4000 isn't worth speaking).
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.WaitInput:
                    // Resumed duels never fire DuelStart; DuelState catches
                    // that and sets InDuel itself.
                    // Suppress "Your move" when the engine is in mid-selection
                    // (LockOn = spell target, Selection = tribute/material pick).
                    // The next EmotionalList prompt or selection cue gets the
                    // user's attention more accurately than a generic turn cue.
                    // Without this, after picking the first XYZ material the
                    // game emits WaitInput(p1=8=Selection) and the user hears
                    // "Your move", thinks the summon completed, navigates away,
                    // and accidentally cancels the in-flight summon.
                    if (!IsMidSelectionInput())
                        Announce(Loc.Get("duel_your_move"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CpuThinking:
                    Announce(Loc.Get("duel_opponent_thinking"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CutinDraw:
                    AnnounceDraw(param1);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.RunSummon:
                    DuelPositionTracker.SetAttack(TryResolveUniqueId(param2, param3));
                    AnnounceCardEvent("duel_summoned", param1, param2, param3);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.RunSpSummon:
                    DuelPositionTracker.SetAttack(TryResolveUniqueId(param2, param3));
                    AnnounceCardEvent("duel_sp_summoned", param1, param2, param3);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CutinActivate:
                    AnnounceCardEvent("duel_activated", param1, param2, param3);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CardSet:
                    // Set monster (face-down DEF) or set spell/trap. Position
                    // tracking only matters for monsters; setting an invalid uid
                    // for a spell is harmless because no field reader will look
                    // at a spell's ATK/DEF anyway.
                    DuelPositionTracker.SetDefense(TryResolveUniqueId(param2, param3));
                    Announce(Loc.Get("duel_card_set"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CutinTurn:
                    // Battle position change (ATK ↔ DEF) animation. Update cache
                    // and announce — the user issued the command but the field
                    // readout would otherwise show the stale position until the
                    // next turn (when TurnAtk/TurnDef bits return to cmdMask).
                    HandlePositionChange(param1, param2, param3);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CutinReverse:
                    // Flip-summon (face-down DEF -> face-up ATK) animation.
                    DuelPositionTracker.SetAttack(TryResolveUniqueId(param2, param3));
                    AnnounceCardEvent("duel_summoned", param1, param2, param3);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CutinFlip:
                    // Forced face-down -> face-up flip (e.g. attacked face-down
                    // monster). Battle position remains DEF — only the face
                    // changes. Without this, ReadCurrentCard sees isFaceUp=true,
                    // misses the cache (resumed duels never recorded the initial
                    // CardSet), falls back to cmdMask, and reports ATK.
                    HandleFlip(param1, param2);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.CardBreak:
                case Il2CppYgomGame.Duel.Engine.ViewType.CardExplosion:
                    DuelPositionTracker.Forget(TryResolveUniqueId(param2, param3));
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
                    // p3=0: informational "Check the field?" — suppress entirely.
                    // The player already hears what happened from other events
                    // (summon, phase change, etc.) and can inspect the field anytime.
                    // p3=1: actionable "Activate a card or effect?" chain window.
                    // Defer to next frame so rapid bursts only speak the last one.
                    if (param3 != 0)
                        DeferDialogText("duel_dialog");
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.RunList:
                    AnnounceDialogText("duel_select_card");
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.ChainSet:
                    Announce(Loc.Get("duel_chain_link", param3));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.RunFusion:
                    Announce(Loc.Get("duel_fusion"));
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
                int me = DuelState.MyPlayerNum();
                int myLP = DuelState.GetLP(me);
                int oppLP = DuelState.GetLP(1 - me);
                int turnNum = DuelState.TurnNumber;
                int turnPlayer = DuelState.CurrentTurnPlayer;
                var phaseEnum = DuelState.CurrentPhase;

                string phase = phaseEnum == Il2CppYgomGame.Duel.Engine.Phase.Null
                    ? Loc.Get("duel_phase_cutscene")
                    : GetPhaseName(phaseEnum);
                string whose = turnPlayer == me
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

        /// <summary>Resets announcement-side state. DuelState resets itself.</summary>
        public static void Reset()
        {
            _lastMessage = "";
            _lastAnnouncedTurnPlayer = -1;
            _lastAnnouncedPhase = -1;
            _pendingPhaseAnnouncement = false;
            _pendingPhaseDeadline = -1f;
            _pendingDialogText = null;
            DuelState.Reset();
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

        /// <summary>
        /// True when the engine is mid-selection — waiting for the user to
        /// pick a target / material / tribute. The "Your move" cue is wrong
        /// in these states because the user is not choosing what to do next,
        /// they're answering an in-flight prompt that will fire its own cue
        /// (EmotionalList opening or selection-mode field tap).
        /// </summary>
        private static bool IsMidSelectionInput()
        {
            try
            {
                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                var worker = client?.worker2d;
                if (worker == null) return false;
                var t = worker.curInputType;
                return t == Il2CppYgomGame.Duel.Engine.MenuActType.Selection
                    || t == Il2CppYgomGame.Duel.Engine.MenuActType.LockOn;
            }
            catch { return false; }
        }

        /// <summary>
        /// True when the game's card-selection picker (EmotionalList) is
        /// currently active and waiting for input. Used to drop deferred
        /// dialog announcements that would precede a picker prompt and
        /// confuse the user (e.g. "You succeeded in Special Summoning a
        /// monster. Check the field?" right before the material picker).
        /// </summary>
        private static bool IsEmotionalListActive()
        {
            try
            {
                var emo = Il2CppYgomGame.Duel.EmotionalList.Instance;
                if (emo == null) return false;
                var go = emo.gameObject;
                if (go == null || !go.activeInHierarchy) return false;
                if (emo.isClosing) return false;
                return emo.selectMaxNum > 0;
            }
            catch { return false; }
        }

        private static void AnnounceTurnChange()
        {
            try
            {
                int turnPlayer = DuelState.CurrentTurnPlayer;
                int turnNum = DuelState.TurnNumber;

                if (turnPlayer == _lastAnnouncedTurnPlayer) return;
                _lastAnnouncedTurnPlayer = turnPlayer;

                string whose = turnPlayer == DuelState.MyPlayerNum()
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
                int phaseVal = (int)DuelState.CurrentPhase;
                if (phaseVal == _lastAnnouncedPhase) return;
                _lastAnnouncedPhase = phaseVal;

                var phase = (Il2CppYgomGame.Duel.Engine.Phase)phaseVal;

                // Skip announcing Null/unknown phases (cutscenes, automated sequences)
                if (phase == Il2CppYgomGame.Duel.Engine.Phase.Null) return;

                Announce(GetPhaseName(phase));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelPhase", $"Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Announces LP change. DuelState has already applied the delta to
        /// its tracked LP by the time we get here, so we just read the new
        /// value and announce it with the damage/recovery amount.
        /// </summary>
        private static void AnnounceLPDamage(int player, int damageAmount)
        {
            try
            {
                bool isMe = DuelState.IsMine(player);
                string who = isMe ? Loc.Get("duel_you") : Loc.Get("duel_opponent");
                int absDamage = Math.Abs(damageAmount);
                int newLP = DuelState.GetLP(player);

                if (damageAmount < 0)
                    Announce(Loc.Get("duel_lp_damage", who, absDamage, newLP));
                else if (damageAmount > 0)
                    Announce(Loc.Get("duel_lp_recover", who, absDamage, newLP));
                else
                    Announce(Loc.Get("duel_lp_update", who, newLP));
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelLP", $"Error: {ex.Message}");
            }
        }

        private static void AnnounceDraw(int player)
        {
            if (DuelState.IsMine(player))
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
            // Path 1 — single-player convention: p2 (or sometimes p3) is the
            // raw uniqueId. Resolve via DLL_DuelGetCardIDByUniqueID2.
            string cardName = TryGetCardName(param2) ?? TryGetCardName(param3);

            // Path 2 — PvP fallback: opponent's uniqueIds aren't in the local
            // engine's uid->cardId table, so p2/p3 lookups return 0. Decode
            // p1 as a packed field position ((uniqueId << 8) | (slot << 6) |
            // (locate << 1) | player), look up the LIVE uid at that field
            // slot, then resolve that.
            if (cardName == null && param1 != 0)
            {
                cardName = TryGetCardNameFromPackedPosition(param1);
            }

            // Diagnostic: when we still fall back to "a card", log everything
            // we probed so we can see which path needs additional fallbacks.
            if (cardName == null)
            {
                int id2 = -1, id3 = -1;
                try { id2 = (int)Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(param2); } catch { }
                try { id3 = (int)Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(param3); } catch { }

                int p1Player = param1 & 1;
                int p1Locate = (param1 >> 1) & 0x1F;
                int p1Slot = (param1 >> 6) & 0x3;
                int p1Uid = param1 >> 8;
                int fieldUid = 0, fieldCardId = 0;
                try
                {
                    fieldUid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(p1Player, p1Locate, p1Slot);
                    if (fieldUid > 0)
                        fieldCardId = (int)Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardIDByUniqueID2(fieldUid);
                }
                catch { }

                DebugLogger.Log(LogCategory.Game, "DuelEvent",
                    $"Card resolve FAIL for {locKey}: " +
                    $"p1={param1} (player={p1Player} locate={p1Locate} slot={p1Slot} uidHi={p1Uid}), " +
                    $"p2={param2}, p3={param3}, " +
                    $"DLL_GetCardIDByUniqueID2(p2)={id2}, (p3)={id3}, " +
                    $"fieldUid@({p1Player},{p1Locate},{p1Slot})={fieldUid}, fieldCardId={fieldCardId}");
            }

            string name = cardName ?? Loc.Get("duel_a_card");
            Announce(Loc.Get(locKey, name));
        }

        /// <summary>
        /// PvP-aware fallback for opponent card names: decodes p1 as a packed
        /// field position ((uidHi &lt;&lt; 8) | (slot &lt;&lt; 6) | (locate &lt;&lt; 1) | player),
        /// queries the LIVE uniqueId at that field slot, then resolves it.
        /// Works for RunSummon, RunSpSummon, etc. where p1 encodes destination.
        /// </summary>
        private static string TryGetCardNameFromPackedPosition(int packed)
        {
            try
            {
                int player = packed & 1;
                int locate = (packed >> 1) & 0x1F;
                int slot = (packed >> 6) & 0x3;

                int uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(player, locate, slot);
                if (uid <= 0) return null;
                return TryGetCardName(uid);
            }
            catch { return null; }
        }

        /// <summary>
        /// Picks the first of two candidate ints that resolves to a valid card
        /// via DLL_DuelGetCardIDByUniqueID2. Used to extract the uniqueId from
        /// an event's p2/p3 — different events use different slots.
        /// Returns 0 if neither resolves.
        /// </summary>
        private static int TryResolveUniqueId(int candidateA, int candidateB)
        {
            // Same guard as TryGetCardName: validate against FindCardInstance
            // so we don't accept locate values (1/2/3) that happen to be in
            // the engine's uid->card table as deck cards.
            var goMgr = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager;
            foreach (int candidate in new[] { candidateA, candidateB })
            {
                if (candidate <= 0) continue;
                try
                {
                    if (goMgr?.FindCardInstance(candidate) != null) return candidate;
                }
                catch { }
            }
            return 0;
        }

        /// <summary>
        /// Handles a CutinTurn event: toggles the cached position for the affected
        /// monster and announces the new position. Without this, ReadCurrentCard
        /// reports the wrong position from the moment of the change until the next
        /// turn (when TurnAtk/TurnDef bits return to the command mask).
        ///
        /// Empirically, CutinTurn uses p1=player, p2=field locate, p3=unknown
        /// (1 in observed cases). Earlier code assumed p2=uniqueId, which by
        /// coincidence resolved to a real *but wrong* card whenever the locate
        /// happened to match a low uniqueId in the duel — we'd announce the
        /// position change against the wrong card name AND seed the position
        /// cache under the wrong uniqueId, so ReadCurrentCard's tracker lookup
        /// would never hit and the field readout stayed stale.
        /// </summary>
        private static void HandlePositionChange(int param1, int param2, int param3)
        {
            int player = param1;
            int locate = param2;

            int uid = 0;
            try
            {
                uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                    player, locate, 0);
            }
            catch { }

            DebugLogger.Log(LogCategory.Game, "DuelEvent",
                $"CutinTurn: p1={param1}, p2={param2}, p3={param3}, " +
                $"player={player}, locate={locate}, uid={uid}");

            if (uid <= 0) return;

            DuelPositionTracker.Toggle(uid);
            bool isDef = DuelPositionTracker.IsDefense(uid) ?? false;

            string cardName = TryGetCardName(uid) ?? Loc.Get("duel_a_card");
            string position = isDef
                ? Loc.Get("duel_defense_position")
                : Loc.Get("duel_attack_position");

            string locKey = player == MyPlayerNum()
                ? "duel_position_changed"
                : "duel_position_changed_opponent";
            Announce(Loc.Get(locKey, position, cardName));
        }

        /// <summary>
        /// Handles a CutinFlip event: a face-down monster has been turned
        /// face-up (typically because it was attacked). Position stays in
        /// defense; only the face changes. Empirically p1=player, p2=locate.
        /// </summary>
        private static void HandleFlip(int player, int locate)
        {
            int uid = 0;
            try
            {
                uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                    player, locate, 0);
            }
            catch { }

            DebugLogger.Log(LogCategory.Game, "DuelEvent",
                $"CutinFlip: player={player}, locate={locate}, uid={uid}");

            if (uid > 0) DuelPositionTracker.SetDefense(uid);
        }

        /// <summary>
        /// Attempts to resolve a card name from a possible unique ID.
        /// Returns null if the ID is invalid or card cannot be resolved.
        /// </summary>
        private static string TryGetCardName(int possibleUniqueId)
        {
            // Validate the uniqueId against the visual layer first. Locate
            // values (low integers like 1, 2, 3) collide with deck-card
            // uniqueIds in DLL_DuelGetCardIDByUniqueID2 and would resolve to
            // whatever card the engine happens to have at that uid (commonly
            // a deck card like Blue-Eyes White Dragon that was never played).
            // FindCardInstance returns the active CardRoot for that uid or
            // null — locate values won't have one.
            if (possibleUniqueId <= 0) return null;
            try
            {
                var root = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager
                    ?.FindCardInstance(possibleUniqueId);
                int cardId = 0;
                if (root != null)
                {
                    try { cardId = root.cardId; } catch { }
                }
                if (cardId <= 0)
                {
                    // Fall back to engine lookup, but only when we've already
                    // confirmed a CardRoot exists for this uid — that filters
                    // out spurious locate-as-uid hits.
                    if (root == null) return null;
                    try
                    {
                        cardId = (int)Il2CppYgomGame.Duel.Engine
                            .DLL_DuelGetCardIDByUniqueID2(possibleUniqueId);
                    }
                    catch { return null; }
                }
                if (cardId <= 0 || cardId > 100000) return null;

                var content = Il2CppYgomGame.Card.Content.Instance;
                if (content == null) return null;
                string name = content.GetName(cardId);
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch { return null; }
        }

        /// <summary>
        /// Composes dialog text and defers it to next frame. Rapid-fire RunDialog
        /// events (2-3 per phase transition) replace each other so only the last
        /// one in a burst is actually spoken.
        /// </summary>
        private static void DeferDialogText(string fallbackKey)
        {
            try
            {
                string text = ComposeDialogText();
                if (!string.IsNullOrEmpty(text))
                {
                    _pendingDialogText = text;
                    return;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelDialog",
                    $"ComposeDialogText error: {ex.Message}");
            }
            _pendingDialogText = Loc.Get(fallbackKey);
        }

        /// <summary>
        /// Composes and announces duel dialog/selection text from the Engine's
        /// DialogMix API immediately. Falls back to the given Loc key.
        /// </summary>
        private static void AnnounceDialogText(string fallbackKey)
        {
            try
            {
                string text = ComposeDialogText();
                if (!string.IsNullOrEmpty(text))
                {
                    Announce(text);
                    return;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelDialog",
                    $"ComposeDialogText error: {ex.Message}");
            }
            Announce(Loc.Get(fallbackKey));
        }

        /// <summary>
        /// Reads Engine.DialogGetMixNum/Type/Data to compose the actual dialog text.
        /// Returns null if no mix data is available or composition fails.
        /// </summary>
        private static string ComposeDialogText()
        {
            int count = Il2CppYgomGame.Duel.Engine.DialogGetMixNum();
            if (count <= 0) return null;

            DebugLogger.Log(LogCategory.Game, "DuelDialog",
                $"DialogMix entries: {count}");

            var content = Il2CppYgomGame.Card.Content.Instance;

            // First pass: resolve all fragments into strings
            var fragments = new string[count];
            var types = new Il2CppYgomGame.Duel.Engine.DialogMixTextType[count];
            for (int i = 0; i < count; i++)
            {
                types[i] = Il2CppYgomGame.Duel.Engine.DialogGetMixType(i);
                int data = Il2CppYgomGame.Duel.Engine.DialogGetMixData(i);

                DebugLogger.Log(LogCategory.Game, "DuelDialog",
                    $"  [{i}] type={types[i]}, data={data}");

                fragments[i] = ResolveFragment(types[i], data, content);
            }

            // Second pass: compose text, substituting %s placeholders in AddString
            // with the next Ins* fragment (InsCard, InsNum, InsType, InsAttr, InsString)
            var sb = new System.Text.StringBuilder();
            int nextIns = 0; // index of next unconsumed Ins* fragment

            for (int i = 0; i < count; i++)
            {
                switch (types[i])
                {
                    case Il2CppYgomGame.Duel.Engine.DialogMixTextType.AddString:
                        string text = fragments[i] ?? "";
                        // Strip color/formatting tags like @3, @0
                        text = System.Text.RegularExpressions.Regex.Replace(
                            text, @"@\d", "");
                        // Substitute %s placeholders with Ins* fragments
                        while (text.Contains("%s"))
                        {
                            string replacement = "";
                            // Find next Ins* fragment
                            while (nextIns < count)
                            {
                                if (IsInsertFragment(types[nextIns]))
                                {
                                    replacement = fragments[nextIns] ?? "";
                                    nextIns++;
                                    break;
                                }
                                nextIns++;
                            }
                            int pos = text.IndexOf("%s");
                            text = text.Substring(0, pos) + replacement
                                + text.Substring(pos + 2);
                        }
                        sb.Append(text);
                        break;

                    case Il2CppYgomGame.Duel.Engine.DialogMixTextType.AddCr:
                        sb.Append(' ');
                        break;

                    case Il2CppYgomGame.Duel.Engine.DialogMixTextType.Null:
                        break;

                    default:
                        // Ins* fragments not consumed by %s — append directly
                        if (i >= nextIns && !string.IsNullOrEmpty(fragments[i]))
                            sb.Append(fragments[i]);
                        break;
                }
            }

            string result = sb.ToString().Trim();
            // Collapse multiple spaces
            result = System.Text.RegularExpressions.Regex.Replace(result, @"  +", " ");
            if (string.IsNullOrEmpty(result)) return null;

            DebugLogger.Log(LogCategory.Game, "DuelDialog",
                $"Composed: {result}");
            return result;
        }

        /// <summary>Resolves a single DialogMix fragment to its text value.</summary>
        private static string ResolveFragment(
            Il2CppYgomGame.Duel.Engine.DialogMixTextType type, int data,
            Il2CppYgomGame.Card.Content content)
        {
            switch (type)
            {
                case Il2CppYgomGame.Duel.Engine.DialogMixTextType.AddString:
                case Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsString:
                case Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsStringNoColor:
                case Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsStringIfable:
                    return content?.GetDialogText(data);

                case Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsCard:
                    return content?.GetName(data);

                case Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsNum:
                    return data.ToString();

                case Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsType:
                    return content?.GetTypeText(
                        (Il2CppYgomGame.Card.Content.Type)data);

                case Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsAttr:
                    return content?.GetAttributeText(
                        (Il2CppYgomGame.Card.Content.Attribute)data);

                default:
                    return null;
            }
        }

        /// <summary>Whether a DialogMixTextType is an Ins* (insertion) fragment.</summary>
        private static bool IsInsertFragment(
            Il2CppYgomGame.Duel.Engine.DialogMixTextType type)
        {
            return type == Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsCard
                || type == Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsNum
                || type == Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsType
                || type == Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsAttr
                || type == Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsString
                || type == Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsStringNoColor
                || type == Il2CppYgomGame.Duel.Engine.DialogMixTextType.InsStringIfable;
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
