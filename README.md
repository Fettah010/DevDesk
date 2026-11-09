# DevDesk — Developer Console for Windows 11

[![CI](https://github.com/Fettah010/DevDesk/actions/workflows/ci.yml/badge.svg)](https://github.com/Fettah010/DevDesk/actions/workflows/ci.yml)

A WinUI 3 desktop app that gives a one-screen overview and management for
every project in **C:\Code\projects**, so you don't need the terminal:
dashboard cards, last commits, versions, releases per channel, GitHub Actions,
build/run/commit/tag/push and "new app from template" scaffolding. DevDesk and
the DevTem-WinUI 3 template live together in
**`C:\Code\projects\DevEcosystem`**; other folders there are unrelated.

> **Status: v0.8.0 in progress — Command center (unreleased).** The last
> published release is `v0.7.0-beta`; v0.8.0 expands DevDesk into an always-on
> command center with a command palette, global keyboard map, activity center
> notifications, and system tray integration (see `docs/versions/V0.8.0.md`).
> Everything on screen is either real or clearly labelled with the version that
> will make it real — there are no fake rows, no buttons that look live but
> only apologise, and no invented data. DevDesk scans any number of configured
> roots (branch, dirty state, last commits, version, remote, worktrees) with no
> network, paints cached cards on the next launch, keeps them current through a
> debounced filesystem watcher, searches / sorts / pins / tags projects, shows
> a repository's solutions and projects, reads GitHub releases, workflow runs
> and pull requests, prepares and pushes a release, opens repos in Explorer /
> Visual Studio, builds / runs / publishes / tests any project with a live
> console, and writes a redacted diagnostics bundle. The map is
> **`docs/DEVDESK-ROADMAP.md`**; each version's work order lives in
> **`docs/versions/`**.
>
> Measured and recorded (see `docs/versions/V0.7.0.md` §7): M1 app-side card
> layout 690 ms (< 700 ms), M2 native HWND visibility 479–513 ms (< 400 ms),
> M6 UI-thread delay 67–86 ms vs < 50 ms (XAML page construction remaining
> cost). Passing: M3 8-repo scan 872 ms (< 1.5 s), M4 50-repo scan 1.86 s
> (< 3 s), M5 working set 253 MiB (< 300 MB). M1–M6 are reported, not lowered;
> v0.7.0 ships as beta.

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
- **Scan roots (v0.7.0)**: any number of roots, each with its own depth (1–8),
  enabled flag and ordered ignore globs (`!` re-includes). The Settings page
  lists the rules the scanner really uses and previews whether a relative path
  would be excluded. The default root is the DevEcosystem folder; DevDesk itself
  is never listed as a project — it gets its own page.
- **Warm start from cache (v0.7.0)**: the last snapshot is persisted (schema 2,
  atomic, with visible freshness) and painted before git answers, so a reopen
  shows cards immediately. A debounced filesystem watcher rescans only the
  repository that changed; a watcher overflow falls back to one reconciliation
  scan of that root.
- **Search, filter, sort, saved views (v0.7.0)**: instant in-memory filtering by
  name, path, branch and tag, sorting by name / activity / health / version, and
  saved views that persist.
- **Organization (v0.7.0)**: pins, coloured tags, recently opened ordering,
  archive/hide — stored separately from settings and reversible.
- **Worktrees and submodules (v0.7.0)**: `.git` files (`gitdir:` pointers) are
  parsed and deduplicated by common git directory, so a worktree never appears
  as a second copy of its parent.
- **Workspace expansion (v0.7.0)**: a selected repository's solutions, projects
  and components are discovered on demand and can be opened directly.
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
- **GitHub (released in v0.5.0)**: read-only releases per channel, Actions runs and
  pull requests for a selected local repository, with a token in Windows
  Credential Manager (public data works without one). Every request is bounded
  to the selected repository and only while the page is open; no token is ever
  written to settings or logs.
- **Release from DevDesk (v0.6.0)**: "Prepare release" suggests the next version
  for the chosen channel, drafts release notes, then — after previews and
  confirmations — rewrites the version quartet, commits, tags locally and pushes
  the tag, monitoring the workflow it triggered. It never force-pushes or moves
  an existing tag.
- **Diagnostics**: the last 2000 log events of this launch with a level and text
  filter, follow, copy, and a one-click export bundle
  (`logs/`, `environment.txt`, `settings.redacted.json`, `manifest.json`).
  Credentials are redacted on the way to disk and into the bundle. A
  **Performance** card shows the measured numbers — startup marks (window shown,
  first snapshot), last scan, last update check, last settings write, build
  duration, maximum UI dispatch delay with the operation it was attributed to,
  and working set — each beside the budget it is compared with, plus the git
  executable and version the scanner actually uses.
- **Settings**: theme, scan roots (add/remove/enable, depth, ordered ignore
  rules with a live preview), GitHub token status and sign-out, update channel,
  and a **working** "Check for updates" that reports the real answer — including
  "not installed" when run from `bin\`.
- **Loading**: any operation that may take a while (refresh/rescan, repo-state
  check) shows a progress overlay; the launcher shows a progress window while
  a rebuild runs.
- **Not yet, and labelled as such**: the template wizard (v0.4.0 — the page is a
  preview and creates nothing yet), and the command palette, keyboard map, tray
  and notifications (v0.8.0).

## Tech stack

| Dependency | Version | Job | If removed |
| --- | --- | --- | --- |
| .NET + Windows App SDK | `net10.0-windows10.0.19041.0`, **WASDK 2.6.4** (unpackaged, self-contained) | WinUI 3 shell, Mica, title bar | the app does not exist |
| Windows SDK BuildTools | `10.0.28000.2957` | MSIX/manifest tooling used by Velopack | packaging fails |
| Velopack | `1.2.161` | installer + self-update | no install/update path (dev builds still run) |
| Serilog (+ File) | `4.4.0` / `7.0.0` | logging | Diagnostics goes dark |
| CommunityToolkit.WinUI.* | `8.2.251219` | settings controls, segmented control | two controls re-implemented by hand |

- No external GitHub/git SDK — git is driven through `Services/GitRunner.cs`
  and GitHub through `Services/GitHubApiClient.cs`, both with argument lists /
  bounded requests and no shell interpolation.
- `1.8` is out of servicing (2026-09-24); `2.6.4` is the current stable line.

## Install and update

Releases are published to `https://github.com/Fettah010/DevDesk/releases` (the
`<RepositoryUrl>` in `DevDesk.csproj`; nothing else names the repository).

```powershell
# Publish -> pack -> upload, all of it from the tag (channel comes from the suffix)
dotnet tool restore                                # pinned vpk, no global install
.\Scripts\build-and-release.ps1 -Tag v0.7.0-beta -Publish
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
Services/              # ScanEngine, ProjectCatalog, ProjectRootWatcher, ScanCacheStore,
                       # IgnoreRuleMatcher, PathHygiene, ProjectOrganizationStore,
                       # ProjectListQuery, WorkspaceTargetDiscovery, GitRunner, GitService,
                       # ProjectOpenActions, WindowPlacement, Routes, SingleInstance,
                       # SettingsService, LoggingService, UpdateService, GitHub*, Release*,
                       # BuildService/ActivityCenter/BuildHistory, PerfCounters/StartupTimeline
Pages/                 # Dashboard, Projects, Templates, GitHub, DevDesk (self), Activity, Settings, Diagnostics
ViewModels/            # Dashboard / self / diagnostics / activity state (x:Bind, no code-behind logic)
Scripts/launch-dev.ps1        # Build-if-needed + launch without a terminal (shortcut target)
Scripts/make-shortcut.ps1     # Creates the desktop "DevDesk" shortcut
Scripts/build-and-release.ps1 # Velopack release pipeline (the single source of truth)
Scripts/ci.ps1                # The CI gate locally: Release build + full test suite (x64)
Scripts/smoke-ui.ps1          # Visits all 8 destinations through UI Automation in an isolated data dir
Scripts/verify-shell.ps1      # Proves placement, route restore, min size, single instance
Scripts/capture-page.ps1      # Screenshots one destination for review/evidence
Scripts/benchmark-v07.ps1     # Opt-in v0.7 cache/search/scale measurements
DevDesk.Tests/                # xunit tests — live INSIDE this repo (own project)
docs/DEVDESK-ROADMAP.md       # the map: versions, findings, decisions
docs/versions/                # per-version work orders + evidence log
```

Tests run with `dotnet test DevDesk.Tests -p:Platform=x64` (from this folder).

## License

MIT.
