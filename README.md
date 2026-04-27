# Duel Links Access

An accessibility mod for Yu-Gi-Oh! Duel Links (Steam, PC) that lets blind and visually impaired players play the game using a screen reader and keyboard. The mod speaks menus, dialogs, card information, and duel events through Tolk (NVDA, JAWS, SAPI, etc.), and adds full keyboard navigation for a game that is otherwise mouse/touch only.

## Status

Alpha. Core gameplay is playable end to end: tutorials, solo duels, deck editing, shop, ticket exchange, missions, and Duel Trials all work with a screen reader. Some screens still need polish, and rare edge cases may require a sighted assist. Bug reports and feedback welcome.

## Requirements

- Yu-Gi-Oh! Duel Links on Steam (Windows, 64-bit)
- A screen reader (NVDA recommended; JAWS, Narrator, and SAPI also work via Tolk)
- [MelonLoader](https://melonwiki.xyz/) **v0.7.3 Open-Beta or newer**, IL2CPP / net6

## Installation

1. **Install MelonLoader** into your Duel Links install folder (default: `C:\Program Files (x86)\Steam\steamapps\common\Yu-Gi-Oh! Duel Links`). Run the MelonLoader installer and point it at `dlpc.exe`. Launch the game once so MelonLoader generates its assemblies.
2. **Download the latest release** `DuelLinksAccess.zip` from the [Releases page](../../releases).
3. **Extract the zip** into your Duel Links install folder. This places:
   - `Mods\DuelLinksAccess.dll`
   - `Tolk.dll` and `nvdaControllerClient64.dll` in the game root (required for screen reader output)
4. **Launch the game.** You should hear "Duel Links Access loaded. F1 for help."

If you don't hear anything, make sure your screen reader is running before you start the game, and confirm MelonLoader shows `DuelLinksAccess` in its console.

## Using the mod

Press **F1** at any time to hear the full key list. A quick summary:

### General
- **Up / Down** — navigate items in menus and dialogs
- **Enter** — activate the current item
- **Escape / Backspace** — go back
- **Space** — re-scan the current screen
- **Tab** — re-read the current item
- **Ctrl+R** — repeat the last announcement
- **F1** — help
- **F11** — activate the current tutorial arrow target (fallback when Enter on the highlighted item doesn't advance the tutorial — e.g. the Shop button on Home during the boot tutorial)
- **F12** — toggle debug mode
- **Ctrl+F11** — open mod settings

### In a duel
- **Up / Down** — move between field rows
- **Left / Right** — move between columns
- **Zone hotkeys:** `C` hand, `M` monsters, `S` spells, `T` field spell, `G` graveyard, `B` banished, `D` extra deck
- **Hold Shift** with any zone hotkey to target the opponent's side
- **1 / 2 / 3** — jump to monster slots; **4** — Extra Monster Zone
- **Enter** — open actions for the current card (summon, attack, activate, etc.)
- **V** — re-read the current card with full details
- **L** — read life points
- **F** — field summary (card counts in each zone)
- **P** — advance to the next phase
- **I** — status (turn, phase, LP)
- **J** — browse the event log (Up/Down scroll, Escape close)

### Deck editor
- **Tab** — switch between Main Deck / Extra Deck / Collection
- **Left / Right** — browse cards one at a time
- **Up / Down** — page through cards (10 at a time)
- **Enter** — add a card (in Collection) or remove it (in a deck)
- **C** — read card details (stats, description, deck counts)
- **I** — deck stats
- **S** — read current skill; **K** — change skill
- **Ctrl+S** — save

### Shop
- **Tab / Shift+Tab** — switch categories
- **Left / Right** — browse items; **Up / Down** — page (5 items)
- **Enter** — purchase
- **G** — gem balance
- **C / I** — item details

### Ticket exchange
- **Left / Right** — browse cards
- **Enter** — select card
- **Space** — confirm exchange
- **G** — ticket count

## What's supported

- Menu and dialog navigation across the entire game UI
- Tutorial flow end to end (including scenario cutscenes and tutorial pointers)
- Duel announcements: turn/phase changes, summons with card names, attacks, damage, destruction, draws
- Card reading with name, type, attribute, level, ATK/DEF, position, and description
- Deck editor with full card database info
- Shop browsing and purchases
- Missions (including tabs, category headers, and progress text)
- Duel Trials navigation
- Card ticket exchange
- Post-duel result screens

Some screens (reward animations, certain image-only banners) are still limited by the game's use of sprite-based text. Work is ongoing.

## Building from source

Requires .NET 6+ SDK and a local Duel Links + MelonLoader install (the `.csproj` hardcodes reference paths to the default Steam location — edit if yours differs).

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-Mod.ps1
```

The build output is copied to your `Mods\` folder automatically. Use `scripts\Deploy-Mod.ps1` to package a release zip.

## License

MIT. See [LICENSE](LICENSE).

## Credits

Built on [MelonLoader](https://melonwiki.xyz/), [Harmony](https://github.com/pardeike/Harmony), and [Tolk](https://github.com/dkager/tolk) for screen reader output.
