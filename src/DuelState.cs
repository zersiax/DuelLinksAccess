using System;
using MelonLoader;

namespace DuelLinksAccess
{
    /// <summary>
    /// Snapshot of one card at a moment in time. Backed by CardRoot for
    /// field cards, HandInfo + Content for hand cards. Returned by
    /// DuelState's per-card accessors. Null Name means the card-db id
    /// couldn't be resolved (e.g., face-down opponent card with mrk=0).
    /// </summary>
    public readonly record struct CardSnapshot(
        int UniqueId,
        int CardDbId,
        string Name,
        int Atk,
        int Def,
        int Level,
        bool IsAttack,
        bool IsFaceUp);

    /// <summary>
    /// Canonical read-only adapter for duel state. Backed by the visual
    /// layer (DuelGameObjectManager.cardRoots, HandCardManager) and
    /// event-tracked counters (phase, turn, LP) — both populated identically
    /// in single-player and PvP/Ranked.
    ///
    /// The motivation: the engine's DLL_Duel* state queries are HOLLOW in
    /// PvP because the server runs the duel logic — DLL_DuelGetCardNum,
    /// DLL_DuelGetCardUniqueID, DLL_DuelComGetCommandMask,
    /// DLL_DuelGetCurrentPhase, DLL_DuelGetMyPlayerNum, etc. all return 0
    /// or stale values. The visual layer (3D world cards) and the
    /// RunEffect event stream ARE the source of truth, and they're equally
    /// populated in single-player. So routing every read through this
    /// adapter gives us one code path that works in both modes.
    ///
    /// Consumers (DuelFieldNavigator, DuelEventAnnouncer, DuelHandler)
    /// query DuelState instead of calling Engine APIs directly. Action
    /// dispatch (TapHandCard, OnDoCardCommand, CardCommand.OnCommand, etc.)
    /// stays where it is — those are commands we issue, not reads.
    ///
    /// Wiring: DuelEventAnnouncer.OnRunEffect calls DuelState.OnRunEffect
    /// from its Harmony postfix entry, so DuelState's tracked counters
    /// always reflect the most recent server-pushed event.
    /// </summary>
    public static class DuelState
    {
        #region Engine locate constants

        private const int LocateHand = 13;
        private const int LocateExtra = 14;
        private const int LocateDeck = 15;
        private const int LocateGrave = 16;

        // Field locates for monster / spell zones (Speed Duel: 3 slots each).
        // The engine fills monsters at 2 → 3 → 1 (middle, right, left) and
        // spells at 9 → 10 → 8. EMZ slots are loc=5 (left) and loc=6 (right);
        // shared between players, populated by Fusion/Synchro/Xyz/Link summons.
        private static readonly int[] MonsterLocates = { 2, 3, 1 };
        private static readonly int[] SpellLocates = { 9, 10, 8 };
        private static readonly int[] EMZLocates = { 5, 6 };

        #endregion

        #region Event-tracked state

        private static int _localSeat = -1;
        private static Il2CppYgomGame.Duel.Engine.Phase _currentPhase
            = Il2CppYgomGame.Duel.Engine.Phase.Null;
        private static int _currentTurnPlayer = -1;
        private static int _turnNumber = 0;
        private static readonly int[] _lp = { -1, -1 };
        private static bool _inDuel;
        private static bool _duelEnded;

        #endregion

        #region Public state properties

        public static bool InDuel => _inDuel;
        public static bool DuelEnded => _duelEnded;
        public static Il2CppYgomGame.Duel.Engine.Phase CurrentPhase => _currentPhase;
        public static int CurrentTurnPlayer => _currentTurnPlayer;
        public static int TurnNumber => _turnNumber;

        /// <summary>Engine's current input-wait mode (BattlePhase, CheckChain, LockOn, etc.).</summary>
        public static Il2CppYgomGame.Duel.Engine.MenuActType InputType
        {
            get
            {
                try
                {
                    return Il2CppYgomGame.Duel.DuelClient.instance?.worker2d?.curInputType
                        ?? Il2CppYgomGame.Duel.Engine.MenuActType.Null;
                }
                catch { return Il2CppYgomGame.Duel.Engine.MenuActType.Null; }
            }
        }

        #endregion

        #region Seat resolution

        /// <summary>
        /// Server seat assigned to the local player (0 or 1). Detected from
        /// the visual layer: nearHandCard is always the local viewer's hand,
        /// so the CardPlace.team of its first card IS our server seat. This
        /// works at duel start once hands are dealt — well before any user
        /// action — and works identically in single-player and PvP.
        ///
        /// Engine APIs (DLL_DuelGetMyPlayerNum, DLL_DuelMyself) are kept as
        /// a last-resort fallback; they're correct in single-player and
        /// lie in PvP, so we only consult them when the visual layer hasn't
        /// initialized yet (very brief window at duel start).
        /// </summary>
        public static int MyPlayerNum()
        {
            if (_localSeat == 0 || _localSeat == 1) return _localSeat;

            // Primary: nearHandCard's first card → its CardPlace.team
            try
            {
                var client = Il2CppYgomGame.Duel.DuelClient.instance;
                var hcm = client?.worker3d?.handCardManager;
                var nearHand = hcm?.nearHandCard;
                var list = nearHand?.m_InfoList;
                if (list != null && list.Count > 0)
                {
                    int uid = list[0].m_UniqueId;
                    var root = client.worker3d.goManager?.FindCardInstance(uid);
                    var place = root?.toLocator?.cardPlace;
                    if (place != null)
                    {
                        int seat = place.team;
                        if (seat == 0 || seat == 1)
                        {
                            _localSeat = seat;
                            DebugLogger.Log(LogCategory.Game, "DuelState",
                                $"Local seat detected: {seat} (via nearHandCard)");
                            return seat;
                        }
                    }
                }
            }
            catch { }

            // Secondary: any CardRoot whose CardStatus.nearSide is true
            try
            {
                var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                if (roots != null)
                {
                    for (int i = 0; i < roots.Count; i++)
                    {
                        var root = roots[i];
                        var status = root?.cardStatus;
                        if (status != null && status.nearSide)
                        {
                            var place = root.toLocator?.cardPlace;
                            if (place != null && (place.team == 0 || place.team == 1))
                            {
                                _localSeat = place.team;
                                DebugLogger.Log(LogCategory.Game, "DuelState",
                                    $"Local seat detected: {_localSeat} (via nearSide CardRoot)");
                                return _localSeat;
                            }
                        }
                    }
                }
            }
            catch { }

            // Last resort: engine API (correct in single-player, wrong in PvP)
            try
            {
                int p = Il2CppYgomGame.Duel.Engine.DLL_DuelGetMyPlayerNum();
                if (p == 0 || p == 1) return p;
            }
            catch { }

            return 0;
        }

