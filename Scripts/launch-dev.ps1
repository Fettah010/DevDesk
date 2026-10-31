# ------------------------------------------------------------------------------
# Launch DevDesk FAST:
#  * If the app is already built and nothing changed since the last successful
#    build -> start it instantly (no dotnet build at all).
#  * Otherwise build (parallel, quiet) while a small progress window shows, so
#    the user never stares at a frozen desktop.
#
# Wire this to a desktop shortcut (see make-shortcut.ps1).
#
# "Rebuild & relaunch" in the app calls this same script with
# -WaitForProcessId <pid>: the running app cannot rebuild its own locked
# exe/dll, so the launcher waits for that pid to exit first, then builds and
# starts the app through the normal flow.
# ------------------------------------------------------------------------------

param(
    [int]$WaitForProcessId = 0
)

$ErrorActionPreference = "Stop"

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$root      = Split-Path -Parent $scriptDir   # DevDesk project root
$exe       = Join-Path $root "bin\x64\Debug\net10.0-windows10.0.19041.0\DevDesk.exe"
$stamp     = Join-Path $root "obj\x64\Debug\launch.stamp"
$stampExit = Join-Path $root "obj\x64\Debug\launch-exit.code"
$buildLog  = Join-Path $root "obj\x64\Debug\launch-build.log"
$traceLog  = Join-Path $root "obj\x64\Debug\launch-trace.log"

$skipSegments = @('bin', 'obj', '.git', '.vs', 'Logs', 'Releases', 'ReleasesLocal')

function Trace-Log([string]$Message) {
    Add-Content -LiteralPath $traceLog -Value ("[{0}] {1}" -f (Get-Date -Format 'HH:mm:ss'), $Message) -ErrorAction SilentlyContinue
}

function Get-NewestSourceWriteTime {
    param([string]$Dir)
    $newest = [datetime]::MinValue
    Get-ChildItem -LiteralPath $Dir -Recurse -File -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $rel = $_.FullName.Substring($Dir.Length).TrimStart('\')
            if ($rel -match '(^|[\\/])(bin|obj|\.git|\.vs|Logs|Releases|ReleasesLocal)([\\/]|$)') { return $false }
            $first = ($rel -split '[\\/]')[0]
            return $skipSegments -notcontains $first
        } |
        ForEach-Object {
            if ($_.LastWriteTimeUtc -gt $newest) { $newest = $_.LastWriteTimeUtc }
        }
    return $newest
}

# Is the built app current? (exe + stamp exist, and no source is newer than
# the stamp written after the last successful build.)
function Test-AppCurrent {
    if (-not (Test-Path -LiteralPath $exe)) { return $false }
    $newestSource = Get-NewestSourceWriteTime $root

    # No stamp yet (e.g. the app was built by hand): fall back to exe-vs-sources
    # so we don't do a pointless rebuild right after a manual `dotnet build`.
    if (-not (Test-Path -LiteralPath $stamp)) {
        return (Get-Item -LiteralPath $exe).LastWriteTimeUtc -ge $newestSource
    }

    $stampTime = (Get-Item -LiteralPath $stamp).LastWriteTimeUtc
    if ((Get-Item -LiteralPath $exe).LastWriteTimeUtc -gt $stampTime) { return $false }
    return $newestSource -le $stampTime
}

function Start-App {
    if (Test-Path -LiteralPath $exe) {
        Start-Process -FilePath $exe
    } else {
        Write-Host "Built exe not found: $exe" -ForegroundColor Red
        Start-Sleep -Seconds 3
        exit 1
    }
}

# "Rebuild & relaunch" (v0.3.0): the app starts this script and closes. Wait for
# the old window to disappear before touching bin\ — the running exe and dll are
# locked, so a build started any earlier fails with MSB3027. A pid that never
# exits ends the relaunch instead of stacking a second window (bounded: 30 s).
if ($WaitForProcessId -gt 0) {
    $previous = Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue
    if ($null -ne $previous) {
        Trace-Log "relaunch: waiting for pid $WaitForProcessId to exit"
        if (-not $previous.WaitForExit(30000)) {
            Trace-Log "relaunch: pid $WaitForProcessId still running after 30s - nothing started"
            exit 2
        }
    }

    Trace-Log "relaunch: pid $WaitForProcessId is gone"
}

