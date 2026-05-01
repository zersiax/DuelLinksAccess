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

        // Deferred dialog text — rapid-fire RunDialog events (multiple per
        // phase transition) replace each other so only the last one speaks
        private static string _pendingDialogText;

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

            if (_pendingDialogText != null)
            {
                Announce(_pendingDialogText);
                _pendingDialogText = null;
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
                    // Don't reset LP here — LifeSet fires BEFORE DuelStart
                    // and already sets the correct starting values
                    DuelPositionTracker.Reset();
                    Announce(Loc.Get("duel_started"));
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.DuelEnd:
                    Announce(Loc.Get("duel_ended"));
                    InDuel = false;
                    DuelEnded = true;
                    DuelPositionTracker.Reset();
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
                    // LifeDamage fires twice per damage event (p3=257 then p3=256).
                    // Only announce on first fire (p3 bit 0 set). p2 carries the
                    // exact damage amount (negative = took damage, positive = gain).
                    if ((param3 & 1) == 1)
                        AnnounceLPDamage(param1, param2);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.LifeSet:
                    TrackLP(param1, param2);
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.WaitInput:
                    // Resumed duels never fire DuelStart, so set InDuel here too
                    if (!InDuel) InDuel = true;
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
            _pendingDialogText = null;
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

        /// <summary>
        /// Announces LP change using the damage amount from LifeDamage p2.
        /// Computes new LP from tracked value instead of polling DLL_DuelGetLP
        /// (which lags behind during damage animations).
        /// </summary>
        private static void AnnounceLPDamage(int player, int damageAmount)
        {
            try
            {
                string who = player == 0 ? Loc.Get("duel_you") : Loc.Get("duel_opponent");
                int lastLP = player == 0 ? _lastMyLP : _lastOppLP;

                // damageAmount is negative for damage, positive for recovery
                int absDamage = Math.Abs(damageAmount);

                if (lastLP >= 0)
                {
                    int newLP = Math.Max(0, lastLP + damageAmount);
                    if (damageAmount < 0)
                        Announce(Loc.Get("duel_lp_damage", who, absDamage, newLP));
                    else
                        Announce(Loc.Get("duel_lp_recover", who, absDamage, newLP));

                    if (player == 0) _lastMyLP = newLP;
                    else _lastOppLP = newLP;
                }
                else
                {
                    // No tracked LP yet — read from engine as fallback
                    int lp = Il2CppYgomGame.Duel.Engine.DLL_DuelGetLP(player);
                    Announce(Loc.Get("duel_lp_update", who, lp));
                    if (player == 0) _lastMyLP = lp;
                    else _lastOppLP = lp;
                }
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelLP", $"Error: {ex.Message}");
            }
        }

        /// <summary>Silently tracks LP on LifeSet using the value from the event directly.</summary>
        private static void TrackLP(int player, int lpValue)
        {
            if (player == 0) _lastMyLP = lpValue;
            else _lastOppLP = lpValue;
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
        /// Picks the first of two candidate ints that resolves to a valid card
        /// via DLL_DuelGetCardIDByUniqueID2. Used to extract the uniqueId from
        /// an event's p2/p3 — different events use different slots.
        /// Returns 0 if neither resolves.
        /// </summary>
        private static int TryResolveUniqueId(int candidateA, int candidateB)
        {
            foreach (int candidate in new[] { candidateA, candidateB })
            {
                if (candidate <= 0) continue;
                try
                {
                    uint cardId = Il2CppYgomGame.Duel.Engine
                        .DLL_DuelGetCardIDByUniqueID2(candidate);
                    if (cardId > 0 && cardId < 100000) return candidate;
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

            string locKey = player == 1
                ? "duel_position_changed_opponent"
                : "duel_position_changed";
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
