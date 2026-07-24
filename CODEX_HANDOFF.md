# Media File Renamer - Codex Handoff

## Purpose

`Media File Renamer` is a private Windows desktop application for staging movie and TV files into Plex-friendly names before they are copied to network storage. It is a personal replacement for FileBot, with a clearer review workflow.

The preferred staging location is:

```text
C:\Users\cclan\renamed-media
```

The app should move or copy files there first. It must not automatically place media into the user's final kids/adult network-library locations.

## Repository and Build

- Repository: `https://github.com/chris-lansman/media-file-renamer` (private)
- Primary branch: `main`
- Current local repository: `C:\Users\clansman\repos\media-file-renamer`
- Windows application project: `src\MediaFileRenamer.App\MediaFileRenamer.App.csproj`
- Test project: `tests\MediaFileRenamer.Tests\MediaFileRenamer.Tests.csproj`
- Last feature commit before this handoff document: `18a6603 Clarify match status in the review workspace`

Run the exact CI test command after restore and a Release build:

```powershell
dotnet test --solution MediaFileRenamer.sln --configuration Release --no-build --no-restore --results-directory TestResults --report-trx --report-trx-filename MediaFileRenamer.trx --minimum-expected-tests 1
```

Build a local self-contained executable:

```powershell
dotnet publish src\MediaFileRenamer.App\MediaFileRenamer.App.csproj --configuration Release --runtime win-x64 --self-contained true --output dist\MediaFileRenamer
```

Local executable:

```text
dist\MediaFileRenamer\MediaFileRenamer.exe
```

## Product Decisions Already Made

- Windows executable first; the user should never need to launch a PowerShell script to use the application.
- The app is a staging and renaming tool, not an automatic media-library importer.
- Existing users retain their saved/default operation. First-run onboarding recommends **Copy to staged folder** until the user has verified the workflow; Move remains available.
- The user primarily uses Plex. The main preset should remain Plex-friendly and include TMDB IDs where available.
- The user wants a side-by-side source and proposed-name review, similar in spirit to FileBot but easier to read.
- File extensions and technical detail should not dominate the review UI. The important signal is whether a row is safely matched or needs attention.
- TMDB and TVDB keys and the optional TVDB subscriber PIN live in local settings and are displayed as normal text fields, not masked. Do not commit them to Git.
- Metadata rewriting is deliberately off by default. Renaming/staging is the primary feature. Future embedded-metadata cleanup must be an advanced opt-in because it can require rewriting large media files.
- Folder cleanup after a successful move is enabled only for empty source folders, with protections for user profile, Downloads, and Windows special folders.

## Current User Workflow

1. Add individual files, a folder, or drag media into the app.
2. Set the staging output folder, operation, and naming preset.
3. Click **Match All**.
4. Review the source list beside the proposed destination list.
5. Use **Choose...** for a deliberate poster-based movie/TV selection when needed.
6. Click **Apply Rename** once the batch is safe.

Successful rows are removed from the list after the move/copy. Rows that fail remain, with a status describing the problem.

## Matching and Metadata

- TMDB is the primary lookup source.
- TMDB remains primary, but ambiguous or missing series searches are merged with TVDB. Genuine TVDB-only candidates are supported and retain a TVDB identity when TMDB has no mapping.
- Episode resolution supports TVDB default, official, DVD, absolute, alternate, and regional orders. Absolute-number and air-date lookups are supported.
- If the filename contains an episode title, TVDB fallback requires that title to agree after punctuation/case normalization. A conflict stays `Review needed`.
- Matched TV rows display the episode provider as `Matched · TMDB` or `Matched · TVDB`.
- Default auto-match confidence: `92%`.
- High-confidence, non-near-tied candidates auto-select. Ambiguous candidates open the poster picker.
- Matching looks at both movie and TV possibilities and includes parent-folder evidence for TV items.
- Candidate rows show TMDB/TVDB provenance, provider IDs, confidence, and scoring evidence.
- Transient metadata `429` and `5xx` responses use bounded retry/backoff, and lookups accept cancellation.
- `Specials`, `Special`, `Season 00`, and `S00` folders are treated as season containers; their parent folder provides the series identity.
- TMDB season `0` is queried directly for specials, and text after an `SxxEyy` code is retained as episode-title evidence.
- A TV row is marked `Matched` only after the episode number and title are resolved; a series-only match remains `Review needed`.
- A selected TV identity is applied to related items in the same source group.
- `Choose...` always opens the match picker. It also supports a local Movie/TV choice when no remote match exists.
- Settings file:

```text
%LOCALAPPDATA%\MediaFileRenamer\settings.json
```

Important implementation files:

