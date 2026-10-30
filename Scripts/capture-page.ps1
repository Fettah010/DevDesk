# ------------------------------------------------------------------------------
# Capture one page of the running app to a PNG, for the evidence log.
#
#   .\Scripts\capture-page.ps1 -Destination Settings
#   .\Scripts\capture-page.ps1 -Destination DevDesk -Out D:\shots\self.png
#
# The window cannot be focused through UI Automation ("Target element cannot
# receive focus" on a WinUI window), so the screenshot is taken from the element
# bounding rectangle instead. The window is brought forward first, otherwise a
# window that happens to sit behind another one is captured occluded.
# ------------------------------------------------------------------------------

param(
    [Parameter(Mandatory = $true)]
    [string]$Destination,
    [string]$Out = "",
    [string]$Exe = "",
    [int]$SettleMs = 1600
)

$ErrorActionPreference = "Stop"

$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($Exe)) {
    $Exe = Join-Path $ProjectRoot "bin\x64\Debug\net10.0-windows10.0.19041.0\DevDesk.exe"
}
if (-not (Test-Path $Exe)) {
    throw "DevDesk.exe not found at $Exe. Build it first: dotnet build DevDesk.csproj -p:Platform=x64"
}
if ([string]::IsNullOrWhiteSpace($Out)) {
    $slug = $Destination.ToLowerInvariant()
    $Out = Join-Path "D:\Windows\Temp\opencode" "devdesk-$slug.png"
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
# AppActivate brings the window forward without P/Invoke: Add-Type of C# source
# fails on this machine (csc cannot find the temp file it writes), so a
# [DllImport("user32.dll")] helper is not an option here.
Add-Type -AssemblyName Microsoft.VisualBasic

$process = Start-Process -FilePath $Exe -PassThru

try {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $windowCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $process.Id)

    $window = $null
    $deadline = (Get-Date).AddSeconds(40)
    while ($null -eq $window -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        if ($process.HasExited) { throw "DevDesk exited during startup." }
        $window = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $windowCondition)
    }
    if ($null -eq $window) { throw "The DevDesk window never appeared within 40 s." }

    Start-Sleep -Milliseconds $SettleMs

    $itemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $items = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $itemCondition)

    $item = $null
    foreach ($candidate in $items) {
        if ($candidate.Current.Name -eq $Destination) { $item = $candidate; break }
    }
    if ($null -eq $item) { throw "Navigation item '$Destination' was not found." }

    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds $SettleMs

    $process.Refresh()
    try { [Microsoft.VisualBasic.Interaction]::AppActivate($process.Id) | Out-Null } catch { }
    Start-Sleep -Milliseconds 400

    $rect = $window.Current.BoundingRectangle
    if ($rect.Width -lt 1 -or $rect.Height -lt 1) { throw "The window has no drawable area." }

    $bitmap = New-Object System.Drawing.Bitmap([int]$rect.Width, [int]$rect.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen([int]$rect.X, [int]$rect.Y, 0, 0, $bitmap.Size)
    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()

    Write-Host "captured $Destination -> $Out ($([int]$rect.Width)x$([int]$rect.Height))"
}
finally {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(10000) | Out-Null
    }
}
