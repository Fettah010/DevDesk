# ------------------------------------------------------------------------------
# Build + publish a full Velopack release and upload it to GitHub Releases.
#
#   .\build-and-release.ps1                                  # release the version in the csproj
#   .\build-and-release.ps1 -Tag v0.1.1-beta                 # release a specific tag
#   .\build-and-release.ps1 -SkipBuild -NoUpload             # pack only, no feed changes
#   .\build-and-release.ps1 -CheckOnly -Tag v0.1.0-beta      # validate and print, do nothing
#
# The version and channel come from the tag (roadmap O2):
#   v1.2.3        -> version 1.2.3, channel stable
#   v1.2.3-beta   -> version 1.2.3, channel beta
#   v1.2.3-dev    -> version 1.2.3, channel dev
# An explicit -Version/-Channel must agree with the tag; mismatches are refused.
#
# Prerequisites:
#   - .NET SDK 10 (global.json pins it)
#   - vpk as a LOCAL tool:  dotnet tool restore   (manifest: .config/dotnet-tools.json)
#   - GITHUB_TOKEN with `repo` scope, unless -NoUpload
#
# This script is the single source of truth for releases: it is used both
# locally and by the GitHub Actions workflow. Output: Releases/.
# ------------------------------------------------------------------------------

param(
    [string]$Version = "",
    [string]$Channel = "auto",
    [string]$Runtime = "win-x64",
    [string]$ReleaseNotes = "",
    [switch]$SkipBuild,
    [switch]$Download,
    [switch]$Publish,
    [switch]$PreRelease,          # avoid: a prerelease-marked feed is invisible to GitHubSource(prerelease:false)
    [string]$Tag = "",
    [string]$RepoUrl = "",        # defaults to <RepositoryUrl>; the only place the slug lives
    [switch]$NoUpload,
    [switch]$CheckOnly
)

$ErrorActionPreference = "Stop"

$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ProjectFile = Join-Path $ProjectRoot "DevDesk.csproj"
$ToolManifest = Join-Path $ProjectRoot ".config\dotnet-tools.json"

$AppId     = "DevDesk"
$MainExe   = "DevDesk.exe"
$PublishDir  = Join-Path $ProjectRoot "bin\Release\net10.0-windows10.0.19041.0\$Runtime\publish"
$ReleasesDir = Join-Path $ProjectRoot "Releases"

# ──────────────────────────────────────────────────────────────────────────────
# 1) Resolve version + channel from the tag, the csproj, or both.
# ──────────────────────────────────────────────────────────────────────────────

[xml]$project = Get-Content $ProjectFile -Raw
$declaredInfo = ($project.Project.PropertyGroup.InformationalVersion | Where-Object { $_ }) | Select-Object -First 1
$declaredRepo = ($project.Project.PropertyGroup.RepositoryUrl | Where-Object { $_ }) | Select-Object -First 1

if ([string]::IsNullOrWhiteSpace($declaredInfo)) {
    throw "DevDesk.csproj has no <InformationalVersion>; run Scripts/bump-version.ps1 first."
}
if ([string]::IsNullOrWhiteSpace($declaredRepo)) {
    throw "DevDesk.csproj has no <RepositoryUrl>; it is the only source of the release feed."
}

$declaredInfo = $declaredInfo.Trim()
$declaredNumeric = ($declaredInfo -split '-')[0]
$declaredSuffix = if ($declaredInfo.Contains('-')) { ($declaredInfo -split '-', 2)[1] } else { "" }

if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = if ($declaredSuffix) { "v$declaredNumeric-$declaredSuffix" } else { "v$declaredNumeric" }
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

$resolvedChannel = switch ($tagSuffix) {
    ""       { "stable" }
    "beta"   { "beta" }
    "dev"    { "dev" }
    "alpha"  { throw "Channel 'alpha' is retired (roadmap D4). Re-tag as -beta." }
    default  { throw "Unknown channel suffix '$tagSuffix' in tag '$Tag'." }
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = $tagVersion
}
elseif ($Version -ne $tagVersion) {
    throw "Tag '$Tag' says version $tagVersion but -Version says $Version. Move the version with bump-version.ps1 first."
}

if ($Version -ne $declaredNumeric) {
    throw "Version $Version does not match the csproj ($declaredInfo). Run Scripts/bump-version.ps1 -Version $Version."
}

if ($Channel -ne "auto") {
    if ($Channel -notin @("stable", "beta", "dev")) {
        throw "Channel '$Channel' is not one of stable|beta|dev."
    }
    if ($Channel -ne $resolvedChannel) {
        throw "Tag '$Tag' selects channel '$resolvedChannel' but -Channel says '$Channel'."
    }
}
$Channel = $resolvedChannel

$GithubRepoUrl = if ([string]::IsNullOrWhiteSpace($RepoUrl)) { $declaredRepo.Trim() } else { $RepoUrl.Trim() }
$GithubToken   = $env:GITHUB_TOKEN

Write-Host "==> Velopack release build" -ForegroundColor Cyan
Write-Host "    AppId     : $AppId"
Write-Host "    Version   : $Version ($declaredInfo)"
Write-Host "    Channel   : $Channel  (tag $Tag)"
Write-Host "    Runtime   : $Runtime"
Write-Host "    Repo      : $GithubRepoUrl"
Write-Host ""

