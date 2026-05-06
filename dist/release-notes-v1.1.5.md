Hotfix release. Fixes tutorial arrow announcement problems introduced during the GX series-unlock flow.

## Fixes

- **Tutorial arrow announcement no longer fires before the home-screen speech finishes.** When a UISelectablePointer arrow (e.g. the Stage/Series button tutorial) appeared while the home screen was still scanning, the old sbhIdle gate fired a generic announcement immediately — cutting it off half a second later with "Home screen". UISelectablePointer arrows now always use the delayed (1.5 s) path so the home-screen scan settles first.

- **Announcement now says the button's live name, not its static container label.** After selecting a series (e.g. GX), the Stage button's live label updates to "MAX". The old code read from the parent ipclick GO (`SeriesButton`) which always returned the static text "ステージ". Now `ScreenButtonHandler.GetLabelForDescendant` finds the actual navigable child item (SeriesLogo) already in the SBH list and returns its current label, so you hear "Tutorial: navigate to MAX and press Enter" instead of "ステージ".

- **You are told to press Escape when the tutorial target is on a different screen.** After a series change the game lands on the character-select sub-screen (Home VC), not the main Single screen where the Stage button lives. SBH is active but has no item matching the arrow's target, so the mod now announces "Tutorial: press Escape to go back, then navigate to X" instead of silently pointing at an unreachable button.

- **Double "Dialog" announcement on TutorialArrowPart suppressed.** Previously both `GameStateTracker` and `DialogHandler` announced "Dialog" when a TutorialArrowPart overlay appeared — the user heard it twice. Both paths now suppress the announcement for that overlay; the real content underneath announces itself.

- **TutorialArrowPart overlays above real dialogs are now dismissed automatically.** DialogHandler detects when the dialog manager has a real VC beneath the arrow overlay and calls `OnPointerClick` to clear it, making the underlying dialog immediately accessible.

- **Double-announcement on Enter key press eliminated.** `ScreenButtonHandler.ActivateViaTutorialArrow` previously called `ScreenReader.Say` for UISelectablePointer arrows and then let normal activation also announce — producing duplicate speech on every keypress. It now returns early for that shape, leaving the announcement to the one-shot delayed path.

## Known issues (non-blocking)

- The Extra Monster Zone (loc=6) is not a navigable column in the monster row. EMZ activity is visible via F-key field summary and summon announcements.
- Extra Deck XYZ / Synchro / Link / Fusion summons fail with "Action failed" — the engine doesn't open the CardCommand popup for these. Workaround pending.
- Opening certain decks (e.g. Structure Decks before they're built) reads as "0 in main deck" because the editor opens in a non-default MODE not yet handled.

## Install

1. Install MelonLoader v0.7.3 Open-Beta against `dlpc.exe` (Yu-Gi-Oh! Duel Links on Steam). Launch once so MelonLoader generates its assemblies, then close it.
2. Extract the zip directly into your Duel Links install folder. `DuelLinksAccess.dll` lands in `Mods\`, `Tolk.dll` and `nvdaControllerClient64.dll` land in the game root next to `dlpc.exe`. Replaces any earlier DLL if present.
3. Launch. You should hear "Duel Links Access loaded. F1 for help."

If your screen reader is something other than NVDA, swap `nvdaControllerClient64.dll` for the controller client matching your reader.
