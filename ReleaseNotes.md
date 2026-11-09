# DevDesk 0.7.0-beta

This release brings scale and multi-root capabilities to DevDesk:

- **Multi-root configuration**: Configure multiple local repository roots with per-root depth, enabled state, and custom ignore rules.
- **Ignore rules**: Root-relative glob matching with ordered `!` negation, default exclusions, and preview of excluded repositories.
- **Worktree and submodule identity**: Safe pointer parsing, distinct working tree paths with worktree badges, and cross-root deduplication.
- **Persistent scan cache**: Schema-v2 persistent cache for instant warm start and cache-first painting with background revalidation.
- **Root-scoped change watcher**: Debounced filesystem invalidation, checkout-targeted card refresh, and reconciliation fallback.
- **Progressive scanning**: Progressive immutable catalog snapshots with real pending card affordances and stable ordering.
- **Search, filter, and sort**: In-memory instant search across name, path, branch, and versions; sort options and persisted saved views.
- **Project organization**: Persisted favorites/pins, colored tags, recently opened tracking, and hide-archived preference.
- **Workspace expansion**: On-demand detection and launching of solutions, projects, and components within repositories.
- **Performance instrumentation**: Startup marks, M1–M6 tracking, UI dispatcher delay attribution, and diagnostics reporting.
