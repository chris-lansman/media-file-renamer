# Media File Renamer

A Windows desktop app for staging Plex-friendly movie and TV filenames before moving them to media storage.

## Current workflow

1. Add files, add a folder, or drag media into the app.
2. Choose an output folder. The default is `%USERPROFILE%\renamed-media`.
3. Pick `Move to staged folder` or `Copy to staged folder`.
4. Use the Plex preset, flat review preset, or a custom token format.
5. Click `Match All`, review every destination beside its original file, then click `Apply Rename`.

Uncertain files are searched as both movies and TV shows. Clear matches are selected automatically; close or ambiguous results open the poster picker for confirmation. You can also use `Choose Match...` to force the picker or manually classify an unmatched file as a movie or TV episode.

Unmatched files preview under `Review Needed` and cannot be moved or copied until they are classified. TV episodes must resolve to a season and episode number before the app will apply them.

## Settings

Use `File > Settings` to save your TMDB API key, TVDB API key, fallback preference, and default output folder. Settings are stored locally at:

```text
%LOCALAPPDATA%\MediaFileRenamer\settings.json
```

The default auto-match confidence is `92%`. Matches at or above that score are chosen automatically unless the next best result is nearly tied; uncertain matches still open the poster picker.

## Format tokens

Custom formats are relative to the output folder. The app adds the original file extension automatically.

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

## Metadata cleanup

The app currently renames/stages only. Embedded artwork removal is intentionally not enabled because it can require rewriting large media files. Clearing embedded title metadata can be added later as an advanced, container-aware option.

## Run the app

Double-click the published executable:

```text
dist\MediaFileRenamer\MediaFileRenamer.exe
```

The PowerShell script is only a developer helper for running from source.

## Development

The solution targets .NET 10 on Windows. Build and run the complete test suite with:

```powershell
dotnet test MediaFileRenamer.sln --configuration Release
```

The tests cover filename parsing, movie and TV planning, custom-path containment, Windows reserved names, TMDB error handling and confidence behavior, duplicate destinations, move/copy behavior, source-folder cleanup, unresolved-media blocking, and WPF window startup.

The app never overwrites an existing destination or silently changes the reviewed destination name. Successful items are removed from the review list; failed items remain with an actionable status.
