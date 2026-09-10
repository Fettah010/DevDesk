# ------------------------------------------------------------------------------
# Launch DevDesk without a terminal: incremental build (if needed) and start.
#
# Wire this to a desktop shortcut (see make-shortcut.ps1). Double-click the
# shortcut, the window opens when the build finishes - no dotnet run needed.
# ------------------------------------------------------------------------------

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $scriptDir   # DevDesk project root

Push-Location $root
try {
    & dotnet build "DevDesk.csproj" -c Debug -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed with exit code $LASTEXITCODE" -ForegroundColor Red
        Start-Sleep -Seconds 3
        exit $LASTEXITCODE
    }

    $exe = Join-Path $root "bin\x64\Debug\net10.0-windows10.0.19041.0\DevDesk.exe"
    if (-not (Test-Path -LiteralPath $exe)) {
        Write-Host "Built exe not found at $exe" -ForegroundColor Red
        Start-Sleep -Seconds 3
        exit 1
    }

    Start-Process -FilePath $exe
}
finally {
    Pop-Location
}