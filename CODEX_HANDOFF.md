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
- Expected local repository: `C:\Users\cclan\Documents\MediaFileRenamer`
- Windows application project: `src\MediaFileRenamer.App\MediaFileRenamer.App.csproj`
- Test project: `tests\MediaFileRenamer.Tests\MediaFileRenamer.Tests.csproj`
- Last feature commit before this handoff document: `18a6603 Clarify match status in the review workspace`

Run tests:

```powershell
dotnet test MediaFileRenamer.sln --configuration Release
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
- Default operation is **Move to staged folder**; Copy is also available.
- The user primarily uses Plex. The main preset should remain Plex-friendly and include TMDB IDs where available.
- The user wants a side-by-side source and proposed-name review, similar in spirit to FileBot but easier to read.
- File extensions and technical detail should not dominate the review UI. The important signal is whether a row is safely matched or needs attention.
- TMDB and TVDB keys live in local settings and are displayed as normal text fields, not masked. Do not commit keys to Git.
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
- TVDB is a saved fallback for TV series searches; returned TVDB IDs are resolved through TMDB for the final match data.
- Default auto-match confidence: `92%`.
- High-confidence, non-near-tied candidates auto-select. Ambiguous candidates open the poster picker.
- Matching looks at both movie and TV possibilities and includes parent-folder evidence for TV items.
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
- `src\MediaFileRenamer.App\Services\TvdbClient.cs` - TVDB fallback lookup.
- `src\MediaFileRenamer.App\Services\RenamePlanner.cs` - Plex/custom destination generation and path safety.
- `src\MediaFileRenamer.App\Services\RenameApplier.cs` - move/copy, collision checks, and empty-folder cleanup.
- `src\MediaFileRenamer.App\ViewModels\MediaPreviewItem.cs` - preview row state, including `MatchState`.

## Naming

Custom formats are relative to the chosen output folder; the original file extension is added automatically.

Supported tokens:

```text
{Title}
{Year}
{Season}
{Episode}
{EpisodeTitle}
{TmdbId}
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

This is a meaningful improvement, but it is not yet sufficient protection against an unattended batch apply.

## Highest-Priority Follow-Up: Hard Review Gate

This is the active product issue from the most recent session.

### Why it matters

FileBot's Rename workspace makes a visual distinction between original files and proposed mappings, but it does not provide an obvious persistent review-count warning. The user correctly pointed out that a person should not have to scan every row to discover an uncertain match.

The current application has a real safety gap:

- `MainWindow.ApplyRename_Click` immediately calls `_applier.Apply(PreviewItems, operation)`.
- `RenameApplier.Apply` blocks unknown media types, unresolved TV episode numbers, missing sources, collisions, and invalid destinations.
- It **does not** block an item simply because `MatchState` is `Review needed`.

Therefore a mixed batch can partially move the items that pass basic validation while an uncertain row is left behind or moved from a local guess. The README currently says unresolved files cannot be moved; that statement should be corrected only after the code enforces it.

### Recommended implementation

Make applying a batch impossible while any row is unresolved:

1. Add a reusable predicate such as `RequiresReview` for rows with `Review needed`, `Ready to match`, or `Blocked` state. A deliberate `Manual choice` is valid.
2. Surface a count, for example `3 need review`, adjacent to the main Apply Rename control and/or the Proposed Names heading.
3. Disable **Apply Rename** while the count is non-zero.
4. If an apply is attempted through another path, show a review dialog or select the first unresolved item and direct the user to **Choose...**. Do not start a partial move.
5. Add focused tests proving that a batch with one unresolved item does not move any item, and that a fully matched/manual-choice batch still moves normally.
6. Update README wording after tests pass.

The desired behavior is deliberately conservative: all items must be explicitly safe before any files in that batch are moved or copied.

## Existing Test Coverage

The test suite currently covers:

- filename parsing;
- movie and TV planning;
- custom-path containment and Windows reserved names;
- TMDB error handling and confidence behavior;
- duplicate destinations;
- move/copy behavior;
- source-folder cleanup;
- unresolved media blocking;
- WPF window startup.

When implementing the review gate, add tests at both the view-model/planning layer and the apply boundary. The current `RenameApplier` tests should be expanded to ensure no row is moved if the batch contains an unresolved match.

## Packaging and Releases

GitHub Actions workflow: `.github\workflows\build.yml`

- Pushes and pull requests to `main` run restore, build, and tests on `windows-latest`.
- Successful pushes to `main` package a self-contained `win-x64` ZIP and SHA-256 file as a 30-day artifact.
- A version tag such as `v0.1.0` creates a permanent GitHub Release with the ZIP and checksum.
- There are intentionally no self-hosted or GitLab runners.

## Practical Continuation Checklist

1. Implement the hard review gate above.
2. Run the full tests.
3. Build the self-contained executable into `dist\MediaFileRenamer`.
4. Manually test a batch containing one obvious match and one intentionally ambiguous/unmatched file. Confirm no move occurs until both are resolved.
5. Commit with a focused message and push `main`; GitHub Actions will publish the development artifact.

## Safety Notes

- Never overwrite an existing destination.
- Do not expose or commit API keys.
- Do not perform a real move/copy during automated UI inspection without explicit user confirmation.
- Keep existing user media untouched while diagnosing matching behavior.
- Do not remove unrelated directories or reverse existing user changes.
