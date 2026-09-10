# AGENTS.md

Guidance for AI agents and developers working on this repository. Read this
first before making changes.

## What this is

**DevDesk** — a WinUI 3 developer console that manages everything in
`C:\Code\projects` (branch, version, last commits, releases per channel,
GitHub Actions, build/run) so the terminal is no longer needed.

**Current status: Phase 0 complete.** The shell, sea-blue theme (light/dark),
navigation and the **ProjectScanner** (git state + versions for every repo in
the DevEcosystem folder, plus open-in-Explorer / open-in-VS card actions) are
real and working. Phase 1+ flows (GitService write ops, BuildService,
GitHubService, TemplateService) are NOT wired yet — their buttons show an
InfoBar instead. Scope lives in
`C:\Code\projects\DevEcosystem\DevDesk\docs\DEVDESK-PLAN.md`.

## Ecosystem layout (this app does not live alone)

Everything related lives in **`C:\Code\projects\DevEcosystem`**; other folders
in `C:\Code\projects` are unrelated and must not be touched.

```
C:\Code\projects\DevEcosystem\
  DevDesk\              # THIS app (own local git repo)
    DevDesk.Tests\      # xunit tests — inside the DevDesk repo, own project
    docs\DEVDESK-PLAN.md # the plan
  DevTem WinUI 3\       # the template (own git repo, remote: Fettah010/winui-3-easy-template)
```

