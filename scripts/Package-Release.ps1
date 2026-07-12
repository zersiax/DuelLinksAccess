param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$GamePath = $env:DUEL_LINKS_PATH,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw "Package-Release.ps1 requires PowerShell 7 or later (pwsh)."
}
$ProjectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$DirtyState = & git -C $ProjectRoot status --porcelain --untracked-files=normal
if ($LASTEXITCODE -ne 0) { throw "Could not inspect Git worktree" }
if ($DirtyState) {
    throw "Refusing to package a dirty worktree. Commit or remove local changes first."
}
if (-not $GamePath) {
    $GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Yu-Gi-Oh! Duel Links"
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $ProjectRoot "dist"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$StageRoot = [IO.Path]::GetFullPath(
    (Join-Path $ProjectRoot "obj\release-package\DuelLinksAccess-v$Version"))
$AllowedStageRoot = [IO.Path]::GetFullPath(
    (Join-Path $ProjectRoot "obj\release-package"))

if (-not $StageRoot.StartsWith(
    $AllowedStageRoot + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to stage outside $AllowedStageRoot"
}

$MainSource = Get-Content -LiteralPath (Join-Path $ProjectRoot "src\Main.cs") -Raw
$MelonVersionMatch = [regex]::Match(
    $MainSource,
    'MelonInfo\(typeof\([^)]+\),\s*"DuelLinksAccess",\s*"([^"]+)"')
if (-not $MelonVersionMatch.Success) {
    throw "Could not read the MelonInfo version from src\Main.cs"
}
if ($MelonVersionMatch.Groups[1].Value -ne $Version) {
    throw "Package version $Version does not match MelonInfo version $($MelonVersionMatch.Groups[1].Value)"
}
$ProjectXml = [xml](Get-Content -LiteralPath (
    Join-Path $ProjectRoot "DuelLinksAccess.csproj") -Raw)
$ProjectVersionNode = $ProjectXml.SelectSingleNode('/Project/PropertyGroup/Version')
if ($null -eq $ProjectVersionNode) {
    throw "Could not read Version from DuelLinksAccess.csproj"
}
if ($ProjectVersionNode.InnerText -ne $Version) {
    throw "Package version $Version does not match project version $($ProjectVersionNode.InnerText)"
}

$BuildArgs = @(
    "build",
    (Join-Path $ProjectRoot "DuelLinksAccess.csproj"),
    "--configuration", "Release",
    "-p:Version=$Version",
    "-p:DuelLinksPath=$GamePath"
)

& dotnet $BuildArgs
if ($LASTEXITCODE -ne 0) {
    throw "Release build failed"
}

$DllPath = Join-Path $ProjectRoot "bin\Release\net6.0\DuelLinksAccess.dll"
if (-not (Test-Path -LiteralPath $DllPath -PathType Leaf)) {
    throw "Release DLL not found at $DllPath"
}
$DllVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($DllPath).ProductVersion
if (($DllVersion -split '\+')[0] -ne $Version) {
    throw "Built DLL version '$DllVersion' does not match package version '$Version'"
}

if (Test-Path -LiteralPath $StageRoot) {
    Remove-Item -LiteralPath $StageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path (Join-Path $StageRoot "Mods") -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Copy-Item -LiteralPath $DllPath -Destination (Join-Path $StageRoot "Mods")
Copy-Item -LiteralPath (Join-Path $ProjectRoot "LICENSE") -Destination $StageRoot
Copy-Item -LiteralPath (Join-Path $ProjectRoot "THIRD_PARTY_NOTICES.md") -Destination $StageRoot
Copy-Item -LiteralPath (Join-Path $ProjectRoot "release\README.txt") -Destination $StageRoot

$LogPath = Join-Path $GamePath "MelonLoader\Latest.log"
if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
    throw "MelonLoader log not found at '$LogPath'; release provenance is incomplete"
}
$LogContent = Get-Content -LiteralPath $LogPath -Raw
$GameVersionMatch = [regex]::Match(
    $LogContent,
    '^.*?Game Version:\s*(.+)$',
    [Text.RegularExpressions.RegexOptions]::Multiline)
if (-not $GameVersionMatch.Success) {
    throw "Could not read Duel Links version from '$LogPath'"
}

$MelonLoaderDll = Join-Path $GamePath "MelonLoader\net6\MelonLoader.dll"
$HarmonyDll = Join-Path $GamePath "MelonLoader\net6\0Harmony.dll"
$InteropDll = Join-Path $GamePath "MelonLoader\net6\Il2CppInterop.Runtime.dll"
$UnityPlayerDll = Join-Path $GamePath "UnityPlayer.dll"
$AssemblyDirectory = Join-Path $GamePath "MelonLoader\Il2CppAssemblies"
$BuildInputs = @(
    $MelonLoaderDll
    $HarmonyDll
    $InteropDll
    (Join-Path $AssemblyDirectory "Il2Cppmscorlib.dll")
    (Join-Path $AssemblyDirectory "UnityEngine.dll")
    (Join-Path $AssemblyDirectory "UnityEngine.CoreModule.dll")
    (Join-Path $AssemblyDirectory "UnityEngine.InputLegacyModule.dll")
    (Join-Path $AssemblyDirectory "UnityEngine.UI.dll")
    (Join-Path $AssemblyDirectory "UnityEngine.IMGUIModule.dll")
    (Join-Path $AssemblyDirectory "UnityEngine.UIModule.dll")
    (Join-Path $AssemblyDirectory "Assembly-CSharp.dll")
)
foreach ($InputPath in $BuildInputs) {
    if (-not (Test-Path -LiteralPath $InputPath -PathType Leaf)) {
        throw "Build input not found at '$InputPath'"
    }
}

$Commit = (& git -C $ProjectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw "Could not resolve Git commit" }
$DllHash = (Get-FileHash -LiteralPath $DllPath -Algorithm SHA256).Hash
$BuildInfo = @(
    "DuelLinksAccess version: $Version"
    "Git commit: $Commit"
    "Duel Links version: $($GameVersionMatch.Groups[1].Value.Trim())"
    "Unity runtime: $([Diagnostics.FileVersionInfo]::GetVersionInfo($UnityPlayerDll).ProductVersion)"
    "MelonLoader: $([Diagnostics.FileVersionInfo]::GetVersionInfo($MelonLoaderDll).ProductVersion)"
    "HarmonyX: $([Diagnostics.FileVersionInfo]::GetVersionInfo($HarmonyDll).ProductVersion)"
    "Il2CppInterop: $([Diagnostics.FileVersionInfo]::GetVersionInfo($InteropDll).ProductVersion)"
    "DuelLinksAccess.dll SHA-256: $DllHash"
    "Build input SHA-256:"
)
foreach ($InputPath in $BuildInputs) {
    $RelativeInput = [IO.Path]::GetRelativePath($GamePath, $InputPath).Replace('\', '/')
    $InputHash = (Get-FileHash -LiteralPath $InputPath -Algorithm SHA256).Hash
    $BuildInfo += "  ${RelativeInput}: $InputHash"
}
Set-Content -LiteralPath (Join-Path $StageRoot "BUILD-INFO.txt") `
    -Value $BuildInfo -Encoding ascii

$ManifestPath = Join-Path $StageRoot "SHA256SUMS.txt"
$ManifestLines = Get-ChildItem -LiteralPath $StageRoot -File -Recurse |
    Sort-Object FullName |
    ForEach-Object {
        $RelativePath = [IO.Path]::GetRelativePath($StageRoot, $_.FullName).Replace('\', '/')
        $Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$Hash  $RelativePath"
    }
Set-Content -LiteralPath $ManifestPath -Value $ManifestLines -Encoding ascii

$ZipPath = Join-Path $OutputDirectory "DuelLinksAccess-v$Version.zip"
if (Test-Path -LiteralPath $ZipPath) {
    Remove-Item -LiteralPath $ZipPath -Force
}

Add-Type -AssemblyName System.IO.Compression
$ZipStream = [IO.File]::Open($ZipPath, [IO.FileMode]::CreateNew)
try {
    $Archive = [IO.Compression.ZipArchive]::new(
        $ZipStream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $FixedTimestamp = [DateTimeOffset]::Parse("2000-01-01T00:00:00Z")
        Get-ChildItem -LiteralPath $StageRoot -File -Recurse |
            Sort-Object FullName |
            ForEach-Object {
                $EntryName = [IO.Path]::GetRelativePath(
                    $StageRoot, $_.FullName).Replace('\', '/')
                $Entry = $Archive.CreateEntry(
                    $EntryName, [IO.Compression.CompressionLevel]::Optimal)
                $Entry.LastWriteTime = $FixedTimestamp
                $InputStream = [IO.File]::OpenRead($_.FullName)
                $EntryStream = $Entry.Open()
                try {
                    $InputStream.CopyTo($EntryStream)
                }
                finally {
                    $EntryStream.Dispose()
                    $InputStream.Dispose()
                }
            }
    }
    finally {
        $Archive.Dispose()
    }
}
finally {
    $ZipStream.Dispose()
}

$ZipHash = (Get-FileHash -LiteralPath $ZipPath -Algorithm SHA256).Hash
Write-Host "Created $ZipPath"
Write-Host "SHA-256: $ZipHash"
