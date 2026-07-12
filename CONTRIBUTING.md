# Contributing

Bug reports, runtime logs, tests, documentation corrections, and focused code changes are useful.

## Before opening a change

- Search existing issues and pull requests.
- Keep unrelated behavior in separate commits.
- Do not commit game assemblies, decompiled Konami code, native screen reader binaries, logs, or account data.
- Describe the Duel Links screen, input sequence, expected speech, actual speech, and game mode.
- Remove account names, Steam identifiers, and other personal data from logs.

## Development checks

Install the .NET 8 SDK. The mod targets .NET 6 because MelonLoader loads the net6 runtime.

Run portable tests first:

```powershell
dotnet test tests\DuelLinksAccess.Tests\DuelLinksAccess.Tests.csproj
```

Run a Release build when local game dependencies are available:

```powershell
.\scripts\Build-Mod.ps1 -Release
```

Set a non-default game path with `DUEL_LINKS_PATH` or `-GamePath`. Normal builds do not copy files into the game. Add `-Deploy` only when testing a build locally.

For behavior changes, add a failing test at a pure policy or state seam before editing the Unity adapter. Keep reflection and IL2CPP access out of unit tests. When a runtime-only defect has no honest automated seam, explain the gap and include the relevant log sequence.

## Pull requests

Keep pull requests reviewable:

- Use one commit per independent fix, test addition, documentation change, or build change.
- Explain why each behavior changed.
- List commands run and their results.
- State which runtime flows still need game verification.
- Avoid broad formatting changes in large handlers.

Accessibility behavior needs precise speech and input descriptions. Do not claim a screen reader or game flow is supported unless evidence exists in code, tests, or a recorded runtime log.
