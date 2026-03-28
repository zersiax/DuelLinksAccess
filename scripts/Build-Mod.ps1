# Build-Mod.ps1 - Builds DuelLinksAccess and copies to game Mods folder
param(
    [switch]$Release
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Config = if ($Release) { "Release" } else { "Debug" }

Write-Host "Building DuelLinksAccess ($Config)..." -ForegroundColor Cyan

Push-Location $ProjectRoot
try {
    dotnet build -c $Config
    if ($LASTEXITCODE -ne 0) {
        Write-Host "BUILD FAILED" -ForegroundColor Red
        exit 1
    }
    Write-Host "Build successful!" -ForegroundColor Green
}
finally {
    Pop-Location
}
