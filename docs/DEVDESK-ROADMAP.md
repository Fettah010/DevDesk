# DevDesk — Roadmap v0.1.0 → v1.0.0

> **Status: ACTIVE ROADMAP.** This document records the initial plan and baseline
> from 2026-10-30. v0.1.0-beta is the last published release; v0.2.0 was
> implemented locally and folded into v0.3.0, which is implemented under
> `docs/versions/V0.3.0.md` and not yet released. This file remains the map;
> each release is implemented from its own work order (see §10).
>
> **Supersedes:** `docs/DEVDESK-PLAN.md` (the Phase-0 plan; it is deleted in the
> working tree at the time of writing and can be recovered with
> `git show HEAD:docs/DEVDESK-PLAN.md`). This roadmap absorbs everything still
> relevant from it and replaces its three-phase sketch with ten real releases.
>
> **Baseline:** WinUI 3 / .NET 10 / unpackaged self-contained, `DevDesk.csproj`
> `0.0.1`, 34 tests green, build 0 warnings / 0 errors, no upstream remote.
>
> **Read in this order:** §0 (what ships when) → §1 (what is actually there today)
> → §3–§6 (the architecture every version builds on) → §7 (the releases) →
> §8 (traceability) → §9 (decisions only the owner can make).

---

## Table of contents

