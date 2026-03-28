# Deploy-Mod.ps1 - Copies built DLL to game Mods folder
param(
    [string]$GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Yu-Gi-Oh! Duel Links",
    [switch]$Release
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Config = if ($Release) { "Release" } else { "Debug" }
$DllPath = Join-Path $ProjectRoot "bin\$Config\net6.0\DuelLinksAccess.dll"
$ModsFolder = Join-Path $GamePath "Mods"

if (-not (Test-Path $DllPath)) {
    Write-Host "DLL not found at $DllPath. Run Build-Mod.ps1 first." -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $ModsFolder)) {
    New-Item -ItemType Directory -Path $ModsFolder | Out-Null
    Write-Host "Created Mods folder: $ModsFolder"
}

Copy-Item $DllPath $ModsFolder -Force
Write-Host "Deployed DuelLinksAccess.dll to $ModsFolder" -ForegroundColor Green
