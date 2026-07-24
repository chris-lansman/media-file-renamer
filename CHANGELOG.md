# Changelog

Notable user-visible changes are recorded here. This project follows
[Semantic Versioning](https://semver.org/) for tagged releases.

## Unreleased

### Added

- Transactional staging safeguards, companion-file handling, operation journals, rollback, cancellation, and undo.
- Unified TMDB/TVDB matching, TVDB-only identities, episode-order selection, alias-aware confidence evidence, and bounded provider retries.
- Parsing and naming for multi-episode ranges, date-based and absolute-numbered episodes, split parts, and movie editions.
- Review filters, provider IDs, clearer confidence evidence, a resizable/scrollable Settings screen, provider test buttons, and About/Credits.
- First-run onboarding for personal provider credentials, connection tests, staging location, and the safer Copy-first workflow.
- Redacted rotating diagnostics and global crash reporting.
- Startup detection and evidence-based recovery for interrupted operations, including safe rollback and explicit resolution.
- Creation/last-write timestamp and safe Windows attribute preservation for local and cross-volume transfers.
- Automated release validation, dependency auditing, startup smoke testing, checksums, and optional signing.
- A disposable fixture generator and real-world acceptance matrix covering matching, companions, transfers, collisions, cancellation, crash recovery, UNC failures, DPI, keyboard use, and accessibility.

### Changed

- A batch must be fully reviewed and pass preflight validation before file operations begin.
- Release builds now treat compiler warnings and formatting drift as failures.
- CI test discovery now uses the .NET 10 Microsoft Testing Platform solution syntax and fails when no tests are discovered.

## 0.1.0

Initial preview release.
