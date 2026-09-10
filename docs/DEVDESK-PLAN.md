# DevDesk — Developer Console / Project Organizer (WinUI 3)

Working plan for a new WinUI 3 desktop app that manages everything currently
handled through the terminal + GitHub website, for all projects living in
`C:\Code\projects` — the DevTem-WinUI 3 template and DevDesk both live inside
the `C:\Code\projects\DevEcosystem` folder (other folders there are unrelated).

> Status: **Phase 0 complete.** The ecosystem is organized under
> `C:\Code\projects\DevEcosystem`:
> `DevDesk/` (this app — **own repo**, containing `DevDesk.Tests/` and
> `docs/DEVDESK-PLAN.md`) · `DevTem WinUI 3/` (the template, own repo).
> "Run the dev without a terminal" is handled by
> `DevDesk/Scripts/launch-dev.ps1` + a desktop shortcut; reopen is instant
> (rebuild only when a source actually changed, progress window while it runs).
> The **ProjectScanner** (`Services/ProjectScanner.cs`) reads every repo in the
> ecosystem (branch, dirty/behind, remote host, last commits, csproj version
> sync) with no network, and cards open repos in Explorer / Visual Studio.
> Everything else (GitService write ops, BuildService, GitHubService,
> TemplateService) is Phase 1+.

---

## 1. Goal (one sentence)

A snappy, animated, see-through Windows 11 app — dark "sea-blue / black" and
light "white / sea-blue" themes — that shows every project on the machine at a
glance (branch, version, last commits, releases per channel, GitHub Actions
status) and lets you build, run, release and scaffold new apps from the template
**without ever touching the terminal**.

---

## 2. Analysis of the current template (what we build on)

The template `C:\Code\projects\DevEcosystem\DevTem WinUI 3` (DevTem-WinUI 3, v0.0.2) already
gives us, and we should **reuse rather than rewrite**:

| Asset | Where | Reuse for DevDesk |
| --- | --- | --- |
| WinUI 3 + WindowsAppSDK 1.8, .NET 10, unpackaged self-contained | `DevTemWinUi3.csproj` | Copy as-is to DevDesk |
| Mica backdrop + extended title bar + NavigationView shell | `MainWindow.xaml(.cs)` | Copy & extend (add more nav items, theme switch) |
| App-token/token replacing by renaming (exe name, RootNamespace, AppId, icon) | csproj / `vpk pack` params | Mechanism behind "make new app from template" |
| Serilog logging (console + rolling file) | `Services/LoggingService.cs` | Copy verbatim |
| Velopack auto-update service (channels, silent bg check, restart) | `Services/UpdateService.cs`, `App.xaml.cs` | Copy verbatim → DevDesk updates itself |
| SettingsCards + Segmented (CommunityToolkit) | `Packages/UpdatesPage.xaml` | Design language base for settings/preferences |
| Release pipeline: `Scripts/build-and-release.ps1` = single source of truth | Templates release infra | DevDesk **invokes this script** (and its template counterparts) rather than re-implementing vpk |
| CI: `.github/workflows/release.yml` (tag `v*` / `workflow_dispatch`) | Template repo | Model that DevDesk helps *drive* (bump → tag → push → watch the run) |

### Real environment it must manage (found during analysis)

| Path | Reality | Consequence for the scanner |
| --- | --- | --- |
| `C:\Code\projects\DevEcosystem\DevTem WinUI 3` | Git repo `origin: github.com/Fettah010/winui-3-easy-template`, branch `main`, versions 0.0.x, Velopack feeds | The canonical "has GitHub + releases" case |
| `C:\Code\projects\VS studio projects\PIgen App` | **One git repo containing 4 .NET projects** (`PIgen`, `PIgen.Backend`, `PIgen.Console`, `PIgen.Tests`) + `PIgen.sln` + `PIgen App.slnx`, **no remote yet**, PLAN.txt files | Must detect "workspace = repo with many projects" and show sub-projects/solutions; handle *local-only repos* gracefully |
| `C:\Code\projects\VS studio projects` | Parent folder, itself **not** a repo | "VS studio projects" (with spaces) is a plain grouping folder — must be traversed, not presented as a project |
| `C:\Code\projects\PIgen App` (future) | Whatever the user creates via "new from template" | Template-instance checklist: name, git init, GitHub repo create, version 0.0.1 |

