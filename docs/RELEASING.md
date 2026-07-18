# Releasing

## Version

Update the version in both locations:

- `DuelLinksAccess.csproj` in the `Version` property
- `src/Main.cs` in the `MelonInfo` attribute

`Package-Release.ps1` rejects a version that does not match either source location.

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

The package contains:

- `Mods/DuelLinksAccess.dll`
- `Tolk.dll` and `nvdaControllerClient64.dll` (x64), taken verbatim from the hash-verified Tolk archive pinned in [THIRD_PARTY_NOTICES.md](../THIRD_PARTY_NOTICES.md)
- `Tolk-LICENSE.txt` (LGPL 3.0) and `nvdaControllerClient-LICENSE.txt` (LGPL 2.1)
- `BUILD-INFO.txt`
- project `LICENSE`
- `README.txt`
- `THIRD_PARTY_NOTICES.md`
- `SHA256SUMS.txt`

Tolk and the NVDA controller client are LGPL and are shipped as loose, user-replaceable DLLs with their license texts included, so speech works on a clean install. Verify the Tolk archive SHA-256 in `THIRD_PARTY_NOTICES.md` before extracting the DLLs from it.

Build the package twice from the same commit and compare ZIP SHA-256 values. They must match. Extract one copy and verify every line of `SHA256SUMS.txt`.

Do not add MelonLoader, Unity assemblies, Il2CppInterop assemblies, generated game assemblies, or decompiled code to the archive.

> **Note:** `scripts/Package-Release.ps1` predates the screen-reader bundling policy and still stages only the mod DLL and docs. Until it is updated to download, hash-verify, and include the Tolk binaries and their license texts, assemble the screen-reader files into the archive as a documented manual step (as was done for v1.4.0).

## Publish

Write release notes from the actual commit range. State runtime checks separately from automated checks. Upload the ZIP and publish its SHA-256 in the release notes. Confirm the Git tag, `MelonInfo`, project version, archive name, and release title all use the same version.
