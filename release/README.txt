DuelLinksAccess installation
============================

Requirements
------------

- Yu-Gi-Oh! Duel Links for Steam on 64-bit Windows
- MelonLoader 0.7.3 or later, installed for dlpc.exe
- NVDA running before the game starts

Tolk and the NVDA controller client are bundled in this archive, so you do
not need to download them separately.

Install
-------

1. Install MelonLoader and launch Duel Links once so it generates its
   assemblies, then close it.
2. Extract this archive into your Duel Links install folder:
   - Mods\DuelLinksAccess.dll  ->  the game's Mods folder
   - Tolk.dll                  ->  the game root, beside dlpc.exe
   - nvdaControllerClient64.dll->  the game root, beside dlpc.exe
   Replaces any earlier DuelLinksAccess.dll if present.
3. Start NVDA, then start Duel Links.

Press F1 in the game to hear the current controls.

If your screen reader is not NVDA, replace nvdaControllerClient64.dll with the
controller client for your reader. Tolk and the NVDA controller client are
LGPL; their license texts (Tolk-LICENSE.txt and nvdaControllerClient-LICENSE.txt)
are included in this archive, and both DLLs may be swapped for your own builds.

Provenance for every bundled file is in BUILD-INFO.txt and SHA256SUMS.txt.
Troubleshooting and current controls:
https://github.com/zersiax/DuelLinksAccess
