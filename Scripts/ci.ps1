# ------------------------------------------------------------------------------
# The same gate CI runs (.github/workflows/ci.yml), on your machine.
#
#   .\Scripts\ci.ps1                 # build Release + headless test suite (x64)
#   .\Scripts\ci.ps1 -Configuration Debug
#   .\Scripts\ci.ps1 -Filter "FullyQualifiedName~DocsHonestyTests"
#
# Why a script instead of two dotnet commands: Platform=x64 is mandatory for
# WinUI (AGENTS gotcha 2) and Release is what CI uses, so the flag that is easy
# to forget is baked in. A local run and a CI run are then the same thing, which
# is the only way "it passed here" means anything.
#
# The resource-intensive v0.7 scale measurements are opt-in through
# Scripts\benchmark-v07.ps1 and do not slow or destabilise the CI gate.
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
    Write-Step "Restore test and app dependencies (x64)"
    dotnet restore (Join-Path $ProjectRoot "DevDesk.Tests") -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        Write-Host "CI FAILED: dependency restore failed." -ForegroundColor Red
        exit 1
    }

    Write-Step "Build app and tests ($Configuration, x64)"
    dotnet build (Join-Path $ProjectRoot "DevDesk.Tests") -c $Configuration -p:Platform=x64 --no-restore
    if ($LASTEXITCODE -ne 0) {
        Write-Host "CI FAILED: the app or test project did not compile." -ForegroundColor Red
        exit 1
    }
}

Write-Step "Test ($Configuration, x64)"
$testArgs = @(
    (Join-Path $ProjectRoot "DevDesk.Tests"),
    "-c", $Configuration,
    "-p:Platform=x64",
    "--no-build",
    "--no-restore",
    "--logger", "trx;LogFileName=test-results.trx",
    "--results-directory", $ResultsDirectory
)
if (-not [string]::IsNullOrWhiteSpace($Filter)) {
    $testArgs += @("--filter", $Filter)
} else {
    $testArgs += @("--filter", "Category!=V07Benchmarks")
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
