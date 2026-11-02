# DevDesk 0.6.0-beta

This release adds a guarded release workflow inside DevDesk:

- Preview a channel-aware version suggestion and synchronized project version
  changes.
- Draft and edit release notes before saving them to the repository.
- Review the exact version, notes, commit, tag, and push remote before making
  local changes.
- Confirm tag publication separately, then monitor the matching GitHub Actions
  run with bounded polling.
- Track release operations in Activity and view the latest published release
  for each channel on the DevDesk self page.

Publishing continues to use the repository's existing, gated GitHub Actions
release pipeline.
