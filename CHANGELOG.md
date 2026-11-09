# Changelog

All notable changes to DevDesk are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Pre-1.0, `MINOR` is one rung of the roadmap ladder and breaking changes are
allowed; the tag suffix (`-beta`, `-dev`) selects the release channel.

## [Unreleased]

## [0.7.0-beta] — 2026-11-09

### Added

- **Multi-root scanning**: configure multiple repository roots with per-root depth, enabled state, and ignore rules.
- **Ignore rules**: root-relative glob matcher with ordered `!` negation, default exclusions, and exclusion preview.
- **Worktree and submodule identity**: safe pointer parsing, distinct checkout paths with worktree badges, and cross-root deduplication.
- **Persistent scan cache**: schema-v2 cache for instant warm start and cache-first painting with background revalidation.
- **Root-scoped change watcher**: debounced filesystem invalidation, checkout-targeted refresh, and reconciliation fallback.
- **Progressive scanning**: progressive immutable catalog snapshots, real pending project cards, and stable ordering.
- **Search, filter, and sort**: instant in-memory search across project facts; sort options and persisted saved views.
- **Project organization**: persisted favorites/pins, colored tags, recently opened tracking, and hide-archived preference.
- **Workspace expansion**: on-demand solution, project, and component discovery with safe launch actions.
- **Performance instrumentation**: M1–M6 tracking with operation-level UI delay attribution and diagnostics reporting.

## [0.3.0-beta] — unreleased

### Added

- Build / run / publish / test any project in the ecosystem from the Projects
  **Build** tab: argument-list `dotnet` processes, one build at a time (the rest
  queue), exe launch with a `dotnet run` fallback, publish path read from the
  real output line, **Open output** (only for a successful publish whose folder
  exists), and "open log / open log folder".
- A live log console: search with match count, follow-tail, wrap, copy, save,
  ANSI/`\r` handling, and a bounded 20,000-line window over a complete log file.
- MSBuild problems parsed into a clickable list (severity, code, file, line,
  column); double-click opens the file at the line.
- Activity center: an Activity page (8th route), footer flyout with the last ten
  runs, title-bar running strip with elapsed time and Stop, cancel / retry /
  open log, and a failure badge.
- Persisted build history (ring of 200) behind the "last build" badges on the
  Dashboard cards and the Projects header; a never-built project shows `—`.
- DevDesk's own page builds itself, runs the real test suite, and offers
  **Rebuild & relaunch** (the launcher waits for the old window, rebuilds only
  if a source changed, then reopens through the single-instance flow).
- Build timings joined the performance instrumentation and the Diagnostics card.

### Changed

- `Routes` gains `activity`; `smoke-ui.ps1` visits 8 destinations.
- Settings' channel example now quotes the current version.

## [0.2.0-beta] — unreleased

### Added

- Local Git status, history, branches, remotes, tags, diffstat and capped file diffs.
- Previewed stage, unstage, commit, fetch, fast-forward pull, push, branch, tag
  and discard operations in the Projects detail page.
- Merge/rebase conflict state with guarded abort and continue actions.

## [0.1.0-beta] — 2026-10-30

### Added

- Project catalog: one shared scan snapshot instead of one scan per page.
- Real diagnostics: in-memory log ring buffer, level/text filters, export bundle.
- Settings schema v1 with atomic writes, migration from the theme-only file and
  corrupt-file recovery.
- Window placement, minimum size and single-instance activation.
- Instrumentation: startup marks (process start → logging → Velopack → settings →
  window shown → first snapshot), a timing for every scan / update check /
  settings write, and a Diagnostics **Performance** card that shows each number
  beside its roadmap budget.
- CI: build and test on every push; releases only from tags.

### Changed

- Windows App SDK 2.6.4 (1.8 is out of servicing), Windows SDK BuildTools,
  Velopack 1.2.161.
- Update feed points at DevDesk instead of the template repository.
- `beta` is the default update channel before 1.0.

### Fixed

- Scans detect `.git` **files** (worktrees), derive the self repo from the app's
  own checkout, skip hygiene folders and can be cancelled.
- `UpdateService` no longer blocks the UI thread while resolving its manager.

[Unreleased]: https://github.com/Fettah010/DevDesk/compare/v0.1.0-beta...HEAD
[0.1.0-beta]: https://github.com/Fettah010/DevDesk/releases/tag/v0.1.0-beta
