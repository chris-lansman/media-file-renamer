# Changelog

Notable user-visible changes are recorded here. This project follows
[Semantic Versioning](https://semver.org/) for tagged releases.

## Unreleased

### Added

- Transactional staging safeguards, companion-file handling, operation journals, rollback, cancellation, and undo.
- Unified TMDB/TVDB matching, TVDB-only identities, episode-order selection, alias-aware confidence evidence, and bounded provider retries.
- Parsing and naming for multi-episode ranges, date-based and absolute-numbered episodes, split parts, and movie editions.
- Review filters, provider IDs, clearer confidence evidence, a resizable/scrollable Settings screen, provider test buttons, and About/Credits.
- Redacted rotating diagnostics and global crash reporting.
- Automated release validation, dependency auditing, startup smoke testing, checksums, and optional signing.

### Changed

- A batch must be fully reviewed and pass preflight validation before file operations begin.
- Release builds now treat compiler warnings and formatting drift as failures.

## 0.1.0

Initial preview release.
