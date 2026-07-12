<#
.SYNOPSIS
    Reads build-relevant values from a MelonLoader log.

.PARAMETER LogPath
    Path to Latest.log.

.PARAMETER GamePath
    Duel Links installation path. Used to locate MelonLoader\Latest.log.
#>

param(
    [string]$LogPath,
    [string]$GamePath = $env:DUEL_LINKS_PATH
)

$ErrorActionPreference = "Stop"

if (-not $LogPath) {
    if (-not $GamePath) {
        $GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Yu-Gi-Oh! Duel Links"
    }
    $LogPath = Join-Path $GamePath "MelonLoader\Latest.log"
}

if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
    throw "MelonLoader log not found at '$LogPath'. Launch the game once or pass -LogPath."
}

$Content = Get-Content -LiteralPath $LogPath -Raw

function Read-LogValue {
    param([string]$Pattern)

    $Match = [regex]::Match($Content, $Pattern, [Text.RegularExpressions.RegexOptions]::Multiline)
    if ($Match.Success) { return $Match.Groups[1].Value.Trim() }
    return "Not found"
}

[pscustomobject]@{
    GameName = Read-LogValue '^.*?Game Name:\s*(.+)$'
    Developer = Read-LogValue '^.*?Game Developer:\s*(.+)$'
    RuntimeType = Read-LogValue '^.*?Runtime Type:\s*(.+)$'
    UnityVersion = Read-LogValue '^.*?Unity Version:\s*v?([\w.-]+).*$'
    MelonLoaderVersion = Read-LogValue 'MelonLoader\s+v([\d.]+)'
    LogPath = [IO.Path]::GetFullPath($LogPath)
} | Format-List
