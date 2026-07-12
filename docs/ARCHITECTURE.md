# Architecture

## Runtime flow

`Main` owns the MelonLoader lifecycle, global hotkeys, and handler priority. Each frame follows this order:

1. Update input consumption.
2. Process global hotkeys.
3. Confirm game readiness and required Harmony patches.
4. Poll the current game screen.
5. Update duel events and handlers in priority order.

Dialog and specialized handlers return before lower-priority handlers can process the same input. `InputManager` also records consumed keys to prevent duplicate actions during one frame.

## Handler adapters

Handlers translate Unity and IL2CPP objects into keyboard actions and speech:

- `DuelHandler` and `DuelFieldNavigator` handle duel navigation and commands.
- `DialogHandler` handles dialogs, toggles, and selection prompts.
- `HomeHandler` exposes a curated Duel World list and a browse-all fallback.
- Shop, trader, ticket, catalog, and deck handlers cover their own screens.
- `ScreenButtonHandler` is the generic fallback for screens without a specialized handler.

These files are adapters to unstable game internals. Reflection, object discovery, and Harmony calls belong there.

## State and policy seams

`DuelState` is the read-only state adapter for duel events and visual card state. It uses registered deck data during initialization and live visual Extra Deck data after that stack loads.

Pure policy and tracker classes hold behavior that can run without the game. The test project source-links those files. New fixes should prefer this pattern when it represents the real call-site behavior.

Do not extract a one-line wrapper only to raise test coverage. A useful policy seam owns a decision, invariant, retry rule, formatting rule, or state transition. Unity adapters should remain thin enough that those decisions are visible.

## Large-file cleanup

Some handlers are large because they support many unrelated game screens. Refactor them incrementally:

1. Lock current behavior with characterization tests at a real seam.
2. Extract one screen flow or state policy at a time.
3. Keep runtime behavior and structural changes in separate commits.
4. Rebuild against current game assemblies after each extraction.

Avoid a wholesale rewrite. Duel Links internals are version-sensitive, and much of the existing reflection code was learned from runtime logs.
