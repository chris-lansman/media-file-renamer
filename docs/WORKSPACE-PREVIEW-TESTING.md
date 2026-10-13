# Review workspace preview

Run `dist/MediaFileRenamer-ux-preview/MediaFileRenamer.exe` from this checkout. This is a separate self-contained test build, version `1.1.11-preview.2`. It uses your normal app settings and saves the review workspace locally; it is not a published release.

Use a small disposable batch first. Close any other copy of the app before testing session restoration.

1. Add movies and TV episodes, including a subtitle with the same filename stem. Search, switch filters, and enable show/season grouping. Review counts must still describe the entire batch.
2. Select a row. Inspect highlighted filename changes, expand full paths and companions, copy a path, and open the source folder.
3. Use Previous/Next review. Start in the middle of a batch, correct a flagged row under Edit media details and Confirm and next. It should continue forward and wrap to earlier unresolved rows. Movies should hide TV-specific fields. After the last flagged row, the Needs review filter should show a review-complete explanation; Show all files should return to the whole batch.
4. Remove one row from the batch and verify its original file remains on disk.
5. Choose Custom token format. Insert tokens, check the live example, and try an unknown token such as `{Ttile}`. The inline error should explain the problem and prevent applying an invalid destination.
6. Resize the window, adjust columns, leave some edited rows staged, and close/reopen. Existing files and edits should return; restored rows must require confirmation. Clear the batch when you want to discard it.
7. Copy the reviewed batch to a disposable output folder. The completion panel should show the result, folder shortcut, cleanup details, and Undo. Confirm Undo removes only the copied output, leaving the originals intact.
8. Check the UI with your normal Windows theme and display scale. The final action should stay visible when the content scrolls.
9. Clear batch, then Restore cleared batch. Corrections should return, sources should remain unchanged, and fresh confirmation should be required. New rows added after clearing must remain. The shortcut applies only to the most recently cleared batch in this session.
10. Re-match All with a mixture of automatic and manual choices. Keep the manual choices once, including related TV episodes; then explicitly include them in a second run. Existing automatic matches should be looked up again.
11. At a narrow width, expand Staging options to change operation or folder. The filename comparison should appear above the table, and readiness wording should track Move versus Copy without implying a person reviewed automatic matches.

Automated validation covers the existing rename/recovery safeguards plus session serialization, fresh-review restoration, search/filter gating, removal without deletion, grouping, companion destinations, busy-state restrictions, naming validation, and rendered layouts at 640, 1024, and 1360 pixels wide.
