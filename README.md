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
