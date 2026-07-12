param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,

    [string]$GamePath = $env:DUEL_LINKS_PATH,
    [string]$OutputDirectory,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$ProjectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
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

if (-not $SkipBuild) {
    $BuildArgs = @(
        "build",
        (Join-Path $ProjectRoot "DuelLinksAccess.csproj"),
        "--configuration", "Release",
        "-p:Version=$Version"
    )
    if ($GamePath) {
        $BuildArgs += "-p:DuelLinksPath=$GamePath"
    }

    & dotnet $BuildArgs
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed"
    }
}

$DllPath = Join-Path $ProjectRoot "bin\Release\net6.0\DuelLinksAccess.dll"
if (-not (Test-Path -LiteralPath $DllPath -PathType Leaf)) {
    throw "Release DLL not found at $DllPath"
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
