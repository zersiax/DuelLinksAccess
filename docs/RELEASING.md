# Releasing

## Version

Update the version in both locations:

- `DuelLinksAccess.csproj` in the `Version` property
- `src/Main.cs` in the `MelonInfo` attribute

`Package-Release.ps1` rejects a version that does not match `MelonInfo`.

## Checks

1. Run portable tests.
2. Run a clean Release build against current game assemblies.
3. Search the diff for logs, account data, absolute personal paths, native DLLs, and game assemblies.
4. Complete runtime checks for the changed game flows.
5. Review [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md) when any dependency changes.

Commands:

```powershell
dotnet test tests\DuelLinksAccess.Tests\DuelLinksAccess.Tests.csproj
dotnet build DuelLinksAccess.csproj -c Release
git diff --check
```

## Package

Create the archive with PowerShell 7 and the release version:

```powershell
pwsh -File .\scripts\Package-Release.ps1 -Version 'X.Y.Z'
```

The package contains only:

- `Mods/DuelLinksAccess.dll`
- `BUILD-INFO.txt`
- project `LICENSE`
- `README.txt`
- `THIRD_PARTY_NOTICES.md`
- `SHA256SUMS.txt`

Build the package twice from the same commit and compare ZIP SHA-256 values. They must match. Extract one copy and verify every line of `SHA256SUMS.txt`.

Do not add Tolk, NVDA client libraries, MelonLoader, Unity assemblies, Il2CppInterop assemblies, generated game assemblies, or decompiled code to the archive.

## Publish

Write release notes from the actual commit range. State runtime checks separately from automated checks. Upload the ZIP and publish its SHA-256 in the release notes. Confirm the Git tag, `MelonInfo`, project version, archive name, and release title all use the same version.
