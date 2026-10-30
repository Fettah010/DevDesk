# ------------------------------------------------------------------------------
# Smoke test: launch DevDesk, visit every navigation destination through UI
# Automation, and assert the run stayed healthy.
#
#   .\Scripts\smoke-ui.ps1                  # Debug build, all destinations
#   .\Scripts\smoke-ui.ps1 -KeepOpen        # leave the window up afterwards
#
# It proves three things that a build cannot:
#   1. every page can be navigated to without a XamlParseException,
#   2. the shared catalog scans exactly once for the whole tour (W-0.1.5),
#   3. nothing was logged at ERR/FTL in this run.
#
# NavigationView items expose SelectionItemPattern only (InvokePattern is not
# supported on them), which is why the selection dance below looks odd.
# ------------------------------------------------------------------------------

param(
    [string]$Exe = "",
    [int]$SettleMs = 1200,
    [int]$MaxScans = 1,
    [switch]$KeepOpen,
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

# A throw-away data directory: settings and logs land here instead of the real
# %LocalAppData%\DevDesk, so the run is isolated, repeatable, and its log file
# contains exactly this run's lines (no byte offsets, no cross-run noise).
$DataDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ("devdesk-smoke-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $DataDirectory -Force | Out-Null
$env:DEVDESK_DATA_DIR = $DataDirectory
$LogDirectory = Join-Path $DataDirectory "Logs"

$Destinations = @("Dashboard", "Projects", "Templates", "GitHub", "DevDesk", "Settings", "Diagnostics")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$process = Start-Process -FilePath $Exe -PassThru
$failures = New-Object System.Collections.Generic.List[string]
$visited = New-Object System.Collections.Generic.List[string]

try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)

    $window = $null
    $deadline = (Get-Date).AddSeconds(40)
    while ($null -eq $window -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        if ($process.HasExited) {
            throw "DevDesk exited during startup (exit code $($process.ExitCode))."
        }
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
    }
    if ($null -eq $window) {
        throw "The DevDesk window never appeared within 40 s."
    }

    Start-Sleep -Milliseconds $SettleMs

    $itemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $items = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCondition)

    foreach ($destination in $Destinations) {
        $item = $null
        foreach ($candidate in $items) {
            if ($candidate.Current.Name -eq $destination) {
                $item = $candidate
                break
            }
        }

        if ($null -eq $item) {
            $failures.Add("Navigation item '$destination' was not found.")
            continue
        }

        try {
            $pattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
            $pattern.Select()
            $visited.Add($destination)
            Start-Sleep -Milliseconds $SettleMs
        }
        catch {
            $failures.Add("Could not select '$destination': $($_.Exception.Message)")
        }
    }
}
finally {
    if (-not $KeepOpen -and -not $process.HasExited) {
        Start-Sleep -Milliseconds 500
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(10000) | Out-Null
    }
}

# Every log file in the throw-away directory was written by this run.
$newLog = ""
$existing = Get-ChildItem $LogDirectory -Filter "applog-*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if ($existing) {
    $stream = [System.IO.File]::Open($existing.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object System.IO.StreamReader($stream)
        $newLog = $reader.ReadToEnd()
    }
    finally {
        $stream.Dispose()
    }
}

$errorLines = @($newLog -split "`r?`n" | Where-Object { $_ -match "\[(ERR|FTL)\]" })
$scanCount = @($newLog -split "`r?`n" | Where-Object { $_ -match "Catalog scan #\d+ finished" }).Count

Write-Host ""
Write-Host "==> Smoke result" -ForegroundColor Cyan
Write-Host ("    destinations visited : {0}/{1}" -f $visited.Count, $Destinations.Count)
if ($visited.Count -gt 0) { Write-Host ("                           {0}" -f ($visited -join " -> ")) }
Write-Host ("    catalog scans        : {0} (allowed {1})" -f $scanCount, $MaxScans)
Write-Host ("    log errors (ERR/FTL) : {0}" -f $errorLines.Count)
Write-Host ("    log folder           : {0}" -f $LogDirectory)

foreach ($failure in $failures) { Write-Host "    FAIL $failure" -ForegroundColor Red }
foreach ($line in $errorLines) { Write-Host "    $line" -ForegroundColor Red }

if ($failures.Count -eq 0 -and $visited.Count -ne $Destinations.Count) {
    $failures.Add("Only $($visited.Count) of $($Destinations.Count) destinations were visited.")
}
if (-not $existing) {
    $failures.Add("No log file was written to $LogDirectory - logging is broken.")
}
if ($scanCount -gt $MaxScans) {
    $failures.Add("The catalog scanned $scanCount times; a full tour must reuse the first snapshot.")
}
if ($errorLines.Count -gt 0) {
    $failures.Add("The application logged $($errorLines.Count) error(s).")
}

if (-not $KeepOpen -and -not $KeepArtifacts) {
    # The killed process can hold its log file for a moment; a few retries keep
    # cleanup from turning a passing smoke run into a failure.
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
    Write-Host "SMOKE FAILED" -ForegroundColor Red
    exit 1
}

Write-Host "Smoke passed." -ForegroundColor Green
exit 0
