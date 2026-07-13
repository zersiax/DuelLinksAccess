# Deploy-Mod.ps1 - Compatibility wrapper that builds before deployment
param(
    [string]$GamePath = $env:DUEL_LINKS_PATH,
    [switch]$Release
)

$ErrorActionPreference = "Stop"
$Arguments = @{ Deploy = $true }
if ($GamePath) { $Arguments.GamePath = $GamePath }
if ($Release) { $Arguments.Release = $true }

& (Join-Path $PSScriptRoot "Build-Mod.ps1") @Arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}
