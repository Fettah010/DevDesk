# AGENTS.md

Guidance for AI agents and developers working on this repository. Read this
first before making changes.

## What this is

**DevDesk** — a WinUI 3 developer console that manages everything in
`C:\Code\projects` (branch, version, last commits, releases per channel,
GitHub Actions, build/run) so the terminal is no longer needed.

**Current status: v0.8.0 "Command center" in planning; `v0.7.0-beta` is the last
published release.** See `docs\versions\V0.8.0.md` for the work order. The shell,
sea-blue theme (light/dark), navigation, the **ScanEngine** (git state + versions
for every repo under any number of configured roots, with worktree identity),
real open-in-Explorer / open-in-VS actions, window placement + single instance,
the Diagnostics log/export pipeline and the **performance instrumentation**
(startup marks, per-operation timings, budgets on screen) are real and working.
Projects has local Git reads and previewed, confirmed write operations, and the
app builds, runs and publishes any project with a live searchable console,
clickable MSBuild problems, a process-wide build queue (Activity page), and
persisted build history behind the badges on Dashboard and Projects. Since
v0.5/v0.6 the app also reads GitHub releases/runs/PRs and prepares + pushes a
release. Since v0.7.0 it caches snapshots, watches roots, searches/sorts/pins/
tags, and expands a repository's solutions and projects. Everything that is not
built yet is **disabled with the version that brings it, or an empty state naming
it** — no fake rows, no buttons that look live but only apologise. The template
wizard (TemplateService → v0.4.0) and the v0.8.0 command centre are labelled, not
faked. The map is
`docs\DEVDESK-ROADMAP.md`; each version's work order and evidence log is in
`docs\versions\`.

The v0.7.0 work order is now being implemented on `main`; it is not release
ready. Its acceptance gaps and evidence are tracked in `docs\versions\V0.7.0.md`.

## Ecosystem layout (this app does not live alone)

Everything related lives in **`C:\Code\projects\DevEcosystem`**; other folders
in `C:\Code\projects` are unrelated and must not be touched.

```
C:\Code\projects\DevEcosystem\
  DevDesk\              # THIS app (own local git repo)
    DevDesk.Tests\      # xunit tests — inside the DevDesk repo, own project
    DevDesk.UiTests\    # opt-in FlaUI UI tests; not part of headless CI
    docs\DEVDESK-ROADMAP.md  # the map (versions, findings, decisions)
    docs\versions\      # per-version work orders + evidence log
  DevTem WinUI 3\       # the template (own git repo, remote: Fettah010/winui-3-easy-template)
