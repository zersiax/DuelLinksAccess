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
- **1 / 2 / 3** — jump directly to a monster slot
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

## The Home and Duel World screen

The main screen of the game is currently pretty vague and complicated. A lot of elements on the screen currently do nothing, and they are still listed because this game often names things oddly, or not at all. So I'm making sure everything that could possibly be clickable is generally available for the player to click, just so you always have a way out if things break. As we learn more about what everything does, we can start removing items we know 100% will never be needed. For now, here's a list of the important items on the screen and what they do.

First, a note on the Space key. At times, the game "scans" for objects a bit too quickly, which causes some things to not register in time. This is why sometimes you have 30 elements on the screen and other times 38 on that same screen. A good rule is to hit Space a second or two after a window loads — that way you always have the latest and most accurate list. This will be improved in the future.

- **Arrow L / Arrow R:** the screen we're on, apart from the options, is actually a map of sorts. There are NPCs walking around, and the map is divided into several areas: street, alley, park, shop, etc. The on-screen Arrow L and Arrow R buttons move between those areas but currently don't tell you which one you're on. The keyboard left/right arrows do the same thing and announce the area name. After using either, press Space to rescan.
- Past the arrows is something that probably either doesn't speak, or speaks Japanese characters. This option almost certainly does nothing — you can ignore it.
- **Present:** a gift icon at the top of the screen where mission rewards and other game rewards can be picked up.
- **Info:** news, notifications, and other things the game wants to surface.
- **Userinfo:** essentially your profile. Not sure how accessible this is yet, but it holds your wins, losses, and similar stats.
- **Option:** settings, data transfer, and license agreements.
- **Neuron code:** not entirely sure what this does — likely a friend-code system.
- **Shop banners and shop htjson banner entries:** generally ignorable. They're upsells; they all just route into the shop as far as I know.
- Items labeled **"card"** appear to be background images.
- **List Open:** currently unknown, doesn't seem to do much.
- **Home:** important. Switches between Duel World and Duel Home. Some things, like Duel Trials, can only be accessed from one of the two modes. Always press Space a second or two after pressing this to find the new content.
- **Menu right base:** does nothing.
- **Select Chara:** change the character you play as.
- **Select Deck:** pick a different deck if you have others.
- **Edit Deck:** opens the deck editor. Basic accessibility — most things work, but going through large amounts of cards is not very efficient yet.
- **Missions:** opens the missions screen. Missions are quests like "play X card 5 times" or "Defeat Joey". The Stage missions (under the World tab) are particularly important — completing them advances your stage and unlocks new features.
- Another string in Japanese: doesn't do anything important.
- **Gate:** duel NPCs at the gate (costs Gate Keys) or out on the map (costs nothing). Gate duels need to be unlocked via certain missions.
- **PvP Arena:** self-explanatory — this is where you fight other players.
- **Shop:** opens the shop. Tricky to make work well because the server returns image-only banners for some packs/promotions. Most things should still be reachable.
- **Duel Studio:** something to do with cinematics, not entirely sure.
- **MapObjectRoot#:** buildings on the map that lead you to different screens. Still working on labeling these better.
- **Standard / Legendary Duelist:** NPCs on the map you can fight.
- **Gift:** a gift on the map that gives you items.

When you press the **Home** button (and Space to rescan), a number of new items appear in each area. Most are MapObjectRoot entries, plus a few others:

- **World:** takes the place of the Home option and switches the screen back to the one described above.
- **BG:** appears to open a list of beginner missions.
- **Standard / Legendary:** seem to duplicate the duelists further down the list.
- **Erase UI:** unclear — best left alone for now.
- **Character deck duel:** unlocks once you have character decks. Not sure yet whether this is purely PvP or has an NPC component.
- **Short Cut:** adds another batch of items to the screen.

Once you press **Short Cut** (yes, the name is awkward), these extra options appear:

- **Event:** shortcut to the event gate for the current event.
- **Card Trader EX:** related to the Card Trader NPC found in one of the map areas.
- **Shortcut button entry:** route to various game screens — needs better labeling.
- **Duel Trials:** puzzles and quizzes that teach you how to play with certain decks. They become important around stage 4 or 5.
- **Card Trader:** still investigating how this differs from Card Trader EX above.

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
