param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$executable = Join-Path $projectRoot "bin\x64\$Configuration\net10.0-windows10.0.19041.0\DevDesk.exe"

dotnet build (Join-Path $projectRoot "DevDesk.csproj") -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "DevDesk did not build (exit $LASTEXITCODE)."
}

$env:DEVDESK_UI_EXE = $executable
dotnet test (Join-Path $projectRoot "DevDesk.UiTests\DevDesk.UiTests.csproj") -c $Configuration -p:Platform=x64
if ($LASTEXITCODE -ne 0) {
    throw "Desktop UI tests failed (exit $LASTEXITCODE)."
}