```

When scaffolding "new apps from the template", new projects go next to the
template inside `DevEcosystem\` (see TemplatesPage default location).

### The scan model (implemented by `Services/ScanEngine.cs`)

- Scan root = `DevEcosystem`, depth 2 (stop descending once a repo is found).
- DevDesk is **never** reported as a scanned project — it becomes the self
  workspace (`ScannerResult.Self`), fed to the dedicated page
  (`Pages/DevDeskPage`).
- A folder counts as a project if it is a git repo **or** holds any
  `*.sln/*.slnx/*.*proj` marker; hygiene folders (bin/obj/.vs/Releases/…) and
  dot-folders are skipped. Git state is read-only (never fetch/push), so a
  scan is fast, local, and never fails because of the network.
- One scan per session, shared through `ProjectCatalog` (`AppServices.Catalog`):
  pages ask for `GetOrRefreshAsync()` and **join** the in-flight scan instead of
  starting their own (`smoke-ui.ps1` asserts exactly one scan for 8 page visits).

## Running DevDesk — NO TERMINAL (the shortcut is the point)

- The user should double-click the **`DevDesk`** desktop shortcut and the
  window opens. Never ask them to type `dotnet run`.
- The shortcut runs `DevDesk\Scripts\launch-dev.ps1` (hidden PowerShell).
  **It does NOT rebuild every time**: a stamp file (`obj\...\launch.stamp`,
  written after each successful build) is compared against source timestamps.
  Reopening when nothing changed starts the exe directly — measured **< 1 s**.
- Only when a source actually changed does it `dotnet build` (parallel `-m`,
  quiet, output to `obj\...\launch-build.log`) while a **small progress
  window** stays visible; it removes itself when the window is up.
- **`Scripts\make-shortcut.ps1` creates/refreshes that shortcut**
  (default name `DevDesk`). Re-run it if paths change or the link breaks.

### Shortcut lifecycle (important for agents)

- The `.lnk` is created **once** by `make-shortcut.ps1` and stores
  **absolute** paths (powershell → `launch-dev.ps1`, icon `Assets\app.ico`).
- It is **NOT (re)created manually or by the build** on every launch — the
  launcher never touches the shortcut. Do not regenerate the shortcut as part
  of builds, tests, launches, or any automated flow; the link stays valid for
  as long as the repo lives at that absolute path.
- Re-run `make-shortcut.ps1` **only** when the project moves to a new location
  or the `.lnk` is missing/broken. That is an occasional, manual step — the
  normal "open the app" flow is just double-clicking the existing shortcut.
- If a future change ever auto-creates the shortcut (e.g. on first run),
  keep it a one-time setup action — never per-launch.
- Guard these scripts when refactoring: if `launch-dev.ps1` or
  `make-shortcut.ps1` stop working, the whole "no terminal" promise breaks.
  Known trap: `Start-Process` does NOT surface the child `ExitCode` in this
  PowerShell — the launcher runs the build in a `Start-Job` and reads the code
  from a file. Keep that pattern; never "simplify" back to `Start-Process` +
  `.ExitCode`.

### Manual build / run / verify (when the shortcut is not an option)

```powershell
dotnet build -c Debug -p:Platform=x64
.\bin\x64\Debug\net10.0-windows10.0.19041.0\DevDesk.exe   # or dotnet run
```

## Tests

- Headless tests live in **`DevDesk\DevDesk.Tests`** — inside the DevDesk repo (own
  project), by design, since "every project owns its folder". The tests
  compile from the same repo without being mixed into the app build
  (`<DefaultItemExcludes>` in `DevDesk.csproj`).
- They cover logic, not pixels: `Models/ProjectModel.cs`, the scan pipeline
  (`Services/ScanEngine.cs`, `PathHygiene`, `SelfLocator` — built against temp
  git repos, requires git), ordered ignore rules, root cache/catalog freshness,
  root watcher debounce and checkout-targeted refresh, multi-root settings, project search/sorting and
  organization persistence, workspace-target discovery, `ProjectCatalog` (one
  scan shared by every page), `ProjectOpenActions` (marker discovery),
  `WindowPlacement`, `Routes`,
  `UpdateService` (plus a source-level guard against blocking calls),
  `SettingsService`/`SettingsMigration`, `LogRedaction`/`LogRingBufferSink`/
  `DiagnosticsExport`, `ReleaseChannels`, the version/release-config guards, and
  the v0.3.0 build stack: `ProcessRunner`, `AnsiText`, `BuildProfileResolver`,
  `BuildSettings`, `BuildCommandBuilder`, `BuildService` (against
  `FakeProcessRunner`), `BuildOutputLocator`, `BuildLogBuffer`, `BuildLogWriter`,
  `MsBuildProblemParser`, `ProblemNavigator`, `ActivityCenter`,
  `ActivityViewModel`, `BuildHistory`, the Dashboard badge join, and
  `SelfRelaunch` (argument list + a source guard that the launcher waits before
  it builds).
- Run them (the WinUI/XAML compiler needs a platform):

```powershell
dotnet test DevDesk.Tests -p:Platform=x64          # from DevDesk\
dotnet test ..\DevDesk.Tests -p:Platform=x64       # or from somewhere else
```

- FlaUI tests are a separate interactive-desktop check, never part of ordinary
  headless CI: `.\Scripts\ui-tests.ps1 -Configuration Debug`. They launch the
  built app with an isolated `DEVDESK_DATA_DIR`.
- The DAILY check is `.\Scripts\ci.ps1` (restore once, build the test project
  and app reference once, then run tests without rebuilding). CI and the script
  use the same commands; use `-Configuration Debug` for debug bits. Keep service
  tests in `DevDesk.Tests/` and desktop UI automation in `DevDesk.UiTests/` —
  never add test files directly under app source folders.

## Layout / key files

| Path | Purpose |
| --- | --- |
| `Program.cs` | Entry point: `LoggingService.Initialize()`, `VelopackApp.Build()`, global exception handlers. |
| `App.xaml` | **Sea-blue theme** resource dictionaries (Light/Dark) + `DevCardStyle` + `DevCornerRadius` (8px). |
| `App.xaml.cs` | UI-thread exception logging + background auto-update check (Velopack, skipped when not installed). |
| `MainWindow.xaml(.cs)` | Mica backdrop, custom title bar, theme-cycle button (left of the title — must never sit under the OS caption buttons), NavigationView → Dashboard/Projects/Templates/GitHub/DevDesk/Settings/Diagnostics. |
| `Models/ProjectModel.cs` | Project/Workspace card model + `CommitInfo` + `ComponentInfo`. |
| `Services/ScanEngine.cs` | The scanner — walks enabled roots at their configured depths with ordered ignores, detects repos by `.git`/project marker, resolves checkout identity and reads branch, dirty/behind, remote host, last 3 commits, and csproj version. Supports a single known-checkout refresh. Never lists DevDesk; returns it as the self workspace. |
| `Services/ProjectCatalog.cs` / `AppServices.cs` | The in-session snapshot: one scan, joined by every page (`GetOrRefreshAsync`), with targeted checkout and root reconciliation refreshes; `AppServices` is the service graph for pages with parameterless constructors. |
| `Services/PathHygiene.cs` / `IgnoreRuleMatcher.cs` / `SelfLocator.cs` | Protected Git/reparse-point safety, root-relative ordered excludes/negation, hygiene folders, and how DevDesk finds its own repo. |
| `Services/ScanCacheStore.cs` / `ProjectRootWatcher.cs` | Atomic schema-v2 scan cache with visible freshness, plus debounced checkout-targeted invalidation and root reconciliation fallback with partial-root retention. |
| `Services/ProjectOrganizationStore.cs` / `ProjectListQuery.cs` | Separately persisted pins, coloured tags, saved views, archive preference/reversible project state, recent opens, and deterministic in-memory search/sorting. |
| `Services/WorkspaceTargetDiscovery.cs` | Bounded, link-safe solution/project discovery for a selected repository. |
| `Services/GitRunner.cs` | Safe git process runner — argument lists only, output redirection, timeout, cancellation, non-interactive; git failure never throws (exit-code path). |
| `Services/GitService.cs` / `GitStatusParser.cs` | Typed read/write boundary over `GitRunner`: structured failures, credential redaction, detailed Git state, and explicit stage/commit/fetch/pull/push/branch/tag/conflict operations. UI writes require previews and confirmations. |
| `Services/ProjectOpenActions.cs` | Open-in-Explorer / reveal-in-Explorer / open-with-default-app (ShellExecute) + solution/project marker finder — all argument-list safe, never throws. |
| `Services/WindowPlacement.cs` / `SingleInstance.cs` / `Routes.cs` | Placement clamped onto a real display; one window per session (second launch focuses the first); the single list of navigation tags. |
| `Services/ThemeService.cs` | Applies System/Light/Dark to the window root. |
| `Services/SettingsService.cs` | JSON settings (`%LocalAppData%\DevDesk\settings.json`, `schemaVersion: 1`), theme / channel / last route / window persisted, unknown keys preserved. |
| `Services/LoggingService.cs` | Serilog to `%LocalAppData%\DevDesk\Logs\applog-YYYYMMDD.log` (14 days) **plus** an in-memory ring (`LoggingService.Ring`, 2000 events) that Diagnostics reads; `DEVDESK_DATA_DIR` moves both settings and logs. |
| `Services/LogRedaction.cs` / `RedactingTextFormatter.cs` / `DiagnosticsExport.cs` | Credential redaction on the way to disk and into the zip; the export bundle (`logs/`, `environment.txt`, `settings.redacted.json`, `manifest.json`). |
| `Services/UpdateService.cs` / `AppInfo.cs` / `ReleaseChannels.cs` | Velopack wrapper (async only — never block the UI thread), version accessors, and the one tag→channel vocabulary. |
| `Services/PerfCounters.cs` / `StartupTimeline.cs` / `UiThreadHeartbeat.cs` | What makes "fast" a measurement (roadmap F18): `Interlocked` counters plus the last duration of every scan / update check / settings write / build-family process, a 20 ms UI dispatcher heartbeat that records maximum observed delay for M6, and startup marks (process start → logging → Velopack → settings → window shown → first snapshot) measured from one origin and logged once each at `INF`. Heartbeat sampling is diagnostic evidence, not proof of every individual operation's blocking time. The budgets live in `PerfBudgets`. |
| `Services/ProcessRunner.cs` | The v0.3.0 process boundary behind `IProcessRunner`: UTF-8, line-buffered events while the child runs, environment injection, timeout, cancel with tree kill, and `NotFound`/`TimedOut`/`Canceled`/`Failed` as outcomes instead of exceptions. `GitRunner` keeps its own behaviour. |
| `Services/BuildService.cs` / `BuildCommandBuilder.cs` / `BuildProfileResolver.cs` | Build / run / publish / test as typed sessions: argument lists only, one build at a time process-wide (the rest queue), exe resolution with a `dotnet run` fallback, publish path from the output line, platform only where it applies. Profiles default to Debug/x64 and persist per project. |
| `Services/ActivityCenter.cs` / `BuildHistory.cs` / `BuildLogWriter.cs` | The queue and the record: `Operation` rows for every build-family run, a ring of 200 stored entries (`build-history.json`, schema 1, atomic, corrupt file backed up), and the complete on-disk log (the console is the bounded window). |
| `Services/MsBuildProblemParser.cs` / `ProblemNavigator.cs` / `AnsiText.cs` | MSBuild diagnostics → severity/code/file/line/column/message (localized prose still parsed by code), the file-open-at-line action, and the ANSI/`\r` normalizer the console needs. |
| `Services/SelfRelaunch.cs` | "Rebuild & relaunch" handover: the launcher's argument list, `TryStart` returning sentences instead of throwing, and the reason the wait lives in `launch-dev.ps1` (D-0.3.12). |
| `Controls/BuildConsole.xaml(.cs)` | The log console: virtualized list, search with a match count and highlight, copy selection / copy all, save, follow-tail and wrap toggles, bounded buffer with an explicit "N earlier lines" row. |
| `Pages/*` | Dashboard (cards grid + build badges, loading overlay, **click a card → Open in Explorer / Open in VS**), Projects (Overview/Changes/Commits/Branches + **Build tab** with console, Problems and Run/Publish, guarded Git operations), Templates (wizard preview, create disabled until v0.4.0), GitHub (empty states until v0.5.0), **DevDesk (the app itself — workspace + components, Build / Run tests / Rebuild & relaunch / Output)**, **Activity (the 8th route: session operations merged with stored history, retry/open-log, no retry for stored rows)**, Settings, Diagnostics (real log view + export + performance card). |
| `ViewModels/*` | Page state for Dashboard / self / Diagnostics / Activity (`x:Bind`); `ProjectCardItem` forwards `ProjectModel` members so the card template can bind them; `BuildLogBuffer` and `ActivityRow` are presentation, never a second source of truth. |
| `Scripts/launch-dev.ps1` | **Build-if-needed + launch without a terminal (shortcut target); progress window during rebuild.** |
| `Scripts/make-shortcut.ps1` | **Creates the desktop `DevDesk` shortcut.** |
| `Scripts/build-and-release.ps1` | Release pipeline (publish → `vpk download` → `vpk pack` → `vpk upload`; **single source of truth**, CI delegates to it). |
| `Scripts/smoke-ui.ps1` | Visits all 8 destinations through UI Automation in a throw-away `DEVDESK_DATA_DIR`; asserts one catalog scan and no ERR/FTL lines. |
| `Scripts/verify-shell.ps1` | Proves placement restore, last-route restore, minimum size, remembered navigation and the real update answer against the exe. |
| `Scripts/capture-page.ps1` | Screenshots one destination for review/evidence. |
| `Scripts/ci.ps1` | **The CI gate locally** — restore once, build the test project/app reference once, then test without rebuilding at `-p:Platform=x64`; TRX into `artifacts\`. Matches `ci.yml`. |
| `Scripts/ui-tests.ps1` / `DevDesk.UiTests/` | Opt-in FlaUI UIA3 smoke test; requires an interactive Windows desktop and stays outside required CI. |
| `Scripts/benchmark-v07.ps1` / `DevDesk.Tests/V07ScaleBenchmarkTests.cs` | Opt-in v0.7 cache, first-card, search, and synthetic 8/50-repository performance checks; M4 currently fails and is a release blocker. |
| `Services/ProjectRootWatcher.cs` / `ProjectOrganizationStore.cs` | Debounced filesystem invalidation and separately persisted pins, tags and recent-open metadata. |
| `Services/WorkspaceTargetDiscovery.cs` | Bounded local solution/project discovery for the selected checkout. |
| `.github/workflows/ci.yml` | Push/PR gate on `main`: build Release, test, artefacts on failure. **Required check name: `Build & test`.** |
| `.github/workflows/release.yml` | Tag (`v*`) / manual dispatch only; `verify` job runs the gate first, then `build-and-release.ps1`; refuses to republish an existing version. |
| `docs/DEVDESK-ROADMAP.md` | The map: versions, findings, decisions, service boundaries. |
| `docs/versions/` | Per-version work orders + the evidence log (filled as items land). |
| `DevDesk.Tests/` | xunit tests (own project, inside this repo). |

## Versioning & releases

Current version: **v0.7.0 "Scale & multi-root" is released as `v0.7.0-beta`
on the beta channel.** `v0.6.0-beta` was the previous release. Keep
`<Version>`, `<AssemblyVersion>`, `<FileVersion>` and `<InformationalVersion>` in
`DevDesk.csproj` in sync (four fields, always) — `Scripts/bump-version.ps1`
rewrites them together and the test suite fails when they drift.

Release flow: tag (`git tag v0.7.0-beta && git push origin v0.7.0-beta`) or
manual `workflow_dispatch`; `Scripts/build-and-release.ps1` is the single source
of truth for publish → `vpk download` → `vpk pack` → `vpk upload`, and CI
delegates to it. The channel comes from the tag; releases are published, never
drafts, and `--pre` is never passed.

## Known gotchas (these have caused real failures)

1. **ResourceDictionary content ordering (XAML compiler WMC0035)**: property
   elements (`MergedDictionaries`, `ThemeDictionaries`) must come BEFORE the
   implicit content items in a `ResourceDictionary`; content must be contiguous
   at the end. Splitting content around a property element breaks the build.
2. **Platform is mandatory**: `<Platforms>x86;x64;ARM64</Platforms>` + WinUI
   means builds/tests need `-p:Platform=x64`; AnyCPU fails.
3. **The scan reads git/fs read-only** — do not make it write (no
   fetch/push/commit) or call the network; scans must be instant and safe to
   re-run. Keep the `GitRunner` argument-list + timeout pattern; preserve the
   `ScannerResult` shape (projects never contain DevDesk, self is separate) and
   let `ProjectCatalog` own the one scan.
4. **Version fields out of sync** (template AGENTS) — surface a badge; release
   wizard must rewrite all three together.
5. **Channel vocabulary** — `stable|beta|dev`, one source of truth
   (`Services/ReleaseChannels.cs`); the channel is derived from the tag.
   `alpha` is retired and refused by name. Don't ship releases to a channel no
   client reads.
6. **vpk v1.2 CLI**: icon flag is `--icon`, ids are `-u/-v/-p/-e`; `vpk` has no
   `--version` (parse the `--help` banner or `dotnet tool list --local`).
7. **"VS studio projects" has spaces** — any process calls must use argument
   lists, never interpolated shell strings (path-injection safety).
8. **`PIgen App` is a repo-in-a-folder with 4 projects and NO remote — outside
   the ecosystem (not scanned)** — a Workspace model must still support this
   shape and never assume a remote exists.
9. **Keep the template a good template** — template scaffolding must be
   copy-only; never mutate `C:\Code\projects\DevEcosystem\DevTem WinUI 3`.
10. **Scan hygiene** — skip `bin/obj/node_modules/.vs/Releases/Logs`; scans
    must be incremental so a big `C:\Code\projects` never slows startup.
11. **Never fake data on screen.** A hardcoded row, an invented run number or a
    button that only apologises is a lie the user cannot tell apart from a real
    read. Not-built features get a disabled control with the version that brings
    it, or an empty state naming that version. `DocsHonestyTests` fails the build
    on phase-era copy and on the removed sample rows.
12. **WinUI tooltips do not appear on disabled controls** — a disabled button
    needs its reason in visible text nearby (and an `AutomationProperties.Name`
    naming the version) or nobody will ever see why it is off.
13. **Never block the UI thread on a Task.** `UpdateServiceTests` scans the app
    source for `.Wait()`/`.Result`/`GetResult()`; a deliberate one must carry a
    `block-exempt` comment explaining why there is no dispatcher to block.
14. **Workflow `shell: pwsh` is PowerShell 7, the desktop shell here is 5.1.**
    `-SkipHttpErrorCheck` (and other 7-only parameters) exist on the runner but
    not in a local `powershell -File` run, so a step that must behave the same in
    both places uses `curl.exe` for HTTP status codes. Test workflow snippets
    locally before trusting them: the release duplicate-check was proven both
    ways (HTTP 200 → refused, HTTP 404 → allowed) with the exact commands.
15. **One clock, one owner per number.** Timed operations take a `TimeProvider`
    (`StartupTimeline`, `PerfCounters` via `ProjectCatalog` / `SettingsService`),
    never a bare `Stopwatch`: a test injects a manual clock instead of racing the
    wall. The startup origin is that clock's timestamp at the first line of
    `Main`, not `Environment.TickCount64` (boot-relative and millisecond-rounded,
    which turns the first marks into 0). A number also has exactly one owner —
    ring evictions come from `LogRingBufferSink.Dropped`, never a copy in
    `PerfCounters`; `ProjectCatalog.ScanCount` stays per-catalog and agrees with
    the process-wide counters because there is one catalog per process.
16. **A performance row that has nothing to show says `—`, not a nearby number.**
    "First snapshot" reads the `first snapshot` mark; a launch that restores
    Diagnostics never scans, and borrowing the window-shown total for that row
    would be a measurement of the wrong thing (the card's budgets are compared
    in `PerfRow`, colour only — never a PASS/FAIL verdict).
17. **A running app cannot rebuild its own exe.** Measured: `dotnet build` while
    DevDesk runs fails with `MSB3026`/`MSB3027` ("The file is locked by:
    DevDesk (pid)") because `bin\…\DevDesk.exe` and `DevDesk.dll` are open.
    "Rebuild & relaunch" therefore hands the wait to `launch-dev.ps1`
    (`-WaitForProcessId <pid>`): the launcher waits ≤30 s, builds only if a
    source is newer than `launch.stamp`, then starts the app through the normal
    single-instance flow. Never "simplify" this into a build inside the app —
    the page explains the lock instead of pretending the build can work.
18. **One build at a time, process-wide.** `BuildService` serializes every
    build-family run through `ActivityCenter`; the others show a real `Queued`
    row rather than a second `dotnet` fighting for `obj\`. A queued operation
    can be cancelled and disappears; a running one is cancelled by killing its
    process tree. Keep the queue in the service, not in a page.
19. **Build badges read `BuildHistory` only.** A project that never built says
    "no build yet" / `—`; it never borrows a neighbouring number or the
    in-session `PerfCounters` count. `HistoryEntry` is a separate DTO from
    `BuildRecord` on purpose, so renaming a record property cannot orphan the
    stored file.
20. **Platform is passed only when it applies.** `PlatformApplies` is true by
    default and false only for a `.csproj` with no Windows App SDK reference
    and no `<Platforms>` containing the value; console solutions must not get
    `-p:Platform=x64`. Publish never passes `-o`: the folder is whatever MSBuild
    printed after ` -> ` (the last such line), kept by `BuildOutputPump`, and
    "Open output" appears only when that folder really exists (D-0.3.3).
21. **The console is a bounded window; the file is complete.** The control keeps
    20,000 lines and shows an explicit "N earlier lines" row; the log file on
    disk holds every line, and "Save as…"/Diagnostics point at it. Do not raise
    the cap to "fix" missing output — that only moves the freeze.

## Conventions

- Keep `Scripts/build-and-release.ps1` the single source of truth for release
  logic; CI must delegate to it.
- Keep `Scripts/ci.ps1` and `.github/workflows/ci.yml` in step: the script is the
  local twin of the gate, and the plan's rule is that a local run and a CI run are
  the same commands. If one changes, change both in the same commit.
- Keep `launch-dev.ps1` / `make-shortcut.ps1` working — they are the product's
  core promise ("open the app by double-click, no terminal, instant reopen").
- Tests live in this repo at `DevDesk.Tests/` — never add test files directly
  under the app source folders.
- Busy/loading UX: any operation that can take a while must show the progress
  overlay (see DashboardPage/DevDeskPage) — never a frozen screen.
- Use `MicaBackdrop` + custom-titlebar pattern; stay on the sea-blue theme /
  8px radius resources in `App.xaml`.
- `#` header comments explain intent (project style); do not add noisy inline
  comments.
- Add real value only: no dependencies that aren't used.