- `src\MediaFileRenamer.App\MainWindow.xaml` - main review workspace and controls.
- `src\MediaFileRenamer.App\MainWindow.xaml.cs` - scan, match, selection, destination refresh, and apply handling.
- `src\MediaFileRenamer.App\MatchPickerWindow.xaml` and `.xaml.cs` - poster/result picker.
- `src\MediaFileRenamer.App\Services\TmdbClient.cs` - TMDB search, confidence scoring, and detail lookup.
- `src\MediaFileRenamer.App\Services\TvdbClient.cs` - TVDB login, series search, and exact episode fallback.
- `src\MediaFileRenamer.App\Services\TvEpisodeMetadataResolver.cs` - TMDB-first episode resolution and guarded TVDB fallback.
- `src\MediaFileRenamer.App\Services\RenamePlanner.cs` - Plex/custom destination generation and path safety.
- `src\MediaFileRenamer.App\Services\RenameApplier.cs` - move/copy, collision checks, and empty-folder cleanup.
- `src\MediaFileRenamer.App\Services\OperationJournalService.cs` - durable operation history and undo.
- `src\MediaFileRenamer.App\RecoveryWindow.xaml` and `.xaml.cs` - asynchronous interrupted-operation inspection, rollback, and resolution.
- `src\MediaFileRenamer.App\Services\MetadataMatchService.cs` - unified TMDB/TVDB candidate aggregation.
- `src\MediaFileRenamer.App\ViewModels\MediaPreviewItem.cs` - preview row state, including `MatchState`.

## Naming

Custom formats are relative to the chosen output folder; the original file extension is added automatically.

Supported tokens:

```text
{Title}
{Year}
{Season}
{Episode}
{EpisodeEnd}
{AirDate}
{AbsoluteEpisode}
{Part}
{EpisodeTitle}
{TmdbId}
{TvdbId}
{ProviderId}
{Edition}
{EditionTag}
```

Examples:

```text
Movies\{Title} ({Year}) {TmdbId}\{Title} ({Year}) {TmdbId}
TV Shows\{Title}\Season {Season}\{Title} - S{Season}E{Episode} - {EpisodeTitle}
```

## Current UI State

The latest UI pass decluttered the review grids and added a semantic `MatchState` badge to the proposed-name grid:

- `Matched`
- `Review needed`
- `Manual choice`
- `Blocked`
- `Complete`

The UI and file-operation boundary both enforce the hard review gate.

## Completed Follow-Up: Hard Review Gate

The hard review gate is implemented across the row model, main window, and file-operation boundary.

### Why it matters

FileBot's Rename workspace makes a visual distinction between original files and proposed mappings, but it does not provide an obvious persistent review-count warning. The user correctly pointed out that a person should not have to scan every row to discover an uncertain match.

Current behavior:

- `MediaPreviewItem.RequiresReview` covers `Review needed`, `Ready to match`, and `Blocked`. A deliberate `Manual choice` is valid, except that TV choices still need season and episode numbers.
- A live review count appears beneath **Apply Rename**.
- **Apply Rename** remains disabled until every item is resolved and has a destination.
- The apply handler selects the first unresolved item and explains the block if invoked through another path.
- `RenameApplier.Apply` independently rejects the entire batch when any row requires review.
- Local Movie/TV classification remains available through **Choose...** without a TMDB key.
- Focused model, WPF state, and apply-boundary tests protect these behaviors.

## Existing Test Coverage

The test suite covers:

- filename parsing;
- movie and TV planning;
- custom-path containment and Windows reserved names;
- TMDB error handling and confidence behavior;
- duplicate destinations;
- companion-file association and naming;
- transactional move/copy behavior, rollback, journals, and undo;
- interrupted-operation detection and evidence-based recovery;
- creation/last-write timestamp and safe Windows attribute preservation;
- source-folder cleanup;
- unresolved media blocking;
- settings normalization and atomic save;
- first-run onboarding state and default-operation persistence;
- diagnostic redaction, provider connection tests, and WPF window startup.

The review-gate tests cover the view model, WPF Apply state/count, offline local picker, whole-batch blocking, and mixed matched/manual batches. Matching tests cover PIN authentication, multiple episode orders, absolute and date-based lookup, exact-coordinate enforcement, caching, TVDB-only candidates, cross-provider aggregation, provider labeling, aliases, retries, and conflicting-title rejection.

## Packaging and Releases

GitHub Actions workflow: `.github\workflows\build.yml`

- Pushes and pull requests to `main` run restore, formatting verification, warnings-as-errors build, tests, and NuGet auditing on `windows-latest`.
- CI uses Microsoft Testing Platform's named `--solution` form and requires at least one discovered test, preventing a zero-test run from passing silently.
- Successful pushes to `main` package a self-contained `win-x64` ZIP and SHA-256 file as a 30-day artifact.
- A version tag such as `v0.1.0` creates a permanent GitHub Release with the ZIP and checksum.
- Optional Authenticode signing is gated on both signing secrets being configured; unsigned builds remain supported.
- There are intentionally no self-hosted or GitLab runners.

## Practical Continuation Checklist

1. Run the strict validation commands from `README.md`.
2. Build the self-contained executable into a clean `dist` folder.
3. Generate disposable fixtures and complete `docs\ACCEPTANCE.md` against the exact packaged candidate, including recovery, timestamp, and UNC rows where network-share support is claimed.
4. Commit with a focused message and push `main`; GitHub Actions will publish the development artifact.

## Safety Notes

- Never overwrite an existing destination.
- Do not expose or commit API keys.
- Do not perform a real move/copy during automated UI inspection without explicit user confirmation.
- Keep existing user media untouched while diagnosing matching behavior.
- Do not remove unrelated directories or reverse existing user changes.
