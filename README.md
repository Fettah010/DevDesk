# DevDesk — Developer Console for Windows 11

A WinUI 3 desktop app that gives a one-screen overview and management for
every project in **C:\Code\projects**, so you don't need the terminal:
dashboard cards, last commits, versions, releases per channel, GitHub Actions,
build/run/commit/tag/push and "new app from template" scaffolding. DevDesk and
the DevTem-WinUI 3 template live together in
**`C:\Code\projects\DevEcosystem`**; other folders there are unrelated.

> **Status: UI phase (Phase 0).** The shell, theme and navigation are real;
> project data is placeholder (`Services/SampleDataService.cs`) until the
> ProjectScanner lands. Complex flows are stubbed in place — buttons show an
> InfoBar instead of running — so the layout is a faithful picture of the
> finished app. Scope is tracked in
> `C:\Code\projects\DevEcosystem\DEVDESK-PLAN.md`.

## Run it — no terminal (double-click)

There is a **`DevDesk`** shortcut on the desktop: it builds (incrementally)
and launches the app, so you never type `dotnet build` / `dotnet run`.

- `Scripts\launch-dev.ps1` — build + start (what the shortcut runs).
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
  and dirty badges).
- **Projects**: two-pane list + detail with Overview / Commits / Releases /
  Actions tabs — all placeholder content.
- **Templates**: the "new project from template" wizard layout (steps, name &
  location, git/GitHub/version options, preview).
- **GitHub**: Actions runs and per-channel releases placeholder panes.
- **Planned**: build/run/publish, git ops, real scanning, GitHub API, release
  wizard, log viewer — see the plan file.

## Tech stack

- .NET 10 · WinUI 3 / Microsoft.WindowsAppSDK 1.8 (unpackaged, self-contained)
- Velopack 1.2 (auto-update skeleton from the template, inactive until installed)
- Serilog 4 logging; CommunityToolkit (SettingsControls, Segmented, Helpers)
- No external GitHub/git SDK yet — git/GitHub process calls + REST land with
  the services in later phases.

## Repo layout

```
MainWindow.xaml(.cs)   # Shell: custom title bar + NavigationView + theme toggle
App.xaml               # Sea-blue theme dictionaries (light/dark) + shared card style
Models/ProjectModel.cs # Project / workspace card model
Services/              # LoggingService (Serilog), SettingsService (JSON),
                       # ThemeService, UpdateService, SampleDataService (placeholder)
Pages/                 # Dashboard, Projects, Templates, GitHub, Settings, Diagnostics
Scripts/launch-dev.ps1        # Build + launch without a terminal (shortcut target)
Scripts/make-shortcut.ps1     # Creates the desktop "DevDesk" shortcut
Scripts/build-and-release.ps1 # Velopack release pipeline (from the template)
```

Sibling (outside this folder, by design): `C:\Code\projects\DevEcosystem\DevDesk.Tests`
— the xunit test project, run with `dotnet test DevDesk.Tests -p:Platform=x64`.

## License

MIT.