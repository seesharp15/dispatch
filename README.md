# Dispatch

Live scanner and dispatch audio, segmented into individual calls and
transcribed as they come in.

Point it at a Broadcastify feed (or a local audio device) and it opens the
stream, watches the audio level to split continuous radio traffic into
discrete calls, stores each call as a WAV file, and runs Whisper over it. The
console shows calls arriving in real time, grouped by day, with transcripts
attached.

---

## How it works

```
Broadcastify / local device
        │
        ▼
    ffmpeg ──► AudioSegmenter ──► one WAV per call ──► TranscriptionWorker ──► Whisper
                (level-based,        (on disk)            (serial queue)          │
                 with pre-roll)                                                   ▼
                                                                              SQLite
                                                                                  │
                                                       server-sent events ◄───────┘
                                                                │
                                                                ▼
                                                          web console
```

- **`FeedCoordinator` / `FeedRecorder`** — one ffmpeg process per active feed,
  decoding to 16 kHz mono PCM.
- **`AudioSegmenter`** — tracks a rolling noise floor and cuts a new recording
  when the level rises above it, keeping a short pre-roll so the first syllable
  isn't clipped.
- **`TranscriptionWorker`** — drains the pending queue one call at a time
  through the `ITranscriber` (Whisper CLI, or a no-op when disabled).
- **`FeedEventHub` / `RecordingEventHub`** — push status and new calls to the
  browser over SSE; the console does not poll.

## Running locally

Requires the .NET 10 SDK, plus `ffmpeg` and the `whisper` CLI on the machine:

```bash
brew install ffmpeg openai-whisper          # macOS
dotnet run --project src/Dispatch.Web
```

`appsettings.Development.json` points at the Homebrew paths. The app fails
fast at startup with a clear message if either binary is missing rather than
failing later on the first feed. Data lands in `src/Dispatch.Web/data/`
(git-ignored).

## Deploying

Containerised, with a Render Blueprint in `render.yaml`:

**→ [docs/deploying-to-render.md](docs/deploying-to-render.md)**

Read the sizing and single-instance notes there before going live — the
instance plan and instance count are both load-bearing.

## Admin accounts

Feed start/stop/delete/archive are restricted to the `Admin` role. The role is
seeded on startup; no user is added to it automatically. To promote the first
admin:

1. Register a normal account through the app.
2. Set `Bootstrap:AdminEmail` (in `appsettings.json`, or as the
   `Bootstrap__AdminEmail` environment variable) to that account's email.
3. Restart. Startup promotes the matching account to `Admin`.

Idempotent — safe to leave set, or to clear once the account is promoted.

## Registration

Open by default. Setting `Auth:InviteCode` to a non-empty value requires that
code to register; the sign-in page shows the field only when the server says
it's needed. Registration and login are rate-limited per IP, and Identity
lockout is on (5 failed attempts, 15 minutes).

Since any account can activate a feed — which spawns real ffmpeg and Whisper
work — gate registration with an invite code unless you actually want open
signup.

## Configuration

| Section | Key settings |
|---|---|
| `Storage` | `RootPath`, `DatabasePath`, `RecordingsPath` |
| `Decoder` | `FfmpegPath`, `SampleRate`, `Channels`, reconnect behaviour |
| `Segmentation` | `ActivationDeltaDb`, `SilenceDeltaDb`, `HangoverSeconds`, `PreRollSeconds` |
| `Transcription` | `Enabled`, `Provider`, `WhisperCliPath`, `WhisperModel`, `Language` |
| `Broadcastify` | Base URLs, stream URL template, state→id map |
| `Auth` / `Bootstrap` | `InviteCode`, `AdminEmail` |
| `ForwardedHeaders` | `TrustProxy` — see the deploy doc before enabling |

Any key can be set as an environment variable using `__` for nesting
(`Storage__RecordingsPath`).