        public static int OppPlayerNum() => 1 - MyPlayerNum();

        /// <summary>True iff the given engine/event player number is the local player.</summary>
        public static bool IsMine(int eventPlayer) => eventPlayer == MyPlayerNum();

        #endregion

        #region Vitals

        /// <summary>
        /// Returns LP for the given player from event-tracked state.
        /// Falls back to DLL_DuelGetLP if events haven't initialized.
        /// </summary>
        public static int GetLP(int player)
        {
            if (player < 0 || player > 1) return -1;
            if (_lp[player] >= 0) return _lp[player];
            try { return Il2CppYgomGame.Duel.Engine.DLL_DuelGetLP(player); }
            catch { return -1; }
        }

        #endregion

        #region Card lookups

        /// <summary>
        /// Returns the card at the given field position, or null if empty.
        /// "player" is the engine/event server seat. "locate" is the
        /// engine locate value (1/2/3 monsters, 8/9/10 spells, 6 EMZ, etc.).
        /// "slot" is the within-zone index, always 0 for one-slot field zones.
        /// </summary>
        public static CardSnapshot? GetFieldCard(int player, int locate, int slot)
        {
            var root = FindFieldCardRoot(player, locate, slot);
            if (root == null)
            {
                // Stack zones (Extra Deck, Grave, Deck) may have engine data
                // for slots whose CardRoot.locator.index doesn't equal `slot` —
                // the visual layer's index field can be assigned out of order
                // (e.g. Extra Deck card with locator.index=1 even though it's
                // the only one in cardRoots, leaving slot=0 reading as Empty).
                // Re-resolve via the engine's per-slot uniqueId API, then look
                // up the CardRoot by uniqueId instead of by index.
                if (IsStackZone(locate))
                {
                    // PRIMARY: registered-deck array from EngineInitializer.
                    // For Extra Deck and Main Deck specifically, this is the
                    // only source that has the full cardDbId list in PvP —
                    // the visual layer only materializes the top of stack,
                    // and DLL_DuelGetCardUniqueID/CardIDByUniqueID2 are
                    // hollow. Confirmed via 2026-05-20 dump:
                    // EngineInitializer.deck1[1] = [13170,4223,10593,...]
                    // for our 9 extra deck cards while cardRoots showed 1.
                    // Privacy is enforced inside GetRegisteredDeckCard
                    // (opponent → null).
                    var fromDeck = GetRegisteredDeckCard(player, locate, slot);
                    if (fromDeck.HasValue) return fromDeck;

                    int uid = 0;
                    try { uid = Il2CppYgomGame.Duel.Engine
                        .DLL_DuelGetCardUniqueID(player, locate, slot); }
                    catch { }
                    if (uid > 0)
                    {
                        var byUid = FindCardRootByUid(uid);
                        if (byUid != null) return MakeSnapshot(byUid, isFaceUp: true);
                        // Engine knows the card but no CardRoot exists — build
                        // a snapshot from the Content database. Common for Extra
                        // Deck cards the game hasn't materialized as 3D objects.
                        return MakeContentSnapshot(uid);
                    }

                    // PvP fallback when the registered-deck array path didn't
                    // apply (e.g. graveyard, which isn't a registered deck):
                    // walk cardRoots gathering all matching (team, position),
                    // order by locator.index for stable slot mapping, return
                    // the Nth match.
                    var nthRoot = FindNthStackCardRoot(player, locate, slot);
                    if (nthRoot != null) return MakeSnapshot(nthRoot, isFaceUp: true);
                    return null;
                }

                // Non-stack field zone (monsters 1-3, EMZ 5-6, spells 8-12).
                // Each locate is single-occupancy, so slot is always 0 and
                // ANY CardRoot whose (team, position) matches is the right
                // card regardless of its locator.index. Fusion summons and
                // other special summons can produce a CardRoot whose
                // locator.index != 0 (overlay-material bookkeeping etc.),
                // causing the strict FindFieldCardRoot lookup to miss the
                // monster that's actually there. Fall through to a
                // position-only walk to recover that case.
                if (slot == 0)
                {
                    try
                    {
                        var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                        if (roots != null)
                        {
                            for (int i = 0; i < roots.Count; i++)
                            {
                                var r = roots[i];
                                var place = r?.toLocator?.cardPlace;
                                if (place != null
                                    && place.team == player
                                    && place.position == locate)
                                {
                                    root = r;
                                    break;
                                }
                            }
                        }
                    }
                    catch { }
                    if (root == null) return null;
                }
                else
                {
                    return null;
                }
            }

            // Face-up determination: engine's DLL_DuelGetCardFace is authoritative
            // in single-player (1=face-up, 0=face-down). In PvP it returns 0
            // everywhere, so we fall back to the visual layer when the engine
            // appears hollow.
            bool isFaceUp = false;
            int engineFace = -1;
            try
            {
                engineFace = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardFace(player, locate, slot);
            }
            catch { }

            if (engineFace == 1)
            {
                isFaceUp = true;
            }
            else if (engineFace == 0 && !IsEngineHollow)
            {
                // Single-player: trust the engine's face=0 answer.
                isFaceUp = false;
            }
            else
            {
                // PvP fallback: visual visibility, picked by zone. For
                // monster zones the source of truth is isMonsterVisible —
                // a SET monster has cardId>0 (we may know our own) but its
                // monster art is hidden, so isMonsterVisible is false. The
                // isStatusVisible flag stays true for set monsters (status
                // indicator on the card back), which is why combining the
                // two flagged set Saggi as "face up". For spell/trap zones
                // we have no equally clean flag; isStatusVisible is the
                // best available proxy and is correct often enough.
                try
                {
                    if (locate >= 1 && locate <= 8)
                        isFaceUp = root.isMonsterVisible;
                    else if (locate >= 9 && locate <= 12)
                        isFaceUp = root.isStatusVisible;
                    else
                        isFaceUp = root.isMonsterVisible || root.isStatusVisible;
                }
                catch { isFaceUp = true; }
            }

            return MakeSnapshot(root, isFaceUp);
        }

