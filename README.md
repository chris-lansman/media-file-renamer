# Media File Renamer

A Windows desktop app for staging Plex-friendly movie and TV filenames before moving them to media storage.

## Current workflow

1. Add files, add a folder, or drag media into the app.
2. Choose an output folder. The default is `C:\Users\cclan\renamed-media`.
3. Pick `Move to staged folder` or `Copy to staged folder`.
4. Use the Plex preset, flat review preset, or a custom token format.
5. Click `Match All`, review every destination beside its original file, then click `Rename`.

Uncertain files are searched as both movies and TV shows. Clear matches are selected automatically; close or ambiguous results open the poster picker for confirmation. TV episodes must resolve to a season and episode number before the app will move them.

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
