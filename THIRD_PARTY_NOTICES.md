# Third-party notices

DuelLinksAccess uses software supplied by the game, MelonLoader, and the user's screen reader setup. The release package does not redistribute those files.

## Tolk

Screen reader output requires Tolk. DuelLinksAccess loads `Tolk.dll` at runtime but does not include it in release archives.

- Project: Tolk
- Copyright: Davy Kager and contributors
- License: GNU Lesser General Public License 3.0
- Source revision: `6d2f4301d0a1ba9b8fa6ecdd9cd65b9f9af58f32`
- Binary archive: https://github.com/ndarilek/tolk/releases/download/refs/heads/master/tolk.zip
- Binary archive SHA-256: `76879FE7FEFDB553AE30891284903E4B569BBA190E23DBF51E54B5876C254C19`
- Source: https://github.com/ndarilek/tolk/tree/6d2f4301d0a1ba9b8fa6ecdd9cd65b9f9af58f32
- License: https://github.com/ndarilek/tolk/blob/6d2f4301d0a1ba9b8fa6ecdd9cd65b9f9af58f32/LICENSE.txt

Tolk's archive also contains screen reader client libraries under their own licenses. Read `LICENSE.txt` and `LICENSE-NVDA.txt` from that archive before installing them.

## Runtime and build dependencies

These dependencies are referenced from the local game or MelonLoader installation. They are not copied into the release archive.

- MelonLoader 0.7.3 or later: Apache License 2.0, https://github.com/LavaGang/MelonLoader
- HarmonyX 2.x: MIT License, https://github.com/BepInEx/HarmonyX
- Il2CppInterop: GNU Lesser General Public License 3.0, https://github.com/BepInEx/Il2CppInterop

Konami and Unity game assemblies are local build inputs. They are never part of a DuelLinksAccess release.