        /// <summary>
        /// Returns the hand card at the given slot for the given player, or
        /// null if the slot is empty. "player" is engine/event server seat;
        /// the local player's hand reads through nearHandCard, opponent's
        /// through farHandCard.
        /// </summary>
        public static CardSnapshot? GetHandCard(int player, int slot)
        {
            try
            {
                var hand = GetHandCardsFor(player);
                var list = hand?.m_InfoList;
                if (list == null || slot < 0 || slot >= list.Count) return null;

                var info = list[slot];
                if (info == null) return null;

                int uid = info.m_UniqueId;
                int mrk = info.m_Mrk;
                if (uid <= 0 && mrk <= 0) return null;

                // HandInfo doesn't carry level/atk/def — pull from Content
                // database using the mrk. Spells/traps return 0s here, which
                // is fine; consumers should check the card kind before
                // announcing stats.
                int level = 0, atk = 0, def = 0;
                if (mrk > 0)
                {
                    try
                    {
                        var content = Il2CppYgomGame.Card.Content.Instance;
                        if (content != null)
                        {
                            level = content.GetLevel(mrk);
                            atk = content.GetAtk(mrk);
                            def = content.GetDef2(mrk);
                        }
                    }
                    catch { }
                }

                return new CardSnapshot(uid, mrk, ResolveCardName(mrk),
                    atk, def, level, IsAttack: false, IsFaceUp: false);
            }
            catch { return null; }
        }

