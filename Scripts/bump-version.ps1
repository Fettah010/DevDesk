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

if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    Write-Error "Version must look like MAJOR.MINOR.PATCH (got '$Version')."
    exit 2
}
foreach ($component in $Version.Split(".")) {
    $parsedComponent = 0
    if (-not [int]::TryParse(
            $component,
            [System.Globalization.NumberStyles]::None,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsedComponent)) {
        Write-Error "Each version component must fit the supported numeric range."
        exit 2
    }
}

$bytes = [System.IO.File]::ReadAllBytes($Project)
$hasUtf8Bom = $bytes.Length -ge 3 -and
    $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$readEncoding = [System.Text.UTF8Encoding]::new($false)
$writeEncoding = [System.Text.UTF8Encoding]::new($hasUtf8Bom)
$text = [System.IO.File]::ReadAllText($Project, $readEncoding)
$newlineMatch = [regex]::Match($text, "\r\n|\n|\r")
$newline = if ($newlineMatch.Success) { $newlineMatch.Value } else { [Environment]::NewLine }
$lines = [regex]::Split($text, "\r\n|\n|\r")

$propertyRegex = [regex]::new('^\s*<(Version|AssemblyVersion|FileVersion|InformationalVersion)>([^<]*)</\1>\s*$')
$current = @{}
$propertyCounts = @{}
foreach ($line in $lines) {
    $m = $propertyRegex.Match($line)
    if ($m.Success) {
        $name = $m.Groups[1].Value
        $propertyCounts[$name] = 1 + [int]$propertyCounts[$name]
        $current[$name] = $m.Groups[2].Value.Trim()
    }
}

$requiredProperties = @("Version", "AssemblyVersion", "FileVersion", "InformationalVersion")
foreach ($name in $requiredProperties) {
    if (-not $propertyCounts.ContainsKey($name)) {
        Write-Error "Could not find a <$name> element in $Project."
        exit 3
    }
    if ($propertyCounts[$name] -ne 1) {
        Write-Error "Expected exactly one <$name> element in $Project."
        exit 3
    }
}

# Suffix: keep the current one unless the caller asked for something.
$effectiveSuffix = $Suffix
if ([string]::IsNullOrWhiteSpace($effectiveSuffix)) {
    $currentInfo = $current["InformationalVersion"]
    $dash = $currentInfo.IndexOf('-')
    $effectiveSuffix = if ($dash -ge 0) { $currentInfo.Substring($dash + 1) } else { "none" }
}

$effectiveSuffix = $effectiveSuffix.Trim()
if ($effectiveSuffix.StartsWith("-", [System.StringComparison]::Ordinal)) {
    $effectiveSuffix = $effectiveSuffix.Substring(1)
}
if ($effectiveSuffix -eq "none" -or $effectiveSuffix -eq "stable") {
    $effectiveSuffix = ""
}
elseif ($effectiveSuffix -notin @("beta", "dev")) {
    Write-Error "Suffix must be stable, beta, dev, or 'none'; got '$effectiveSuffix'."
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

$updatedText = $lines -join $newline
$directory = [System.IO.Path]::GetDirectoryName($Project)
$temporaryFile = Join-Path $directory (".{0}.{1}.tmp" -f [System.IO.Path]::GetFileName($Project), [guid]::NewGuid().ToString("N"))
$backupFile = Join-Path $directory (".{0}.{1}.bak" -f [System.IO.Path]::GetFileName($Project), [guid]::NewGuid().ToString("N"))
$replacementCompleted = $false

try {
    [System.IO.File]::WriteAllText($temporaryFile, $updatedText, $writeEncoding)
    [System.IO.File]::Replace($temporaryFile, $Project, $backupFile)
    $replacementCompleted = $true
}
finally {
    if ([System.IO.File]::Exists($temporaryFile)) {
        [System.IO.File]::Delete($temporaryFile)
    }
    if ($replacementCompleted -and [System.IO.File]::Exists($backupFile)) {
        [System.IO.File]::Delete($backupFile)
    }
}

Write-Host "Updated $Project" -ForegroundColor Cyan
foreach ($key in $wanted.Keys) {
    Write-Host ("  {0,-20} {1} -> {2}" -f $key, $(if ($current[$key]) { $current[$key] } else { "(missing)" }), $wanted[$key])
}
exit 0
