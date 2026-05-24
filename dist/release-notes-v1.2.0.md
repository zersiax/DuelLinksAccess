Minor release. New curated Home / Duel World screen, PvP / Ranked duel support, Card Trader conversion catalog, and assorted duel fixes.

## New

- **Curated Home / Duel World screen.** The world map no longer pours forty-plus raw UI items at you. A new handler maintains an explicit list of named destinations — area selector, map NPCs filtered to the area you're in (Billboards, Gate, Shop, School, Duel Center), footer destinations (Gate, Colosseum, Shop, Labo), header buttons (Events with badge count, Gifts, Info, Player Info, Options, Neuron Code), character/deck buttons, missions (with the live stage label like "Stage 7"), shortcuts, menu/settings, duel-world series switcher, and quick-duel shortcuts. Up/Down navigate, Left/Right cycle areas (or characters on the character panel), Enter activates, G announces gem + crystal balance. **B toggles to "browse all" mode** if a button isn't in the curated list — hands the screen back to the generic scanner so nothing is unreachable. Suspends automatically when a tutorial arrow is on screen so the orphan-arrow handler can take over.

- **PvP / Ranked duel support.** The previous duel code talked directly to the engine; in PvP the engine is a renderer — server runs the duel logic and most `DLL_Duel*` queries return 0 / empty / wrong. A new `DuelState` adapter reads from the visual layer (cardRoots, HandCardManager) plus event-tracked counters, working identically in single-player and PvP. Specific fixes:
  - **Seat detection.** `DLL_DuelGetMyPlayerNum` always returned 0 in PvP; now resolves to the actual server seat via the local viewer's hand anchor.
  - **Hand / field card reads** route through the visual layer when the engine is hollow.
  - **Phase advancement (P key)** now sends the request to the server unconditionally instead of relying on a local-only button click that PvP ignored.
  - **Extra deck card identification in PvP.** The engine only renders the top card; all the others read as "Empty" or "Unknown" via every obvious lookup path. The mod now reads from `EngineInitializer.deck{seat}` (the registered-deck cache populated at duel start, including skill-registered cards mid-duel). Opponent's array is server-withheld — privacy is enforced engine-side.
  - **Hand play and field actions in PvP** read the live `CardCommand` popup since the engine's command mask is 0 in PvP. Attack short-circuit when `DLL_DuelGetAttackTargetMask` reports legal targets.
  - **Direct-attack / EMZ slot count fixes.** Both used hollow APIs that always returned 0 in PvP, causing misannouncements ("direct attack" when opp had monsters; "0 cards" in EMZ during a fusion summon). Both now route through the visual layer.
  - **Phase change announcements** are now driven by the event payload instead of polling the hollow current-phase API, with 300 ms debounce to collapse the rapid Draw → Standby → Main burst at turn start into one announcement.

- **Extra Deck SummonSp + EMZ navigation.** Fusion / Synchro / Xyz / Link summoning now works in both single-player and PvP. Tapping a card in the extra deck dispatches `SummonSp` directly (the popup never opens for extra deck, same family as TurnAtk / TurnDef / Reverse). Material selection goes through the existing EmotionalList handler. The Extra Monster Zone is now a navigable grid row between your monsters and your opponent's — reachable via Up arrow from your monster row, or Alpha-4 / Alpha-5 hotkeys. Per-slot ownership resolved at lookup; opponent-owned EMZ Synchro / Xyz / Fusion / Link monsters can now be attacked from your main row.

- **Card Catalog (conversion catalog).** New handler for the conversion screen opened from the Card Trader when you have eligible cards. Left/Right browse, V for description, B for batch conversion, Enter to confirm an individual conversion once the gold button activates.

