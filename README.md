# dispatch
A transcription service for live dispatch audio feeds

## Change notes
- Transcript processing now surfaces an estimated progress percentage (derived from audio file size) and shows queue position when waiting to be transcribed.
- Added scaffolding for Broadcastify feed discovery (domain models, discovery service, DI registration, and state map configuration).
- Local SQLite database artifacts are now ignored to keep repo clean.