When scaffolding "new apps from the template", new projects go next to the
template inside `DevEcosystem\` (see TemplatesPage default location).

### The scan model (implemented by `Services/ProjectScanner.cs`)

- Scan root = `DevEcosystem`, depth 2 (stop descending once a repo is found).
- DevDesk is **never** reported as a scanned project — it becomes the self
  workspace (`ScannerResult.Self`), fed to the dedicated page
  (`Pages/DevDeskPage`).
- A folder counts as a project if it is a git repo **or** holds any
  `*.sln/*.slnx/*.*proj` marker; hygiene folders (bin/obj/.vs/Releases/…) and
  dot-folders are skipped. Git state is read-only (never fetch/push), so a
  scan is fast, local, and never fails because of the network.

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
- They cover pure logic: `Models/ProjectModel.cs`,
  `Services/ProjectScanner.cs` (built against temp git repos, requires git),
  `Services/ProjectOpenActions.cs` (marker discovery), `ThemeService`.
- Run them (the WinUI/XAML compiler needs a platform):

```powershell
dotnet test DevDesk.Tests -p:Platform=x64          # from DevDesk\
dotnet test ..\DevDesk.Tests -p:Platform=x64       # or from somewhere else
```

- The DAILY check is `dotnet build -p:Platform=x64` + `dotnet test -p:Platform=x64`.
  Keep tests in `DevDesk.Tests/` — never add files directly under the app
  project folders.

## Layout / key files

| Path | Purpose |
| --- | --- |
| `Program.cs` | Entry point: `LoggingService.Initialize()`, `VelopackApp.Build()`, global exception handlers. |
| `App.xaml` | **Sea-blue theme** resource dictionaries (Light/Dark) + `DevCardStyle` + `DevCornerRadius` (8px). |
| `App.xaml.cs` | UI-thread exception logging + background auto-update check (Velopack, skipped when not installed). |
| `MainWindow.xaml(.cs)` | Mica backdrop, custom title bar, theme-cycle button (left of the title — must never sit under the OS caption buttons), NavigationView → Dashboard/Projects/Templates/GitHub/DevDesk/Settings/Diagnostics. |
| `Models/ProjectModel.cs` | Project/Workspace card model + `CommitInfo` + `ComponentInfo`. |
| `Services/ProjectScanner.cs` | **Phase 0 scanner** — walks the scan root (depth 2), detects repos by `.git`/project marker, reads branch, dirty/behind, remote host, last 3 commits, and csproj version (with 3-field sync check). Never lists DevDesk; returns it as the self workspace. |
| `Services/GitRunner.cs` | Safe git process runner — argument lists only, output redirection, 15s timeout; git failure never throws (exit-code path). |
| `Services/ProjectOpenActions.cs` | Open-in-Explorer / open-with-default-app (ShellExecute) + solution/project marker finder for cards — all argument-list safe, never throws. |
| `Services/ThemeService.cs` | Applies System/Light/Dark to the window root. |
| `Services/SettingsService.cs` | JSON settings (`%LocalAppData%\DevDesk\settings.json`), theme persisted. |
| `Services/LoggingService.cs` | Serilog; `Logs/applog-YYYYMMDD.log` next to the exe. |
| `Services/UpdateService.cs` / `AppInfo.cs` | Velopack wrapper / version accessors (from the template). |
| `Pages/*` | Dashboard (cards grid, loading overlay, **click a card → Open in Explorer / Open in VS**), Projects (list + detail tabs), Templates (wizard layout), GitHub (Actions/releases), **DevDesk (the app itself — workspace + components, open-explorer / open-plan actions)**, Settings, Diagnostics (log view). |
| `Scripts/launch-dev.ps1` | **Build-if-needed + launch without a terminal (shortcut target); progress window during rebuild.** |
| `Scripts/make-shortcut.ps1` | **Creates the desktop `DevDesk` shortcut.** |
| `Scripts/build-and-release.ps1` | Release pipeline (from template; **single source of truth**). |
| `.github/workflows/release.yml` | CI release pipeline (tag `v*` / manual dispatch). Useful later: DevDesk drives these for other projects. |
| `docs/DEVDESK-PLAN.md` | The plan (lives inside DevDesk). |
| `DevDesk.Tests/` | xunit tests (own project, inside this repo). |

## Versioning & releases

Current version: **0.0.1** — keep `<Version>`, `<AssemblyVersion>`,
`<FileVersion>` in `DevDesk.csproj` in sync (three fields, always).

Release infra is copied from the template and unchanged: tag flow
(`git tag v0.0.2 && git push origin v0.0.2`) or manual `workflow_dispatch`;
`Scripts/build-and-release.ps1` is the single source of truth for publish →
`vpk download` → `vpk pack` → `vpk upload`.

## Known gotchas (these have caused real failures)

1. **ResourceDictionary content ordering (XAML compiler WMC0035)**: property
   elements (`MergedDictionaries`, `ThemeDictionaries`) must come BEFORE the
   implicit content items in a `ResourceDictionary`; content must be contiguous
   at the end. Splitting content around a property element breaks the build.
2. **Platform is mandatory**: `<Platforms>x86;x64;ARM64</Platforms>` + WinUI
   means builds/tests need `-p:Platform=x64`; AnyCPU fails.
3. **`ProjectScanner` reads git/fs read-only** — do not make it write (no
   fetch/push/commit) or call the network; scans must be instant and safe to
   re-run. Keep the `GitRunner` argument-list + timeout pattern; preserve the
   `ScannerResult` shape (projects never contain DevDesk, self is separate).
4. **Version fields out of sync** (template AGENTS) — surface a badge; release
   wizard must rewrite all three together.
5. **Channel vocabulary** — template CI uses `stable|beta|alpha`, UI uses
   `stable|beta|dev`. DevDesk's plan defines one source of truth
   (`stable|beta|dev`). Don't ship releases to a channel no client reads.
6. **vpk v1.2 CLI**: icon flag is `--icon`, ids are `-u/-v/-p/-e`; check
   `vpk pack --help` when unsure.
7. **"VS studio projects" has spaces** — any process calls must use argument
   lists, never interpolated shell strings (path-injection safety).
8. **`PIgen App` is a repo-in-a-folder with 4 projects and NO remote — outside
   the ecosystem (not scanned)** — a Workspace model must still support this
   shape and never assume a remote exists.
9. **Keep the template a good template** — template scaffolding must be
   copy-only; never mutate `C:\Code\projects\DevEcosystem\DevTem WinUI 3`.
10. **Scan hygiene** — skip `bin/obj/node_modules/.vs/Releases/Logs`; scans
    must be incremental so a big `C:\Code\projects` never slows startup.

## Conventions

- Keep `Scripts/build-and-release.ps1` the single source of truth for release
  logic; CI must delegate to it.
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