- **Card Trader screen (Patreon track, included in this release).** Type-aware item naming (Card / ChangeCard / Item / BoxChip / Skill / SkillTicket / Pack / RespectOrb / Rare-Super-Ultra rarity lists / Chroniclizer / Process / ChangeSkill / SoldOut), markup stripping for both Unity rich text and game `[Dragon/Fusion/Effect]` tags, affordability check ("can trade" / "cannot trade"), verbose cost breakdown showing what you have versus need, gold balance with G, batch catalog with B, two-press confirmation flow with drift warning if the in-game scroll-snap moved the selection during your press. Trade execution is reliable for items at the end of the list and degraded for middle-of-list items due to the scroll-snap drift — confirmation dialog always opens so you can read it and back out if it switched on you.

## Fixes

- **Turn-1 first-player phase lockup.** First press of P from Main Phase 1 of turn 1 sometimes did nothing because the engine reported the Battle Phase as legal but the server refused it. Retry escalation: if a previous P-press asked Battle from the same phase and ten seconds elapsed without progress, the next press goes straight to End Phase. Normal turns advance on the first press as before.
- **EmotionalList tribute material no longer misidentified as "Pierce!" or other unrelated cards.** `DLL_DuelGetCardIDByUniqueID2(mixedId)` returns a real cardId for any low integer (it indexes into a deck table), so `mixedId = 7` resolved to whichever cardId happened to live at slot 7 of the deck. Replaced with a CardRoot walk that prefers `root.cardId` and only falls back to the cardModel for face-down rendering, mirroring the privacy-gated MakeSnapshot path. Also tightened the direct-cardDbId fallback from `> 0` to `>= 1000`.
- **Sequential XYZ material pickers no longer drop the second prompt.** The `_emoListHandled` flag could trap the second picker because the first one's `goActive=false` reset transition was never observed before the next prompt opened. Now time-based: the flag clears after a brief grace period post-confirm.
- **"Your move" no longer fires mid-summon between material picks.** When the engine fires `WaitInput(Selection)` while you're picking the second XYZ material, the suppression now checks `worker.curInputType` and lets the next EmotionalList prompt win the announcement.
- **MakeSnapshot privacy gate.** The cardPlane / cardModel fallback for face-down rendering now requires the card to be face-up or owned by you. Prevents leaking opponent face-down card identities if the visual layer ever happens to know them (tutorial / AI duels where the engine has full visibility).
- **Fusion-summoned monsters in stack zones / EMZ are no longer missed by field reads.** The CardRoot's `locator.index` for fusion-summoned monsters carries non-zero overlay-material bookkeeping; the strict `index == slot` match was missing them. Non-stack field zones now fall through to a position-only walk if the strict match misses.

## Misc

- **Deck editor verbose card reading hotkey changed from C to V** to match the in-duel `V` hotkey. The verbose re-read no longer prepends "X of Y" — you've already navigated to the card, so the position prefix is redundant.
- **DialogHandler no longer fires `onClick.Invoke` on disabled buttons** (e.g. the NEXT button on a screen with unconfigured settings) — the prior behavior bypassed the game's intentional interactivity gate.

## Known issues (non-blocking)

- Card Trader trade execution drifts for middle-of-list items (last items in the list are stable). The confirmation dialog opens so you can read and back out.
- Opening certain decks (Structure Decks before they're built) still reads as "0 in main deck" because the editor opens in a non-default MODE not yet handled.
- Multi-effect spell-card selectors that fire `RunDialog (55) p1=6 p2=259 p3=0` and leave `worker2d.curInputType=Null` (e.g. Dragon's Gunfire) are not yet detected — user has to mouse-click.

## Install

1. Install MelonLoader v0.7.3 Open-Beta against `dlpc.exe` (Yu-Gi-Oh! Duel Links on Steam). Launch once so MelonLoader generates its assemblies, then close it.
2. Extract the zip directly into your Duel Links install folder. `DuelLinksAccess.dll` lands in `Mods\`, `Tolk.dll` and `nvdaControllerClient64.dll` land in the game root next to `dlpc.exe`. Replaces any earlier DLL if present.
3. Launch. You should hear "Duel Links Access loaded. F1 for help."

If your screen reader is something other than NVDA, swap `nvdaControllerClient64.dll` for the controller client matching your reader.

See `README.md` (bundled in the zip) for the full key bindings and the Home / Duel World screen guide.
