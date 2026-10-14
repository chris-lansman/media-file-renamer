# Media File Renamer - Codex Handoff

Last verified: 2026-10-13.

## Current State

- Release integration target: `main`, version `1.1.11`. The user authorized merging the approved `codex/review-workspace-polish` work and publishing the release; follow-up changes should start from updated `main` after integration.
- Latest implementation: `1355b14` (review/workflow refinements); validation record: `b7e06b3`. These are the replacement commit IDs after the identity cleanup.
- The user reviewed the preview and said the changes look great. Preserve the current visual style and keep import controls aligned to the right.
- Local self-contained preview: `dist\MediaFileRenamer-ux-preview\MediaFileRenamer.exe`, version `1.1.11-preview.2+c56a160...`. ZIP and SHA-256 files are alongside it in `dist`; build outputs are ignored by Git and are not uploaded by a branch push.
- Validation: 212 tests passed, formatting and diff checks passed, strict Release build had 0 warnings/errors. Ready and unresolved layouts were rendered at 640/1024/1360 pixels. The exact packaged executable passed two isolated save/reopen cycles and Clear batch restoration without changing the source fixture.
- The release preparation updates MSTest.Sdk to 4.5.1. All 212 tests, formatting, strict Release build, and the dependency audit pass; no vulnerable or deprecated dependencies remain. Startup and isolated workspace restoration checks pass for the 1.1.11 candidate, as do disposable local copy/move, collision, cancel/rollback, Undo/timestamps, and crash-recovery acceptance cases.
- The older `codex/stable-acceptance` and `codex/v1.1-polish` branches are already ancestors of `main`; their unique-commit counts are zero. Only the review-workspace branch needs integration. Verify GitHub PR, tag, workflow, and release assets before declaring publication complete.
- Personal-provider lookups, actual display scaling/themes, and the real-world acceptance matrix still require manual verification. See `docs\WORKSPACE-PREVIEW-TESTING.md` for the current checklist and evidence locations.

## Commit Identity and History Cleanup

- This repository is public, verified through the GitHub API on 2026-10-13. Earlier descriptions of it as private were outdated.
- The user requested the commit display name `chris-lansman` and removal of the work identity from existing history. Repository-local Git configuration now sets `user.name` to `chris-lansman` and `user.email` to `28708312+chris-lansman@users.noreply.github.com`; other repositories' identity settings were not changed.
- Four remote branches and twelve affected annotated release tags were rewritten and pushed with atomic, explicit force-with-lease guards. All 83 retained branch/tag commit trees were verified unchanged, and author/committer/tagger metadata no longer contains the work identity. Bot/service attribution and earlier private-email commits were preserved.
- Fresh agents must verify the repository-local identity before committing. Do not rely on Git's inferred Windows username or machine email. Use the alias and the verified GitHub noreply address for this project.
- Existing clones should be re-cloned or deliberately synchronized with the rewritten refs; merging old history can restore the removed identity. Historical signatures and old commit IDs are affected. GitHub's read-only pull-request refs, caches, other clones, and historical download assets are outside the branch/tag rewrite; do not claim complete erasure from every stored copy.
- A local, ignored recovery bundle exists under `tmp\identity-cleanup-20261013\before-rewrite.bundle`. It retains the old metadata for recovery; never push its original refs back to GitHub.
- Existing preview binaries were packaged before the identity rewrite, so their informational-version suffix still contains the original source hash. The app's code and behavior are unchanged.

## Purpose

`Media File Renamer` is a personal Windows desktop application for staging movie and TV files into Plex-friendly names before they are copied to network storage. It is a personal replacement for FileBot, with a clearer review workflow.

The preferred staging location is:

```text
C:\Users\cclan\renamed-media
```

The app should move or copy files there first. It must not automatically place media into the user's final kids/adult network-library locations.

## Repository and Build