# Runs `dotnet build` in a background job (robust exit code + no pipe deadlock)
# while the splash keeps pumping. The job writes the exit code to $stampExit;
# the main thread polls for that file.
function Invoke-Build {
    param()
    New-Item -ItemType Directory -Path (Split-Path -Parent $buildLog) -Force | Out-Null
    Remove-Item -LiteralPath $buildLog, "$buildLog.err", $stampExit -ErrorAction SilentlyContinue

    $job = Start-Job -ArgumentList $root, $buildLog, $stampExit -ScriptBlock {
        param($root, $buildLog, $stampExit)
        try {
            Push-Location $root
            $e = -1
            & dotnet build DevDesk.csproj -c Debug -p:Platform=x64 -m -nologo 2>&1 |
                Out-File -LiteralPath $buildLog -Encoding utf8
            $e = $LASTEXITCODE
            Set-Content -LiteralPath $stampExit -Value $e
        }
        catch {
            Set-Content -LiteralPath $stampExit -Value "1"
        }
    }

    $watchdog = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not (Test-Path -LiteralPath $stampExit)) {
        if ($watchdog.Elapsed.TotalSeconds -gt 180) {
            Trace-Log "build timed out"
            Stop-Job $job -ErrorAction SilentlyContinue | Out-Null
            Set-Status "Build timed out"
            Start-Sleep -Seconds 2
            exit 1
        }
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 50
    }

    $code = [int](Get-Content -LiteralPath $stampExit -Raw).Trim()
    Stop-Job $job -ErrorAction SilentlyContinue | Out-Null
    Remove-Job $job -Force -ErrorAction SilentlyContinue | Out-Null
    Remove-Item -LiteralPath $stampExit -ErrorAction SilentlyContinue
    return $code
}

if (Test-AppCurrent) {
    Trace-Log "app is current - starting directly"
    Start-App
    exit 0
}

Trace-Log "app is stale - building"

# --- Show a small progress window while the build runs -----------------------
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$splash = New-Object System.Windows.Forms.Form
$splash.Text = "DevDesk"
$splash.StartPosition = "CenterScreen"
$splash.ClientSize = [System.Drawing.Size]::new(380, 104)
$splash.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedDialog
$splash.MaximizeBox = $false; $splash.MinimizeBox = $false
$splash.ShowInTaskbar = $false
$splash.ControlBox = $false
$splash.BackColor = [System.Drawing.Color]::FromArgb(245, 246, 250)

$label = New-Object System.Windows.Forms.Label
$label.Text = "Building DevDesk…"
$label.SetBounds(18, 14, 344, 24)
$label.Font = [System.Drawing.Font]::new("Segoe UI", 10, [System.Drawing.FontStyle]::Regular)
$splash.Controls.Add($label)

$bar = New-Object System.Windows.Forms.ProgressBar
$bar.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
$bar.SetBounds(18, 46, 344, 18)
$splash.Controls.Add($bar)

$splash.Show()
$splash.Refresh()

function Set-Status([string]$Text) {
    $label.Text = $Text
    $splash.Refresh()
    [System.Windows.Forms.Application]::DoEvents()
}

Set-Status "Rebuilding (sources changed)…"

try {
    $code = Invoke-Build
    Trace-Log "dotnet build exited $code"

    if ($code -ne 0) {
        # No MessageBox here — a failed hidden build must never block waiting
        # for a click nobody can make. The splash shows the failure briefly.
        Set-Status "Build failed (exit $code)"
        Start-Sleep -Seconds 3
        exit $code
    }

    # Remember this successful build so the next launch skips the build.
    Set-Content -LiteralPath $stamp -Value (Get-Date).ToString("o")
    Trace-Log "stamp written"

    Set-Status "Launching…"
    Start-App
    Start-Sleep -Milliseconds 400
}
finally {
    Trace-Log "closing splash"
    $splash.Close()
    $splash.Dispose()
}