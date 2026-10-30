# AGENTS.md

Guidance for AI agents and developers working on this repository. Read this
first before making changes.

## What this is

**DevDesk** — a WinUI 3 developer console that manages everything in
`C:\Code\projects` (branch, version, last commits, releases per channel,
GitHub Actions, build/run) so the terminal is no longer needed.

**Current status: v0.2.0 "Git, safely" in progress; v0.1.0 is published.** The shell, sea-blue theme
(light/dark), navigation, the **ScanEngine** (git state + versions for every repo
in the DevEcosystem folder), real open-in-Explorer / open-in-VS actions, window
placement + single instance, the Diagnostics log/export pipeline and the
**performance instrumentation** (startup marks, per-operation timings, budgets on
screen) are real and working. Projects now has local Git reads and previewed,
confirmed write operations. Everything that is not built yet is **disabled with the version that
brings it, or an empty state naming it** — no fake rows, no buttons that look
live but only apologise. Later flows (BuildService → v0.3.0, TemplateService
→ v0.4.0, GitHubService → v0.5.0) are labelled, not
faked. The map is `docs\DEVDESK-ROADMAP.md`; each version's work order and
evidence log is in `docs\versions\`.

## Ecosystem layout (this app does not live alone)

Everything related lives in **`C:\Code\projects\DevEcosystem`**; other folders
in `C:\Code\projects` are unrelated and must not be touched.

```
C:\Code\projects\DevEcosystem\
  DevDesk\              # THIS app (own local git repo)
    DevDesk.Tests\      # xunit tests — inside the DevDesk repo, own project
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
  starting their own (`smoke-ui.ps1` asserts exactly one scan for 7 page visits).

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

- Tests live in **`DevDesk\DevDesk.Tests`** — inside the DevDesk repo (own
  project), by design, since "every project owns its folder". The tests
  compile from the same repo without being mixed into the app build
  (`<DefaultItemExcludes>` in `DevDesk.csproj`).
- They cover logic, not pixels: `Models/ProjectModel.cs`, the scan pipeline
  (`Services/ScanEngine.cs`, `PathHygiene`, `SelfLocator` — built against temp
  git repos, requires git), `ProjectCatalog` (one scan shared by every page),
  `ProjectOpenActions` (marker discovery), `WindowPlacement`, `Routes`,
  `UpdateService` (plus a source-level guard against blocking calls),
  `SettingsService`/`SettingsMigration`, `LogRedaction`/`LogRingBufferSink`/
  `DiagnosticsExport`, `ReleaseChannels`, and the version/release-config guards.
- Run them (the WinUI/XAML compiler needs a platform):

```powershell
dotnet test DevDesk.Tests -p:Platform=x64          # from DevDesk\
dotnet test ..\DevDesk.Tests -p:Platform=x64       # or from somewhere else
```

- The DAILY check is `.\Scripts\ci.ps1` (Release build + full suite at x64 — the
  same two commands CI runs; `-Configuration Debug` if you want debug bits).
  `dotnet build -p:Platform=x64` + `dotnet test -p:Platform=x64` is the
  equivalent by hand. Keep tests in `DevDesk.Tests/` — never add files directly
  under the app project folders.

## Layout / key files

