# Changelog

Notable user-visible changes are recorded here. This project follows
[Semantic Versioning](https://semver.org/) for tagged releases.

## 1.1.9 - 2026-09-10

### Added

- Hover over a matched cover thumbnail to see a larger poster with the matched title and year. The preview appears after a short delay and closes when the pointer leaves.

## 1.1.8 - 2026-09-10

### Added

- Matched media displays a small provider poster beside its original filename, including related TV episodes.
- Move results report source folders that could not be removed, with paths and reasons available in the status tooltip.

### Fixed

- Empty read-only source folders can be removed after a successful move.
- Cleanup normalizes trailing directory separators so it stops precisely at the selected source folder.
- Folders containing remaining files are preserved and their cleanup failures are no longer silent.

## 1.1.7 - 2026-09-10

### Added

- Adding files, folders, or drag-and-dropped media now starts metadata matching automatically and leaves only uncertain items for review.

### Changed

- A single credible metadata result can be selected automatically at 80% confidence or better, while multiple candidates still require the configured confidence threshold and a clear lead.
- Batch re-matching and retrying unresolved items no longer open a sequence of chooser dialogs; unresolved rows remain focused for deliberate review.
- The main workflow now labels the manual batch action as **Re-match All** and explains that newly added media matches automatically.

### Fixed

- Movie folder lookups now use the title before the release year, preventing resolution, codec, audio, and release-group text from contaminating searches such as the Austin Powers trilogy.
- Plex-standard movie filenames contain the title and year only; the TMDB identifier remains on the movie folder where Plex expects it.

## 1.1.6 - 2026-09-08

### Fixed

- The portable updater no longer deadlocks while verifying the downloaded package after the app closes.
- The app now waits for the post-exit helper to confirm it is ready before closing. If a later install step fails, it relaunches the existing version and displays the specific result instead of failing silently.

### Changed

- Release packaging now exercises the real self-contained update helper, file replacement, and relaunch path before publishing an asset.

## 1.1.5 - 2026-09-07

### Fixed

- Movie releases with audio channel layouts such as `TrueHD.7.1` no longer get misread as date-based TV episodes.
- Successful Move operations now clean empty nested source folders up to the folder selected by the user, while Copy operations and non-empty folders remain untouched.
- Update checks now explain unavailable private release feeds and GitHub rate limiting instead of showing a generic error.

## 1.1.4 - 2026-08-21

### Fixed

- The updater now stages the portable app host together with the .NET runtime files it needs, so the post-exit helper can reliably install and restart the update.

## 1.1.3 - 2026-08-21

### Added

- The app now checks GitHub Releases at startup and offers an available update.
- The update window can download the published Windows package, verify its SHA-256 checksum, install it after the app closes, and restart the updated app.

### Security

- Automatic updates accept only the expected ZIP and checksum assets from this repository's HTTPS GitHub Release path, reject unsafe archive paths, and preserve a rollback copy while replacing files.

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
