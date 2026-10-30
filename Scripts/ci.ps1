# ------------------------------------------------------------------------------
# The same gate CI runs (.github/workflows/ci.yml), on your machine.
#
#   .\Scripts\ci.ps1                 # build Release + full test suite (x64)
#   .\Scripts\ci.ps1 -Configuration Debug
#   .\Scripts\ci.ps1 -Filter "FullyQualifiedName~DocsHonestyTests"
#
# Why a script instead of two dotnet commands: Platform=x64 is mandatory for
# WinUI (AGENTS gotcha 2) and Release is what CI uses, so the flag that is easy
# to forget is baked in. A local run and a CI run are then the same thing, which
# is the only way "it passed here" means anything.
#
# Exit code is 0 only when both steps pass.
# ------------------------------------------------------------------------------

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Filter = "",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ResultsDirectory = Join-Path $ProjectRoot "artifacts\test-results"

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host "==> $text" -ForegroundColor Cyan
}

Write-Step "dotnet --version (global.json)"
dotnet --version
if ($LASTEXITCODE -ne 0) { throw "dotnet is not usable (exit $LASTEXITCODE)." }

$started = Get-Date

if (-not $SkipBuild) {
    Write-Step "Build ($Configuration, x64)"
    dotnet build (Join-Path $ProjectRoot "DevDesk.csproj") -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "CI FAILED: the build did not compile." -ForegroundColor Red
        exit 1
    }
}

Write-Step "Test ($Configuration, x64)"
$testArgs = @(
    (Join-Path $ProjectRoot "DevDesk.Tests"),
    "-c", $Configuration,
    "-p:Platform=x64",
    "--logger", "trx;LogFileName=test-results.trx",
    "--results-directory", $ResultsDirectory
)
if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $testArgs += @("--filter", $Filter)
}

dotnet test @testArgs
$testCode = $LASTEXITCODE

$elapsed = (Get-Date) - $started

Write-Host ""
if ($testCode -ne 0) {
    Write-Host ("CI FAILED: tests did not pass (exit {0}) after {1:n1}s. TRX: {2}" -f $testCode, $elapsed.TotalSeconds, $ResultsDirectory) -ForegroundColor Red
    exit $testCode
}

Write-Host ("CI PASSED in {0:n1}s. TRX: {1}" -f $elapsed.TotalSeconds, $ResultsDirectory) -ForegroundColor Green
exit 0