| Path | Purpose |
| --- | --- |
| `Program.cs` | Entry point: `LoggingService.Initialize()`, `VelopackApp.Build()`, global exception handlers. |
| `App.xaml` | **Sea-blue theme** resource dictionaries (Light/Dark) + `DevCardStyle` + `DevCornerRadius` (8px). |
| `App.xaml.cs` | UI-thread exception logging + background auto-update check (Velopack, skipped when not installed). |
| `MainWindow.xaml(.cs)` | Mica backdrop, custom title bar, theme-cycle button (left of the title — must never sit under the OS caption buttons), NavigationView → Dashboard/Projects/Templates/GitHub/DevDesk/Settings/Diagnostics. |
| `Models/ProjectModel.cs` | Project/Workspace card model + `CommitInfo` + `ComponentInfo`. |
| `Services/ScanEngine.cs` | The scanner — walks the scan root (depth 2), detects repos by `.git`/project marker, reads branch, dirty/behind, remote host, last 3 commits, and csproj version (with 3-field sync check). Never lists DevDesk; returns it as the self workspace. |
| `Services/ProjectCatalog.cs` / `AppServices.cs` | The in-session snapshot: one scan, joined by every page (`GetOrRefreshAsync`); `AppServices` is the service graph for pages with parameterless constructors. |
| `Services/PathHygiene.cs` / `SelfLocator.cs` | What is never walked (bin/obj/.vs/Logs/worktrees) and how DevDesk finds its own repo. |
| `Services/GitRunner.cs` | Safe git process runner — argument lists only, output redirection, timeout, cancellation, non-interactive; git failure never throws (exit-code path). |
| `Services/GitService.cs` / `GitStatusParser.cs` | Typed read/write boundary over `GitRunner`: structured failures, credential redaction, detailed Git state, and explicit stage/commit/fetch/pull/push/branch/tag/conflict operations. UI writes require previews and confirmations. |
| `Services/ProjectOpenActions.cs` | Open-in-Explorer / reveal-in-Explorer / open-with-default-app (ShellExecute) + solution/project marker finder — all argument-list safe, never throws. |
| `Services/WindowPlacement.cs` / `SingleInstance.cs` / `Routes.cs` | Placement clamped onto a real display; one window per session (second launch focuses the first); the single list of navigation tags. |
| `Services/ThemeService.cs` | Applies System/Light/Dark to the window root. |
| `Services/SettingsService.cs` | JSON settings (`%LocalAppData%\DevDesk\settings.json`, `schemaVersion: 1`), theme / channel / last route / window persisted, unknown keys preserved. |
| `Services/LoggingService.cs` | Serilog to `%LocalAppData%\DevDesk\Logs\applog-YYYYMMDD.log` (14 days) **plus** an in-memory ring (`LoggingService.Ring`, 2000 events) that Diagnostics reads; `DEVDESK_DATA_DIR` moves both settings and logs. |
| `Services/LogRedaction.cs` / `RedactingTextFormatter.cs` / `DiagnosticsExport.cs` | Credential redaction on the way to disk and into the zip; the export bundle (`logs/`, `environment.txt`, `settings.redacted.json`, `manifest.json`). |
| `Services/UpdateService.cs` / `AppInfo.cs` / `ReleaseChannels.cs` | Velopack wrapper (async only — never block the UI thread), version accessors, and the one tag→channel vocabulary. |
| `Services/PerfCounters.cs` / `StartupTimeline.cs` | What makes "fast" a measurement (roadmap F18): `Interlocked` counters plus the last duration of every scan / update check / settings write, and the startup marks (process start → logging → Velopack → settings → window shown → first snapshot) measured from one origin and logged once each at `INF`. The budgets live in `PerfBudgets`. |
| `Pages/*` | Dashboard (cards grid, loading overlay, **click a card → Open in Explorer / Open in VS**), Projects (real Overview/Changes/Commits/Branches, guarded Git operations, conflict handling), Templates (wizard preview, create disabled until v0.4.0), GitHub (empty states until v0.5.0), **DevDesk (the app itself — workspace + components, open-explorer / open-roadmap)**, Settings, Diagnostics (real log view + export + performance card). |
| `ViewModels/*` | Page state for Dashboard / self / Diagnostics (`x:Bind`); code-behind stays for shell actions only. |
| `Scripts/launch-dev.ps1` | **Build-if-needed + launch without a terminal (shortcut target); progress window during rebuild.** |
| `Scripts/make-shortcut.ps1` | **Creates the desktop `DevDesk` shortcut.** |
| `Scripts/build-and-release.ps1` | Release pipeline (publish → `vpk download` → `vpk pack` → `vpk upload`; **single source of truth**, CI delegates to it). |
| `Scripts/smoke-ui.ps1` | Visits all 7 destinations through UI Automation in a throw-away `DEVDESK_DATA_DIR`; asserts one catalog scan and no ERR/FTL lines. |
| `Scripts/verify-shell.ps1` | Proves placement restore, last-route restore, minimum size, remembered navigation and the real update answer against the exe. |
| `Scripts/capture-page.ps1` | Screenshots one destination for review/evidence. |
| `Scripts/ci.ps1` | **The CI gate locally** — Release build + full test suite at `-p:Platform=x64`, TRX into `artifacts\`. Same two commands as `ci.yml`. |
| `.github/workflows/ci.yml` | Push/PR gate on `main`: build Release, test, artefacts on failure. **Required check name: `Build & test`.** |
| `.github/workflows/release.yml` | Tag (`v*`) / manual dispatch only; `verify` job runs the gate first, then `build-and-release.ps1`; refuses to republish an existing version. |
| `docs/DEVDESK-ROADMAP.md` | The map: versions, findings, decisions, service boundaries. |
| `docs/versions/` | Per-version work orders + the evidence log (filled as items land). |
| `DevDesk.Tests/` | xunit tests (own project, inside this repo). |

## Versioning & releases

Current version: **0.2.0-beta in progress** (v0.1.0-beta is published). Keep `<Version>`,
`<AssemblyVersion>`, `<FileVersion>` in `DevDesk.csproj` in sync (three fields,
always) — `Scripts/bump-version.ps1` rewrites them together and the test suite
fails when they drift.

Release flow: tag (`git tag v0.2.0-beta && git push origin v0.2.0-beta`) or
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