---

## 3. The scan model ("smart in detecting / ignoring stuff")

Recursive folder scanning is **the** core skill; get this right and the rest of
the app is easy.

### Detection rules (a folder becomes a card)
Treat a directory as a **project** if it contains any of:
- `.git/` (git repo)
- `*.sln`, `*.slnx`, `*.slnf`
- `*.csproj` / `*.vcxproj` / `*.fsproj` at its top level

**Project vs Workspace:** if a detected repo additionally contains ≥ 2 solutions
or ≥ 2 top-level project files *inside subfolders*, present it as a **Workspace**
(a repo that hosts several sub-projects), e.g. `PIgen App`. A plain `*.slnx` +
single `*.csproj` = a simple Project. Workspaces expand to list their
solutions/projects, all tied back to the one git repo (commits/branch/releases
are repo-level and shared).

### Traversal rules
- **Roots:** default `C:\Code\projects\DevEcosystem` (only what is inside the
  ecosystem folder counts); user-configurable list of roots later.
- **Self-exclusion:** DevDesk's own repo is never presented as a scanned
  project — it has a dedicated page (`Pages/DevDeskPage`) showing its repo
  state and components (tests, plan doc, scripts, workflow).
- **Depth:** scan up to a sane depth (default 2 for the ecosystem), stop
  descending once a repo is found inside a non-repo parent folder (nested
  repo = own card).
- **Skip/don't-crash on:** `node_modules`, `bin`, `obj`, `.vs`, `.vscode`,
  `packages`, `Releases`, `Logs`, `.venv`, `dist`, `build`, hidden/system dirs
  (`.git` itself, `Thumbs.db`, `$RECYCLE.BIN`), and any path that errors on
  access (broken symlink/permission) — the scan must never throw the UI.
- **Ignore list:** global list in Settings (regex) + per-root overrides; default
  list ships with the above. `.gitignore`-style semantics (prefix `!` to
  un-ignore).
- **Cache:** scan results cached to disk (file watchers to re-scan on change),
  manual "Refresh" button too. Scanning runs off the UI thread; skeleton
  results stream in as they're found (progressive listing).

### What a project card/tile shows (the analytics)
- Name, path (collapsed when too deep), icon (exe/ico detection, else generic)
- Git: current branch, commit count "ahead/behind" vs upstream, **last 5
  commits** (subject, author, relative date)
- Version: `<Version>`/`<AssemblyVersion>`/`<FileVersion>` from csproj →
  **sync check badge** (warns if the three fields differ — AGENTS gotcha)
- GitHub: remote present? → releases per channel (latest per feed), last
  Actions run + status, repo URL
- Status badges: local-only (no remote), dirty working tree, version mismatch,
  action failed, behind upstream

---

## 4. What the app can DO (no terminal)

Every action is a button/dialog → a process wrapper runs the underlying tool
and streams output into an embedded console/log view.

| Area | Actions |
| --- | --- |
| **Build / Run** | `dotnet build -p:Platform=x64` (Debug/Release, pick arch), `dotnet run`, `dotnet publish`; open output folder; open in VS (`devenv`/`.slnx`); open terminal here (escape hatch, never required) |
| **Git** | Refresh status; view full commit log (pageable); create branch/checkout/tag; clean/refresh; commit+push with message dialog; pull/push |
| **Releases** | New release wizard: suggested next version (semver bump from latest tag + commit count), channel (`stable`/`beta`/`dev`), prerelease flag, release notes field → flow: bump csproj (all 3 fields) → commit → `git tag vX` → push (triggers CI) **or** run `Scripts/build-and-release.ps1` locally (respects `-Channel`, `-Publish`, `-PreRelease`, `-Tag`) |
| **GitHub** | List/reload Actions runs (status, duration, conclusion), open run in browser, cancel/re-run via API; list releases per channel with assets; toggle draft/publish |
| **Templates / New project** | Clone the template → new sibling folder (e.g. `C:\Code\projects\DevEcosystem\NewApp`), **smart token replace** (AssemblyName, RootNamespace, csproj filename, AppId/`-u`, `--packTitle`, `--packAuthors`, icon via `Assets/app.ico`, `UpdateService.GitHubRepoUrl`, window title, Home text, version → 0.0.1), then optional: `git init`, create GitHub repo (token), first commit, `dotnet build` smoke check |
| **Run fast** | Double-click the **DevDesk** desktop shortcut → `Scripts/launch-dev.ps1` builds (incremental `dotnet build -c Debug -p:Platform=x64`) and starts the exe. Never `dotnet run` from a terminal again. `Scripts/make-shortcut.ps1` (re)creates the shortcut |
| **Settings** | Roots to scan, ignore patterns, theme, default channel, platforms (x64/x86/ARM64), GitHub token (stored securely), branch→channel mapping |

