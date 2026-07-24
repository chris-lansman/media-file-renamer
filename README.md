# Media File Renamer

A Windows desktop app for staging Plex-friendly movie and TV filenames before moving them to media storage.

## Current workflow

1. Add files, add a folder, or drag media into the app.
2. Choose an output folder. The default is `%USERPROFILE%\renamed-media`.
3. Pick `Move to staged folder` or `Copy to staged folder`.
4. Use the Plex preset, flat review preset, or a custom token format.
5. Click `Match All`, review every destination beside its original file, then click `Apply Rename`.

Uncertain files are searched as both movies and TV shows. TMDB and TVDB candidates are merged when the first result is missing or ambiguous, and TVDB-only series are retained instead of being discarded. Clear matches are selected automatically; close results open the poster picker with provider IDs, confidence, and the evidence behind the score. You can also use `Choose...` to force the picker or manually classify an unmatched file as a movie or TV episode. Local Movie/TV classification remains available when no TMDB key is configured.

Season-zero specials are resolved through TMDB like regular episodes. When TMDB does not have the episode, an enabled TVDB fallback queries the selected episode order for the exact season/episode, absolute number, or air date. Folders named `Specials`, `Special`, `Season 00`, or `S00` are treated as season containers, so the parent series folder supplies the show identity instead of the generic folder/file label. Multi-episode ranges, date-based episodes, absolute-numbered episodes in explicit TV folders, split parts, and common movie editions are recognized.

A TV row is only marked `Matched` when its series, season/episode number, and episode title are resolved. The match badge identifies the episode provider as `Matched · TMDB` or `Matched · TVDB`. A series-only result remains `Review needed` instead of presenting a generic filename as a completed match. If a filename already contains an episode title that conflicts with TVDB, the app leaves the row for review instead of silently replacing it.

The review counter beside `Apply Rename` shows how many rows still need attention. Filters isolate matched, unresolved, or failed rows. The button remains disabled while any row is marked `Review needed`, `Ready to match`, or `Blocked`. The file-operation layer enforces the same all-or-nothing review gate, so a mixed batch cannot partially move safe-looking rows while an unresolved row remains. TV episodes must resolve to a season and episode number before the app will apply them.

Apply performs a batch preflight and shows the exact operation, file count, companion-file count, size, and destination before changing anything. Subtitles, NFO files, artwork, and other same-stem companions follow the media file. Copy and cross-volume move operations use a temporary file, verify the completed length, and atomically finalize it. A failure or cancellation rolls back completed transfers. Recent operation journals are available from `File > Operation History`, and the most recent completed operation can be undone.

## Settings

Use `File > Settings` to save your TMDB API key, TVDB API key, optional TVDB subscriber PIN, fallback preference, and default output folder. Settings are stored locally at:

```text
%LOCALAPPDATA%\MediaFileRenamer\settings.json
```

The default auto-match confidence is `92%`. Matches at or above that score are chosen automatically unless the next best result is nearly tied; uncertain matches still open the poster picker.

Metadata access uses bring-your-own credentials. The application does not include, share, or proxy a TMDB or TVDB credential: each user supplies credentials issued for their own use. They remain visible in Settings and are stored in the local settings file so their owner can inspect and update them easily.

Use the provider **Test** buttons in Settings to validate credentials without saving first. Settings are scrollable and resizable. Diagnostic logs are stored under `%LOCALAPPDATA%\MediaFileRenamer\Logs`; provider credentials and local paths are redacted from those logs.

## Format tokens

Custom formats are relative to the output folder. The app adds the original file extension automatically.

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

## Metadata cleanup

The app currently renames/stages only. Embedded artwork removal is intentionally not enabled because it can require rewriting large media files. Clearing embedded title metadata can be added later as an advanced, container-aware option.

## Run the app

Double-click the published executable:

```text
dist\MediaFileRenamer\MediaFileRenamer.exe
```

The PowerShell script is only a developer helper for running from source.

## Downloadable builds

Every push to `main` that passes the test suite produces a downloadable Windows ZIP on that workflow run's GitHub Actions page. These development artifacts are retained for 30 days.

Version tags such as `v1.0.0` produce a permanent GitHub Release containing:

```text
MediaFileRenamer-win-x64.zip
MediaFileRenamer-win-x64.zip.sha256
```

The ZIP is a self-contained Windows x64 build and does not require a separate .NET installation. The checksum can be used to verify that the downloaded ZIP is unchanged.

## Install and verify

Download both release files into the same folder, then verify the ZIP before extracting it:

```powershell
$expected = (Get-Content .\MediaFileRenamer-win-x64.zip.sha256).Split(' ', [StringSplitOptions]::RemoveEmptyEntries)[0]
$actual = (Get-FileHash .\MediaFileRenamer-win-x64.zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "Checksum mismatch" }
```

Extract the ZIP to a user-owned folder and run `MediaFileRenamer.exe`. Published builds are self-contained and portable; uninstalling consists of closing the app and deleting that extracted folder. User settings and operation data under `%LOCALAPPDATA%\MediaFileRenamer` are intentionally separate.

A build is Authenticode-signed only when the repository's optional signing secrets are configured. On an unsigned build, Windows may show a SmartScreen warning. Check the executable's **Properties > Digital Signatures** tab before relying on a claimed publisher identity.

Public-repository builds also receive GitHub artifact provenance. Verify an attested ZIP with:

```powershell
gh attestation verify .\MediaFileRenamer-win-x64.zip --repo chris-lansman/media-file-renamer
```

## Versions and releases

Release tags use semantic versions such as `v1.0.0`. Tagged builds embed the tag version in the executable and create a permanent GitHub Release; regular `main` builds receive a `1.0.0-ci.<run>` version. User-visible changes are maintained in [CHANGELOG.md](CHANGELOG.md).

The release workflow verifies formatting, treats compiler warnings as errors, runs the full test suite, audits vulnerable and deprecated NuGet dependencies, publishes a self-contained Windows x64 package, starts that exact published executable as a smoke test, and verifies its SHA-256 checksum.

Maintainer setup for optional signing and the remaining MSIX requirements is documented in [docs/RELEASING.md](docs/RELEASING.md).

Before promoting a build for daily end-user use, run the disposable fixture
generator and complete the [real-world acceptance matrix](docs/ACCEPTANCE.md).

## Development

The solution targets .NET 10 on Windows. Build and run the complete test suite with:

```powershell
dotnet restore MediaFileRenamer.sln
dotnet format MediaFileRenamer.sln --verify-no-changes --no-restore --severity warn
dotnet build MediaFileRenamer.sln --configuration Release --no-restore -p:TreatWarningsAsErrors=true
dotnet test --solution MediaFileRenamer.sln --configuration Release --no-build --no-restore --results-directory TestResults --report-trx --report-trx-filename MediaFileRenamer.trx --minimum-expected-tests 1
.github\scripts\Test-NuGetAudit.ps1 -SolutionPath MediaFileRenamer.sln
```

The tests cover advanced filename parsing, movie/TV and custom naming, Windows path safety, unified TMDB/TVDB matching, provider-order episode lookup, retries and cancellation, metadata-provider labeling, companion files, collision and write preflight, transactional move/copy rollback, timestamp preservation, operation journals, interrupted-operation recovery and undo, settings recovery, first-run onboarding, diagnostic redaction, provider checks, the all-or-nothing review gate, offline manual classification, and WPF window startup/state.

The app never overwrites an existing destination or silently changes the reviewed destination name. Successful items are removed from the review list; failed items remain with an actionable status.
