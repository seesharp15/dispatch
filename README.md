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
- Newly added recordings now flash green briefly to highlight recent arrivals.
- API timestamps are now marked as UTC so the UI renders them in local time.
- Recording lists now support archiving per recording or per day, with a show-archived toggle in the feed activity pane.
- Recording day expansion state and scroll position are now preserved during live updates so new items append without collapsing your view.
- New-recording flash highlights only trigger for truly new arrivals within a short time window, avoiding full-list flashes on load.
- Completed recordings no longer display a status badge, keeping focus on in-progress/queued states.
- Broadcastify discovery results are now cached per state for 30 days and only fetched on state expansion.
- Status badges now color-code Live/Processing as green, Pending as orange, and Paused/Failed in red.
- Queue indicators now show position and total size as `Queue: (position/total)`.
- Recording status/queue indicators now sit after the audio player, and transcript toggles only appear once a transcript is complete.
- Feed discovery now preloads all state feeds into the left tree (cached server-side), so search works across the full list without manually expanding states.
- Start buttons now render in neutral gray while Stop remains red for clearer at-a-glance status.
- Recording list scroll position now anchors to the visible item during refreshes so updates don’t jump your view.
