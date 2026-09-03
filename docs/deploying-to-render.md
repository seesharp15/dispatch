# Deploying Dispatch to Render

Everything the deploy needs is in the repo: `Dockerfile` (app + ffmpeg +
whisper) and `render.yaml` (one web service, one disk). This document covers
the parts that aren't in those files — the first-run steps, the sizing
decisions, and the constraints worth knowing before you rely on it.

---

## 1. Create the service

1. Push this repo to GitHub/GitLab.
2. In Render: **New → Blueprint**, point it at the repo, apply `render.yaml`.
3. Render builds the Dockerfile and attaches a 20 GB disk at `/var/data`.

The first build is slow — 10–20 minutes. It installs CPU-only PyTorch and
bakes the `base.en` Whisper model into the image. Later builds reuse those
layers unless the Dockerfile changes.

## 2. Set the two secret env vars

`render.yaml` declares these with `sync: false`, meaning Render prompts for
them instead of reading them from the repo. Set both in the Dashboard under
the service's **Environment** tab:

| Variable | Purpose |
|---|---|
| `Bootstrap__AdminEmail` | Email of the account to promote to `Admin` on startup. Feed start/stop/delete/archive are admin-only. |
| `Auth__InviteCode` | If non-empty, registration requires this code. Leave empty for open registration. |

## 3. Become the first admin

1. Deploy with `Bootstrap__AdminEmail` set to the address you'll use.
2. Register that account through the app.
3. Restart the service. On startup it promotes the matching account to `Admin`.

Idempotent and safe to leave set. The sign-in page shows an invite-code field
only when `Auth__InviteCode` is non-empty, so you can flip registration open or
closed without a code change.

## 4. Custom domain

Add it under **Settings → Custom Domains** and create the CNAME Render shows
you. Render issues the certificate and terminates TLS at its edge; the app
handles the rest (see "TLS and the proxy" below).

---

## Sizing

**Instance plan: `standard` (2 GB) is the floor.** Whisper `base.en` holds
roughly 1 GB resident while transcribing, and each active feed adds an ffmpeg
process. On a 512 MB `starter` the transcription worker gets OOM-killed on its
first real call. Move up a plan before adding feeds, not after.

**Transcription is CPU-bound and serial.** One `TranscriptionWorker` processes
the queue one recording at a time at roughly 0.7× realtime on CPU. Past a
handful of busy feeds the queue grows faster than it drains — the UI shows
this as rising `Queue: (n/total)` numbers on pending calls. Options when that
happens, cheapest first: fewer feeds, a smaller model (`tiny.en`), a larger
instance, or swapping `WhisperCliTranscriber` for whisper.cpp or a hosted
transcription API.

**Disk: 20 GB.** Recordings are 16 kHz mono WAV — about 115 MB per feed-hour
of *speech*. The segmenter drops silence, so a quiet feed costs far less than
a busy one. Nothing prunes old recordings automatically: archiving a call
hides it from the UI but leaves the file on disk. Watch the disk metric and
grow it (Render disks can grow, never shrink) or add a cleanup job before it
fills — a full disk stops recording.

## Constraints

**One instance, always.** `numInstances: 1` is load-bearing, not a cost
choice. A second instance would open its own ffmpeg stream for every feed
(duplicate recordings, double the Broadcastify connections) and contend for
the same single-writer SQLite file. The in-memory `FeedCoordinator`, the
SSE hubs and the child-process registry are all per-instance state.

**Deploys interrupt live feeds.** Services with disks can't do zero-downtime
deploys — Render stops the old container before starting the new one. Active
feeds drop for the length of a deploy and reconnect afterwards;
`maxShutdownDelaySeconds: 60` gives in-flight recordings a chance to flush
first. Deploy when it's quiet.

**SQLite is fine here.** WAL mode is on by default, and the write load
(recording rows, transcript updates, heartbeats) is small. Revisit only if
request latency shows lock contention — Postgres is a migration, not a
one-liner.

## TLS and the proxy

Render terminates TLS at its edge and forwards plain HTTP to the container
over its private network. `ForwardedHeaders__TrustProxy=true` (set in both
`render.yaml` and the Dockerfile) tells the app to trust exactly one hop of
`X-Forwarded-Proto` / `X-Forwarded-For`. That's what makes three things
correct:

- the auth cookie is issued `Secure` (`CookieSecurePolicy.Always` outside dev),
- `UseHttpsRedirection` sees the real scheme instead of redirect-looping,
- the login/register rate limits partition by real client IP rather than
  bucketing every request behind the proxy into one key.

`ForwardLimit = 1` and the cleared allow-lists are only safe because the
container is unreachable except through Render's proxy. **If you ever put this
somewhere the container is directly reachable, set
`ForwardedHeaders__TrustProxy=false`** — otherwise a client can spoof its own
IP and walk around the rate limits.

## Sessions across deploys

ASP.NET Core encrypts the auth cookie with a Data Protection key ring, and its
default home is a directory inside the container image — thrown away on every
deploy. `Storage__DataProtectionKeysPath=/var/data/keys` moves it onto the disk
so a deploy doesn't sign every user out. Verified by registering in one
container, destroying it, and reusing the same cookie against a fresh container
on the same disk.

Startup logs a warning that the keys are stored unencrypted. That is expected
here: on Linux with no certificate or key-management service configured there's
nothing to encrypt them with, and the disk is private to the service. Don't
copy `/var/data/keys` anywhere it could be read.

## Health check

`GET /healthz` returns `{"status":"ok"}`. Two deliberate properties:

- **Dependency-free.** It answers as soon as the app can serve HTTP, so a
  stalled feed or a long transcription queue never causes Render to kill and
  restart the instance mid-recording.
- **Handled before HTTPS redirection.** Health probes arrive over plain HTTP on
  the private network without `X-Forwarded-Proto`. If the redirect middleware
  saw them first they'd get a `307`, which reads as an unhealthy instance and
  blocks the deploy from going live. The health branch is mapped ahead of
  `UseHttpsRedirection` in `Program.cs` for exactly this reason — keep it there.

## Operating notes

- **Logs:** `render logs -r <service>` or the Dashboard. ffmpeg and whisper
  failures surface as warnings from `FeedRecorder` / `WhisperCliTranscriber`.
- **Database access:** it's a file on the disk — reach it with
  `render ssh <service>` then `sqlite3 /var/data/dispatch.db`.
- **Backups:** Render disks snapshot daily on paid plans. The database and the
  recordings are both under `/var/data`, so one snapshot covers both.
- **Local dev** is unchanged: `dotnet run` with `appsettings.Development.json`
  pointing at Homebrew's ffmpeg/whisper.

## Before opening it to the public

- **Broadcastify's terms** govern re-streaming and redistributing their feeds.
  Confirm what your use allows before pointing real users at it — this is a
  licensing question, not a technical one.
- **Registration is open by default.** Every account can activate feeds, and
  each activation spawns real ffmpeg + Whisper work. The rate limits bound the
  damage but don't eliminate it. Set `Auth__InviteCode` unless you actually
  want open signup.
- Both HTML pages send `<meta name="robots" content="noindex">`. Remove it if
  you want the app indexed.
