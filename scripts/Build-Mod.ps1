# Build-Mod.ps1 - Builds DuelLinksAccess; deployment is optional
param(
    [string]$GamePath = $env:DUEL_LINKS_PATH,
    [switch]$Release,
    [switch]$Deploy
)

$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$Config = if ($Release) { "Release" } else { "Debug" }
$BuildArgs = @(
    "build",
    (Join-Path $ProjectRoot "DuelLinksAccess.csproj"),
    "--configuration", $Config
)
if ($GamePath) {
    $BuildArgs += "-p:DuelLinksPath=$GamePath"
}
if ($Deploy) {
    $BuildArgs += "-p:DeployToMods=true"
}

Write-Host "Building DuelLinksAccess ($Config)..." -ForegroundColor Cyan

Push-Location $ProjectRoot
try {
    & dotnet $BuildArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "BUILD FAILED" -ForegroundColor Red
        exit 1
    }
    Write-Host "Build successful!" -ForegroundColor Green
}
finally {
    Pop-Location
}
