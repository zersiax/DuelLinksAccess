<#
.SYNOPSIS
    Checks the local DuelLinksAccess build and runtime prerequisites.

.PARAMETER GamePath
    Duel Links installation path containing dlpc.exe.

.PARAMETER ProjectPath
    Repository root. Defaults to the parent of this script directory.
#>

param(
    [string]$GamePath = $env:DUEL_LINKS_PATH,
    [string]$ProjectPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
$Errors = 0
$Warnings = 0

if (-not $GamePath) {
    $GamePath = "C:\Program Files (x86)\Steam\steamapps\common\Yu-Gi-Oh! Duel Links"
}

function Test-RequiredFile {
    param([string]$Name, [string]$Path)

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Write-Host "OK: $Name"
        return
    }

    Write-Host "ERROR: $Name not found at '$Path'"
    $script:Errors++
}

function Test-RuntimeFile {
    param([string]$Name, [string]$Path)

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        Write-Host "OK: $Name"
        return
    }

    Write-Host "WARNING: $Name not found at '$Path'"
    $script:Warnings++
}

$GamePath = [IO.Path]::GetFullPath($GamePath)
$ProjectPath = [IO.Path]::GetFullPath($ProjectPath)
$MelonLoaderPath = Join-Path $GamePath "MelonLoader"
$AssembliesPath = Join-Path $MelonLoaderPath "Il2CppAssemblies"

Write-Host "DuelLinksAccess setup check"
Write-Host "Game: $GamePath"
Write-Host "Project: $ProjectPath"
Write-Host ""

Test-RequiredFile "Duel Links executable" (Join-Path $GamePath "dlpc.exe")
Test-RequiredFile "MelonLoader net6 assembly" (Join-Path $MelonLoaderPath "net6\MelonLoader.dll")
Test-RequiredFile "Harmony assembly" (Join-Path $MelonLoaderPath "net6\0Harmony.dll")
Test-RequiredFile "Il2CppInterop runtime" (Join-Path $MelonLoaderPath "net6\Il2CppInterop.Runtime.dll")
Test-RequiredFile "generated Assembly-CSharp" (Join-Path $AssembliesPath "Assembly-CSharp.dll")
Test-RequiredFile "generated Il2Cppmscorlib" (Join-Path $AssembliesPath "Il2Cppmscorlib.dll")
Test-RequiredFile "generated UnityEngine" (Join-Path $AssembliesPath "UnityEngine.dll")
Test-RequiredFile "generated UnityEngine.CoreModule" (Join-Path $AssembliesPath "UnityEngine.CoreModule.dll")
Test-RequiredFile "generated UnityEngine.InputLegacyModule" (Join-Path $AssembliesPath "UnityEngine.InputLegacyModule.dll")
Test-RequiredFile "generated UnityEngine.UI" (Join-Path $AssembliesPath "UnityEngine.UI.dll")
Test-RequiredFile "generated UnityEngine.IMGUIModule" (Join-Path $AssembliesPath "UnityEngine.IMGUIModule.dll")
Test-RequiredFile "generated UnityEngine.UIModule" (Join-Path $AssembliesPath "UnityEngine.UIModule.dll")
Test-RequiredFile "project file" (Join-Path $ProjectPath "DuelLinksAccess.csproj")
Test-RequiredFile "mod entry point" (Join-Path $ProjectPath "src\Main.cs")

Test-RuntimeFile "Tolk" (Join-Path $GamePath "Tolk.dll")
Test-RuntimeFile "NVDA controller client" (Join-Path $GamePath "nvdaControllerClient64.dll")
Test-RuntimeFile "MelonLoader log" (Join-Path $MelonLoaderPath "Latest.log")

Write-Host ""
Write-Host "Errors: $Errors"
Write-Host "Warnings: $Warnings"

if ($Errors -gt 0) {
    Write-Host "Setup cannot build the mod. Fix required files above."
    exit 1
}

if ($Warnings -gt 0) {
    Write-Host "Build prerequisites are present, but runtime speech or log checks need attention."
} else {
    Write-Host "Build and runtime prerequisites are present."
}
