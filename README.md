# DevDesk — Developer Console for Windows 11

[![CI](https://github.com/Fettah010/DevDesk/actions/workflows/ci.yml/badge.svg)](https://github.com/Fettah010/DevDesk/actions/workflows/ci.yml)

A WinUI 3 desktop app that gives a one-screen overview and management for
every project in **C:\Code\projects**, so you don't need the terminal:
dashboard cards, last commits, versions, releases per channel, GitHub Actions,
build/run/commit/tag/push and "new app from template" scaffolding. DevDesk and
the DevTem-WinUI 3 template live together in
**`C:\Code\projects\DevEcosystem`**; other folders there are unrelated.

> **Status: v0.3.0 in progress — Build & run cockpit.** v0.1.0 "Trustworthy
> base" is published on GitHub (v0.2.0 was completed locally and folded into
> this work; it was never tagged). Everything on screen is either real or
> clearly labelled with the version that will make it real — there are no fake
> rows, no buttons that look live but only apologise, and no invented data. Today
> DevDesk scans every repo inside `DevEcosystem` (branch, dirty state, last
> commits, version, remote) with no network, opens repos in Explorer / Visual
> Studio, remembers its window and route, keeps one instance per session, writes
> a redacted diagnostics bundle, and builds / runs / publishes / tests any
> project with a live searchable console, clickable errors and a real build
> queue. The map is **`docs/DEVDESK-ROADMAP.md`**; each version's work order
> lives in **`docs/versions/`**.

## Run it — no terminal (double-click)

There is a **`DevDesk`** shortcut on the desktop: it launches the app, so you
never type `dotnet build` / `dotnet run`. Opening it is snappy (< 1 s) when
the app is already built — the launcher only rebuilds when a source file
actually changed (a small progress window shows during the rebuild), then
reuses that build on every reopen.

- `Scripts\launch-dev.ps1` — build (only when needed) + start.
- `Scripts\make-shortcut.ps1` — (re)creates the desktop shortcut; run it if
  the link is ever missing or the project has moved.

Manual alternative:

```powershell
dotnet build -c Debug -p:Platform=x64
dotnet run
```

### What works right now

- **Theme**: sea-blue accent, light / dark / system, persisted across launches
  (title-bar brightness button or Settings → Appearance).
- **Navigation**: Dashboard, Projects, Templates, GitHub, DevDesk, Activity, Settings,
  Diagnostics — the last page you were on is reopened next launch.
- **Window**: size and position are remembered and always restored onto a
  screen that still exists; minimum 960×600; a second double-click focuses the
  window that is already open instead of starting another copy.
- **Dashboard**: summary cards (projects / clean / attention / remotes) and the
  project-card grid (branch, status, last commits, version pill, remote and
  dirty badges) — scanned live from the DevEcosystem folder, never DevDesk
  itself. **Click a card** to open the repo in Explorer or Visual Studio.
- **Projects**: two-pane list + detail. Overview, working tree changes, commit
  history, branches, remotes and local tags read the selected repository.
  Stage/unstage, commit/amend, fetch, fast-forward pull, push, branch and local
  tag actions show a command preview before running; destructive actions require
  typed confirmation. Conflicts can be inspected, opened in Visual Studio,
  continued or aborted. The **Build** tab builds, runs and publishes the
  selected project with a live console (search, follow-tail, copy, save),
  clickable MSBuild problems, a persisted "last build" badge, and **Open
  output** after a publish (the folder MSBuild reported).
- **DevDesk**: this app's own workspace — real repo state (branch, version,
  commits) and its components (tests, roadmap, scripts, CI workflow), with
  "open in Explorer", "open roadmap", **Build**, **Run tests** and **Rebuild &
  relaunch** (which closes the window, lets the launcher rebuild if a source
  changed, and reopens the new build).
- **Activity**: every build-family operation of this session plus the stored
  history — status, duration, exit code, first error, log path — with cancel,
  retry and open-log. Builds run one at a time; the rest queue and say so. A
  running operation also appears in the title bar with its elapsed time and a
  Stop button, and in the footer flyout.
- **Diagnostics**: the last 2000 log events of this launch with a level and text
  filter, follow, copy, and a one-click export bundle
  (`logs/`, `environment.txt`, `settings.redacted.json`, `manifest.json`).
  Credentials are redacted on the way to disk and into the bundle. A
  **Performance** card shows the measured numbers — startup marks (window shown,
  first snapshot), last scan, last update check, last settings write, build
  duration and working set — each beside the budget it is compared with.
