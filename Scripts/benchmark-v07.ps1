param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path

dotnet test (Join-Path $projectRoot "DevDesk.Tests\DevDesk.Tests.csproj") `
    -c $Configuration `
    -p:Platform=x64 `
    --filter "Category=V07Benchmarks" `
    --logger "console;verbosity=detailed"
if ($LASTEXITCODE -ne 0) {
    throw "The v0.7 performance benchmarks failed (exit $LASTEXITCODE)."
}
