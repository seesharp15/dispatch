# dispatch
A transcription service for live dispatch audio feeds

## Change notes
- Transcript processing now surfaces an estimated progress percentage (derived from audio file size) and shows queue position when waiting to be transcribed.
- Added scaffolding for Broadcastify feed discovery (domain models, discovery service, DI registration, and state map configuration).
- Local SQLite database artifacts are now ignored to keep repo clean.
- Redesigned the web UI to mirror the WEBB-style three-pane layout with feed discovery tree, drag-and-drop activation, and recordings grouped by day in the right pane.
- Feed activity pane now allows full scroll through overflowed recordings.
- Stopped feeds now remain visible in the active list so they can be restarted.
- Right-hand recordings pane now flexes and scrolls correctly within the viewport layout.
- Active feeds now render as compact single-line rows with truncation to prevent button overflow.
- Settings page now allows toggling auto-refresh and adjusting the refresh interval.
- Settings navigation now properly swaps the dashboard and settings view (no stacking).
- Scrollable list containers now cap at viewport height with overflow scrolling.
- Collapsed recording day groups now shrink to header height instead of filling available space.
- Recording entries are more compact and transcripts can be expanded or collapsed per item.
- Recording rows are now single-line with inline audio, right-aligned transcript toggle, and no 100% indicator for completed items.
- Right-clicking a recording now provides a reprocess transcript action, with the transcript button aligned to the right in the row.
- Double-clicking a recording toggles the transcript open/closed, matching the transcript button behavior.
- State treeview now pins “Statewide” to the top before alphabetized counties.
- Start/stop controls now optimistically update the UI for immediate feedback while the request completes.
- Active feeds now include a remove (X) control to drop a stream from the list.
- UI corner radii have been tightened to a subtle, near-square look across controls and panels.
