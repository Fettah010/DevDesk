# ------------------------------------------------------------------------------
# Move all four version fields of DevDesk.csproj together.
#
#   .\bump-version.ps1 -Version 0.1.1 -Suffix beta
#   .\bump-version.ps1 -Version 0.1.1              # keeps the current suffix
#   .\bump-version.ps1 -Version 1.0.0 -Suffix none # stable, no suffix
#
# Rules (roadmap §10.2):
#   - Version/AssemblyVersion/FileVersion/InformationalVersion never diverge.
#   - The suffix is part of InformationalVersion only (0.1.1-beta).
#   - Running the same command twice changes nothing (idempotent).
# ------------------------------------------------------------------------------

param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    # Omit to keep the current suffix; "none" removes it (stable).
    [string]$Suffix = "",

    [string]$Project = ""
)

$ErrorActionPreference = "Stop"

if ([string]::IsNullOrWhiteSpace($Project)) {
    $Project = Join-Path (Join-Path $PSScriptRoot "..") "DevDesk.csproj"
}
$Project = (Resolve-Path $Project).Path

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "Version must look like MAJOR.MINOR.PATCH (got '$Version')."
    exit 2
}

$encoding = [System.Text.UTF8Encoding]::new($false)
$text = [System.IO.File]::ReadAllText($Project, $encoding)
$lines = $text -split "`n"

$propertyRegex = [regex]::new('^\s*<(Version|AssemblyVersion|FileVersion|InformationalVersion)>([^<]*)</\1>\s*$')
$current = @{}
foreach ($line in $lines) {
    $m = $propertyRegex.Match($line)
    if ($m.Success) {
        $current[$m.Groups[1].Value] = $m.Groups[2].Value.Trim()
    }
}

if (-not $current.ContainsKey("Version")) {
    Write-Error "Could not find a <Version> element in $Project."
    exit 3
}

# Suffix: keep the current one unless the caller asked for something.
$effectiveSuffix = $Suffix
if ([string]::IsNullOrWhiteSpace($effectiveSuffix)) {
    $currentInfo = $current["InformationalVersion"]
    $dash = $currentInfo.IndexOf('-')
    $effectiveSuffix = if ($dash -ge 0) { $currentInfo.Substring($dash + 1) } else { "none" }
}

$effectiveSuffix = $effectiveSuffix.Trim().TrimStart('-')
if ($effectiveSuffix -eq "none") {
    $effectiveSuffix = ""
}
elseif ($effectiveSuffix -notmatch '^[a-z][a-z0-9]*$') {
    Write-Error "Suffix must be lowercase letters/digits (or 'none'); got '$effectiveSuffix'."
    exit 2
}

$wanted = [ordered]@{
    Version              = $Version
    AssemblyVersion      = "$Version.0"
    FileVersion          = "$Version.0"
    InformationalVersion = if ($effectiveSuffix) { "$Version-$effectiveSuffix" } else { $Version }
}

$alreadyCurrent = $true
foreach ($key in $wanted.Keys) {
    if ($current[$key] -ne $wanted[$key]) {
        $alreadyCurrent = $false
        break
    }
}

if ($alreadyCurrent) {
    Write-Host "Already at $($wanted.InformationalVersion) - nothing to do." -ForegroundColor Green
    exit 0
}

for ($i = 0; $i -lt $lines.Count; $i++) {
    $m = $propertyRegex.Match($lines[$i])
    if (-not $m.Success) {
        continue
    }

    $name = $m.Groups[1].Value
    if ($wanted.Contains($name)) {
        $indent = $lines[$i].Substring(0, $lines[$i].IndexOf('<'))
        $lines[$i] = "$indent<$name>$($wanted[$name])</$name>"
    }
}

[System.IO.File]::WriteAllText($Project, ($lines -join "`n"), $encoding)

Write-Host "Updated $Project" -ForegroundColor Cyan
foreach ($key in $wanted.Keys) {
    Write-Host ("  {0,-20} {1} -> {2}" -f $key, $(if ($current[$key]) { $current[$key] } else { "(missing)" }), $wanted[$key])
}
exit 0