### Online/offline behavior
- **Git operations:** local — always work.
- **GitHub reads:** REST via HttpClient (no heavy SDK), cached with TTL to
  dodge the 60 req/h unauthenticated limit; needs `GITHUB_TOKEN` (user setting,
  see security) for Actions/write ops.
- **No-GitHub projects (PIgen today):** everything local works; GitHub section
  shows an empty-state with "Add remote" hint — never an error wall.

---

## 5. Architecture

### Stack
- WinUI 3 (WindowsAppSDK 1.8.x) + .NET 10, unpackaged, self-contained — same as template
- **CommunityToolkit.Mvvm 8.x** (`ObservableObject`, `ObservableProperty`,
  `RelayCommand`) — the template roadmap (NEXT-DEV #5) wants MVVM; DevDesk
  establishes it
- **Microsoft.Extensions.DependencyInjection** for services
- Velopack 1.2 (DevDesk self-updates like the template) · Serilog (reused)
- GitHub: `System.Net.Http` + JSON only (no external GH SDK; fewer deps)
- Git: `LibGit2Sharp` for reads (history, status, branches, tags) **or**
  process-wrapper over installed `git` for everything (2.55 present) —
  decision: **process-wrapper for write ops, LibGit2Sharp optional for reads**;
  start with plain `git` invoke (portable, zero native deps, matches how the
  pipeline already shells out)

### Proposed layout (sibling of template → `C:\Code\projects\DevEcosystem\DevDesk`)
```
DevDesk/
  DevDesk.csproj                (clone of template csproj, renamed, v0.0.1)
  Program.cs / App.xaml(.cs)    (logging + Velopack bootstrap, reused)
  MainWindow.xaml(.cs)          (extended: eval-based nav, theme, flyouts)
  Models/
    ProjectModel.cs             (Project | Workspace, sub-projects)
    CommitInfo.cs / ReleaseInfo.cs / ActionRunInfo.cs / SettingsModel.cs
  Services/
    ProjectScanner.cs           (the detection/traversal engine)
    GitService.cs               (process wrapper: log/status/branch/tag/commit/push)
    GitHubService.cs            (REST: releases, actions, repo create; caching)
    ReleaseService.cs           (version bump, feed parse, release wizard logic)
    TemplateService.cs          (copy + token-replace + scaffold new app)
    BuildService.cs             (dotnet build/run/publish w/ streamed output)
    ProcessRunner.cs            (async process exec + output capture)
    SettingsService.cs          (JSON in %LocalAppData%\DevDesk + PasswordVault for token)
    ThemeService.cs             (System/Light/Dark + accent seeding)
    LoggingService.cs           (reused as-is)
    UpdateService.cs            (reused as-is)
  Pages/
    DashboardPage.xaml          (summary + project cards grid)
    ProjectDetailPage.xaml      (commits, branches, versions, releases, actions, actions run UI)
    ReleasesPage.xaml           (cross-project releases + feeds)
    GitHubPage.xaml             (cross-project actions/releases/branches)
    TemplatesPage.xaml          (new-project wizard)
    SettingsPage.xaml           (roots, ignores, theme, token, channels)
    DiagnosticsPage.xaml        (live log view + export — NEXT-DEV #4 pattern)
  Controls/                     (reusable: StatusBadge, VersionChip, CommitRow, ActionPill, EmptyState)
  Assets/app.ico                (own icon; keep template's untouched)
  Scripts/build-and-release.ps1 (its OWN copy for DevDesk releases)
  .github/workflows/release.yml (its own copy)
```

### Concurrency & UI rules
- All service calls `async`; heavy work (scanning, git, net) on background
  threads; marshal back with `DispatcherQueue`.
- Long-running ops (build, release) → cancellable; UI shows status + streamed
  log region; no modal blocking of the whole app.
- In-memory + disk cache layer for GitHub and scan results; "last refreshed"
  timestamps everywhere.

### Security
- GitHub token stored via **Windows Credential Manager (`Windows.Security.Credentials.PasswordVault`)**; never in plain JSON, never logged (reuse ResolveToken pattern; redact in log lines).
- Template token-replace must never seed secrets into the new repo.
- All `ProcessRunner` calls use argument lists (no string interpolation into shell commands) to avoid injection via weird folder names (e.g. `VS studio projects`).

---

## 6. Channels — one source of truth (fixes template gotcha #5)

Template today: CI offers `stable|beta|alpha`, UI offers `stable|beta|dev` —
a release to `alpha` is invisible to clients. DevDesk defines a single
`Channel` enum/list: **`stable | beta | dev`** (+ optional `alpha`) used in:
- the per-project release card (read feed `releases.<channel>.json`)
- the release wizard channel picker
- Settings: `branch → channel` mapping for the frontend (e.g. `main→stable`,
  `develop→beta`) so "release on a branch" = pick branch → it maps/filters channel.
Document this in DevDesk's own AGENTS.md so template consumers can converge.

---

## 7. Design language ("Windows 11 animated, see-through")

| Concern | Direction |
| --- | --- |
| Chrome | `MicaBackdrop` (Acrylic on overlays/dialogs) + extended tall title bar, nav-in-title-bar like template; rounded window |
| Theme | RequestedTheme System/Light/Dark; **Dark = deep black-navy** (`#0B0E14` base, panel `#141A26`, outline ok) with **"sea blue" accent** (`Accent #2D7FF9` / hover `#1E62C9`); **Light = white/very-light gray** with same sea-blue accent; meshed with WinUI `ThemeResource` system brushes so all controls adapt |
| Corners | Consistent **8px** radius resource (`ControlCornerRadius`) applied to cards/chips/buttons/dialogs; coloked `CornerRadius="8"` everywhere, smaller 6-4px only for tags |
| Motion | `Frame`-based page transitions (slide+fade ~200ms, ease-in-out), staggered entrance of dashboard cards (scale 0.98→1 + fade), `ItemsRepeater`/`ListView` item animations via `EntranceThemeTransition`, hover/pointer subtle elevation, `ProgressRing` + indeterminate shimmer for busy states |
| Components | NavigationView (compact pane, icon+label), `SettingsCard`/`Segmented`, `InfoBar`, `InfoBadge`, `Expander`, `ToggleSwitch`, `TeachingTip` for first-run hints — all stock WinUI 3 / CommunityToolkit (already referenced) |
| Feel | Snappy: virtualization via `ItemsRepeater`/`ListView` recycling, debounced refresh, optimistic updates, no janky layout thrash; keep every list item cheap |

---

## 8. Suggested phased roadmap ("don't do everything at once")

### Phase 0 — Foundation (the shell + the scanner)  ✅ done
**Did:** Scaffold `C:\Code\projects\DevEcosystem\DevDesk` from the template (clone + rename +
token replace, v0.0.1). Establish theme/branding (sea-blue accent, corners,
light/dark), extended nav shell (Dashboard, Projects, Templates, GitHub, DevDesk,
Settings, Diagnostics), persistence (SettingsService — theme), reuse logging +
Velopack. Built **ProjectScanner** + Dashboard cards (name, path, branch, last
3 commits, remote hint, version badges + sync check). **Open in Explorer /
open in VS from cards** (click a card → action menu; argument-list + ShellExecute
only). Moved the ecosystem together (`DevEcosystem/`), added
`Scripts/launch-dev.ps1` + `Scripts/make-shortcut.ps1` (desktop shortcut,
no-terminal run, instant reopen via build stamp), the `DevDesk.Tests/` project,
and the dedicated **DevDesk page** (this app's own workspace + components —
never listed among scanned projects).
**Exit criteria:** App shows every real project on this machine (including
`PIgen App` as a Workspace and not crashing on `VS studio projects`), light+dark
look right, `dotnet build -p:Platform=x64` green, app self-updates when
installed.
**Adapted for the scoped ecosystem:** the scan root is `DevEcosystem` (the
user's decision), so `PIgen App` and `VS studio projects` are NOT scanned — the
shape they prove (workspace repo with several projects + no remote, folder names
with spaces) is exercised by the `ProjectScannerTests` workspace/no-remote and
`ProjectOpenActions` tests instead. Build green (0 errors), **34/34 tests green**,
launcher verified (rebuild ~8 s, reopen <1 s), app window confirmed.

### Phase 1 — Git depth + versions + templates
**Do:** GitService read/write (full history pager, status, branch/tag list,
commit+push), csproj version extraction + **sync check** (all 3 fields),
`TemplateService` "New project from template" wizard (custom name/path, token
replacement, optional git init + first commit + smoke build), BuildService
(build/run/publish from UI with live output).
**Exit criteria:** Create a brand-new app from the template via UI only, build
it, and run it; someone can replace terminal typing for basic git/build on any
supported project.

### Phase 2 — GitHub + releases
**Do:** GitHubService (releases per channel via feed JSON **and** API, Actions
runs with status + cancel/re-run, open in browser, draft/publish toggle),
ReleaseService release wizard (version suggestion, channel, prerelease, notes →
bump csproj → commit → tag → push **or** run `build-and-release.ps1`), release
monitor ("waiting on run #n…" → green banner), token via PasswordVault,
branch→channel mapping setting.
**Exit criteria:** Ship v0.0.2 of DevDesk using DevDesk itself (UI-only), watch
the GitHub Actions run from the UI, and see the release land on its channel.

### Phase 3 — Polish & hardening
**Do:** Motion pass (page transitions, card entrances), scan refinements
(nested-solution corner cases, watcher-driven updates), GitHub caching + TTL +
offline states, DiagnosticsPage (live log export — NEXT-DEV #4 pattern),
single-instance enforcement, unit tests for scanner/version parsing (xunit —
NEXT-DEV #3 pattern), CI test step in DevDesk's own `release.yml`.
**Exit criteria:** Feels "Windows 11 premium", zero error-walls on edge repos,
tests green in CI.

---

## 9. Explicitly out of scope (for now)

- Editing code files / IDE feature within the app (open-in-VS is enough)
- Cross-platform (WinUI 3 = Windows-only, per brief)
- Team/multi-user features, project sharing, cloud sync
- Code review features (PR listing is a nice-to-have for a later phase)

---

## 10. Risks & gotchas (learned from the template + this scan)

1. **"VS studio projects" has spaces** → all process calls must use argument
   **lists**, never interpolated shell strings.
2. **PIgen App = repo-in-folder containing 4 projects, no remote** → scanner
   must model Workspaces; UI must never assume a remote exists.
3. **`.slnx` (new XML solution format)** is now present — don't rely on `.sln`
   parsing; treat both tokens equally.
4. **WindowsAppSDK self-contained + rename** (new app from template) → the
   rename must also touch `WindowsPackageType` defaults, icon, `-u` AppId,
   publish dir names, and Velopack `GithubSource` URL or the clone silently
   checks the template's update feed.
5. **Version fields out of sync** (AGENTS: keep `<Version>`,
   `<AssemblyVersion>`, `<FileVersion>` equal) → surfacing a badge is the
   minimum; release wizard should rewrite all three together.
6. **Channel vocabulary** stable|beta|dev vs CI's `alpha` → single Channel
   source-of-truth (§6) kills the mismatch inside DevDesk and documents it for
   generated apps.
7. **GitHub rate limits** (60/h unauthenticated) → mandatory caching + token
   hint; never fail a scan because GitHub is rate-limited (repository data is
   local; GH enrichments are best-effort).
8. **Don't duplicate the release pipeline** → DevDesk always delegates vpk
   work to the project's own `Scripts/build-and-release.ps1`; that keeps
   "single source of truth" intact (template convention).
9. **Keep the template a good template** → all renames/replacements when
   scaffolding a new app must never mutate `DevTem WinUI 3` itself (copy-only).
   New-app naming/token logic must be tested against a scratch copy, not the
   original.
10. **Scan hygiene** → skip `bin/obj/Releases/Logs/.vs` by default; scans must
    be incremental (watcher + cache) so a big `C:\Code\projects` never slows
    startup.