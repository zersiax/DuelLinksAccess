# Third-party notices

DuelLinksAccess bundles the Tolk screen-reader abstraction library and the NVDA controller client so that speech works out of the box. Both are LGPL and are redistributed here under the terms below, with their full license texts included in the release archive. Other dependencies (MelonLoader, HarmonyX, Il2CppInterop, and the game/Unity assemblies) are referenced from the local install and are never redistributed.

## Tolk (bundled)

Screen-reader output goes through Tolk. `Tolk.dll` is loaded at runtime and is included in the release archive.

- Project: Tolk
- Copyright: Davy Kager and contributors
- License: GNU Lesser General Public License 3.0 (see `Tolk-LICENSE.txt` in the archive)
- Source revision: `6d2f4301d0a1ba9b8fa6ecdd9cd65b9f9af58f32`
- Binary archive: https://github.com/ndarilek/tolk/releases/download/refs/heads/master/tolk.zip
- Binary archive SHA-256: `76879FE7FEFDB553AE30891284903E4B569BBA190E23DBF51E54B5876C254C19`
- Source: https://github.com/ndarilek/tolk/tree/6d2f4301d0a1ba9b8fa6ecdd9cd65b9f9af58f32
- License text: https://github.com/ndarilek/tolk/blob/6d2f4301d0a1ba9b8fa6ecdd9cd65b9f9af58f32/LICENSE.txt

The bundled `Tolk.dll` and `nvdaControllerClient64.dll` are the x64 binaries taken verbatim from that hash-verified archive.

## NVDA controller client (bundled)

`nvdaControllerClient64.dll` bridges Tolk to NVDA and is included in the release archive.

- Project: NVDA controller client (from NV Access), redistributed within the Tolk archive above
- License: GNU Lesser General Public License 2.1 (see `nvdaControllerClient-LICENSE.txt` in the archive)

Both DLLs are LGPL and are shipped as separate, user-replaceable files: you may substitute your own build of either library. If your screen reader is not NVDA, replace `nvdaControllerClient64.dll` with the controller client for your reader (the other clients ship in the Tolk archive above).

## Runtime and build dependencies (not redistributed)

These are referenced from the local game or MelonLoader installation and are not copied into the release archive.

- MelonLoader 0.7.3 or later: Apache License 2.0, https://github.com/LavaGang/MelonLoader
- HarmonyX 2.x: MIT License, https://github.com/BepInEx/HarmonyX
- Il2CppInterop: GNU Lesser General Public License 3.0, https://github.com/BepInEx/Il2CppInterop

Konami and Unity game assemblies are local build inputs. They are never part of a DuelLinksAccess release.