- [0. Executive summary](#0-executive-summary)
- [1. Baseline analysis (evidence, 2026-10-30)](#1-baseline-analysis-evidence-2026-10-30)
- [2. Product definition](#2-product-definition)
- [3. Target architecture](#3-target-architecture)
- [4. Domain model & contracts](#4-domain-model--contracts)
- [5. UX system](#5-ux-system)
- [6. Engineering standards](#6-engineering-standards)
- [7. The releases (v0.1.0 → v1.0.0)](#7-the-releases-v010--v100)
- [8. Traceability, risks, metrics](#8-traceability-risks-metrics)
- [9. Open decisions (owner input required)](#9-open-decisions-owner-input-required)
- [10. Operating model](#10-operating-model)

---

## 0. Executive summary

DevDesk is a **local-first Windows developer console** for a folder of repositories:
one screen for branch/commit/version/health, then build, run, commit, release and
scaffold — without the terminal. Today it is a convincing shell with one genuinely
real subsystem (the read-only scanner). This roadmap turns that shell into a product.

### 0.1 The release ladder

| Version | Theme | Headline (what a user can newly do) | Size | Risk |
| --- | --- | --- | --- | --- |
| **v0.1.0** | Trustworthy base | Install it once, it updates itself; one scan feeds every page; settings/logs are real | L | Medium |
| **v0.2.0** | Git, safely | Commit, branch, pull, push from the UI with guardrails and a real history | L | Medium |
| **v0.3.0** | Build & run cockpit | Build/run/publish any project with live, searchable output and clickable errors | L | Medium |
| **v0.4.0** | Template forge | Create, build and run a brand-new app from the template; dry-run + rollback | M | High |
| **v0.5.0** | GitHub, read-only | Actions runs, releases per channel and PRs at a glance; token in Credential Manager | L | Medium |
| **v0.6.0** | Ship from DevDesk | Release wizard: bump → notes → tag → CI → monitor → published, channels unified | L | High |
| **v0.7.0** | Scale & multi-root | Many roots, many repos, worktrees, search/filter, instant warm start | M | Medium |
| **v0.8.0** | Command center | Command palette, keyboard map, activity center, tray + notifications | M | Low |
| **v0.9.0** | Release candidate | Accessibility, motion, docs, onboarding, security and perf hardening | M | Medium |
| **v1.0.0** | Stable | Frozen contracts, signed installer, proven upgrade path, stable channel | S | Low |

Sizes are T-shirt estimates for one focused developer; they exist to show weight,
not to promise dates. Every release ends with a **demonstrable exit demo** (§7) and
ships to the `beta` channel; only `1.0.0` ships to `stable`.

### 0.2 The shape of the product at 1.0

- **One window, no terminal.** Every daily repo action (inspect → build → test →
  commit → push → release → scaffold) is a button with a progress surface.
- **Instant.** Warm start paints project cards from cache in well under a second;
  the network is never on the startup path.
- **Honest.** No dead buttons, no fake rows, no silent failures. Every screen has
  a loading, empty, offline, partial and error state that says what to do next.
- **Safe.** Destructive operations are explicit, previewable and confirmed; scans
  never write; secrets never touch JSON or logs; the template is never mutated.
- **Local-first.** Git reads, builds and scaffolding work with no network. GitHub
  is an enrichment that degrades into a clearly-labelled cache.
- **Native.** Mica, sea-blue light/dark themes, 8px radius, keyboard-complete,
  Narrator-correct, high-contrast and 200% text scaling clean.
- **Self-hosting.** DevDesk manages DevDesk: its own releases are produced by
  DevDesk and it updates itself through Velopack.

### 0.3 The five pillars (used to judge every version)

1. **Trust** — nothing on screen is a placeholder; every failure has a next step.
2. **Safety** — no command ever runs without the user knowing exactly what it does.
3. **Speed** — cache first, scan incrementally, never block the UI thread.
4. **Local-first** — the app is fully useful offline; the network is optional.
5. **Craft** — WinUI idioms, motion with intent, keyboard and accessibility parity.

---

## 1. Baseline analysis (evidence, 2026-10-30)

### 1.1 Verified state

| Fact | Value | How it was verified |
| --- | --- | --- |
| Version fields | `0.0.1` / `0.0.1.0` / `0.0.1.0` in `DevDesk.csproj:22-24` | file read |
| `InformationalVersion` | absent | file read |
| Build | **0 warnings, 0 errors** | `dotnet build DevDesk.csproj -c Debug -p:Platform=x64` |
| Tests | **34 passed, 0 failed, 0 skipped** (12 s) | `dotnet test DevDesk.Tests -p:Platform=x64` |
| Toolchain | .NET SDK `10.0.403`, git `2.56.0.windows.3`, `gh` present at `D:\Program Files\GitHub CLI\gh.exe` | shell |
| `vpk` (Velopack CLI) | **not installed** | `Get-Command vpk` |
| DevDesk git remote | **none** (local repo only, branch `main`) | `git remote -v` |
| Release output | no `Releases/` produced yet | folder listing |
| Framework | `Microsoft.WindowsAppSDK 1.8.260804001`, `Microsoft.Windows.SDK.BuildTools 10.0.28000.2705` | csproj |
| Update library | `Velopack 1.2.0` | csproj |
| UI stack | WinUI 3, unpackaged, self-contained, Mica, custom title bar, sea-blue theme | `DevDesk.csproj`, `App.xaml`, `MainWindow.xaml` |
| Pages | 7: Dashboard, Projects, Templates, GitHub, DevDesk (self), Settings, Diagnostics | `Pages/` |
| View models | **none**; 0 `INotifyPropertyChanged`; all logic in code-behind | grep |
| Accessibility hooks | **0** `AutomationProperties` | grep |
| Localization hooks | **0** `x:Uid`, **0** `.resw` files | grep |
| Keyboard accelerators | **0** `KeyboardAccelerator` | grep |
| Cancellation | only in `GitRunner` (timeout) and `UpdateService` | grep |

### 1.2 What genuinely works today (do not rebuild it)

- `Services/ProjectScanner.cs` — depth-2 walk of `C:\Code\projects\DevEcosystem`,
  repo/marker detection, branch, dirty, behind, remote host, last 3 commits,
  csproj version + 3-field sync check, self-as-workspace, never throws on bad
  folders. Backed by 34 tests including real temp git repos.
- `Services/GitRunner.cs` — argument lists, redirected output, 15 s timeout,
  never throws (exit-code path). This is the correct foundation for `GitService`.
- `Services/ProjectOpenActions.cs` — ShellExecute open-in-Explorer / open-in-VS,
  deep marker discovery, argument-list safe.
- `App.xaml` sea-blue theme dictionaries + `DevCardStyle` + 8px radius.
- `MainWindow` Mica + tall custom title bar + NavigationView shell.
- `Scripts/launch-dev.ps1` (build stamp, progress window, instant reopen) and
  `Scripts/make-shortcut.ps1` (create-once desktop shortcut). **Keep working** —
  they are the product's core promise.
- Serilog logging, Velopack bootstrap, settings persistence (theme only).

### 1.3 Placeholder inventory (every dead end, and where it dies)

| Surface | Current behaviour | Fixed in |
| --- | --- | --- |
| Projects → Overview/Commits/Releases/Actions tabs | Commits/Releases/Actions are hardcoded rows; header buttons show "not wired" InfoBar | 0.2 (tabs), 0.5 (releases/actions) |
| Projects → Build / Open terminal / Push header buttons | InfoBar "BuildService / GitService land in Phase 1" | 0.2 / 0.3 |
| Projects → filter box | placeholder text only | 0.7 |
| Templates → Create / Browse | InfoBar "lands in Phase 1" | 0.4 |
| GitHub → refresh / token | InfoBar "Phase 2" | 0.5 |
| Settings → token, add root, check updates | InfoBars | 0.5 (token), 0.7 (roots), 0.1 (updates) |
| Diagnostics → log list | 9 hardcoded sample lines | 0.1 |
| DevDesk → component action buttons | InfoBar "not wired yet" | 0.3 |
| Dashboard → card actions | two-item flyout only (Explorer / VS) | 0.2+ grows into a real context menu |
| Self-update | silently skipped unless installed; source URL points at the **template** repo | 0.1 |

### 1.4 Findings (each one becomes roadmap work)

| # | Finding | Evidence | Impact | Fixed in |
| --- | --- | --- | --- | --- |
| **F1** | DevDesk has **no upstream remote**, and both the updater and the release script point at the template repository | `git remote -v` empty; `Services/UpdateService.cs:17` and `Scripts/build-and-release.ps1:40` = `Fettah010/winui-3-easy-template` | A DevDesk release would upload into the template's GitHub Releases and DevDesk would update from the template's feed | 0.1.0 |
| **F2** | Version identity is incomplete; prereleases are invisible | csproj has three fields, no `InformationalVersion`; `AppInfo` reads `AssemblyVersion` | UI cannot distinguish `0.6.0-beta` from `0.6.0`; updater channel logic has nothing to read | 0.1.0 |
| **F3** | The UI framework is **out of servicing** | WASDK `1.8.260804001`; Microsoft's release-channel table: 1.8 end of servicing **2026-09-24**, current stable **2.6.4** (2026-10-27) | Shipping a 1.0 product on an unsupported framework is a support and security liability | 0.1.0 |
| **F4** | The release pipeline has **never run** on this machine | `vpk` not on PATH; no `Releases/`; publish dir in the script assumes a `Platform`-neutral path that nobody has verified | "Install and self-update" is unproven, so every later version inherits an unproven foundation | 0.1.0 |
| **F5** | Every page scans independently | `DashboardPage.xaml.cs:13`, `ProjectsPage.xaml.cs:21`, `DevDeskPage.xaml.cs:12` each construct `new ProjectScanner()` and scan | 2–3× duplicated git work per session, inconsistent snapshots, no shared cache | 0.1.0 |
| **F6** | Scanner correctness gaps | `.git` **file** (worktrees/submodules) is not detected (`Directory.Exists`); self path hardcoded to `<root>\DevDesk`; `CountUnder` walks `bin/obj/node_modules` with no hygiene; no cancellation/progress; `Directory.GetFiles(...).FirstOrDefault()` picks csproj non-deterministically | Silent misses on worktrees, wrong self detection if the repo is renamed/moved, slow scans on big workspace repos | 0.1.0 (core) / 0.7 (scale) |
| **F7** | No cache or incremental model | `ScanAsync()` always walks + spawns git; nothing persisted | Warm start is always a full scan; a large root will feel slow | 0.1.0 (cache) / 0.7 (watchers) |
| **F8** | Settings are one field and not durable | `SettingsService.cs` persists only `theme`; `File.WriteAllText` (non-atomic); no schema version | A corrupt write loses settings; every later feature needs settings | 0.1.0 |
| **F9** | No MVVM, no shared state, no view-model tests | 0 `INotifyPropertyChanged`; logic lives in `Page` code-behind | Every new feature is untestable UI code; pages cannot be reloaded without a rescan | 0.1.0 (spine) → adopted per page |
| **F10** | Placeholders are indistinguishable from features | see §1.3 | Trust: the app feels like a demo, and a user cannot tell what is real | 0.1.0 (labels) → each version removes its own |
| **F11** | Channel vocabulary is split | CI `release.yml:31-34` offers `stable/beta/alpha`; README/plan/UI say `stable/beta/dev` | Releases can land on a channel no client reads (the template's gotcha #5, inherited) | 0.1.0 (constants) / 0.6.0 (CI + wizard) |
| **F12** | There is no authentication path | `UpdateService` reads only `GITHUB_TOKEN` env var; Settings says "Phase 2" | Unauthenticated GitHub = 60 req/h; Actions/releases can never be reliable | 0.5.0 |
| **F13** | Accessibility and localization are absent | 0 AutomationProperties, 0 `x:Uid`, 0 `.resw`, 0 accelerators | The app cannot be called "premium" or shipped broadly; Narrator users are locked out | 0.9.0 |
| **F14** | No window/session hygiene | fixed `Resize(1240, 800)`, no minimum size, no placement persistence, no single-instance guard, last page not restored | Double-clicking twice opens two apps that fight over the same cache/log | 0.1.0 |
| **F15** | CI never tests | `.github/workflows/release.yml` is release-only (tag/dispatch) | Nothing stops a broken commit from being tagged | 0.1.0 |
| **F16** | Documentation drift | `AGENTS.md`/`README.md` still say "Phase 0" and link the deleted plan; the ecosystem now holds **6 repos** (`DevDesk`, `DevCli`, `DevFlutt`, `DevTem WinUI 3`, `DevTemWPF`, `Formixa`) plus 2 non-repos (`copilot-worktrees` empty, `Dzirivo` two markdown plans); README claims MIT but there is **no `LICENSE`**; no `CHANGELOG.md` | New agents and the future owner inherit wrong facts; legal ambiguity on the MIT claim | 0.1.0 |
| **F17** | Failure UX is ad-hoc | each page builds its own `InfoBar` strings; no operation queue or history | Users cannot see what happened, retry, or copy an error | 0.3.0 (activity center) / 0.8.0 (notifications) |
| **F18** | No performance measurement | no budgets, no timings, no startup instrumentation | "Fast" stays an opinion; regressions go unnoticed | 0.1.0 (instrument) / 0.9.0 (gates) |

### 1.5 Environment and ecosystem drift (as of 2026-10-30)

- `C:\Code\projects\DevEcosystem` contains six git repos and two plain folders.
  None of the non-DevDesk repos has a csproj at its root except `Formixa`;
  `DevTemWPF` and `DevCli` are `.slnx`-rooted, `DevFlutt` is markerless at the root
  (still detected as a repo). `Dzirivo` is a planning folder and is correctly
  skipped. `copilot-worktrees` is empty today but is exactly the folder shape that
  will later contain **git worktrees** (`.git` as a *file*), which F6 misses.
- The template `DevTem WinUI 3` has now completed its own v0.1.0 → v1.0 program
  (`docs/V0.1.0-ADVANCED-PLAN.md` … `docs/V1.0-ADVANCED-PLAN.md`). DevDesk should
  borrow its **operating model** (one work-order file per release, evidence tables,
  explicit non-goals, "nothing commits/tags without a human request"), not its code.
- Platform context: Windows App SDK stable is **2.6.4** (Oct 2026) and WinUI 3 is
  open source. Microsoft's own multi-repo dashboard **Dev Home was retired in May
  2025**; its remaining developer features moved into Windows Advanced Settings,
  not into a repository dashboard. The niche DevDesk occupies is genuinely
  unclaimed on Windows.

---

## 2. Product definition

### 2.1 Problem

A developer with several repositories on one machine keeps a terminal, a browser
tab, GitHub's website and Visual Studio open to answer four recurring questions:
*what state is each repo in, what changed, does it build, and what is released?*
Each tool knows one slice. DevDesk answers all four in one window, on one machine,
with no cloud account required.

### 2.2 Audience

- **Primary (v0.x):** the owner — one Windows machine, one ecosystem folder,
  6+ repos, GitHub-hosted releases, WinUI 3 templates.
- **Secondary (v1.0):** any Windows developer with a `C:\Code`-style folder who
  wants a dashboard instead of a shell prompt.

Design for the primary user; never make the secondary user's generality slow down
the primary case.

### 2.3 Jobs to be done

| # | When I… | I want to… | Delivered by |
| --- | --- | --- | --- |
| J1 | open my machine in the morning | see every repo's branch, health, version and last commit at a glance | 0.1 (exists) |
| J2 | switch context | find a repo by typing part of its name | 0.7 (search), 0.8 (palette) |
| J3 | change code | build/run and read the errors without a terminal | 0.3 |
| J4 | finish a change | stage, write a message, commit and push with guardrails | 0.2 |
| J5 | cut a version | bump, tag, watch CI and confirm the release landed | 0.6 |
| J6 | start something new | scaffold an app from the template in a few clicks | 0.4 |
| J7 | check CI | see the last Actions run and any open PR per repo | 0.5 |
| J8 | debug the tool | read real logs, export them, understand a failure | 0.1 (logs), 0.3 (activity) |
| J9 | work offline | keep using everything local and see clearly stale GitHub data | 0.5 |
| J10 | use only the keyboard | reach every action and screen | 0.8–0.9 |

### 2.4 Product principles

1. **Local truth first.** Git and the filesystem are the source of truth; GitHub is
   enrichment. If the network is down, the app still works and says what is stale.
2. **One scan, many views.** A single catalog owns project state; pages observe it.
3. **Nothing destructive without a preview.** Every write operation shows the exact
   command and asks once, in proportion to risk.
4. **Never freeze.** Anything over ~150 ms gets a progress surface and a cancel path.
5. **No dead ends.** A control either works, is disabled with a reason, or is absent.
6. **Cache, then verify.** Show known state immediately, refresh behind it.
7. **Boring technology.** Git CLI, `dotnet`, `HttpClient`, JSON. No new dependency
   without a named job and a removal story.
8. **The template is read-only.** Scaffolding copies; it never mutates the source.
9. **Secrets stay in the OS.** Tokens in Credential Manager; never JSON, never logs.
10. **Premium is a behaviour, not a colour.** Keyboard, Narrator, contrast, scaling
    and motion are part of "done", not a polish phase.

### 2.5 Permanent non-goals (do not revisit before 1.0)

- A code editor or IDE features (open in Visual Studio is the boundary).
- Hosting CI or acting as a build server.
- Team features: accounts, sharing, cloud sync, comments, code review.
- Cross-platform (WinUI 3 is Windows-only by design).
- A plugin/extension SDK.
- **LLM/AI features.** DevDesk's value is deterministic state and safe actions.
  A "summarise my repo" chat box is not a 1.0 feature and would compromise the
  local-first, offline, no-telemetry promise.
- Mobile/web clients.

### 2.6 What "1.0" means (the stability contract)

At `1.0.0` these are frozen and only extended compatibly:

1. **Settings schema v1** (file location, keys, migration path).
2. **Cache file schema** (safe to delete, never authoritative).
3. **Channel vocabulary** `stable | beta | dev` and the tag→channel mapping.
4. **Service contracts** used by pages (`IProjectCatalog`, `IGitService`,
   `IBuildService`, `IReleaseService`, `IGitHubService`, `ITemplateService`).
5. **Command palette ids** and the keyboard map.
6. **Release artifact shape** (Velopack app id `DevDesk`, installer name, feed).
7. **Upgrade compatibility**: any `0.x` install can update to `1.0.0` without
   losing settings, logs or cache (cache may be discarded and rebuilt).

### 2.7 Success metrics (targets, measured per §6.3)

| # | Metric | Target at 1.0 |
| --- | --- | --- |
| M1 | Warm start → interactive cards | < 700 ms |
| M2 | Cached first paint (window visible) | < 400 ms |
| M3 | Full scan, 8 real repos | < 1.5 s |
| M4 | Full scan, 50 repos (SSD) | < 3 s |
| M5 | Memory, 100 repos loaded | < 300 MB |
| M6 | UI thread blocked by any operation | < 50 ms |
| M7 | Crash-free sessions (local log audit) | > 99.5% over last 50 runs |
| M8 | Dead-end controls | 0 |
| M9 | Test count / logic coverage of services | ≥ 150 tests, ≥ 80% lines in `Services/` |
| M10 | Keyboard reachability | 100% of actions |

---

## 3. Target architecture

### 3.1 Shape

```
┌──────────────────────────────────────────────────────────────┐
│  Views (Pages/*.xaml)          plain XAML, no business logic  │
├──────────────────────────────────────────────────────────────┤
│  ViewModels (ViewModels/*)     ObservableObject, commands,    │
│                                page state machine             │
├──────────────────────────────────────────────────────────────┤
│  Services (Services/*)         one job each, no UI types      │
│  ProjectCatalog · ScanEngine · GitService · BuildService      │
│  ReleaseService · GitHubService · TemplateService             │
│  SettingsService · LoggingService · UpdateService             │
├──────────────────────────────────────────────────────────────┤
│  Infrastructure                ProcessRunner · GitRunner      │
│                                HttpPipeline · JsonStore       │
│                                TimeProvider · PathGuard       │
└──────────────────────────────────────────────────────────────┘
```

Rules:

- A `Page` may only: bind, forward commands, and host XAML. No `Process`, no
  `HttpClient`, no `File` writes.
- A service never references `Microsoft.UI.Xaml` (keeps it unit-testable and
  prevents UI-thread coupling).
- Infrastructure is injectable/fakeable: `IProcessRunner`, `HttpMessageHandler`,
  `TimeProvider`, `IFileSystem` (only where a test needs it).

### 3.2 Service responsibilities

| Service | Owns | Never does | Introduced |
| --- | --- | --- | --- |
| `ProjectCatalog` | one in-memory snapshot of all projects + self, refresh orchestration, cache read/write, change notifications | spawns git directly (delegates to `ScanEngine`) | 0.1.0 |
| `ScanEngine` | walking, detection rules, `GitRunner` read calls, model building | caching, UI | 0.1.0 (extracted from `ProjectScanner`) |
| `GitService` | every write + deep read (log pager, branches, tags, diff, stage, commit, push, pull) | caching project state (catalog refreshes after) | 0.2.0 |
| `BuildService` | build/run/publish profiles, process lifecycle, output streaming, problem parsing | deciding what to build (VM does) | 0.3.0 |
| `TemplateService` | template discovery, token map, copy/rename plan, dry-run, rollback, smoke build | touching the template source, network | 0.4.0 |
| `GitHubService` | REST reads/writes, auth, ETag cache, rate-limit state | blocking scans or startup | 0.5.0 |
| `ReleaseService` | channel model, semver suggestion, version bump, tag/push, CI monitor, feed parse | running vpk itself (delegates to the project's script) | 0.6.0 |
| `SettingsService` | schema v1, atomic writes, migrations, change events | holding secrets | 0.1.0 |
| `LoggingService` | Serilog + in-memory ring buffer + file | being called from hot loops at INF | 0.1.0 |
| `UpdateService` | Velopack check/download/apply, channel resolution | UI prompts (App owns dialogs) | 0.1.0 |
| `ActivityCenter` | running/finished operations, retry, history | business logic | 0.3.0 |
| `CommandRegistry` | palette entries, shortcuts, enablement | direct UI calls | 0.8.0 |

### 3.3 Concurrency model

- **One scan at a time.** `ProjectCatalog.RefreshAsync` cancels the previous run
  (`CancellationTokenSource` linked), so a double-click cannot corrupt state.
- **Bounded parallelism.** Per-repo git calls run with `SemaphoreSlim` (default
  `min(4, CPU)`); disk-bound, not thread-bound. Never `Task.Run` around an already
  async method (today's `DashboardPage` does exactly that — remove it).
- **All UI mutations on the dispatcher.** Services return data; the VM marshals.
- **Long operations are cancellable** (scan, build, publish, release monitor, GitHub
  refresh) and surface a cancel button in the progress surface.
- **Process kills are tree kills** (`Kill(entireProcessTree: true)`), with a
  graceful timeout before force.
- **No blocking waits on the UI thread** — including `UpdateService`'s current
  `SemaphoreSlim.Wait()` inside a property getter (fix in 0.1.0).

### 3.4 Freshness model

| Data | Source of truth | TTL / trigger | Shown when stale |
| --- | --- | --- | --- |
| Repo state (branch, dirty, behind, commits, version) | local git + fs | cached; refreshed on demand, after any write op, on file-watcher debounce (0.7) | "as of HH:mm" label |
| Releases / Actions / PRs | GitHub REST | 5 min TTL, ETag revalidation, manual refresh | "cached HH:mm" badge + offline chip |
| Build results | `BuildService` history file | until next build | "last build HH:mm" + exit code |
| Scan results | `ProjectCatalog` | persisted cache + memory | cards render immediately, refresh behind |

Rule: **never** put a network call on the startup path. GitHub enrichment starts
after the window is interactive, per visible project only.

### 3.5 Error model (the never-crash contract)

- Services return `Result<T>` (`Value`, `Error` with `Kind`, `Message`, `Detail`,
  `Hint`) instead of throwing for expected failures.
- Unexpected exceptions are caught once at the service boundary, logged with a
  correlation id, and surfaced as a user message + "Open log" action.
- The UI never shows a raw exception string; it shows what failed, what to do,
  and a details expander with the technical text.
- `InfoBar` is for page-level, transient messages. Persistent failures appear in
  the **activity center** so they are not lost when the page changes.
- Global handlers in `Program.cs`/`App.xaml.cs` stay (they are good); add a
  crash marker file so the next launch can offer "previous session ended
  unexpectedly — open the log?".

### 3.6 Process execution contract (the only way to run anything)

1. `ProcessStartInfo` with `ArgumentList` only — **never** a composed command
   string (AGENTS gotcha #7; paths contain spaces: `DevTem WinUI 3`,
   `VS studio projects`).
2. `WorkingDirectory` validated (`Directory.Exists`) and never user-interpolated
   into an argument.
3. `CreateNoWindow = true`, stdout/stderr redirected, UTF-8 decoding, no shell.
4. Per-class timeouts: git read 15 s, git network 120 s, build 30 min (cancelable),
   publish 30 min, `dotnet` first-run warmup tolerated.
5. Kill process tree on cancel; report `Canceled` distinctly from `Failed`.
6. Log the executable + arguments (redacted) and the exit code at INF, output at
   DBG/VRB.
7. Never prompt interactively: `GIT_TERMINAL_PROMPT=0`, `DOTNET_CLI_TELEMETRY_OPTOUT=1`,
   `DOTNET_NOLOGO=1`, `GIT_OPTIONAL_LOCKS=0` for reads.

### 3.7 Git: CLI, not LibGit2Sharp

Decision: keep the **git CLI** for reads and writes.

- Zero native dependencies, identical semantics to the user's terminal, and the
  release scripts already assume git.
- Worktrees, submodules, credential-manager prompts and hooks behave exactly as
  the user expects.
- LibGit2Sharp would duplicate write semantics, add native binaries to a
  self-contained WinUI package, and still need the CLI for hooks/credential flows.

Reads use `--porcelain=v2` (0.1.0) and `-c core.quotepath=false`
(unicode paths), `--no-optional-locks` (don't fight an open IDE), and
`-z`/`\0` parsing where available to survive newlines in paths.

### 3.8 GitHub: `HttpClient` + `System.Text.Json`

Decision: no Octokit. The surface is small (repo, runs, releases, PRs, rate
limit), and a hand-written client lets us own ETag caching, backoff, redaction
and cancellation. Conditional requests (`If-None-Match`) count against the rate
limit only on 200, so cache-first is mandatory for the 60 req/h anonymous case
and kind to the 5000/h authenticated case.

### 3.9 Persistence

| File | Location | Contents | Rules |
| --- | --- | --- | --- |
| `settings.json` | `%LocalAppData%\DevDesk\` | schema version, theme, roots, ignores, channel, platform, window placement, feature flags | atomic write (temp + `File.Replace`), never secrets, corrupt → back up + defaults |
| `scan-cache.json` | same | last snapshot per root + timestamps | safe to delete; versioned; ignored on schema mismatch |
| `github-cache.json` | same | ETag + payload per repo/resource, timestamps | TTL, size cap, purge on token change |
| `build-history.json` | same | last N builds (project, profile, start, duration, exit, first error) | capped ring (e.g. 200) |
| `crash.marker` | same | last session start time | cleared on clean exit |
| `Logs\applog-YYYYMMDD.log` | next to exe (today) | Serilog rolling | keep 14 days; move to `%LocalAppData%` in 0.1.0 (install dir may be read-only) |
| Token | **Windows Credential Manager** | GitHub PAT | `PasswordVault` primary, DPAPI file fallback if vault is unavailable; never JSON/log |

### 3.10 Composition and navigation

- `AppServices` static composition root exposing typed singletons (`IProjectCatalog
  Catalog { get; }`, …), created in `Program.Run`/`App` ctor after logging.
- **No DI container at 0.1.0**: `Frame.Navigate` needs parameterless page
  constructors, so a container buys little and costs indirection. Revisit only if
  multi-window or plugin work arrives (trigger recorded in §9).
- Pages get their VM from `AppServices`/`ViewModelLocator`; VMs are cheap and
  recreated per navigation, but the **catalog is shared**, so a page change is
  never a rescan.
- `NavigationService` wraps `ContentFrame`, records the route in settings, and
  drives the back stack (0.8).

### 3.11 Target folder layout (final, not all at once)

```
DevDesk/
  Program.cs, App.xaml(.cs), MainWindow.xaml(.cs)
  Models/            ProjectModel, CommitInfo, BranchInfo, BuildResult,
                     ReleaseInfo, ActionRunInfo, PullRequestInfo, TemplateDescriptor
  Services/          ProjectCatalog, ScanEngine, GitService, BuildService,
                     TemplateService, GitHubService, ReleaseService,
                     SettingsService, LoggingService, UpdateService, ActivityCenter
  Infrastructure/    ProcessRunner, GitRunner, HttpPipeline, JsonStore, PathGuard, Clock
  ViewModels/        DashboardViewModel, ProjectDetailViewModel, TemplatesViewModel,
                     GitHubViewModel, ReleaseViewModel, SettingsViewModel,
                     DiagnosticsViewModel, SelfViewModel, CommandPaletteViewModel
  Controls/          ProjectCard, StatusBadge, VersionChip, CommitRow, LogConsole,
                     OperationCard, EmptyState, BusyOverlay, TagChip, KpiTile
  Pages/             one XAML page per screen (thin)
  Converters/        value → brush/visibility/severity
  Assets/, Scripts/, docs/, DevDesk.Tests/
  .github/workflows/ ci.yml (build+test) · release.yml (tag → publish)
```

---

## 4. Domain model & contracts

### 4.1 Entities

| Entity | Key fields | Notes |
| --- | --- | --- |
| `ProjectModel` | Name, Path, Kind (Project/Workspace/Self), Branch, Version, VersionMismatch, Remote (host, url, owner/repo), Dirty, Ahead/Behind, StatusText, RecentCommits, Components | extended from today's model; becomes immutable snapshot |
| `RepoSnapshot` | Project + `ScannedAt` + `Head` sha + `IsStale` | what the catalog caches |
| `CommitInfo` | Hash, ShortHash, Subject, Author, Email?, Date (relative + absolute), Parents | 0.2 adds paging + graph metadata |
| `BranchInfo` | Name, IsCurrent, IsRemote, Ahead, Behind, Upstream, TipShort | 0.2 |
| `FileChange` | Path, Status (M/A/D/R/U), Staged, Additions/Deletions? | 0.2 |
| `BuildProfile` | Name, Configuration, Platform, Target, ExtraArgs | 0.3 |
| `BuildResult` | Project, Profile, StartedAt, Duration, ExitCode, Status, FirstError, LogPath | 0.3 |
| `ReleaseInfo` | Channel, Version, Tag, IsPrerelease, PublishedAt, Assets, Notes?, Source (feed/API) | 0.5/0.6 |
| `ActionRunInfo` | Id, Workflow, Branch, Event, Status, Conclusion, StartedAt, Url, RunNumber | 0.5 |
| `PullRequestInfo` | Number, Title, Author, State, IsDraft, Head→Base, Checks, Url, UpdatedAt | 0.5 |
| `TemplateDescriptor` | Path, Name, Version, TokenMap, RenamePlan, DetectedMarkers, Warnings | 0.4 |
| `ScanSettings` | Roots (path, depth, ignores), Channel, Platform, Theme, Window | 0.1/0.7 |
| `Operation` | Id, Kind, Title, Status (Queued/Running/Succeeded/Failed/Canceled), StartedAt, FinishedAt, Error, CanRetry | 0.3 |

### 4.2 Invariants (tested)

1. `Catalog.Projects` never contains the self repo; `Catalog.Self` is exactly one.
2. One card per repository root — worktrees/submodules never duplicate a card.
3. `Version`, `AssemblyVersion`, `FileVersion` and `InformationalVersion` always
   move together; a mismatch is surfaced, never silently fixed.
4. Channel is exactly one of `stable|beta|dev` in code, UI, CI and tags.
5. A scan never writes to any repository (test: fs timestamps + git status clean).
6. A scan never touches the network (test: injected HTTP handler that throws).
7. No secret appears in settings, cache files, logs or crash reports (grep test).
8. A cancelled operation leaves no child process and no partial file (temp+rename
   for writes; kill tree for processes).
9. Scaffolding never writes inside the template path (path guard test).
10. Every user-visible string that can fail has a state, a message and a next step.

### 4.3 Settings schema v1

```json
{
  "schemaVersion": 1,
  "theme": "System",
  "channel": "beta",
  "platform": "x64",
  "roots": [
    { "path": "C:\\Code\\projects\\DevEcosystem", "depth": 2,
      "ignores": ["copilot-worktrees", "**/node_modules", "**/bin", "**/obj"] }
  ],
  "defaultReleaseChannel": "beta",
  "window": { "x": 120, "y": 80, "width": 1280, "height": 820, "maximized": false },
  "lastRoute": "dashboard",
  "diagnostics": { "logLevel": "Information", "keepDays": 14 },
  "features": { "backgroundHealthCheck": false, "minimizeToTray": false }
}
```

Policy: additive keys only within a major version; unknown keys preserved on save;
`schemaVersion` bump requires a migration function with a test; corrupt file →
rename to `settings.corrupt-<timestamp>.json`, start from defaults, tell the user.

### 4.4 Cache files

- Always carry `schemaVersion` + `writtenAt`.
- Never authoritative: if the schema is unknown, delete and rescan.
- Size caps: GitHub cache ≤ 5 MB, scan cache ≤ 2 MB, build history ≤ 200 entries.
- Writes are atomic (temp file + replace) and throttled (≤ 1/s).

---

## 5. UX system

### 5.1 Information architecture

```
Dashboard      one screen: KPIs + every project card + attention strip
Projects       list + detail (Overview · Changes · Commits · Branches · Build · Releases · Actions)
Templates      template catalog → new-app wizard → build & run result
GitHub         account · repos · runs · releases · pull requests
DevDesk (self) this app's own repo, components, release state, update control
Activity       running + finished operations, retry, open log        (0.3, footer)
Settings       appearance · roots · ignores · channels · GitHub · builds · diagnostics
Diagnostics    live log, filters, copy, export, environment report
```

Navigation stays a left `NavigationView`; `Activity` joins the footer with a badge
for failures. The command palette (0.8) is an overlay, not a page.

### 5.2 Page-by-page definition of done

| Page | Real content at 1.0 | Complete in |
| --- | --- | --- |
| Dashboard | KPI tiles, attention strip, filterable card grid, card context menu (open, build, run, commit, release, copy path, pin), last-refresh + cancel | 0.1 → 0.7 |
| Projects | list + detail with 7 real tabs, header actions, operation drawer, keyboard navigation | 0.2–0.6 |
| Templates | template picker, 4-step wizard, dry-run preview, progress, result with build/run/open | 0.4 |
| GitHub | account state, repo picker, runs table with filters, releases by channel, PR list, rate-limit footer | 0.5 |
| DevDesk | self repo state, components, own release/update control, links to plan/logs | 0.1 |
| Activity | live operations, history, error details, retry/cancel, copy log excerpt | 0.3 |
| Settings | appearance, roots, ignores, channels, GitHub token, build defaults, diagnostics, about | 0.1–0.7 |
| Diagnostics | live tail, level filter, search, copy, export zip, environment report | 0.1 |

### 5.3 State matrix (mandatory for every list and detail surface)

| State | UI contract |
| --- | --- |
| Loading (no data) | skeleton or ring + "Scanning…" + cancel; never an empty white area |
| Loading (has cached data) | data visible + subtle progress in the header + timestamp |
| Empty (legitimately zero) | icon + one sentence + the action that creates the first item |
| Error | what failed, hint, "Try again", "Open log" — never a raw exception |
| Partial | per-item error chips (e.g. 3 of 8 repos failed) + summary |
| Offline / stale | "cached HH:mm" chip + "Retry" — GitHub only, never blocks local data |
| Disabled | reason in tooltip, never a clickable no-op |
| Cancelled | "Cancelled" chip + retry |

### 5.4 Safety UX (confirmation tiers)

| Tier | Examples | Interaction |
| --- | --- | --- |
| T0 silent | scan, refresh, open folder, copy | no prompt; activity entry |
| T1 confirm | commit, push, pull, create branch/tag, build release | dialog with exact command + target |
| T2 confirm + summary | delete branch, discard changes, publish release, create GitHub repo | dialog states consequence + recovery ("recoverable in 30 days" where true) |
| T3 typed confirmation | force-push, `reset --hard`, delete repo | type the branch/repo name; disabled by default in settings |

Rules: previews show the literal command; nothing runs until the dialog returns;
every T1+ action is logged with the actor (the user) and the result; no auto-retry
of write operations.

### 5.5 Visual and motion language

- Mica window backdrop; Acrylic only for flyouts/dialogs; sea-blue accent as today.
- 8px card radius, 8px grid, one spacing scale (4/8/12/16/24/32).
- Typography: WinUI `Title/Subtitle/BodyStrong/Body/Caption`; no custom fonts.
- Motion: 150–200 ms, ease-out; page transitions `EntranceNavigationTransitionInfo`;
  card entrance stagger ≤ 250 ms total; **respect `UISettings.AnimationsEnabled`**
  and Windows "reduce motion".
- Status colour is semantic, never the only signal: icon + text + colour.
- Dense data uses monospace only for hashes/paths, never for prose.

### 5.6 Keyboard map (target at 1.0)

| Key | Action |
| --- | --- |
| `Ctrl+K` | command palette |
| `Ctrl+R` / `F5` | refresh current surface |
| `Ctrl+,` | Settings |
| `Ctrl+1..7` | jump to nav item |
| `Ctrl+Shift+B` | build current project |
| `Ctrl+Shift+R` | run current project |
| `Ctrl+Enter` | commit dialog (from Changes) |
| `Esc` | close overlay / cancel dialog |
| `F1` | shortcuts help |

Every command in the palette is the same object the button uses (`CommandRegistry`),
so enablement and wording never diverge.

### 5.7 Accessibility (gate at 0.9, designed in from 0.1)

- Every interactive element: accessible name, role, keyboard focus, visible focus.
- Logical focus order; no focus traps; dialogs return focus.
- High contrast: no hardcoded colours outside theme dictionaries (`App.xaml` only).
- Text scaling to 200%: no fixed-height text containers; test at 100/150/200%.
- Narrator: live regions for scan/build completion; no duplicate announcements.
- Reduced motion honoured; no essential information only in animation.
- Contrast: body text ≥ 4.5:1; accent-on-background verified both themes.

### 5.8 Notifications (0.8)

- In-app activity toasts for completions; Windows toast only when the window is
  not focused and only if the user opted in.
- Background health check (opt-in): local git reads on an interval, no network,
  raises at most one notification per repo per state change (dedupe by state hash).

---

## 6. Engineering standards

### 6.1 Testing pyramid

| Layer | What | Tooling | Gate |
| --- | --- | --- | --- |
| Unit | parsers, semver, channel mapping, settings migration, path guard, command builders, VM state machines | xunit, no I/O | every version |
| Integration (local) | real temp git repos (today's harness), real file copies, fake process runner | xunit + temp dirs | every version |
| Contract | GitHub JSON fixtures → DTOs; release feed fixtures; csproj bump round-trip | xunit + `HttpMessageHandler` | 0.5+ |
| Process | `ProcessRunner` cancel/kill/timeout against a long-running `ping`/`dotnet` | xunit | 0.3 |
| UI smoke | launch exe, navigate all pages, screenshot, exit | scripted (extend `local-smoke-test.ps1`) | 0.9+ |
| Manual matrix | light/dark/HC, 100/150/200%, x64/arm64, Win10/Win11 | checklist in the release work order | 0.9/1.0 |

Rules: no test writes outside `%TEMP%`; no test touches the network; no test
depends on this machine's repos except the explicitly-marked "real ecosystem"
test (which must stay skip-safe).

### 6.2 Test harnesses to build once

- `TempRepo` fixture (exists, promote to shared test helper).
- `FakeProcessRunner` (records args, returns scripted output, honors cancellation).
- `FakeHttpHandler` (fixtures, ETag simulation, 403/rate-limit, timeouts).
- `FakeClock` (`TimeProvider`) for TTL/semver-dating tests.
- `SettingsSandbox` (isolated `%LocalAppData%` root via env var or injected path).

### 6.3 Performance budgets and measurement

Instrument with `Stopwatch` + Serilog `INF` timing events (never per-item spam):

| Budget | Threshold | Measured by |
| --- | --- | --- |
| Window visible (warm) | < 400 ms | `Program` timestamps |
| First interactive card (cache) | < 700 ms | catalog event |
| Full scan 8 repos | < 1.5 s | scan timer |
| Full scan 50 repos | < 3 s | synthetic test |
| UI thread block | < 50 ms | dispatcher watchdog (debug only) |
| Build output latency | < 150 ms per chunk → UI | build service timer |
| Memory (100 repos) | < 300 MB | process working set in Diagnostics |
| Settings/cache write | < 20 ms, off UI thread | JsonStore timer |

`Diagnostics` shows a live "Performance" card (startup, last scan, last build,
memory) so the numbers are always visible, not just in tests.

### 6.4 Logging and diagnostics

- Serilog with a **ring buffer sink** (last 2000 events) + rolling file.
- Correlation id per operation (`scan-7f3a`, `build-…`), shown in error details.
- Levels: INF for state changes, DBG for commands, VRB for raw output; never log
  tokens, full env, or file contents.
- `Diagnostics` page: live tail, level filter, text filter, copy visible, export
  zip (logs + settings redacted + environment), open log folder.
- Crash marker (see §3.5) + "report" bundle that contains no secrets.

### 6.5 Security rules

1. Tokens only in Credential Manager/DPAPI; never in JSON, argv, or logs.
2. Redact `https://user:pass@…` and `?token=…` from every logged string.
3. All process calls argument-list; no `cmd /c`, no PowerShell interpolation.
4. Path validation before any recursive delete/copy: resolved path must be inside
   the intended root; reject reparse points when copying/removing.
5. Zip export: no absolute paths, no `..`, size cap.
6. Scaffolding: never write into the template; never overwrite a non-empty target
   without explicit confirmation.
7. No telemetry, no analytics, no auto-upload. The only network calls are GitHub
   (and `git` when the user asks).
8. Do not log repository contents; log paths and metadata only.

### 6.6 Code conventions

- File-scoped namespaces, nullable enabled, `required`/`init` for models.
- Async all the way; `Async` suffix; `CancellationToken` on every public async API
  that can be cancelled.
- No `async void` except event handlers; event handlers must try/catch + log.
- No `Task.Run` around async I/O; use `ConfigureAwait(false)` in services.
- One type per file, file name = type name (as today).
- Comments explain *why* only (project style); no noisy inline narration.
- `#region` avoided; long XAML split into `Controls/`.
- Every new dependency: one line in the work order saying which job it does and
  what happens if it is removed.

### 6.7 Documentation contract (per release)

Update in the same commit as the code: `README.md` (status + screenshots),
`AGENTS.md` (architecture rules + gotchas), `CHANGELOG.md` (Keep-a-Changelog),
this roadmap (mark the version shipped, never rewrite its scope), and the version's
work-order file if one was created.

### 6.8 Definition of Done (every release)

1. Build clean: `dotnet build -c Debug -p:Platform=x64` → 0 warnings, 0 errors.
2. Tests green: `dotnet test DevDesk.Tests -p:Platform=x64`; ledger updated.
3. No new placeholder controls; every removed stub is gone from §1.3.
4. Exit demo executed and recorded in the work order.
5. Installer built, installed over the previous version, update check passes.
6. Docs updated; `AGENTS.md` rules still true.
7. Logs contain no secret (grep run).
8. Tag created and pushed; release notes published to the right channel.

---

## 7. The releases (v0.1.0 → v1.0.0)

> Numbers below are **sequence**, not calendar. Each release is a coherent,
> demoable product increment. A release may slip; it may not absorb the next
> release's scope (that is how 1.0 becomes 2028).

---

### v0.1.0 — Trustworthy base

> **Theme:** make the prototype honest, installable, self-updating and fast enough
> to build on. **Size:** L · **Risk:** Medium · **Channel:** beta · **Depends on:** —

**Why this version exists.** Every later version assumes one shared source of
project state, a settings file that survives, a release feed that belongs to
DevDesk, and a supported UI framework. Today none of those is true (F1–F9,
F14–F16). Doing this first prevents nine releases of rework.

**Scope**

| ID | Work item | Acceptance |
| --- | --- | --- |
| W-0.1.1 | **Identity + versioning.** `0.0.1` → `0.1.0` in all three fields; add `<InformationalVersion>0.1.0-beta</InformationalVersion>`; add `Scripts/bump-version.ps1` as the only way to move all four fields; `AppInfo` reads informational version and exposes `IsPrerelease`; add `LICENSE` (MIT text) and `CHANGELOG.md` | Settings/About show `0.1.0-beta`; a test asserts the four fields agree; the bump script is idempotent and preserves the suffix; `LICENSE` present |
| W-0.1.2 | **Own remote + feed.** Create the DevDesk GitHub repo, set `origin`, point `UpdateService.GitHubRepoUrl`, `build-and-release.ps1` default and `release.yml` at it | `git remote -v` shows origin; no DevDesk code references the template repo; grep test in CI |
| W-0.1.3 | **Prove the release path.** Install pinned `vpk 1.2.161`; fix the publish path/`Platform` assumption; run publish → pack → upload; install `setup.exe`; verify a silent update check | `Releases/` has `setup.exe` + `releases.*.json`; installed app reports `0.1.0-beta` and finds the next build |
| W-0.1.4 | **Platform refresh.** WindowsAppSDK `1.8.260804001` → stable `2.6.4`; SDK BuildTools to current; Velopack `1.2.0` → `1.2.161`; re-validate Mica/title bar/theme/DPI. If 2.6 blocks, pin 1.8 **and** record a hard expiry (must not ship 1.0 on 1.8) | Build 0/0 on 2.6.4; window, theme cycle and backdrop visually verified; dependency table in README |
| W-0.1.5 | **Architecture spine.** Extract `ScanEngine` from `ProjectScanner`; add `ProjectCatalog` (single snapshot + refresh + events); add `ViewModelBase`/`RelayCommand` (CommunityToolkit.Mvvm 8.4.x); add `AppServices` composition root; move Dashboard + DevDesk pages onto it | Dashboard and DevDesk share one scan; navigating between them triggers **zero** extra scans (asserted by a counter in tests) |
| W-0.1.6 | **Scan correctness.** Detect `.git` file as well as directory; derive self from the app's repo root (walk up for `.git`) instead of the literal name `DevDesk`; skip reparse points; add `CancellationToken` + progress; add hygiene to `CountUnder`; deterministic csproj selection; trim `\r` in parsers; use `--porcelain=v2`, `-c core.quotepath=false`, `--no-optional-locks` | New tests: worktree repo detected; self moved/renamed still detected; `bin/obj` not walked; cancellation returns quickly; 8-repo scan < 1.5 s |
| W-0.1.7 | **Settings v2.** Schema version, atomic write, roots array (single root for now), ignores, channel, platform, window placement, last route; migration from today's `{theme}`; corrupt-file recovery | Tests: migration, unknown-key preservation, corrupt file backup, atomic replace |
| W-0.1.8 | **Real diagnostics.** Serilog in-memory ring sink (2000), file tail, level + text filter, copy, export zip, open folder, environment report (OS, .NET, git, vpk, WASDK) | Diagnostics shows real events; export works; no sample rows remain |
| W-0.1.9 | **Shell hygiene.** Minimum window size; remember size/position/state; single instance with activation redirect; restore last route; fix `UpdateService` blocking `SemaphoreSlim.Wait()` and page-level duplicated scanning | Second launch focuses the first window; size/route restored; no UI-thread waits in services |
| W-0.1.10 | **Honesty pass.** Every remaining stub is either removed, disabled with a reason, or labelled "planned for vX"; update `README`, `AGENTS`, ecosystem description, plan references | §1.3 reduced to items scheduled for later versions; no "Phase 1" copy left |
| W-0.1.11 | **CI gates.** Add `.github/workflows/ci.yml`: build + test on PR/push (windows-latest, x64); keep release on tags; cache NuGet | A deliberately failing test blocks the PR (proven once) |
| W-0.1.12 | **Instrumentation.** Startup phase timings, scan timer, operation counters in Diagnostics | Performance card shows real numbers |

**Not in this version.** No git writes, no builds, no GitHub API, no template
wizard, no new pages, no redesign. Motion/accessibility beyond focus basics.

**Data/contract changes.** Settings v2; `ProjectModel` gains `Kind`, `ScannedAt`,
`RemoteUrl`, `Ahead`; `ScannerResult` becomes `CatalogSnapshot` internally (public
shape preserved for tests).

**UX changes.** Dashboard header shows last refresh + cancel; Diagnostics real;
DevDesk page gains update control; all remaining stubs labelled.

**Tests added (target +25).** Worktree detection, self discovery, cancellation,
settings migration/atomicity, catalog single-scan, channel constants, app-info
version, log redaction, view-model dashboard state.

**Exit demo.** Delete any installed copy → run `Releases\setup.exe` → launch →
8 repos appear in < 1.5 s → open Diagnostics (real events, export a zip) → close →
reopen (instant, same window size/route) → tag `v0.1.0-beta` and let CI publish →
install that artifact → bump to `v0.1.1-beta`, publish → the installed app finds
and applies the update.

**Risks.**

| Risk | Mitigation |
| --- | --- |
| WASDK 2.6 breaks a control/backdrop | Timebox the upgrade; if blocked, stay on 1.8 with a dated work item and re-test at 0.3 |
| New GitHub repo name/visibility is an owner decision | Default `Fettah010/DevDesk` (private first), confirm in §9 |
| `vpk` publish path assumptions | Prove with `-SkipBuild` + a manual publish before wiring CI |
| First installer is unsigned → SmartScreen | Document it; signing decision at 1.0 (§9) |

---

### v0.2.0 — Git, safely

> **Theme:** the terminal stops being necessary for daily git. **Size:** L ·
> **Risk:** Medium · **Channel:** beta · **Depends on:** 0.1.0

**Why.** J4 is the highest-frequency terminal task. It also stress-tests the entire
safety architecture (preview → confirm → run → refresh → report) before builds and
releases add more risk.

**Scope**

| ID | Work item | Acceptance |
| --- | --- | --- |
| W-0.2.1 | **`GitService` core.** Typed methods, `Result<T>`, cancellation, per-op timeout, structured stderr mapping (auth, network, conflict, locked index), redaction | Unit tests for every mapped failure; no method throws |
| W-0.2.2 | **Deep reads.** Status (`--porcelain=v2 -z`), staged/unstaged/untracked, log pager (50/page), branches local+remote, tags, remotes, ahead/behind, commit diffstat, file diff (text, capped) | Detail tabs render real data for all six ecosystem repos |
| W-0.2.3 | **Write ops.** stage/unstage all or selected, commit (message, amend, sign-off), fetch (explicit), pull (`--ff-only` default), push (set upstream), branch create/switch/delete, tag create/delete local, discard file changes, checkout file | Every op runs from UI on a scratch repo and produces the expected `git log`/`status` |
| W-0.2.4 | **Safety layer.** Command preview dialog for T1, typed confirm for T2/T3, `GIT_TERMINAL_PROMPT=0` with a clear "auth prompt blocked — open terminal once" hint, no `--force` anywhere | Attempting destructive ops shows preview; cancel leaves repo byte-identical |
| W-0.2.5 | **Commit experience.** Changes list with per-file selection, message box with length guidance, amend toggle showing the previous message, commit-and-push option | Commit dialog round-trips a real change; amend pre-fills |
| W-0.2.6 | **Conflict path.** Detect merge/rebase state; show conflicted files; "Open in VS" + "abort merge" (T2) + "continue" | A scratch merge conflict is visible and abortable from UI |
| W-0.2.7 | **Refresh discipline.** Catalog refresh after every write; optimistic local update; operation log entry per action | UI reflects a commit within 300 ms without a full rescan |
| W-0.2.8 | **Projects detail UI.** Real Overview + Changes + Commits + Branches tabs; operation drawer with streamed output | No placeholder rows remain in those tabs |

**Not in this version.** Rebase UI, interactive staging (hunk-level), submodule
management, stash UI (0.7 if demanded), GitHub PR actions.

**UX changes.** Header actions become real (Build stays disabled until 0.3);
context menu grows (commit, pull, push, branch, copy path); operation drawer.

**Tests added (target +30).** Porcelain v2 parser, log parser (unicode/rename),
command builder (no interpolation), state machine for conflict, cancellation mid
push (simulated), auth failure mapping, "no force flags" assertion.

**Exit demo.** On a scratch clone: edit a file → see it in Changes → stage one file
→ commit with amend preview → create branch → switch → pull → push → open the repo
in VS; then attempt `discard changes` and confirm the preview appears first.

**Risks.** Credential prompts hanging (mitigated by `GIT_TERMINAL_PROMPT=0` and a
hint), partial index locks from an open IDE (retry once, explain), destructive-op
UX complexity (tiered confirmations, no force).

---

### v0.3.0 — Build & run cockpit

> **Theme:** compile, run, publish and read errors without a terminal. **Size:** L ·
> **Risk:** Medium · **Channel:** beta · **Depends on:** 0.2 (operation/safety layer)

**Why.** J3 is the second most frequent terminal task and the prerequisite for
template smoke builds (0.4) and release verification (0.6).

**Scope**

| ID | Work item | Acceptance |
| --- | --- | --- |
| W-0.3.1 | **`ProcessRunner`.** Streaming stdout/stderr, UTF-8, line-buffered events, cancel, kill tree, timeout, exit code, env injection | Kill test leaves no `dotnet`/`msbuild` child; output arrives line-by-line |
| W-0.3.2 | **Profiles.** Per-project `BuildProfile` (Debug/Release, x64/x86/arm64, target, extra args), defaults from settings, editable per project | Building `DevDesk` defaults to Debug/x64; profile persists |
| W-0.3.3 | **Build/run/publish.** `dotnet build`, `dotnet run` (with stop), `dotnet publish`, `--no-restore` option, output folder open | All six ecosystem repos build or report a precise reason they cannot |
| W-0.3.4 | **Log console.** Virtualized output control, ANSI-tolerant, search, copy selection, copy all, save to file, follow-tail toggle, wrap toggle | 100k-line build stays responsive; search highlights matches |
| W-0.3.5 | **Problem parsing.** MSBuild diagnostics → list (severity, code, file, line, message); double-click opens the file at the line in VS/editor | A deliberate compile error appears as a clickable problem |
| W-0.3.6 | **Activity center.** Shell footer flyout + Activity page: queued/running/finished, cancel, retry, open log, duration, exit code | Two builds queue sanely; cancel works; history survives restart |
| W-0.3.7 | **Build history + badges.** Last build per project (time, result, duration) persisted; card + detail badges | Badge matches the actual last run after restart |
| W-0.3.8 | **DevDesk self page actions.** Build/test/run DevDesk itself (replaces the "not wired" buttons) | Dogfood: build DevDesk from DevDesk |

**Not in this version.** Custom MSBuild targets, test-runner UI (`dotnet test`
can be a build target later), remote builds, containers.

**UX changes.** Build/Run split buttons with profile flyout; operation drawer
promoted to Activity; status strip in the title bar for running work.

**Tests added (target +28).** Runner cancel/kill/timeout, chunk splitter, ANSI
filter, MSBuild regex (warnings, errors, multi-line), profile resolution, queue
semantics, history cap, "no UI thread block" (dispatcher watchdog in a headless
harness where possible).

**Exit demo.** Build `Formixa` (success → open output), introduce a fake error in a
scratch repo → error appears as a clickable problem → fix → rebuild; run
`DevTem WinUI 3` and stop it from the UI; restart the app and see both in history.

**Risks.** Output volume/UI jank (virtualize + batch dispatcher updates), orphan
processes (kill tree + `JobObject` if needed), MSBuild localization (parse codes,
not prose).

---

### v0.4.0 — Template forge

> **Theme:** a new app from the template in minutes, with a preview and a rollback.
> **Size:** M · **Risk:** High (file operations) · **Channel:** beta · **Depends on:** 0.1 (settings), 0.3 (smoke build)

**Why.** J6 is the step that currently requires six manual steps and a rename
checklist; it is also the most dangerous feature in the product (recursive copy +
token replacement), so it must land on top of a proven process/safety layer.

**Scope**

| ID | Work item | Acceptance |
| --- | --- | --- |
| W-0.4.1 | **Template discovery.** Detect template repos by markers (`*.csproj`, `App.xaml`, `Assets/app.ico`, `Scripts/build-and-release.ps1`), read name/version/description, cache descriptors | `DevTem WinUI 3` is listed with its real version |
| W-0.4.2 | **Token map + rename plan.** Symbols for app name, safe name, root namespace, assembly name, exe name, app id, pack title/authors, repo URL, window subtitle, version `0.1.0`; file renames (csproj, sln/slnx, scripts); marker-level replacements | A dry-run on a scratch copy lists every change and nothing else |
| W-0.4.3 | **Dry-run preview UI.** Tree of created/renamed files, per-file replacements, warnings, "copy only, template untouched" banner | Preview matches the actual result byte-for-byte (test) |
| W-0.4.4 | **Safe execution.** Copy to target with path guard, binary-safe, overwrite policy, progress per file, cancel, rollback deletes exactly what it created | Cancelled/failed run leaves no target folder (test) |
| W-0.4.5 | **Post-create chain.** Optional `git init` + first commit, smoke `dotnet build`, open in Explorer/VS, "run it" handoff to 0.3 | New app builds with 0 errors from the wizard |
| W-0.4.6 | **Wizard UX.** 4 steps (template → name/location/options → preview → result), validation (identifier-safe name, existing folder, path length, drive space), remembered last location | Invalid names blocked with a reason; back/forward preserves input |
| W-0.4.7 | **Template immutability guard.** Assert target is outside template; hash template tree before/after in tests; refuse if template is inside target | Test proves the template tree hash is unchanged |

**Not in this version.** GitHub repo creation (0.5), publishing the new app (0.6),
multiple template kinds beyond the WinUI template, updating an existing scaffold.

**UX changes.** Templates page becomes a real catalog + wizard; Dashboard gains a
"New app" primary action.

**Tests added (target +30).** Token replacement (all symbols), rename plan,
binary copy, rollback, path traversal, template hash immutability, name validation,
preview/result equality, target collision.

**Exit demo.** From an empty target folder: create `TryApp`, preview shows the
renames, create → build succeeds → run opens the window → `git log` has one commit
→ template repo `git status` is clean and its hash unchanged.

**Risks.** Windows path length, locked files, AV interference (retry + clear
message), template drift (descriptor warnings when an expected marker is missing).

---

### v0.5.0 — GitHub, read-only

> **Theme:** CI, releases and PRs at a glance, with a token that never leaks.
> **Size:** L · **Risk:** Medium · **Channel:** beta · **Depends on:** 0.1 (cache, settings)

**Why.** J7 is the "browser tab" job. Read-only first keeps the risk low while it
proves auth, caching, rate-limit behaviour and offline semantics that 0.6 depends
on.

**Scope**

| ID | Work item | Acceptance |
| --- | --- | --- |
| W-0.5.1 | **Repo identity.** Parse https/ssh remotes → owner/repo; handle `.git` suffix, enterprise hosts (no enrichment), missing remote | Unit matrix for 10 URL shapes; local-only repos show an empty state |
| W-0.5.2 | **Auth.** Token entry in Settings, validate via `/user`, store in Credential Manager (DPAPI fallback), scopes hint, sign out clears cache | Token survives restart; sign out removes it from vault and cache; grep test finds no token in files/logs |
| W-0.5.3 | **HTTP pipeline.** `HttpClient` with UA, ETag/If-None-Match, 304 handling, `X-RateLimit-*` tracking, 403/secondary-limit backoff, timeouts, cancellation, single-flight per resource | Fixture tests for 200/304/403/429/timeout; no thundering herd on refresh |
| W-0.5.4 | **Read surface.** Repo info, latest release per channel, releases list + assets, workflow runs (status/conclusion/duration/branch), run → browser, PR list (state, draft, checks summary) | GitHub page shows real data for five repos; run links open correctly |
| W-0.5.5 | **Caching + offline.** Persisted per-repo cache with TTL, stale badge, "offline" detection (network failure vs auth failure), manual refresh, purge | With the network disabled: cached data + clear offline chip; no error wall |
| W-0.5.6 | **Lazy enrichment.** Only visible/relevant cards fetch; sequential queue with backoff; rate-limit budget in the footer | Startup performs zero GitHub calls |
| W-0.5.7 | **GitHub page UX.** Account card, repo picker, runs table with filters, releases by channel, PR list, empty/auth-missing states | Every state matrix row implemented |

**Not in this version.** Writing to GitHub (rerun/cancel/create repo/publish), device
flow OAuth, notifications, issue management.

**UX changes.** GitHub page real; card enrichment chips; Settings → GitHub section.

**Tests added (target +30).** URL parsing, ETag flow, rate-limit state machine,
cache TTL/staleness, redaction, token store abstraction, fixture DTO parsing,
"startup makes no HTTP call" assertion.

**Exit demo.** Paste a PAT → repos show real latest releases and last run → open a
run → disable Wi-Fi → refresh: cached data + offline chip → re-enable → refresh
200/304 path → inspect logs: no token appears.

**Risks.** Token scopes confusion (validate + explain), API shape drift (fixtures +
defensive DTOs), rate limits (cache-first + budget display).

---

### v0.6.0 — Ship from DevDesk

> **Theme:** version → notes → tag → CI → monitor → published, in one flow.
> **Size:** L · **Risk:** High (irreversible tags) · **Channel:** beta · **Depends on:** 0.2, 0.3, 0.5

**Why.** J5 is the last terminal-and-browser workflow. It is also the dogfood test:
after this release, DevDesk releases DevDesk.

**Scope**

| ID | Work item | Acceptance |
| --- | --- | --- |
| W-0.6.1 | **Channel model.** One `ReleaseChannel` enum (stable / beta / dev), tag↔channel mapping (`-beta`/`-dev` suffix), default channel by version, validation | One test per mapping; no string literal channel outside the enum |
| W-0.6.2 | **Semver suggestion.** Latest tag + commit count + channel → suggested next version; user override with validation (must be greater, valid semver, correct suffix) | Suggestions match a table of scenarios; invalid input blocked with reason |
| W-0.6.3 | **Atomic version bump.** All three fields + `InformationalVersion` in one write; single csproj; dry-run diff; rollback if any write fails | Test: bump → all four fields equal; failure injection leaves the file untouched |
| W-0.6.4 | **Notes generation.** Commits since last tag grouped (feat/fix/docs/chore/other) with PR links when available; editable markdown; saved to `ReleaseNotes.md` | Notes for a real range look sane and are editable |
| W-0.6.5 | **Release execution.** Commit bump → tag → push (T2 confirm) **or** run the project's `Scripts/build-and-release.ps1` (delegation rule, never reimplement vpk) | Both paths produce a release on the chosen channel |
| W-0.6.6 | **CI monitor.** Watch the workflow run with backoff polling, live status, cancel/retry link, success banner with asset links, failure with log link | A real tag run is watched to completion inside the app |
| W-0.6.7 | **Channel alignment.** `release.yml` options → stable / beta / dev; channel derived from the tag; `build-and-release.ps1` channel validation; remove `alpha` | A `-beta` tag publishes to beta; app on beta sees it, app on stable does not |
| W-0.6.8 | **Release view.** Per-project release timeline + channel chips; feeds parsed from `releases.<channel>.json` | Cards/page show the last release per channel |

**Not in this version.** Auto-publishing without confirmation, multi-project batch
releases, rollback of a published release, draft editing on GitHub.

**UX changes.** Release wizard (5 steps), Release tab real, "Ship" action on cards,
self page gains "release DevDesk" (T2).

**Tests added (target +28).** Semver compare/suggest, channel mapping, bump
atomicity, notes grouping, tag conflict detection, feed parser (all channels),
"never force" assertion, monitor backoff schedule.

**Exit demo.** Ship `v0.6.1-beta` of DevDesk using DevDesk: suggestion accepted →
notes edited → bump → tag pushed → CI watched → release appears in the app's own
DevDesk page → installed app updates to it.

**Risks.** Irreversible tags (validate twice, never auto-push), CI minutes, channel
mistakes (mapping tests + preview shows the exact tag), release notes quality
(editable, not auto-published blind).

---

### v0.7.0 — Scale & multi-root

> **Theme:** works for a folder of 50 repos, not just the ecosystem. **Size:** M ·
> **Risk:** Medium · **Channel:** beta · **Depends on:** 0.1 (catalog), 0.5 (cache pattern)

**Why.** The scanner is the core skill of the product; today it is tuned to one
small folder (F6/F7). A 1.0 dashboard that stutters on a real `C:\Code` is not a
product.

**Scope**

| ID | Work item | Acceptance |
| --- | --- | --- |
| W-0.7.1 | **Multi-root settings UI.** Add/remove/reorder roots, per-root depth, enable/disable, default ecosystem root | Two roots scanned; disabling one removes its cards on refresh |
| W-0.7.2 | **Ignore rules.** Glob matcher with `!` negation, defaults (`bin/obj/.vs/node_modules/Releases/Logs`), per-root overrides, validation + preview of what a rule excludes | Rule tests (glob, negation, case, separators); excluded repo disappears |
| W-0.7.3 | **Worktrees/submodules.** `.git` file parsing (`gitdir:`), `git worktree list`, dedupe by common git dir, "worktree of X" badge | A worktree never duplicates its parent card; badge links to the parent |
| W-0.7.4 | **Incremental cache + watcher.** Persisted snapshot; `FileSystemWatcher` per root with 500 ms debounce; changed-repo-only rescan; cache schema v2 | Warm start paints cards with zero git calls; editing a file updates only that card |
| W-0.7.5 | **Progressive scan.** Stream cards as repos resolve; skeleton placeholders; cancel | Cards appear within 300 ms on a cold scan of 50 repos |
| W-0.7.6 | **Search/filter/sort.** Instant filter (name/path/branch/tag), sort (name/activity/health/version), saved filters, keyboard nav | 50 cards filter under 50 ms |
| W-0.7.7 | **Organization.** Pin/favorite, tags/colours, recently opened, hide archived | Pinned repos sort first and persist |
| W-0.7.8 | **Workspace expansion.** Solutions/projects/components per repo, expandable card, open specific solution | `DevDesk` shows app/tests/docs/scripts; `Formixa` shows its solution |
| W-0.7.9 | **Large-root guards.** Per-repo time budget, skip-huge-dir heuristics, UNC/path-length handling, permission failures as per-card warnings | Scan of a synthetic 50-repo root meets M4 |

**Not in this version.** Cloud/remote roots, monorepo graph visualization,
cross-repo dependency analysis.

**UX changes.** Settings → Roots; Dashboard toolbar (search/sort/filter/pin);
card badges (worktree, pinned, tag).

**Tests added (target +26).** Glob/negation, .git-file parsing, dedupe, watcher
debounce, cache v2 round-trip, progressive events, filter ranking, 50-repo perf.

**Exit demo.** Add `C:\Code\projects` as a second root with ignores → 50+ repos
appear progressively → search `form` → pin `DevDesk` → open a worktree → edit a
file in another repo and watch only that card refresh → restart: instant cards.

**Risks.** Watcher storms (debounce + coalesce), network roots (detect and warn),
path length, memory growth (cap + virtualize).

---

### v0.8.0 — Command center

> **Theme:** mouse-optional, notification-aware, always-on. **Size:** M ·
> **Risk:** Low · **Channel:** beta · **Depends on:** 0.3 (activity), 0.7 (search)

**Why.** Daily use rewards speed. A palette + shortcuts + activity notifications
turn a dashboard into a tool you keep open. Low risk because it composes existing
commands instead of adding new side effects.

**Scope**

| ID | Work item | Acceptance |
| --- | --- | --- |
| W-0.8.1 | **`CommandRegistry`.** Every action as a command (id, title, keywords, icon, shortcut, canExecute, run); buttons and palette consume the same objects | No duplicated enablement logic; a missing command fails a test |
| W-0.8.2 | **Command palette.** `Ctrl+K`, fuzzy ranking (recency + keyword), grouped results (projects, actions, pages, settings), keyboard-only operation | Open any repo/action in ≤ 3 keystrokes in the demo |
| W-0.8.3 | **Keyboard map + help.** Global accelerators (§5.6), conflict detection at startup, `F1` help overlay, discoverable tooltips | Shortcut test asserts uniqueness |
| W-0.8.4 | **Activity center v2.** Badge for running/failed, toast on completion, retry, copy error, open log, clear finished | A failed build while on another page raises one toast + badge |
| W-0.8.5 | **Tray + background health (opt-in).** Minimize to tray, restore, exit; interval health check (local git only) with deduped notifications | Closing to tray keeps operations running; health check makes zero network calls |
| W-0.8.6 | **Context menus.** Real right-click menus on cards/rows matching palette commands | Menu and palette invoke the same command id |

**Not in this version.** A scripting/automation language, scheduled tasks beyond
health checks, global hotkeys outside the app.

**UX changes.** Palette overlay, activity badge, tray icon, help overlay.

**Tests added (target +18).** Registry completeness, fuzzy ranking, shortcut
uniqueness, notification dedupe, tray lifecycle (headless where possible).

**Exit demo.** `Ctrl+K` → type "form" → build Formixa; `Ctrl+Shift+B` on DevDesk;
switch to Settings; build fails → toast + badge → click → activity details; close
to tray → reopen; `F1` shows the map.

**Risks.** Shortcut conflicts with Windows/VS (detect + allow rebind), tray
lifecycle leaks (single instance + explicit dispose), notification spam (dedupe).

---

### v0.9.0 — Release candidate

> **Theme:** premium, accessible, documented, measured. **Size:** M · **Risk:** Medium ·
> **Channel:** beta (rc) · **Depends on:** 0.1–0.8

**Why.** This is where the product earns "1.0": no sharp edges, no unmeasured
claims, no undocumented behaviour.

**Scope**

| ID | Work item | Acceptance |
| --- | --- | --- |
| W-0.9.1 | **Accessibility audit + fixes.** Names/roles/focus/live regions, contrast, HC, 200% text, reduced motion | Checklist with per-screen evidence; no unlabeled interactive element |
| W-0.9.2 | **Motion + visual pass.** Consistent durations, page transitions, card entrance, busy states, empty-state art | Motion disabled → identical information and no layout jump |
| W-0.9.3 | **Localization infrastructure.** `.resw` + `x:Uid` for all strings, pseudo-loc run, English shipped, RTL-ready layout rules | No hardcoded UI string in XAML/code (grep test); pseudo-loc reveals no truncation |
| W-0.9.4 | **First-run + help.** Welcome card (scan roots, GitHub token optional, shortcuts), help overlay links, About with versions/environment | Fresh profile goes from install to first useful card without docs |
| W-0.9.5 | **Docs set.** `README` (real), `AGENTS` (new architecture rules), `CHANGELOG`, `docs/USER-GUIDE.md`, `docs/ARCHITECTURE.md`, `docs/TROUBLESHOOTING.md`, `SECURITY.md` | Docs build-free and accurate; every screenshot current |
| W-0.9.6 | **Perf + reliability gates.** Budgets in §6.3 enforced by tests where possible; soak test (100 scans, 20 builds); crash-marker flow | Numbers recorded in Diagnostics + release notes |
| W-0.9.7 | **Security review.** Token handling, process args, path guards, zip export, log redaction, `git` env | Checklist signed off in the work order; grep tests green |
| W-0.9.8 | **Distribution polish.** Per-user install, shortcuts, uninstall leaves no user data unless chosen, portable zip (updates disabled, documented), optional winget manifest if the repo is public; Store/MSIX explicitly deferred | Install/upgrade/uninstall/reinstall cycle clean; portable zip runs from a folder |
| W-0.9.9 | **RC soak.** Install rc, use it for all DevDesk work for a week (dogfood), log every friction point; fix or file | Friction list closed or explicitly deferred |

**Not in this version.** New features. Anything discovered is fixed or moved to
1.0 blocking list, not added ad hoc.

**Exit demo.** Narrator-only walkthrough of scan → build → commit → release;
200% text on every page; fresh install → first card; uninstall/reinstall keeps
settings; budgets table filled with measured numbers.

**Risks.** Accessibility late discoveries (start the audit at 0.7, this release
only closes gaps), localization churn (freeze strings at rc), soak time (run it
while doing 1.0 prep).

---

### v1.0.0 — Stable

> **Theme:** freeze, prove, sign, ship. **Size:** S · **Risk:** Low · **Channel:** stable ·
> **Depends on:** 0.9.0

**Why.** 1.0 is a promise: the contracts in §2.6 hold, upgrades work, and the app
can be recommended without caveats.

**Scope**

| ID | Work item | Acceptance |
| --- | --- | --- |
| W-1.0.1 | **Contract freeze.** Settings v1, cache schema, services, palette ids, shortcut map, release artifact; document deprecation policy | Freeze list checked into `docs/`; any drift fails a review checklist |
| W-1.0.2 | **Upgrade proof.** Install `0.1.0` → update through at least three hops → `1.0.0`; settings/logs preserved; cache rebuilt | Recorded transcript + screenshots |
| W-1.0.3 | **Matrix.** Win11 22H2/23H2/24H2 (+ Win10 1809 if kept, §9) × x64/arm64 builds; 100/150/200% DPI; light/dark/HC | Matrix table filled; only documented limitations remain |
| W-1.0.4 | **Signing decision.** Sign installer + exe if a certificate exists; otherwise publish SHA-256 and an explicit SmartScreen note | Decision recorded; artifacts verifiable |
| W-1.0.5 | **Stable channel.** Default channel becomes `stable`; beta remains for the next cycle; one version per channel verified | Fresh install subscribes to stable; beta build does not see stable-only |
| W-1.0.6 | **Release + announcement.** Notes, README screenshots, `docs/` index, tag `v1.0.0` | Release published; links work |
| W-1.0.7 | **Post-1.0 policy.** Semver rules, support window, deprecation, how a 1.1 feature is proposed | `docs/` policy page |
| W-1.0.8 | **Rollback story.** Keep the previous installer linked; document manual downgrade (Velopack does not downgrade automatically) and settings compatibility both ways | Downgrade from `1.0.1` to `1.0.0` documented and tried once |

**Not in this version.** Any new surface. A 1.0 diff that adds a feature fails its
own release.

**Exit demo.** Fresh machine (or clean profile): install `v1.0.0` from the release
page; scan; build; commit; release a scratch project; check updates; uninstall.
Then the upgrade chain from `0.1.0`.

**Risks.** Late-breaking bug (freeze + hotfix policy `1.0.1`), unsigned SmartScreen
(decision in §9), Win10 residue (drop officially if untestable).

---

## 8. Traceability, risks, metrics

### 8.1 Feature → version

| Capability | 0.1 | 0.2 | 0.3 | 0.4 | 0.5 | 0.6 | 0.7 | 0.8 | 0.9 | 1.0 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Single shared scan + cache | ● | | | | | | ● | | | |
| Self-update + own release feed | ● | | | | | | | | | ● |
| Real logs/diagnostics | ● | | | | | | | | | |
| Settings schema + multi-root | ● | | | | | | ● | | | |
| Git read (history/branches/diff) | | ● | | | | | | | | |
| Git write (commit/push/branch) | | ● | | | | | | | | |
| Build/run/publish + logs | | | ● | | | | | | | |
| Problem list (clickable errors) | | | ● | | | | | | | |
| Activity center | | | ● | | | | | ● | | |
| Template wizard + rollback | | | | ● | | | | | | |
| GitHub reads + token vault | | | | | ● | | | | | |
| Offline/stale GitHub states | | | | | ● | | | | | |
| Release wizard + channels | | | | | | ● | | | | |
| CI run monitor | | | | | | ● | | | | |
| Multi-root/ignore/watchers | | | | | | | ● | | | |
| Worktree dedupe | | | | | | | ● | | | |
| Search/filter/pin | | | | | | | ● | | | |
| Command palette + shortcuts | | | | | | | | ● | | |
| Tray + notifications | | | | | | | | ● | | |
| Accessibility + localization | | | | | | | | | ● | |
| Docs set + onboarding | | | | | | | | | ● | |
| Frozen contracts + signing | | | | | | | | | | ● |

### 8.2 Finding → version

| Finding | Fixed in |
| --- | --- |
| F1 no remote / wrong feed | 0.1.0 |
| F2 version identity | 0.1.0 |
| F3 out-of-support framework | 0.1.0 |
| F4 unproven release path | 0.1.0 |
| F5 duplicate scans | 0.1.0 |
| F6 scanner correctness | 0.1.0 (core) / 0.7.0 (worktrees, scale) |
| F7 no cache | 0.1.0 / 0.7.0 |
| F8 thin settings | 0.1.0 / 0.7.0 |
| F9 no MVVM | 0.1.0 + adopted per page |
| F10 placeholders | 0.1.0 (labels) → 0.6.0 (last removed) |
| F11 channel mismatch | 0.1.0 (constants) / 0.6.0 (CI) |
| F12 no auth | 0.5.0 |
| F13 accessibility/localization | 0.9.0 |
| F14 window hygiene | 0.1.0 |
| F15 CI doesn't test | 0.1.0 |
| F16 docs drift | 0.1.0 / 0.9.0 |
| F17 failure UX | 0.3.0 / 0.8.0 |
| F18 no perf measurement | 0.1.0 / 0.9.0 |

### 8.3 Dependency schedule

| Package / tool | Current | Target | Version | Why |
| --- | --- | --- | --- | --- |
| `Microsoft.WindowsAppSDK` | 1.8.260804001 | 2.6.4 | 0.1.0 | 1.8 out of servicing 2026-09-24; stable is 2.6.4 |
| `Microsoft.Windows.SDK.BuildTools` | 10.0.28000.2705 | current | 0.1.0 | keep in step with WASDK |
| `Velopack` | 1.2.0 | 1.2.161 | 0.1.0 | current stable 1.2 line (1.3 is preview) |
| `CommunityToolkit.Mvvm` | — | 8.4.x | 0.1.0 | MVVM foundation (template already uses it) |
| `CommunityToolkit.WinUI.*` | 8.2.x | review | 0.3.0 | only if a control earns its place |
| `Serilog` | 4.4.0 | latest 4.x | 0.1.0 | ring-buffer sink |
| xunit | 2.9.2 | latest 2.x | 0.9.0 | consider 3.x only with a migration ticket |
| `vpk` CLI | not installed | 1.2.161 (pinned) | 0.1.0 | release path |

Rules: one dependency change per version unless a security fix forces it; no
package added without a work-order line naming its job.

### 8.4 Risk register

| # | Risk | Likelihood | Impact | Mitigation | Owner |
| --- | --- | --- | --- | --- | --- |
| R1 | WASDK 2.6 migration breaks shell/backdrop | Medium | High | Timebox in 0.1.0; fallback pin with expiry; visual checklist | dev |
| R2 | Release/tag mistakes are irreversible | Medium | High | Preview + T2/T3 confirmations; never auto-push; tag validation | dev |
| R3 | Scaffolding corrupts the template | Low | High | Copy-only, path guard, hash test, rollback | dev |
| R4 | Token leak | Low | High | Vault-only, redaction, grep tests, no argv | dev |
| R5 | Process leaks / hung builds | Medium | Medium | Kill tree, timeouts, job object if needed | dev |
| R6 | GitHub API drift / rate limits | Medium | Medium | Fixtures, ETag cache, budget display, offline mode | dev |
| R7 | Scope creep ("while we're here") | High | High | Version boundaries in §7 are contractual; non-goals list | owner |
| R8 | Performance regressions | Medium | Medium | Budgets + Diagnostics card + soak | dev |
| R9 | Accessibility discovered late | Medium | Medium | Start audit at 0.7; gate at 0.9 | dev |
| R10 | Docs drift returns | High | Medium | Docs in DoD; AGENTS rule updated per version | dev |
| R11 | Codesigning/SmartScreen friction | High | Medium | Decide at 1.0; document hashes if unsigned | owner |
| R12 | Single-developer bandwidth | High | Medium | Small releases; each demoable; defer non-blocking polish | owner |
| R13 | Windows-only assumption breaks on ARM64 | Low | Medium | Build arm64 from 0.9; CI matrix | dev |
| R14 | Local cache corruption | Low | Low | Atomic writes, schema validation, delete-and-rebuild | dev |

### 8.5 Release gates (applies to every version)

1. §6.8 Definition of Done.
2. Version ladder position is respected (no later-version scope).
3. The version's own section in this file is marked `SHIPPED` with the tag.
4. Installer + update verified on an installed copy, not just the dev build.
5. `AGENTS.md` still tells the truth about how to build, run, test and release.

---

## 9. Open decisions (owner input required)

| # | Decision | Default if silent | Why it matters | Needed by |
| --- | --- | --- | --- | --- |
| D1 | GitHub repo for DevDesk releases (owner/name, private first?) | **Resolved by owner:** public `Fettah010/DevDesk` | F1 blocks self-update and all release work | 0.1.0 |
| D2 | Code signing certificate (buy / use existing / ship unsigned) | Unsigned + SHA-256 + SmartScreen note | Installer trust; affects 1.0 "stable" claim | 0.9.0 |
| D3 | Minimum OS: keep Win10 1809 or require Win11 | Keep Win10 1809 TFM, test Win11 only; drop at 1.0 if unverified | Test matrix size; WASDK 2.x support | 0.9.0 |
| D4 | Channel vocabulary: stable / beta / dev (drop `alpha`) | Drop `alpha`; map any old tag to beta | F11; clients must read the channel being published | 0.1.0 |
| D5 | Default self-update channel before 1.0 | `beta`; `stable` at 1.0 | Velopack one-version-per-channel chain | 0.1.0 |
| D6 | GitHub auth: PAT now, device flow later? | PAT at 0.5; device flow considered for 1.1 | UX vs scope; device flow needs an OAuth app client id | 0.5.0 |
| D7 | Scan scope at 1.0 | Ecosystem root by default; other roots user-added | Product focus vs generality | 0.7.0 |
| D8 | `copilot-worktrees` in the ecosystem root | Add to default ignores; worktrees supported and deduped | Prevents duplicate/ghost cards | 0.1.0 |
| D9 | Add a DI container? | No; `AppServices` + typed interfaces | Complexity vs testability; revisit on multi-window/plugins | 0.1.0 |
| D10 | Ship a portable (no-install) build alongside the installer? | Yes, document it; updates disabled in portable mode | Users who refuse installers | 0.9.0 |
| D11 | Keep `docs/DEVDESK-PLAN.md` name or standardize on this file? | This file is the roadmap; `DEVDESK-PLAN.md` retires | AGENTS/DevDeskPage link targets | 0.1.0 |
| D12 | Telemetry | None, ever | Privacy is part of the pitch | — |
| D13 | Distribution surfaces beyond the Velopack installer | Installer (default) + portable zip at 1.0; winget only if the repo is public; Store/MSIX not in 1.0 | Support surface and update semantics differ per channel | 0.9.0 |

---

## 10. Operating model

### 10.1 How a version is delivered

1. **Open the work order.** Copy §7's version section into
   `docs/versions/V0.X.0.md`, add dates and the exact file list. This roadmap stays
   the map and is never rewritten (only its status line changes).
2. **Branch** `feat/v0.x-<theme>`; keep `main` releasable.
3. **Implement in work-item order**; each W-item is one reviewable commit where
   possible, with its tests.
4. **Dogfood**: install the build, use it for the next day's work on DevDesk
   itself. Friction goes into the work order, not into a later version.
5. **Run the exit demo** exactly as written; record screenshots/log excerpts.
6. **Ship**: bump via the version script (§10.2), tag `vX.Y.Z[-beta]`, let CI
   publish, install the artifact, verify the update, mark the section `SHIPPED`.
7. **Retire**: close the work order; only this roadmap and `CHANGELOG.md` carry
   the history.

### 10.2 Versioning rules

- `MAJOR.MINOR.PATCH`; pre-1.0 `MINOR` = one rung of this ladder (breaking changes
  allowed, documented in `CHANGELOG`).
- Pre-1.0 tags carry `-beta` (published to `beta`); `1.0.0` is plain and goes to
  `stable`.
- All three csproj fields + `InformationalVersion` move together, via one script
  (`Scripts/bump-version.ps1`, added in 0.1.0 — never hand-edit four fields).
- One version per channel (Velopack requirement); never reuse a tag.
- `1.0.x` = fixes only; `1.1.0` = features; `2.0.0` = breaking settings/contracts.

### 10.3 Change control

- A version may **drop** scope freely; it may **add** scope only if the addition is
  required to make its own exit demo true. Anything else is a new rung or a 1.x
  candidate.
- Any change to §2.6 (frozen contracts) before 1.0 needs a note in `CHANGELOG`.
- Any new dependency needs a named job and a removal story (§6.6).
- `AGENTS.md` changes in the same commit as the behaviour it describes.

### 10.4 First ten commits of v0.1.0 (concrete starting point)

1. `docs: add the v0.1.0 work order` (from §7).
2. `chore: add LICENSE, CHANGELOG, .editorconfig`.
3. `build: bump WindowsAppSDK to 2.6.4, Velopack to 1.2.161` (verify build + window).
4. `feat: add AppInfo.InformationalVersion + channel constants`.
5. `refactor: extract ScanEngine from ProjectScanner` (tests stay green).
6. `feat: add ProjectCatalog with a single shared snapshot`.
7. `feat: settings v2 (schema, atomic write, migration)`.
8. `refactor: move Dashboard and DevDesk pages onto the catalog` (scan-count test).
9. `feat: real diagnostics (ring sink, tail, export)`.
10. `chore: repo remote, feed URL, vpk pin, first installer` (prove the release path).

### 10.5 What success looks like at each rung

| Rung | One sentence that must be true |
| --- | --- |
| 0.1.0 | "I installed DevDesk and it updates itself; nothing on screen is a lie." |
| 0.2.0 | "I have not opened a terminal to commit or push in a week." |
| 0.3.0 | "I build, run and debug my projects from DevDesk." |
| 0.4.0 | "I made a new app with DevDesk and it built first time." |
| 0.5.0 | "I stopped opening the GitHub Actions tab." |
| 0.6.0 | "DevDesk released DevDesk." |
| 0.7.0 | "It handles my whole `C:\Code` folder and still feels instant." |
| 0.8.0 | "I can drive it without the mouse." |
| 0.9.0 | "I would recommend it to another Windows developer." |
| 1.0.0 | "It is stable, documented, and I trust it with releases." |

---

*End of roadmap. Implement one rung at a time; keep this file honest.*
