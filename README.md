# Duel Links Access

Duel Links Access is an accessibility mod for the Steam version of Yu-Gi-Oh! Duel Links. It adds keyboard navigation and screen reader output to many common game flows.

The mod is alpha software. Solo duels, much of the tutorial, deck editing, common shops, ticket exchange, missions, Duel Trials, and parts of Duel World are accessible. Some screens still expose unlabeled controls or require the generic browse-all fallback. Image-only selectors and new game content may need more work after a Duel Links update.

## Requirements

- 64-bit Windows and Yu-Gi-Oh! Duel Links from Steam
- [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.3 or later, configured for IL2CPP and net6
- [NVDA](https://www.nvaccess.org/) running before the game starts
- Tolk from the verified upstream archive listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)

NVDA is the packaged and tested screen reader path. Tolk supports other screen readers, but this project does not currently claim tested support for them. Narrator and SAPI output are not enabled by the mod.

## Installation

1. Install MelonLoader for `dlpc.exe` in the Duel Links folder. Launch the game once so MelonLoader generates its assemblies.
2. Download Tolk from the URL in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and verify the ZIP SHA-256.
3. Put `Tolk.dll` and `nvdaControllerClient64.dll` beside `dlpc.exe`.
4. Download the current `DuelLinksAccess-vX.Y.Z.zip` from [GitHub Releases](https://github.com/zersiax/DuelLinksAccess/releases).
5. Extract `Mods/DuelLinksAccess.dll` into the game's `Mods` folder.
6. Start NVDA, then start Duel Links. The mod should announce that it loaded.

The release archive does not redistribute Tolk, MelonLoader, screen reader client libraries, or game files.

## Controls

Press `F1` in the game to hear the complete current key list.

### General

- `Up` / `Down`: move through items
- `Enter` or numpad `Enter`: activate
- `Escape` or `Backspace`: go back
- `Space`: rescan the current screen
- `Tab`: repeat the current item
- `Ctrl+R`: repeat the last announcement
- `F11`: activate an unreachable tutorial pointer target
- `F12`: toggle debug logging

### Duel

- `Up` / `Down`: change field row
- `Left` / `Right`: change slot
- `C`, `M`, `S`, `T`, `G`, `B`, `D`: hand, monsters, spells, field spell, graveyard, banished cards, Extra Deck
- Hold `Shift` with a zone key for the opponent's zone
- `1`, `2`, `3`: main monster slots
- `4`, `5`: left and right Extra Monster Zones
- `Enter`: open actions for the current card
- `V`: read full card details
- `L`: read life points
- `F`: summarize field counts
- `P`: advance phase
- `I`: read duel status
- `J`: browse the event log
- `Space` during Draw Phase: retry automatic draw if it was delayed

Automatic draw sends the game's normal draw command. Draw-related effects and activation windows still use the normal duel engine path.

### Deck editor

- `Tab`: switch Main Deck, Extra Deck, and collection
- `Left` / `Right`: browse cards
- `Up` / `Down`: move by ten cards
- `Enter`: add or remove a card
- `V`: read card details
- `I`: read deck statistics
- `S`: read the current skill
- `K`: change skill
- `U`: set the deck as active
- `Ctrl+S`: save

### Shop and ticket exchange

- `Tab` / `Shift+Tab`: change shop category
- `Left` / `Right`: browse items or cards
- `Up` / `Down`: move by five shop items
- `Enter`: purchase or select
- `C` or `I`: read shop item details
- `G`: read gem or ticket balance
- `Space`: confirm a ticket exchange

### Home and Duel World

- `Up` / `Down`: browse known destinations
- `Left` / `Right`: change area, or change character in the character panel
- `Enter`: activate the current destination
- `G`: read gem balance
- `B`: switch between the curated destination list and generic browse-all mode

Use browse-all mode when a new event or game update adds a control that the curated list does not know yet. Generic items may have weak labels because the game often stores text in sprites.

## Troubleshooting

If startup is silent:

1. Confirm NVDA was running before Duel Links started.
2. Confirm `Tolk.dll` and `nvdaControllerClient64.dll` are beside `dlpc.exe`.
3. Confirm `DuelLinksAccess.dll` is under `Mods`.
4. Check `MelonLoader/Latest.log` for `DuelLinksAccess` errors.

Attach a recent MelonLoader log to bug reports after removing account names, Steam identifiers, and other personal data.

## Development

See [Building](docs/BUILDING.md), [Architecture](docs/ARCHITECTURE.md), and [Contributing](CONTRIBUTING.md). Release maintainers should also read [Releasing](docs/RELEASING.md).

Portable tests do not require a game installation:

```powershell
dotnet test tests\DuelLinksAccess.Tests\DuelLinksAccess.Tests.csproj
```

The full mod build requires local MelonLoader and generated Duel Links IL2CPP assemblies:

```powershell
.\scripts\Build-Mod.ps1 -Release
```

## License

Duel Links Access source code is licensed under the MIT License. See [LICENSE](LICENSE). Runtime dependencies use their own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
