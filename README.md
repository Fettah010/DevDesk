# DevDesk — Developer Console for Windows 11

A WinUI 3 desktop app that gives a one-screen overview and management for
every project in **C:\Code\projects**, so you don't need the terminal:
dashboard cards, last commits, versions, releases per channel, GitHub Actions,
build/run/commit/tag/push and "new app from template" scaffolding. DevDesk and
the DevTem-WinUI 3 template live together in
**`C:\Code\projects\DevEcosystem`**; other folders there are unrelated.

> **Status: Phase 0 complete.** The shell, theme and navigation are real; the
> **ProjectScanner** reads every repo inside `DevEcosystem` (branch, status,
> last commits, version sync, remote host) with no network, and cards open
> repos in Explorer / Visual Studio. Phase 1+ flows are stubbed in place —
> buttons show an InfoBar instead of running. Scope is tracked in
> `C:\Code\projects\DevEcosystem\DevDesk\docs\DEVDESK-PLAN.md`.

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
- **Navigation**: Dashboard, Projects, Templates, GitHub, Settings,
  Diagnostics — with an animated (`EntranceNavigationTransitionInfo`) Frame.
- **Dashboard**: summary cards (projects / clean / attention / remotes) and
  the project-card grid (branch, status, last commits, version pill, remote
  and dirty badges) — scanned live from the DevEcosystem folder, never
  DevDesk itself. **Click a card** to open the repo in Explorer or Visual
  Studio.
- **DevDesk**: a dedicated page for this app itself — real repo state read by
  the scanner (branch, version, commits) and its components (tests, plan doc,
  scripts, CI workflow), with live "open in Explorer" / "open plan doc"
  actions.
- **Loading**: any operation that may take a while (refresh/rescan, repo-state
  check) shows a progress overlay; the launcher shows a progress window while
  a rebuild runs.
- **Projects**: two-pane list + detail with Overview / Commits / Releases /
  Actions tabs — all placeholder content.
- **Templates**: the "new project from template" wizard layout (steps, name &
  location, git/GitHub/version options, preview).
- **GitHub**: Actions runs and per-channel releases placeholder panes.
- **Planned**: build/run/publish, git *write* ops (commit/push/branch), the
  "new from template" wizard, GitHub API, release wizard, log viewer — see the
  plan file.

## Tech stack

- .NET 10 · WinUI 3 / Microsoft.WindowsAppSDK 1.8 (unpackaged, self-contained)
- Velopack 1.2 (auto-update skeleton from the template, inactive until installed)
- Serilog 4 logging; CommunityToolkit (SettingsControls, Segmented, Helpers)
- No external GitHub/git SDK — git is driven through `Services/GitRunner.cs`
  (argument lists, no shell interpolation); GitHub REST arrives with the
  services in later phases.

## Repo layout

```
MainWindow.xaml(.cs)   # Shell: custom title bar + NavigationView + theme toggle
App.xaml               # Sea-blue theme dictionaries (light/dark) + shared card style
Models/ProjectModel.cs # Project / workspace card model + ComponentInfo
Services/              # LoggingService (Serilog), SettingsService (JSON),
                       # ThemeService, UpdateService, GitRunner, ProjectScanner
Pages/                 # Dashboard, Projects, Templates, GitHub, DevDesk (self), Settings, Diagnostics
Scripts/launch-dev.ps1        # Build-if-needed + launch without a terminal (shortcut target)
Scripts/make-shortcut.ps1     # Creates the desktop "DevDesk" shortcut
Scripts/build-and-release.ps1 # Velopack release pipeline (from the template)
DevDesk.Tests/                # xunit tests — live INSIDE this repo (own project)
docs/DEVDESK-PLAN.md          # the plan
```

Tests run with `dotnet test DevDesk.Tests -p:Platform=x64` (from this folder).

## License

MIT.