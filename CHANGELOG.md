# Changelog

Notable user-visible changes are recorded here. This project follows
[Semantic Versioning](https://semver.org/) for tagged releases.

## Unreleased

## 1.1.2 - 2026-08-21

### Fixed

- Leading-year movie releases such as `1987.Lethal.Weapon.1920x1080.BDRip.x264.DTS-HD.MA.mkv` now extract `Lethal Weapon` and `1987` before matching.
- Release-name cleanup now recognizes common resolution, BDRip, and DTS-HD MA tokens so they do not contaminate movie lookups.

## 1.1.1 - 2026-08-12

### Added

- Actionable `Review needed` guidance that explains the problem and focuses the first unresolved row.
- A searchable episode browser for choosing an exact episode from an already matched show.
- An explicit **Confirm Edited Details** action so human corrections clear the review gate safely.
- Real-provider regression coverage for non-standard Scooby-Doo episode names.

### Changed

- Missing seasons are inferred from sibling files only when every explicit sibling season agrees.
- High-confidence partial episode titles can resolve conservatively when the provider title contains extra words.
- Review actions now distinguish retrying metadata, choosing a show, browsing episodes, and confirming manual edits.

## 1.1.0 - 2026-07-30

### Added

- Remembered source-folder show mappings with reusable TMDB/TVDB identities and episode-order preferences.
- Bounded, expiring, corruption-tolerant metadata caching for repeat TMDB and TVDB lookups.
- Automatic Windows light/dark palette support while retaining High Contrast behavior.
- Selected operation details, safe per-operation Undo, Copy Details, and Open Destination actions.
- Manual update checking from About with an always-available release download link.
- Weekly dependency auditing, grouped Dependabot updates, and a broader real-world filename regression corpus.
- PGS `.sup` subtitle companion handling and additional modern release-tag cleanup.

### Changed

- The main workspace now uses one unified original-to-proposed review table with row actions, a clearer empty state, accurate batch summaries, retry-unresolved support, and a dynamic Copy/Move action.
- Settings validates confidence and output-folder values explicitly and can review or remove remembered show mappings.
- Local classification uses a compact workflow when provider credentials are unavailable.

## 1.0.0 - 2026-07-24

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
