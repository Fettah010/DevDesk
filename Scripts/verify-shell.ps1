# ------------------------------------------------------------------------------
# Proves the shell behaviours of v0.1.9 against the real app: a hand-edited
# off-screen geometry is pulled back on screen, the last route is restored, the
# minimum size is enforced, navigation is remembered, and "Check for updates"
# asks the updater instead of printing a canned sentence.
#
#   .\Scripts\verify-shell.ps1
#   .\Scripts\verify-shell.ps1 -KeepArtifacts     # keep the throw-away data dir
#
# Runs against an isolated DEVDESK_DATA_DIR, so the real profile is untouched and
# settings.json can be seeded with hostile values.
# ------------------------------------------------------------------------------

param(
    [string]$Exe = "",
    [int]$StartupTimeoutSeconds = 40,
    [switch]$KeepArtifacts
)

$ErrorActionPreference = "Stop"

$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $ProjectRoot "bin\x64\Debug\net10.0-windows10.0.19041.0\DevDesk.exe"
}
if (-not (Test-Path $Exe)) {
    throw "DevDesk.exe not found at $Exe. Build it first: dotnet build DevDesk.csproj -p:Platform=x64"
}

$DataDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("devdesk-shell-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
$env:DEVDESK_DATA_DIR = $DataDirectory

$SettingsPath = Join-Path $DataDirectory "settings.json"
$LogDirectory = Join-Path $DataDirectory "Logs"

# Hostile geometry on purpose: far past the bottom-right corner of any display,
# and a route that only exists in the footer of the navigation view.
@'
{
  "schemaVersion": 1,
  "theme": "System",
  "channel": "beta",
  "window": { "x": 5000, "y": 5000, "width": 1240, "height": 800, "maximized": false },
  "lastRoute": "diagnostics"
}
'@ | Set-Content -LiteralPath $SettingsPath -Encoding utf8

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName Microsoft.VisualBasic

# UI Automation's TransformPattern.Resize hides this WinUI window (measured: the
# HWND loses WS_VISIBLE and the whole navigation tree leaves the automation
# tree), which then fails the navigation checks for the wrong reason. SetWindowPos
# is the same call a user's resize ends in, and it keeps the window visible and
# the minimum size enforced.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class DevDeskShellNative
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
"@

$failures = New-Object System.Collections.Generic.List[string]
$results = New-Object System.Collections.Generic.List[string]

function Check([string]$name, [scriptblock]$body) {
    try {
        $detail = & $body
        $results.Add("PASS $name -> $detail")
    }
    catch {
        $results.Add("FAIL $name -> $($_.Exception.Message)")
        $failures.Add($name)
    }
}

function Find-Descendants($element, $controlType) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty, $controlType)
    return $element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Select-NavigationItem($window, [string]$name) {
    foreach ($item in (Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem))) {
        if ($item.Current.Name -eq $name) {
            $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
            return
        }
    }
    throw "Navigation item '$name' was not found."
}

function Get-SelectedNavigationItem($window) {
    foreach ($item in (Find-Descendants $window ([System.Windows.Automation.ControlType]::ListItem))) {
        $pattern = $null
        if ($item.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pattern)) {
            if ($pattern.Current.IsSelected) { return $item.Current.Name }
        }
    }
    return ""
}

function Read-Log {
    $file = Get-ChildItem $LogDirectory -Filter "applog-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $file) { return "" }

    $stream = [System.IO.File]::Open($file.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object System.IO.StreamReader($stream)
        return $reader.ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
}

$process = Start-Process -FilePath $Exe -PassThru

try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)

    $window = $null
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
    while ($null -eq $window -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        if ($process.HasExited) { throw "DevDesk exited during startup." }
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
    }
    if ($null -eq $window) { throw "The DevDesk window never appeared within $StartupTimeoutSeconds s." }

    Start-Sleep -Milliseconds 1500

    Check "placement is pulled back onto a screen" {
        $rect = $window.Current.BoundingRectangle
        if ($rect.X -lt 0 -or $rect.Y -lt 0) { throw "window starts off-screen at $($rect.X),$($rect.Y)" }
        if ($rect.Width -ne 1240 -or $rect.Height -ne 800) { throw "size is $($rect.Width)x$($rect.Height), expected 1240x800" }

        $log = Read-Log
        if ($log -notmatch "Window placement restored to 1240x800 at") { throw "no placement log line" }

        "$([int]$rect.X),$([int]$rect.Y) $([int]$rect.Width)x$([int]$rect.Height) (saved 5000,5000)"
    }

    Check "the last route is restored" {
        $selected = Get-SelectedNavigationItem $window
        if ($selected -ne "Diagnostics") { throw "selected '$selected', expected Diagnostics" }
        $selected
    }

    Check "the minimum size is enforced" {
        # SWP_NOZORDER | SWP_NOMOVE: resize in place, exactly like a drag.
        $handle = [IntPtr]$window.Current.NativeWindowHandle
        if (-not [DevDeskShellNative]::SetWindowPos($handle, [IntPtr]::Zero, 0, 0, 400, 300, 0x0006)) {
            throw "SetWindowPos failed for handle $handle"
        }
        Start-Sleep -Milliseconds 700
        $rect = $window.Current.BoundingRectangle
        if ($rect.Width -lt 940 -or $rect.Height -lt 580) {
            throw "window shrank to $([int]$rect.Width)x$([int]$rect.Height)"
        }
        "asked 400x300, got $([int]$rect.Width)x$([int]$rect.Height)"
    }

    Check "navigation is remembered in settings" {
        Select-NavigationItem $window "Projects"
        Start-Sleep -Milliseconds 1200
        $json = Get-Content $SettingsPath -Raw | ConvertFrom-Json
        if ($json.lastRoute -ne "projects") { throw "lastRoute is '$($json.lastRoute)'" }
        $json.lastRoute
    }

    Check "check for updates asks the updater" {
        Select-NavigationItem $window "Settings"
        Start-Sleep -Milliseconds 1200

        $button = $null
        foreach ($candidate in (Find-Descendants $window ([System.Windows.Automation.ControlType]::Button))) {
            if ($candidate.Current.Name -like "*Check for updates*") { $button = $candidate; break }
        }
        if ($null -eq $button) { throw "the Check for updates button was not found" }

        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $message = $null
        $wait = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $wait -and $null -eq $message) {
            Start-Sleep -Milliseconds 500
            foreach ($text in (Find-Descendants $window ([System.Windows.Automation.ControlType]::Text))) {
                if ($text.Current.Name -like "*only available for installed apps*") { $message = $text.Current.Name; break }
            }
        }

        if ($null -eq $message) { throw "no real update-check answer appeared" }
        $message
    }
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(10000) | Out-Null
    }
}

Write-Host ""
Write-Host "==> Shell verification" -ForegroundColor Cyan
foreach ($line in $results) {
    if ($line.StartsWith("PASS")) { Write-Host "    $line" -ForegroundColor Green }
    else { Write-Host "    $line" -ForegroundColor Red }
}
Write-Host "    data directory       : $DataDirectory"

if (-not $KeepArtifacts) {
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Remove-Item -LiteralPath $DataDirectory -Recurse -Force -ErrorAction Stop
            break
        }
        catch {
            Start-Sleep -Milliseconds 200
        }
    }
}

if ($failures.Count -gt 0) {
    Write-Host "SHELL VERIFICATION FAILED ($($failures.Count))" -ForegroundColor Red
    exit 1
}

Write-Host "Shell verification passed." -ForegroundColor Green
exit 0
