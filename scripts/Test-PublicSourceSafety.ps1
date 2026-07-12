$ErrorActionPreference = "Stop"
$ProjectRoot = Split-Path -Parent $PSScriptRoot
$AllSourcePatterns = @('API\.Account_create\s*\(')
$MainPatterns = @(
    'API\.User_tutorial_dialog\s*\('
    'TutorialViewController\.StartTutorialDuel\s*\('
    'TutorialUtil\.ShowFirstTimer\s*\('
    'TutorialManager\.waitTarget\s*='
    'TutorialManager\.fetch\s*\('
)

$Failed = $false
Get-ChildItem (Join-Path $ProjectRoot "src") -Filter *.cs -File -Recurse |
    ForEach-Object {
        $Content = Get-Content -LiteralPath $_.FullName -Raw
        foreach ($Pattern in $AllSourcePatterns) {
            if ($Content -match $Pattern) {
                Write-Error "Forbidden public-build API in $($_.FullName): $Pattern"
                $Failed = $true
            }
        }
    }

$MainPath = Join-Path $ProjectRoot "src\Main.cs"
$MainContent = Get-Content -LiteralPath $MainPath -Raw
foreach ($Pattern in $MainPatterns) {
    if ($MainContent -match $Pattern) {
        Write-Error "Forbidden public-build mutation in ${MainPath}: $Pattern"
        $Failed = $true
    }
}

if ($Failed) { exit 1 }
Write-Host "Public source safety check passed."