- **Settings**: theme, scan roots and ignore list (read-only, what the scanner
  actually uses), update channel, and a **working** "Check for updates" that
  reports the real answer — including "not installed" when run from `bin\`.
- **Loading**: any operation that may take a while (refresh/rescan, repo-state
  check) shows a progress overlay; the launcher shows a progress window while
  a rebuild runs.
- **Not yet, and labelled as such**: the template
  wizard (v0.4.0), GitHub runs & releases and
  the token (v0.5.0), filtering and multiple scan roots (v0.7.0).

## Tech stack

| Dependency | Version | Job | If removed |
| --- | --- | --- | --- |
| .NET + Windows App SDK | `net10.0-windows10.0.19041.0`, **WASDK 2.6.4** (unpackaged, self-contained) | WinUI 3 shell, Mica, title bar | the app does not exist |
| Windows SDK BuildTools | `10.0.28000.2957` | MSIX/manifest tooling used by Velopack | packaging fails |
| Velopack | `1.2.161` | installer + self-update | no install/update path (dev builds still run) |
| Serilog (+ File) | `4.4.0` / `7.0.0` | logging | Diagnostics goes dark |
| CommunityToolkit.WinUI.* | `8.2.251219` | settings controls, segmented control | two controls re-implemented by hand |

- No external GitHub/git SDK — git is driven through `Services/GitRunner.cs`
  (argument lists, no shell interpolation); GitHub REST arrives in 0.5.0.
- `1.8` is out of servicing (2026-09-24); `2.6.4` is the current stable line.

## Install and update

Releases are published to `https://github.com/Fettah010/DevDesk/releases` (the
`<RepositoryUrl>` in `DevDesk.csproj`; nothing else names the repository).

```powershell
# Publish -> pack -> upload, all of it from the tag (channel comes from the suffix)
dotnet tool restore                                # pinned vpk, no global install
.\Scripts\build-and-release.ps1 -Tag v0.1.0-beta -Publish
```

- **The installer is unsigned.** Windows SmartScreen will show *"Windows
  protected your PC"* on first run of `DevDesk-win-Setup.exe`: choose **More
  info → Run anyway**. There is no code-signing certificate yet; this is
  documented rather than worked around.
- **Channels** are `stable`, `beta`, `dev`; `beta` is the default before 1.0. A
  tag decides the channel: `v1.2.3` → stable, `v1.2.3-beta` → beta,
  `v1.2.3-dev` → dev. `alpha` is retired and refused.
- **A release must be published, not a draft.** `vpk upload github` leaves a
  draft unless `--publish` is passed, and no client can see a draft — the script
  warns when it would leave one.
- **Never pass `--pre`.** GitHub's "prerelease" flag is not the Velopack
  channel; `GithubSource(prerelease: false)` ignores prerelease-marked releases.
- After installing once, the app checks the feed in the background and offers a
  restart when an update is ready. Running from `bin\` (not installed) never
  touches the network.

## Quality gate (CI)

Every push and pull request to `main` runs `.github/workflows/ci.yml`: build in
**Release**, then the full test suite, both `-p:Platform=x64` (WinUI refuses
AnyCPU). The SDK comes from `global.json` (`10.0.403`, `rollForward:
latestFeature`), so a machine with 10.0.5xx still builds and CI and local runs
agree on the compiler. A tag cannot publish when that gate is red —
`release.yml` runs the same commands as a `verify` job that `release` depends on,
and refuses to republish a version that already exists on the feed.

Run the same thing locally (this is the whole point of the script — no
half-remembered flags):

```powershell
.\Scripts\ci.ps1                                  # Release build + all tests, TRX in artifacts\
.\Scripts\ci.ps1 -Configuration Debug
.\Scripts\ci.ps1 -Filter "FullyQualifiedName~DocsHonestyTests"
```

- **Required status check for branch protection: `Build & test`** (the CI job
  name). Enabling that protection is a repository owner action (Settings →
  Branches), not something a file in this repo can turn on.
- A failing test leaves `artifacts/test-results/test-results.trx` locally and a
  `test-results` artifact on the run, so a red gate comes with evidence.

## Repo layout

```
MainWindow.xaml(.cs)   # Shell: custom title bar + NavigationView + theme toggle
App.xaml               # Sea-blue theme dictionaries (light/dark) + shared card style
Models/ProjectModel.cs # Project / workspace card model + CommitInfo + ComponentInfo
Services/              # ScanEngine, ProjectCatalog, AppServices, PathHygiene,
                       # GitRunner, ProjectOpenActions, WindowPlacement, Routes,
                       # SingleInstance, SettingsService, LoggingService, UpdateService
Pages/                 # Dashboard, Projects, Templates, GitHub, DevDesk (self), Settings, Diagnostics
ViewModels/            # Dashboard / self / diagnostics state (x:Bind, no code-behind logic)
Scripts/launch-dev.ps1        # Build-if-needed + launch without a terminal (shortcut target)
Scripts/make-shortcut.ps1     # Creates the desktop "DevDesk" shortcut
Scripts/build-and-release.ps1 # Velopack release pipeline (the single source of truth)
Scripts/ci.ps1                # The CI gate locally: Release build + full test suite (x64)
Scripts/smoke-ui.ps1          # Visits all 7 pages through UI Automation in an isolated data dir
Scripts/verify-shell.ps1      # Proves placement, route restore, min size, single instance
DevDesk.Tests/                # xunit tests — live INSIDE this repo (own project)
docs/DEVDESK-ROADMAP.md       # the map: versions, findings, decisions
docs/versions/                # per-version work orders + evidence log
```

Tests run with `dotnet test DevDesk.Tests -p:Platform=x64` (from this folder).

## License

MIT.