- Repository: `https://github.com/chris-lansman/media-file-renamer` (public; verified 2026-10-13)
- Primary branch: `main`
- Current local repository: `C:\Users\clansman\repos\media-file-renamer`
- Windows application project: `src\MediaFileRenamer.App\MediaFileRenamer.App.csproj`
- Test project: `tests\MediaFileRenamer.Tests\MediaFileRenamer.Tests.csproj`
- Current feature and validation commits are listed under Current State above; verify local/remote heads when resuming.

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
2. Matching starts automatically. Check the staging output folder, operation, and naming preset; narrow windows summarize these under expandable **Staging options**.
3. Review original and proposed filenames in one table. Search, grouping, filters, and Previous/Next review navigation stay above the table; selected-file actions sit beside the highlighted filename comparison.
4. Resolve flagged rows with **Choose Match...**, **Browse Episodes...**, or **Edit media details**. **Confirm and next** continues forward from the selected file, wrapping around to earlier unresolved rows.
5. **Ready to move/copy** means no flagged rows remain, rather than claiming a human inspected automatic matches. After the final flagged item, an empty Needs review filter offers **Show all files**.
6. Confirm **Move N files** or **Copy N files**. The final action and operation status stay visible while the workspace scrolls.

Successful rows are removed from the list after the move/copy. Rows that fail remain, with a status describing the problem.

**Re-match All** repeats matching for existing provider results. When manual choices exist, a dialog defaults to keeping them and allows explicit replacement. Kept manual TV choices are also protected against related-show updates.

**Clear batch** removes queue entries without deleting source files. **Restore cleared batch** recovers only the most recently cleared batch during the current session, rescans sources/companions, preserves newly added rows, and requires fresh review. Closing the app discards this shortcut. File-operation Undo remains separate.

Unfinished work is persisted to `%LOCALAPPDATA%\MediaFileRenamer\workspace.json`. Startup restoration rescans sources and companions, replans destinations, reports missing sources, and always requires fresh review. Never trust saved destinations or status as transfer authorization.

## Matching and Metadata

- TMDB is the primary lookup source.
- TMDB remains primary, but ambiguous or missing series searches are merged with TVDB. Genuine TVDB-only candidates are supported and retain a TVDB identity when TMDB has no mapping.
- Episode resolution supports TVDB default, official, DVD, absolute, alternate, and regional orders. Absolute-number and air-date lookups are supported.
- If the filename contains an episode title, TVDB fallback requires that title to agree after punctuation/case normalization. A conflict stays `Review needed`.
- Matched TV rows display the episode provider as `Matched · TMDB` or `Matched · TVDB`.
- Default auto-match confidence: `92%`.
- High-confidence, non-near-tied candidates auto-select. Automatic batch matching leaves uncertain rows for review; choosing a match opens the poster picker deliberately.
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
- A live review count above the table covers the entire batch, including rows hidden by filters.
- **Move N files** / **Copy N files** remains disabled until every item is resolved and has a destination.
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
- Version tags and explicitly dispatched workflow runs package a self-contained `win-x64` ZIP and SHA-256 file; ordinary branch pushes do not upload local preview binaries.
- A version tag such as `v0.1.0` creates a permanent GitHub Release with the ZIP and checksum.
- Optional Authenticode signing is gated on both signing secrets being configured; unsigned builds remain supported.
- There are intentionally no self-hosted or GitLab runners.

## Practical Continuation Checklist

1. Run the strict validation commands from `README.md`.
2. Build the self-contained executable into a clean `dist` folder.
3. Generate disposable fixtures and complete `docs\ACCEPTANCE.md` against the exact packaged candidate, including recovery, timestamp, and UNC rows where network-share support is claimed.
4. Commit and push the active development branch. Use a pull request for integration into `main`; packaging requires an explicit workflow dispatch or version tag. Do not claim a release is complete while the dependency deprecation gate is failing.

## Safety Notes

- Never overwrite an existing destination.
- Do not expose or commit API keys.
- Do not perform a real move/copy during automated UI inspection without explicit user confirmation.
- Keep existing user media untouched while diagnosing matching behavior.
- Do not remove unrelated directories or reverse existing user changes.