        /// <summary>Number of cards in the given player's hand.</summary>
        public static int GetHandSize(int player)
        {
            try
            {
                var hand = GetHandCardsFor(player);
                return hand?.m_InfoList?.Count ?? 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Generic card count for a (player, locate) tuple. Hand routes
        /// through the visual layer; other locates try the engine first,
        /// then check the per-half-field DeckCardPlace for stack zones
        /// (extra deck, deck, grave, banished), and finally fall back to
        /// counting matching CardRoots if everything above is hollow.
        /// </summary>
        public static int GetCardCount(int player, int locate)
        {
            if (locate == LocateHand) return GetHandSize(player);

            // Local player's extra deck / main deck: the EngineInitializer's
            // registered-deck array is the only authoritative source in PvP.
            // The visual layer materializes only the top of stack so a 9-card
            // extra deck reports as 1 CardRoot; the engine DLL queries are
            // hollow. The registered array is fixed at duel start (and may
            // grow if a skill adds cards), so the count from here is accurate
            // for "cards the user could have summoned from extra deck this
            // duel" even if some have already been used.
            if (locate == LocateExtra || locate == LocateDeck)
            {
                int registered = GetRegisteredDeckCount(player, locate);
                if (registered > 0) return registered;
            }

            int engineCount = 0;
            try { engineCount = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardNum(player, locate); }
            catch { }
            if (engineCount > 0) return engineCount;

            // Per-half-field DeckCardPlace fallback. In PvP the engine hollows
            // out DLL_DuelGetCardNum for every locate including extra deck,
            // and only the *top* card gets a CardRoot in goManager.cardRoots
            // (so visual-walk undercounts a 6-card extra deck as 1). The half-
            // field's DeckCardPlace.innerCards is the full list and is set
            // when the deck is registered, so it works for the local player
            // in PvP. Restricted to stack zones to avoid altering working
            // single-occupancy paths.
            if (IsStackZone(locate))
            {
                var place = GetStackPlace(player, locate);
                if (place != null)
                {
                    try
                    {
                        int n = place.innerCards?.Count ?? 0;
                        if (n > 0) return n;
                        // Some places track count separately from innerCards
                        // populating the list (e.g. while a draw animation is
                        // pending). Fall back to localCardNum.
                        int alt = place.localCardNum;
                        if (alt > 0) return alt;
                    }
                    catch { }
                }
            }

            // Visual fallback — last resort. For non-stack zones this is the
            // primary PvP path. For stack zones it'll typically undercount in
            // PvP (only top-of-stack materialized) but we keep it as a final
            // safety net.
            int count = 0;
            try
            {
                var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                if (roots != null)
                {
                    for (int i = 0; i < roots.Count; i++)
                    {
                        var root = roots[i];
                        var place = root?.toLocator?.cardPlace;
                        if (place != null && place.team == player && place.position == locate)
                            count++;
                    }
                }
            }
            catch { }
            return count;
        }

        // The helpers below (GetStackCardRootByIndex, GetStackPlace,
        // DumpStackZoneState, _lastStackDumpTime) were diagnostic scaffolding
        // for the 2026-05-20 investigation into extra-deck data sources.
        // The investigation concluded that EngineInitializer.deck{N} is the
        // authoritative source (see GetRegisteredDeckArray above) and the
        // DeckCardPlace path doesn't carry the data in PvP. The helpers are
        // left intact in case future work needs the same probes for graveyard
        // or banished zones, but they're no longer called from GetFieldCard.
        private static Il2CppYgomGame.Duel.CardRoot GetStackCardRootByIndex(
            int player, int locate, int slot)
        {
            if (slot < 0) return null;
            var place = GetStackPlace(player, locate);
            if (place == null)
            {
                DebugLogger.Log(LogCategory.Game, "DuelState",
                    $"GetStackCardRootByIndex(p={player}, l={locate}, slot={slot}): place is null");
                return null;
            }
            int innerCount = -1, localNum = -1, topIdx = -1, botIdx = -1;
            try { innerCount = place.innerCards?.Count ?? -1; } catch { }
            try { localNum = place.localCardNum; } catch { }
            try { topIdx = place.localTopCardIndex; } catch { }
            try { botIdx = place.bottomIndex; } catch { }

            int engineUid = 0;
            try
            {
                engineUid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                    player, locate, slot);
            }
            catch { }

            DebugLogger.Log(LogCategory.Game, "DuelState",
                $"GetStackCardRootByIndex(p={player}, l={locate}, slot={slot}): " +
                $"innerCards.Count={innerCount} localCardNum={localNum} " +
                $"localTopCardIndex={topIdx} bottomIndex={botIdx} " +
                $"DLL_DuelGetCardUniqueID={engineUid}");

            // Throttled one-shot dump: on the first slot=0 read of a stack
            // zone (per duel or every few seconds), dump the entire visual-
            // layer card inventory so we can see where extra-deck data
            // actually lives. Far too verbose to fire every read, but
            // invaluable for the first navigation of a new zone.
            if (slot == 0)
            {
                try
                {
                    float now = UnityEngine.Time.unscaledTime;
                    if (now - _lastStackDumpTime > 2f)
                    {
                        _lastStackDumpTime = now;
                        DumpStackZoneState(player, locate, place);
                    }
                }
                catch { }
            }

            try
            {
                var inner = place.innerCards;
                if (inner != null && slot < inner.Count)
                {
                    var root = inner[slot];
                    if (root != null) return root;
                }
            }
            catch { }
            return null;
        }

        private static float _lastStackDumpTime;

        /// <summary>
        /// Dumps everything we can probe about a stack zone — the full
        /// cardInstancePool inventory filtered to this (player, locate),
        /// every per-slot uid the engine reports, and the DeckCardPlace's
        /// own GameObject child counts. The goal is to surface the actual
        /// storage location of extra-deck card data when both innerCards
        /// and cardRoots are clearly undersized vs. the deck's real size.
        /// </summary>
        private static void DumpStackZoneState(
            int player, int locate, Il2CppYgomGame.Duel.DeckCardPlace place)
        {
            try
            {
                DebugLogger.Log(LogCategory.Game, "DuelState",
                    $"=== DumpStackZoneState p={player} l={locate} ===");

                // 1. cardInstancePool: every CardRoot ever allocated for this
                // duel, including ones not in goManager.cardRoots.
                var gom = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager;
                var pool = gom?.cardInstancePool;
                var poolList = pool?.list;
                if (poolList != null)
                {
                    int total = poolList.Count;
                    int matching = 0;
                    int rendered = 0;
                    DebugLogger.Log(LogCategory.Game, "DuelState",
                        $"  cardInstancePool.list.Count={total}");
                    for (int i = 0; i < total; i++)
                    {
                        var root = poolList[i];
                        if (root == null) continue;
                        var p = root.toLocator?.cardPlace;
                        if (p == null) continue;
                        if (p.team != player || p.position != locate) continue;
                        matching++;
                        int rid = 0, ruid = 0, rmid = 0, rlidx = -1;
                        try { rid = root.cardId; } catch { }
                        try { ruid = root.uniqueId; } catch { }
                        try { rmid = root.cardPlane?.cardModel?.cardId ?? 0; } catch { }
                        try { rlidx = root.toLocator.index; } catch { }
                        bool inRoots = false;
                        try
                        {
                            var live = gom.cardRoots;
                            if (live != null)
                            {
                                for (int j = 0; j < live.Count; j++)
                                {
                                    if (live[j] == root) { inRoots = true; break; }
                                }
                            }
                        }
                        catch { }
                        if (inRoots) rendered++;
                        DebugLogger.Log(LogCategory.Game, "DuelState",
                            $"  pool[match {matching - 1}]: locator.index={rlidx} cardId={rid} " +
                            $"modelCardId={rmid} uniqueId={ruid} inGoManagerCardRoots={inRoots}");
                    }
                    DebugLogger.Log(LogCategory.Game, "DuelState",
                        $"  total pool matches at (p={player},l={locate})={matching} renderedInGoManager={rendered}");
                }

                // 2. Per-slot engine uid probes for slots 0..localCardNum-1
                int localNum = -1;
                try { localNum = place.localCardNum; } catch { }
                if (localNum > 0 && localNum <= 60)
                {
                    var uidLine = new System.Text.StringBuilder("  per-slot DLL_DuelGetCardUniqueID: ");
                    for (int s = 0; s < localNum; s++)
                    {
                        int uid = 0;
                        try
                        {
                            uid = Il2CppYgomGame.Duel.Engine.DLL_DuelGetCardUniqueID(
                                player, locate, s);
                        }
                        catch { }
                        // For each non-zero uid, also try the cardId resolver
                        int cid = 0;
                        if (uid > 0)
                        {
                            try
                            {
                                uint c = Il2CppYgomGame.Duel.Engine
                                    .DLL_DuelGetCardIDByUniqueID2(uid);
                                if (c < 100000) cid = (int)c;
                            }
                            catch { }
                        }
                        uidLine.Append($"[{s}]uid={uid}/cid={cid} ");
                    }
                    DebugLogger.Log(LogCategory.Game, "DuelState", uidLine.ToString());
                }

                // 3. DeckCardPlace's own GameObject children — outDeck / inDeck
                // / fadingDeck / shuffleDeck might house additional CardRoots.
                try
                {
                    int outChildren = place.outDeck?.transform?.childCount ?? -1;
                    int inChildren  = place.inDeck?.transform?.childCount ?? -1;
                    int fadingChildren = place.fadingDeck?.transform?.childCount ?? -1;
                    int shuffleChildren = place.shuffleDeck?.transform?.childCount ?? -1;
                    DebugLogger.Log(LogCategory.Game, "DuelState",
                        $"  place GameObject children: outDeck={outChildren} " +
                        $"inDeck={inChildren} fadingDeck={fadingChildren} " +
                        $"shuffleDeck={shuffleChildren}");
                }
                catch { }

                // 4. EngineInitializer deck data — this is where
                // DLL_DuelSysSetDeck2 caches the registered deck. Structure:
                // engineInitializer.deck{0..5} is an
                // Il2CppReferenceArray<Il2CppStructArray<int>> — outer
                // probably indexed by [main, extra, side], inner by card
                // position. The visual layer doesn't materialize extra-deck
                // cards in PvP, but this static cache should still have the
                // mrk (card db id) list for the local player's deck.
                try
                {
                    var init = Il2CppYgomGame.Duel.DuelClient.GetEngineInitializer();
                    if (init == null)
                    {
                        DebugLogger.Log(LogCategory.Game, "DuelState",
                            "  EngineInitializer is null");
                    }
                    else
                    {
                        int myNum = -1;
                        int rushFlag = -1;
                        try { myNum = init.myPlayerNum; } catch { }
                        try { rushFlag = init.isRushDuel ? 1 : 0; } catch { }
                        DebugLogger.Log(LogCategory.Game, "DuelState",
                            $"  EngineInitializer: myPlayerNum={myNum} isRushDuel={rushFlag}");

                        for (int p = 0; p < 2; p++)
                        {
                            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<
                                Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>> deck = null;
                            try
                            {
                                deck = p switch
                                {
                                    0 => init.deck0,
                                    1 => init.deck1,
                                    _ => null
                                };
                            }
                            catch { }
                            if (deck == null)
                            {
                                DebugLogger.Log(LogCategory.Game, "DuelState",
                                    $"  deck{p}: null");
                                continue;
                            }
                            int outerLen = deck.Length;
                            DebugLogger.Log(LogCategory.Game, "DuelState",
                                $"  deck{p}.Length={outerLen}");
                            for (int s = 0; s < outerLen && s < 5; s++)
                            {
                                var inner = deck[s];
                                if (inner == null)
                                {
                                    DebugLogger.Log(LogCategory.Game, "DuelState",
                                        $"    deck{p}[{s}]: null");
                                    continue;
                                }
                                int innerLen = inner.Length;
                                // Dump up to 12 ints from each inner array
                                var sb = new System.Text.StringBuilder();
                                int dumpN = Math.Min(innerLen, 12);
                                for (int i = 0; i < dumpN; i++)
                                {
                                    if (i > 0) sb.Append(',');
                                    sb.Append(inner[i]);
                                }
                                if (innerLen > dumpN) sb.Append(",...");
                                DebugLogger.Log(LogCategory.Game, "DuelState",
                                    $"    deck{p}[{s}]: Length={innerLen} [{sb}]");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    DebugLogger.Log(LogCategory.Game, "DuelState",
                        $"  EngineInitializer dump threw: {ex.Message}");
                }

                DebugLogger.Log(LogCategory.Game, "DuelState", "=== End DumpStackZoneState ===");
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelState",
                    $"DumpStackZoneState threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Returns the per-team DeckCardPlace for the given stack-zone locate
        /// (extra deck, deck, grave, banished). DuelFieldBase exposes a 2-
        /// element halfFields array — one HalfField per player — and each
        /// HalfField holds its side's extraPlace/deckPlace/gravePlace/
        /// excludePlace. Index 0 corresponds to server seat 0 and index 1 to
        /// server seat 1, matching CardPlace.team.
        /// </summary>
        private static Il2CppYgomGame.Duel.DeckCardPlace GetStackPlace(int player, int locate)
        {
            if (player != 0 && player != 1) return null;
            try
            {
                var field = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.duelField;
                var halves = field?.halfFields;
                if (halves == null || player >= halves.Count) return null;
                var half = halves[player];
                if (half == null) return null;
                switch (locate)
                {
                    case LocateExtra: return half.extraPlace;
                    case LocateDeck:  return half.deckPlace;
                    case LocateGrave: return half.gravePlace;
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Counts monsters / spells on the given player's field. Folds BOTH
        /// Extra Monster Zone slots (loc=5 left, loc=6 right) into the monster
        /// count so Synchro/Xyz/Link/Fusion summons appear in the F-key summary.
        /// </summary>
        public static void CountFieldCards(int player, out int monsters, out int spells)
        {
            monsters = 0;
            spells = 0;
            foreach (int loc in MonsterLocates)
                if (GetFieldCard(player, loc, 0) != null) monsters++;
            foreach (int emzLoc in EMZLocates)
                if (GetFieldCard(player, emzLoc, 0) != null) monsters++;
            foreach (int loc in SpellLocates)
                if (GetFieldCard(player, loc, 0) != null) spells++;
        }

        #endregion

        #region Commands (engine direct — these still work in PvP)

        public static uint GetCommandMask(int player, int locate, int slot)
        {
            try { return Il2CppYgomGame.Duel.Engine.DLL_DuelComGetCommandMask(player, locate, slot); }
            catch { return 0; }
        }

        public static int GetAttackTargetMask(int player, int locate)
        {
            try { return Il2CppYgomGame.Duel.Engine.DLL_DuelGetAttackTargetMask(player, locate); }
            catch { return 0; }
        }

        #endregion

        #region Event hook (called from DuelEventAnnouncer.OnRunEffect)

        /// <summary>
        /// Updates DuelState's tracked counters from the RunEffect event
        /// stream. Called at the top of DuelEventAnnouncer.OnRunEffect so
        /// every consumer that reads DuelState during the same event cycle
        /// sees the fresh values.
        /// </summary>
        public static void OnRunEffect(int id, int p1, int p2, int p3)
        {
            var viewType = (Il2CppYgomGame.Duel.Engine.ViewType)id;
            switch (viewType)
            {
                case Il2CppYgomGame.Duel.Engine.ViewType.DuelStart:
                    _inDuel = true;
                    _duelEnded = false;
                    _localSeat = -1; // matchmaking may have assigned a new seat
                    _currentPhase = Il2CppYgomGame.Duel.Engine.Phase.Null;
                    _currentTurnPlayer = -1;
                    _turnNumber = 0;
                    // Don't reset _lp — LifeSet fires BEFORE DuelStart with
                    // the starting values, and we want to keep them.
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.DuelEnd:
                    _duelEnded = true;
                    _inDuel = false;
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.PhaseChange:
                    _currentPhase = (Il2CppYgomGame.Duel.Engine.Phase)p2;
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.TurnChange:
                    if (p1 == 0 || p1 == 1) _currentTurnPlayer = p1;
                    _turnNumber++;
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.LifeSet:
                    if (p1 == 0 || p1 == 1) _lp[p1] = p2;
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.LifeDamage:
                    // LifeDamage fires twice per damage event (p3 bit 0 set
                    // on first fire only). p2 is the delta (negative for
                    // damage, positive for recovery).
                    if ((p3 & 1) == 1 && (p1 == 0 || p1 == 1) && _lp[p1] >= 0)
                    {
                        _lp[p1] = Math.Max(0, _lp[p1] + p2);
                    }
                    break;

                case Il2CppYgomGame.Duel.Engine.ViewType.WaitInput:
                    // Resumed duels never fire DuelStart, so set InDuel here too
                    if (!_inDuel) _inDuel = true;
                    break;
            }
        }

        /// <summary>Reset to a clean pre-duel state.</summary>
        public static void Reset()
        {
            _inDuel = false;
            _duelEnded = false;
            _localSeat = -1;
            _currentPhase = Il2CppYgomGame.Duel.Engine.Phase.Null;
            _currentTurnPlayer = -1;
            _turnNumber = 0;
            _lp[0] = -1;
            _lp[1] = -1;
        }

        #endregion

        #region Private helpers

        private static Il2CppYgomGame.Duel.HandCards GetHandCardsFor(int player)
        {
            try
            {
                var hcm = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.handCardManager;
                if (hcm == null) return null;
                // "near" is always the local viewer's hand, "far" is opponent's.
                return player == MyPlayerNum() ? hcm.nearHandCard : hcm.farHandCard;
            }
            catch { return null; }
        }

        private static Il2CppYgomGame.Duel.CardRoot FindFieldCardRoot(
            int player, int locate, int slot)
        {
            try
            {
                var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                if (roots == null) return null;

                for (int i = 0; i < roots.Count; i++)
                {
                    var root = roots[i];
                    if (root == null) continue;
                    var locator = root.toLocator;
                    var place = locator?.cardPlace;
                    if (place == null) continue;

                    if (place.team == player
                        && place.position == locate
                        && locator.index == slot)
                    {
                        return root;
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// True for multi-card zones (extra deck, grave, deck) where the
        /// visual layer's locator.index assignment may not match our
        /// 0-based slot enumeration — engine API is the authority.
        /// </summary>
        private static bool IsStackZone(int locate)
        {
            return locate == LocateExtra
                || locate == LocateGrave
                || locate == LocateDeck;
        }

        /// <summary>
        /// Finds the CardRoot whose uniqueId matches, regardless of its
        /// locator.index or position. Used for stack zones where engine
        /// gives us a uid but FindFieldCardRoot's index match misses.
        /// </summary>
        private static Il2CppYgomGame.Duel.CardRoot FindCardRootByUid(int uid)
        {
            if (uid <= 0) return null;
            try
            {
                var gom = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager;
                return gom?.FindCardInstance(uid);
            }
            catch { return null; }
        }

        /// <summary>
        /// PvP-safe stack-zone slot lookup. Walks cardRoots, collects every
        /// entry whose (team, position) matches, orders by locator.index for
        /// a stable mapping, returns the slot-th match. Used as the fallback
        /// when DLL_DuelGetCardUniqueID is hollow (PvP). Without this, Extra
        /// Deck cards whose locator.index is non-zero (notably when XYZ
        /// monsters are present) read as Empty even though the count is
        /// non-zero.
        ///
        /// Diagnostic: logs the per-call match count and locator.index list
        /// so a debug-mode log session can prove (or disprove) whether the
        /// CardRoots actually exist in PvP. If matchCount=0 the visual layer
        /// doesn't store extra-deck cards in cardRoots at all, and we need a
        /// different strategy (probably reading from a per-player extra-deck
        /// manager). If matchCount > 0 and the user still hears "Empty"
        /// then something downstream of MakeSnapshot is rejecting the
        /// result.
        /// </summary>
        private static Il2CppYgomGame.Duel.CardRoot FindNthStackCardRoot(
            int player, int locate, int slot)
        {
            if (slot < 0) return null;
            try
            {
                var roots = Il2CppYgomGame.Duel.DuelClient.instance?.worker3d?.goManager?.cardRoots;
                if (roots == null)
                {
                    DebugLogger.Log(LogCategory.Game, "DuelState",
                        $"FindNthStackCardRoot(p={player}, l={locate}, slot={slot}): cardRoots is null");
                    return null;
                }

                // Collect matching locator.index values for diagnostic logging.
                // Also remember the first matching CardRoot's cardId AND
                // cardPlane.cardModel.cardId — the latter is the privacy-
                // gated fallback used by MakeSnapshot; if BOTH are 0 then the
                // visual layer simply doesn't carry the cardId at this point
                // and we need a different data source.
                var indices = new System.Collections.Generic.List<int>();
                int firstCardId = -1, firstModelId = -1, firstUid = -1;
                for (int i = 0; i < roots.Count; i++)
                {
                    var root = roots[i];
                    var locator = root?.toLocator;
                    var place = locator?.cardPlace;
                    if (place != null && place.team == player && place.position == locate)
                    {
                        indices.Add(locator.index);
                        if (firstCardId < 0)
                        {
                            try { firstCardId = root.cardId; } catch { }
                            try { firstUid = root.uniqueId; } catch { }
                            try
                            {
                                var model = root.cardPlane?.cardModel;
                                firstModelId = model?.cardId ?? -1;
                            }
                            catch { }
                        }
                    }
                }

                DebugLogger.Log(LogCategory.Game, "DuelState",
                    $"FindNthStackCardRoot(p={player}, l={locate}, slot={slot}): " +
                    $"matches={indices.Count} indices=[{string.Join(",", indices)}] " +
                    $"firstCardId={firstCardId} firstModelId={firstModelId} firstUid={firstUid}");

                if (slot >= indices.Count) return null;

                // Pick the Nth match ordered by locator.index. Iterate
                // matchCount times, each pass selecting the smallest index
                // greater than what we've already chosen. O(n*k) where k is
                // matchCount, fine for tiny n.
                int chosenIndex = int.MinValue;
                Il2CppYgomGame.Duel.CardRoot chosenRoot = null;
                for (int pick = 0; pick <= slot; pick++)
                {
                    int bestIdx = int.MaxValue;
                    Il2CppYgomGame.Duel.CardRoot bestRoot = null;
                    for (int i = 0; i < roots.Count; i++)
                    {
                        var root = roots[i];
                        var locator = root?.toLocator;
                        var place = locator?.cardPlace;
                        if (place == null) continue;
                        if (place.team != player || place.position != locate) continue;

                        int idx = locator.index;
                        if (idx <= chosenIndex) continue; // already picked
                        if (idx < bestIdx)
                        {
                            bestIdx = idx;
                            bestRoot = root;
                        }
                    }
                    if (bestRoot == null) return null;
                    chosenIndex = bestIdx;
                    chosenRoot = bestRoot;
                }
                return chosenRoot;
            }
            catch (Exception ex)
            {
                DebugLogger.Log(LogCategory.Game, "DuelState",
                    $"FindNthStackCardRoot threw: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Builds a CardSnapshot from the Content database for a uniqueId
        /// when no CardRoot exists in the visual layer (extra deck cards
        /// the engine knows about but hasn't materialized as 3D objects).
        /// Returns null if the cardId can't be resolved.
        /// </summary>
        private static CardSnapshot? MakeContentSnapshot(int uid)
        {
            int cardId = 0;
            try
            {
                uint resolved = Il2CppYgomGame.Duel.Engine
                    .DLL_DuelGetCardIDByUniqueID2(uid);
                if (resolved > 0 && resolved < 100000) cardId = (int)resolved;
            }
            catch { }
            return BuildContentSnapshot(cardId, uid);
        }

        /// <summary>
        /// Builds a CardSnapshot for a card identified only by its Content
        /// database id (cardDbId / mrk). Used when reading from the
        /// EngineInitializer's registered deck arrays, where each entry IS
        /// the cardDbId directly — no uniqueId, no CardRoot, no engine
        /// resolver needed. Returns null if cardId is out of range.
        /// </summary>
        private static CardSnapshot? BuildContentSnapshot(int cardId, int uid = 0)
        {
            if (cardId <= 0 || cardId >= 100000) return null;

            int atk = 0, def = 0, level = 0;
            try
            {
                var content = Il2CppYgomGame.Card.Content.Instance;
                if (content != null)
                {
                    atk = content.GetAtk(cardId);
                    def = content.GetDef2(cardId);
                    level = content.GetLevel(cardId);
                }
            }
            catch { }

            return new CardSnapshot(uid, cardId, ResolveCardName(cardId),
                atk, def, level, IsAttack: false, IsFaceUp: true);
        }

        /// <summary>
        /// Returns the inner Il2CppStructArray for the given player's
        /// (main / extra / side) deck from the EngineInitializer's cached
        /// registered-deck data. Outer index convention: 0=main, 1=extra,
        /// 2=side. Returns null when the engineInitializer isn't available,
        /// when the player slot is out of range (we only handle 0/1),
        /// when the deckType is out of range, or when the requested player
        /// isn't the local seat (opponent's deck is server-withheld and the
        /// array is empty client-side anyway — explicit gate keeps us safe
        /// if that ever changes).
        /// </summary>
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int>
            GetRegisteredDeckArray(int player, int deckType)
        {
            if (player != 0 && player != 1) return null;
            if (deckType < 0 || deckType > 2) return null;
            try
            {
                var init = Il2CppYgomGame.Duel.DuelClient.GetEngineInitializer();
                if (init == null) return null;
                // Privacy gate — only ever serve the local player's deck.
                if (player != init.myPlayerNum) return null;
                var deck = player switch
                {
                    0 => init.deck0,
                    1 => init.deck1,
                    _ => null
                };
                if (deck == null || deck.Length <= deckType) return null;
                return deck[deckType];
            }
            catch { return null; }
        }

        /// <summary>
        /// Snapshot for a card identified only by its position in the
        /// registered-deck array. Locate maps to deck type: Extra (14) →
        /// outer[1], Deck (15) → outer[0]. Used for stack zones in PvP
        /// where the visual layer doesn't materialize all cards and the
        /// engine's DLL_Duel queries are hollow.
        /// </summary>
        private static CardSnapshot? GetRegisteredDeckCard(int player, int locate, int slot)
        {
            if (slot < 0) return null;
            int deckType = locate switch
            {
                LocateExtra => 1,
                LocateDeck => 0,
                _ => -1
            };
            if (deckType < 0) return null;
            var inner = GetRegisteredDeckArray(player, deckType);
            if (inner == null || slot >= inner.Length) return null;
            int cardId = inner[slot];
            return BuildContentSnapshot(cardId);
        }

        /// <summary>
        /// Count of cards in the registered deck array for the given stack
        /// zone. Returns 0 for opponents / non-stack locates / when the
        /// engine cache isn't ready. Used by GetCardCount as the primary
        /// source for the local player's extra deck and main deck in PvP.
        /// </summary>
        private static int GetRegisteredDeckCount(int player, int locate)
        {
            int deckType = locate switch
            {
                LocateExtra => 1,
                LocateDeck => 0,
                _ => -1
            };
            if (deckType < 0) return 0;
            var inner = GetRegisteredDeckArray(player, deckType);
            return inner?.Length ?? 0;
        }

        private static CardSnapshot MakeSnapshot(Il2CppYgomGame.Duel.CardRoot root,
            bool isFaceUp)
        {
            int cardId = 0, uniqueId = 0, atk = 0, def = 0, level = 0;
            bool isAttack = false;
            try { cardId = root.cardId; } catch { }
            try { uniqueId = root.uniqueId; } catch { }
            try { atk = root.atk; } catch { }
            try { def = root.def; } catch { }
            try { level = root.level; } catch { }
            try { isAttack = root.isAttack; } catch { }

            // Fall back to CardModel.cardId when the root reports 0. The visual
            // layer renders face-down stack cards (extra deck, deck) with
            // root.cardId=0 — but the underlying CardPlane/CardModel had to
            // load the actual card texture and still carries the real id.
            // This recovers names for extra-deck CardRoots in PvP where the
            // engine uid path is hollow.
            //
            // Privacy gate: only apply when the card is face-up (public info)
            // OR owned by the local player (you may see your own set cards'
            // identities). Without this gate the fallback would leak the
            // opponent's set monsters / face-down spells if the client
            // happens to know them (e.g., AI duels where the engine has full
            // visibility).
            if (cardId == 0)
            {
                bool localOwned = false;
                try
                {
                    var team = root.toLocator?.cardPlace?.team ?? -1;
                    localOwned = team >= 0 && team == MyPlayerNum();
                }
                catch { }

                if (isFaceUp || localOwned)
                {
                    try
                    {
                        var model = root.cardPlane?.cardModel;
                        if (model != null) cardId = model.cardId;
                    }
                    catch { }
                }
            }

            return new CardSnapshot(uniqueId, cardId, ResolveCardName(cardId),
                atk, def, level, isAttack, isFaceUp);
        }

        /// <summary>
        /// True when the engine's card-state queries are empty even though
        /// the visual layer has data — the signal that this is a PvP duel
        /// (server runs the logic, local engine is a renderer). Compares
        /// engine hand count vs nearHandCard.m_InfoList.Count.
        /// </summary>
        public static bool IsEngineHollow
        {
            get
            {
                try
                {
                    int my = MyPlayerNum();
                    int engineHand = 0;
                    try { engineHand = Il2CppYgomGame.Duel.Engine
                        .DLL_DuelGetCardNum(my, LocateHand); }
                    catch { }
                    if (engineHand > 0) return false;

                    var hand = GetHandCardsFor(my);
                    int visualHand = hand?.m_InfoList?.Count ?? 0;
                    return visualHand > 0;
                }
                catch { return false; }
            }
        }

        private static string ResolveCardName(int cardDbId)
        {
            if (cardDbId <= 0 || cardDbId > 100000) return null;
            try
            {
                var content = Il2CppYgomGame.Card.Content.Instance;
                if (content == null) return null;
                string name = content.GetName(cardDbId);
                return string.IsNullOrEmpty(name) ? null : name;
            }
            catch { return null; }
        }

        #endregion
    }
}
