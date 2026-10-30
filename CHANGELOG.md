# Changelog

All notable changes to DevDesk are documented here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and
the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).
Pre-1.0, `MINOR` is one rung of the roadmap ladder and breaking changes are
allowed; the tag suffix (`-beta`, `-dev`) selects the release channel.

## [Unreleased]

### Added

- One version identity: `Version`, `AssemblyVersion`, `FileVersion` and
  `InformationalVersion` move together via `Scripts/bump-version.ps1`.
- `LICENSE` (MIT) and this changelog.

## [0.1.0-beta] — unreleased

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
