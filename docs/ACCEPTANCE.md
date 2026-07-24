# Real-world acceptance matrix

Use this matrix before declaring a release suitable for daily end-user use.
Automated tests remain required, but they do not replace these checks against
the packaged application, real metadata services, Windows desktop settings,
and a disposable network share.

Never use irreplaceable media or a production library for interruption,
collision, crash, or Undo tests. Never paste provider credentials into this
document, screenshots, logs, issue reports, or the fixture manifest.

## Prepare a run

1. Download the packaged Windows x64 ZIP produced by the candidate workflow,
   verify its checksum, extract it to a user-owned folder, and launch that
   exact executable.
2. Use a clean Windows user profile or a VM snapshot when validating the
   first-run experience. Do not rename or remove an existing personal settings
   file just to simulate first run.
3. Generate uniquely named synthetic inputs:

   ```powershell
   .\scripts\New-AcceptanceFixture.ps1 `
     -Root C:\MediaFileRenamer-Acceptance `
     -StressFileSizeMB 512
   ```

4. For UNC checks, use a share created solely for destructive testing:

   ```powershell
   .\scripts\New-AcceptanceFixture.ps1 `
     -Root C:\MediaFileRenamer-Acceptance `
     -UncRoot \\test-server\disposable-share\MediaFileRenamer `
     -StressFileSizeMB 1024
   ```

The script creates a fresh run folder every time. It does not overwrite or
delete files and does not read or store credentials. Its JSON manifest records
all scenario paths and the known source timestamp (`2020-02-03T04:05:06Z`).
If a transfer completes too quickly to cancel or interrupt, increase
`StressFileSizeMB` or use a larger disposable media file.

Record this run header:

| Field | Value |
| --- | --- |
| Candidate version and commit | |
| ZIP SHA-256 verified | Yes / No |
| Windows edition and build | |
| Display scale(s) | |
| Local source/destination file system | |
| UNC server/share and protocol | Do not record credentials |
| TMDB test status | Success / Not run |
| TVDB test status | Success / Not run |
| Tester and date | |

For every row below, record `Pass`, `Fail`, `Blocked`, or `Not applicable` plus
a short evidence note. A required row may be `Not applicable` only when the
released feature is explicitly unsupported and documented as such. UNC rows
are required for any release that claims network-share support.

## First run and metadata providers

| ID | Required | Test | Expected result | Result / evidence |
| --- | --- | --- | --- | --- |
| ONB-01 | Yes | Start the packaged app in a clean Windows profile. Complete the first-run checklist without entering credentials. | The checklist explains credentials, provider tests, output folder, and copy versus move. The app remains usable for local/manual classification and can reopen onboarding or Settings. | |
| CRED-01 | Yes | In Settings, leave TMDB and TVDB fields empty and use each provider Test action. | No network request is attempted for an empty required credential. The message identifies what is missing and never displays credential content. | |
| CRED-02 | Yes | Enter the tester's own TMDB credential, test it, save, close, restart, and run a movie search. | The test succeeds, the saved value reloads, and the search returns plausible TMDB candidates. | |
| CRED-03 | Yes | Enter the tester's own TVDB key and optional PIN, test it, save, close, restart, and resolve a TV episode. | The login test succeeds and the episode lookup reports TVDB when fallback is used. | |
| CRED-04 | Yes | Enter a deliberately invalid throwaway value for each provider and test it. Restore the valid value afterward. | Authentication failure is actionable and does not echo the rejected value. | |
| CRED-05 | Yes | After provider tests and searches, inspect `%LOCALAPPDATA%\MediaFileRenamer\Logs`. | Logs contain no API key, PIN, authorization header, or unredacted user-profile path. | |

## Representative media and companions

Add the `local-copy` input folder from the manifest, run Match All, and review
every row. Metadata services can return changing titles, artwork, and ordering,
so verify the identity and provider ID rather than relying only on result rank.

| ID | Required | Fixture / action | Expected result | Result / evidence |
| --- | --- | --- | --- | --- |
| MATCH-01 | Yes | `Blade.Runner.1982.Directors.Cut.1080p.mkv` | Identified as the 1982 movie; the Plex destination includes year, TMDB ID, and edition tag. | |
| MATCH-02 | Yes | `Curious.George.S00E07.mkv` and `Curious.George.S00E08.mkv` under `Specials` | Parent folder supplies the show identity; the files resolve to “Curious George Comes to America” and “Curious George Goes to the Hospital,” with season zero preserved. | |
| MATCH-03 | Yes | `Example.Show.S01E01-E02.Opening.Night.mkv` | The complete episode range is preserved in the destination. | |
| MATCH-04 | Yes | `Daily.Show.2026-07-23.mkv` | Parsed as a date-based TV episode and not silently converted to a movie. | |
| MATCH-05 | Yes | `012 - The Promise.mkv` under `Anime Show (TV Series 2024)\Season 01` | Absolute episode 12 is available for resolution; a bare number outside explicit TV context is not assumed to be TV. | |
| MATCH-06 | Yes | `Example.Show.S01E03.pt2.mkv` | Split part 2 is preserved in the destination. | |
| COMP-01 | Yes | Review the movie and TV companion files. | `.en.srt`, `.en.forced.srt`, `.nfo`, and artwork follow the correct primary file; subtitle qualifiers remain intact; companions are counted once in preflight. | |

## Local operations, collisions, cancellation, and recovery

Run each scenario from a fresh fixture input. Before applying, confirm that
preflight shows the exact operation, source count, companion count, byte total,
and destination root.

| ID | Required | Test | Expected result | Result / evidence |
| --- | --- | --- | --- | --- |
| COPY-01 | Yes | Copy the entire `local-copy` input to its local destination. | All reviewed primaries and companions arrive; sources remain; byte lengths and creation/last-write timestamps match their sources; the journal is `Completed`. | |
| COPY-02 | Yes | Undo COPY-01 without modifying its destinations. | Only files created by that operation are removed; original sources remain; no unrelated file or directory is removed. | |
| MOVE-01 | Yes | Move the entire `local-move` input to its local destination. | All reviewed files arrive; transferred files preserve creation/last-write timestamps; source files and newly empty source directories are removed; the journal is `Completed`. | |
| MOVE-02 | Yes | Undo MOVE-01 without modifying its destinations. | Primaries and companions return to their original paths with timestamps intact; destination files created by the move are removed. | |
| COLL-01 | Yes | In the `collision` scenario, match all rows, then place a sentinel file at one exact planned destination before Apply. | Preflight blocks the whole batch before transfer. The sentinel is unchanged and no other source or destination changes. | |
| CANCEL-01 | Yes | Start copying the `cancellation` stress file and press Cancel while bytes are transferring. | The operation stops promptly, sources remain, temporary/partial destinations are removed, completed items from the batch roll back, and history reports cancellation rather than success. | |
| CRASH-01 | Yes | Start copying a fresh stress fixture, end only `MediaFileRenamer.exe` from Task Manager during transfer, then relaunch. | Startup identifies the interrupted `InProgress` journal, explains recovery choices, and recovery leaves either the verified completed operation or the original source state without a partial final file. | |
| CRASH-02 | Yes | Relaunch once more after CRASH-01 recovery. | The same journal is no longer presented as unresolved; recovery is idempotent and no additional file is changed. | |
| UNDO-01 | Yes | After a successful copy, change one destination file and request Undo. | Undo refuses to delete or replace the changed file and clearly identifies the conflict. | |

Use `Get-Item <path> | Select-Object Length, CreationTimeUtc,
LastWriteTimeUtc` to capture timestamp and length evidence. Check for leftover
temporary files in the destination after cancellation and recovery.

## UNC and network interruption

Use only a disposable share. Do not disconnect a production server, workstation
interface, mapped drive used by others, or a share containing real media. A
second test machine or a share that the tester can safely pause is preferred.

| ID | Required for UNC support | Test | Expected result | Result / evidence |
| --- | --- | --- | --- | --- |
| UNC-01 | Yes | Copy a fresh representative fixture from local storage to the manifest's UNC destination. | Primaries and companions arrive with verified lengths and timestamps; local sources remain; no temporary files remain. | |
| UNC-02 | Yes | Move a fresh representative fixture from local storage to a new UNC destination, then Undo. | Cross-volume move verifies the copy before deleting sources; Undo restores all files and timestamps. | |
| NET-01 | Yes | Start copying the `network` stress file to the disposable UNC target. While it is transferring, pause only that disposable share or disconnect only the test server. Restore the share afterward. | The app reports an actionable I/O failure, does not finalize a partial destination, rolls back completed outputs where reachable, and retains enough journal state for recovery. | |
| NET-02 | Yes | Relaunch after NET-01, with the share first unavailable and then restored. | Startup does not hang. It explains that recovery is blocked while unavailable and completes or safely retries recovery after the same share returns. | |

## Windows desktop accessibility

Perform these checks with the packaged executable, not a source/debug build.

| ID | Required | Test | Expected result | Result / evidence |
| --- | --- | --- | --- | --- |
| DPI-01 | Yes | At 100%, 150%, and 200% display scale, open the main window, Settings, match picker, operation history, and recovery/onboarding UI. | Text is sharp and readable; labels, buttons, status badges, paths, and credential controls are not clipped or overlapped; dialogs remain resizable or scrollable. | |
| DPI-02 | Yes | Move the app between monitors with different scaling, if available. | Layout reflows without disappearing controls, unusable window size, or a restart requirement. | |
| KEY-01 | Yes | Complete first run, add a folder, match/review, open Settings/history, and cancel a dialog using only the keyboard. | Focus is visible and logical; Tab/Shift+Tab reach every control; Enter/Space and Esc behave conventionally; no keyboard trap occurs. | |
| A11Y-01 | Yes | Enable Windows High Contrast and 200% text size. | Content remains legible, focus and selection remain visible, and status is not communicated by color alone. | |
| A11Y-02 | No (recommended) | With Narrator, traverse the main workflow and dialogs. | Interactive controls have meaningful names, state and errors are announced, list rows have usable identity/status, and credential values are not announced unless the field is intentionally focused. | |

Full Narrator workflow certification is a recommended compatibility check, not
a release gate. Keyboard-only operation, meaningful automation names, High
Contrast, and text/display scaling remain required.

## Release decision

Do not call the build stable for daily use until:

- the GitHub `Validate` and packaging jobs are green for the exact candidate;
- every required local, recovery, and accessibility row passes;
- TMDB and TVDB tests pass with tester-owned credentials;
- every UNC row passes if UNC/network-share use is supported;
- failures have reproducible notes and are fixed or explicitly removed from the
  release's supported behavior; and
- the candidate ZIP, commit, test evidence, and known limitations are recorded.

After acceptance, manually remove only the uniquely named local and UNC run
folders printed by the fixture script. Review the paths before deletion.
