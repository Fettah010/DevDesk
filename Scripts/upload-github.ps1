# ------------------------------------------------------------------------------
# Upload an already-packed Releases/ folder to GitHub Releases.
# Use this after `build-and-release.ps1 -SkipBuild -NoUpload` (or a manual pack)
# when you only want to ship what is on disk.
#
#   .\upload-github.ps1                     # channel + version from the csproj
#   .\upload-github.ps1 -Tag v0.1.1-beta    # explicit tag, validated against it
#   .\upload-github.ps1 -Publish            # published (NOT draft) release
#
# The repository is read from <RepositoryUrl> in DevDesk.csproj: there is no
# second place to keep the slug. vpk is a LOCAL tool (dotnet tool restore).
# Requires: $env:GITHUB_TOKEN (PAT with repo scope).
# ------------------------------------------------------------------------------

param(
    [string]$Tag = "",
    [string]$RepoUrl = "",
    [switch]$Publish,       # without it the release stays a DRAFT and no client sees it
    [switch]$PreRelease     # avoid: GitHubSource(prerelease:false) ignores these
)

$ErrorActionPreference = "Stop"

$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ProjectFile = Join-Path $ProjectRoot "DevDesk.csproj"
$ToolManifest = Join-Path $ProjectRoot ".config\dotnet-tools.json"
$ReleasesDir = Join-Path $ProjectRoot "Releases"

if ([string]::IsNullOrWhiteSpace($RepoUrl)) {
    [xml]$project = Get-Content $ProjectFile -Raw
    $RepoUrl = ($project.Project.PropertyGroup.RepositoryUrl | Where-Object { $_ }) | Select-Object -First 1
    if ([string]::IsNullOrWhiteSpace($RepoUrl)) {
        throw "DevDesk.csproj has no <RepositoryUrl>; it is the only source of the release slug."
    }
    $RepoUrl = $RepoUrl.Trim()
}

$GithubToken = $env:GITHUB_TOKEN
if ([string]::IsNullOrWhiteSpace($GithubToken)) {
    throw "GITHUB_TOKEN is not set. Run: `$env:GITHUB_TOKEN = 'ghp_...'"
}
if (-not (Test-Path $ReleasesDir)) {
    throw "No Releases folder found at $ReleasesDir. Run .\build-and-release.ps1 -SkipBuild -NoUpload first."
}

if ([string]::IsNullOrWhiteSpace($Tag)) {
    [xml]$project = Get-Content $ProjectFile -Raw
    $info = (($project.Project.PropertyGroup.InformationalVersion | Where-Object { $_ }) | Select-Object -First 1).Trim()
    $n = ($info -split '-')[0]
    $s = if ($info.Contains('-')) { ($info -split '-', 2)[1] } else { "" }
    $Tag = if ($s) { "v$n-$s" } else { "v$n" }
}

if ($Tag -notmatch '^v((0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*))(?:-([A-Za-z0-9]+))?$') {
    throw "Tag '$Tag' is not of the form vX.Y.Z[-channel]."
}
$tagVersion = $Matches[1]
$tagSuffix = if ($Matches[5]) { $Matches[5].ToLowerInvariant() } else { "" }
foreach ($component in $tagVersion.Split(".")) {
    $parsedComponent = 0
    if (-not [int]::TryParse(
            $component,
            [System.Globalization.NumberStyles]::None,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsedComponent)) {
        throw "Tag '$Tag' contains a version component outside the supported numeric range."
    }
}

$channel = switch ($tagSuffix) {
    "" { "stable" }
    "beta" { "beta" }
    "dev" { "dev" }
    "alpha" { throw "Channel 'alpha' is retired (roadmap D4). Re-tag as -beta." }
    default { throw "Unknown channel suffix '$tagSuffix' in tag '$Tag'." }
}

$UseLocalTool = Test-Path $ToolManifest
if (-not $UseLocalTool) {
    throw "No .config/dotnet-tools.json. Run 'dotnet new tool-manifest' and 'dotnet tool install vpk --version 1.2.161' (never install it globally)."
}
& dotnet tool restore | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed; cannot run the pinned vpk." }

$vpkArgs = @(
    "upload", "github",
    "--repoUrl", $RepoUrl,
    "--token", $GithubToken,
    "-o", $ReleasesDir,
    "-c", $channel,
    "--tag", $Tag,
    "--releaseName", "DevDesk $tagVersion"
)
if ($Publish) { $vpkArgs += "--publish" }
else { Write-Warning "-Publish was not passed: the release stays a DRAFT and installed clients will not see it." }
if ($PreRelease) {
    Write-Warning "-PreRelease marks the GitHub release as a pre-release; GithubSource(prerelease:false) ignores those."
    $vpkArgs += "--pre"
}

Write-Host "==> Uploading $ReleasesDir to $RepoUrl (channel $channel, tag $Tag)" -ForegroundColor Cyan
& dotnet tool run vpk @vpkArgs
if ($LASTEXITCODE -ne 0) { throw "vpk upload github failed with exit code $LASTEXITCODE" }

Write-Host "==> Done." -ForegroundColor Green