if ($CheckOnly) {
    Write-Host "CheckOnly: version=$Version channel=$Channel tag=$Tag repo=$GithubRepoUrl"
    exit 0
}

# ──────────────────────────────────────────────────────────────────────────────
# 2) Build, pack, upload. vpk is a pinned local tool, never a global install.
# ──────────────────────────────────────────────────────────────────────────────

$UseLocalTool = Test-Path $ToolManifest
if ($UseLocalTool) {
    & dotnet tool restore | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed; cannot run the pinned vpk." }
}

function Invoke-Vpk {
    param([string[]]$VpkArgs)
    # Out-Host keeps vpk's output visible without polluting the function's
    # return value: without it, $packCode would be an array of log lines and
    # every successful pack would look like a failure.
    if ($UseLocalTool) {
        & dotnet tool run vpk @VpkArgs | Out-Host
    }
    else {
        Write-Warning "No .config/dotnet-tools.json - falling back to 'vpk' on PATH (unpinned)."
        & vpk @VpkArgs | Out-Host
    }
    return $LASTEXITCODE
}

# 1) Publish a self-contained, unpackaged build (no trimming, no single-file).
if (-not $SkipBuild) {
    Write-Host "==> dotnet publish (self-contained, WinUI + WinAppSDK bundled)" -ForegroundColor Cyan
    & dotnet publish $ProjectFile `
        -c Release -r $Runtime `
        --self-contained true `
        /p:PublishSingleFile=false `
        /p:PublishTrimmed=false `
        /p:WindowsAppSDKSelfContained=true `
        /p:EnableMsixTooling=true

    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
}

if (-not (Test-Path (Join-Path $PublishDir $MainExe))) {
    throw "Publish output not found at $PublishDir. Build did not produce $MainExe"
}

# (Optional) download the previous release so `vpk pack` can build delta packages.
if ($Download -and -not [string]::IsNullOrWhiteSpace($GithubToken)) {
    Write-Host "`n==> vpk download github (previous release for delta generation)" -ForegroundColor Cyan
    try {
        $code = Invoke-Vpk @("download", "github", "--repoUrl", $GithubRepoUrl, "--token", $GithubToken, "-o", $ReleasesDir, "-c", $Channel)
        if ($code -ne 0) {
            Write-Warning "vpk download exited with $code - continuing without deltas."
        }
    }
    catch {
        Write-Warning "vpk download failed: $_ - continuing without deltas."
    }
}

Write-Host "`n==> vpk pack" -ForegroundColor Cyan
$packArgs = @(
    "pack",
    "-u", $AppId,
    "-v", $Version,
    "-p", $PublishDir,
    "-o", $ReleasesDir,
    "-c", $Channel,
    "-r", $Runtime,
    "-e", $MainExe,
    "--packTitle", "DevDesk",
    "--packAuthors", "Fettah"
)
$packIcon = Join-Path $ProjectRoot "Assets\app.ico"
if (Test-Path $packIcon) {
    $packArgs += @("--icon", $packIcon)
}
if (-not [string]::IsNullOrWhiteSpace($ReleaseNotes)) {
    if (-not (Test-Path $ReleaseNotes)) { throw "Release notes file not found: $ReleaseNotes" }
    $packArgs += @("--releaseNotes", $ReleaseNotes)
}
$packCode = Invoke-Vpk $packArgs
if ($packCode -ne 0) { throw "vpk pack failed with exit code $packCode" }

# 3) Upload. A draft release is invisible to every client, so say so loudly.
Write-Host "`n==> vpk upload github" -ForegroundColor Cyan
if ($NoUpload) {
    Write-Host "NoUpload: artefacts are ready in $ReleasesDir (feed untouched)." -ForegroundColor Yellow
    return
}
if ([string]::IsNullOrWhiteSpace($GithubToken)) {
    Write-Warning "GITHUB_TOKEN is not set - skipping upload. Release is ready in $ReleasesDir"
    Write-Host "Run .\upload-github.ps1 after exporting GITHUB_TOKEN." -ForegroundColor Yellow
    return
}

$uploadArgs = @(
    "upload", "github",
    "--repoUrl", $GithubRepoUrl,
    "--token", $GithubToken,
    "-o", $ReleasesDir,
    "-c", $Channel,
    "--tag", $Tag,
    "--releaseName", "DevDesk $Version"
)
if ($Publish)    { $uploadArgs += "--publish" }
else { Write-Warning "-Publish was not passed: the release stays a DRAFT and installed clients will not see it." }
if ($PreRelease) {
    Write-Warning "-PreRelease marks the GitHub release as a pre-release; GitHubSource(prerelease:false) ignores those."
    $uploadArgs += "--pre"
}
$uploadCode = Invoke-Vpk $uploadArgs
if ($uploadCode -ne 0) { throw "vpk upload github failed with exit code $uploadCode" }

Write-Host "`n==> Done. DevDesk $Version ($Channel) published to $GithubRepoUrl" -ForegroundColor Green
