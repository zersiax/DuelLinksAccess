# Building

## Prerequisites

- Windows
- .NET 8 SDK or later
- Yu-Gi-Oh! Duel Links from Steam
- MelonLoader 0.7.3 or later installed for `dlpc.exe`
- One game launch after MelonLoader installation, so generated IL2CPP assemblies exist

The project targets `net6.0` because that is the MelonLoader runtime used by the game. `global.json` selects the .NET 8 SDK for builds and tests.

## Portable tests

Tests target .NET 8, source-link pure C# policies, and do not require game files:

```powershell
dotnet test tests\DuelLinksAccess.Tests\DuelLinksAccess.Tests.csproj
```

To prove the tests are portable, provide a nonexistent game path:

```powershell
dotnet test tests\DuelLinksAccess.Tests\DuelLinksAccess.Tests.csproj -p:DuelLinksPath=Z:\missing-game
```

## Mod build

Check a local setup when diagnosing missing dependencies:

```powershell
.\scripts\Test-ModSetup.ps1 -GamePath 'C:\Program Files (x86)\Steam\steamapps\common\Yu-Gi-Oh! Duel Links'
```

The default Steam path is used when no override is supplied:

```powershell
.\scripts\Build-Mod.ps1 -Release
```

For another installation path:

```powershell
.\scripts\Build-Mod.ps1 -Release -GamePath 'D:\SteamLibrary\steamapps\common\Yu-Gi-Oh! Duel Links'
```

You can also set `DUEL_LINKS_PATH` before running `dotnet build` directly.

Builds write to `bin` and do not modify the game installation. To copy the DLL into the local `Mods` folder for runtime testing:

```powershell
.\scripts\Build-Mod.ps1 -Release -Deploy
```

## Dependency rules

References to MelonLoader, Unity, Il2CppInterop, and `Assembly-CSharp.dll` use the local game installation with `Private=false`. Never copy these files into a release or commit them to Git.

Tolk is a runtime dependency, not a compile-time reference. Release archives do not redistribute Tolk or screen reader client libraries. See [third-party notices](../THIRD_PARTY_NOTICES.md).
