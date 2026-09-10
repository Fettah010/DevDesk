# ------------------------------------------------------------------------------
# Create (or refresh) the "DevDesk" desktop shortcut that launches the app
# without the terminal: powershell hidden -> launch-dev.ps1 -> build + start.
#
#   powershell -File .\make-shortcut.ps1            # default name "DevDesk"
#   powershell -File .\make-shortcut.ps1 -Name "DevDesk (Debug)"
#
# The shortcut is created ONCE (absolute paths) and reused on every launch —
# it is NOT part of builds or automated flows. Re-run it only when the project
# moves to a new location or the .lnk is missing/broken.
# ------------------------------------------------------------------------------

param(
    [string]$Name = "DevDesk"
)

$ErrorActionPreference = "Stop"

$scriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$root       = Split-Path -Parent $scriptDir          # DevDesk project root
$launch     = Join-Path $scriptDir "launch-dev.ps1"
$icon       = Join-Path $root "Assets\app.ico"
$desktop    = [Environment]::GetFolderPath("Desktop")
$lnk        = Join-Path $desktop "$Name.lnk"

if (-not (Test-Path -LiteralPath $launch)) { throw "launch script not found: $launch" }

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($lnk)
$shortcut.TargetPath    = "powershell.exe"
$shortcut.Arguments     = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$launch`""
$shortcut.WorkingDirectory = $root
$shortcut.IconLocation  = "$icon,0"
$shortcut.Description   = "Build & launch DevDesk (no terminal)"
$shortcut.WindowStyle   = 7          # minimized - keeps the PowerShell window out of the way
$shortcut.Save()

Write-Host "Shortcut created: $lnk" -ForegroundColor Green
if (-not (Test-Path -LiteralPath $icon)) {
    Write-Host "Note: app icon not found at $icon - shortcut uses the default icon." -ForegroundColor Yellow
}