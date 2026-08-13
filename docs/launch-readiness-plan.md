# Public Launch Readiness Plan

Source: review of the uncommitted diff on top of `a852671` (2026-08-13). Each
item below is scoped to be handed to an agent independently — file pointers
and acceptance criteria included so no prior conversation context is needed.

Priority key: **P0** = blocks public launch, **P1** = do before/shortly after
launch, **P2** = monitor, not urgent yet.

**Status (2026-08-13): all items done.** See commit references in each
section. P1-2's TLS/proxy behavior was implemented with safe defaults
(loopback-only trusted proxy) rather than a specific deploy target, since
that wasn't known at the time — see its note below before deploying behind
a non-loopback proxy/load balancer.

---

## P0-1. Throttle registration and login — done (`8b5a2a3`)

**Problem:** `POST /api/auth/register` and `POST /api/auth/login` in
`src/Dispatch.Web/Program.cs` are `AllowAnonymous` with no rate limiting, no
CAPTCHA, and `lockoutOnFailure: false` on `PasswordSignInAsync`. Because
registering immediately lets a user activate feeds that spin up real `ffmpeg`
+ Whisper transcription processes (`FeedRecorder`, `WhisperCliTranscriber`),
this isn't just a brute-force risk — it's a compute/cost DoS vector. A script
looping registration + feed-activate calls can consume unbounded CPU.

**Steps:**
1. Add ASP.NET Core's built-in rate limiter (`Microsoft.AspNetCore.RateLimiting`,
   available since .NET 7) in `Program.cs`:
   - A fixed-window or token-bucket policy applied to `/api/auth/register`,
     `/api/auth/login`, and `/api/feeds/*/activate` — key by remote IP.
   - Suggested limits: register 5/hour/IP, login 10/15min/IP, activate
     20/min/user.
2. Enable Identity lockout: set `options.SignIn.RequireConfirmedEmail` stays
   false (no email infra yet — see P0-2 note), but flip
   `lockoutOnFailure: true` in the `PasswordSignInAsync` call, and configure
   `options.Lockout` (e.g. 5 attempts, 15 min lockout) in the
   `AddIdentity<...>` block.
3. Decide whether registration should be fully open or gated by an invite
   code / allowlist for initial launch (cheapest mitigation: a shared invite
   code checked in the register handler, stored in config). Recommend
   starting with an invite code until there's a reason to open registration
   fully.

**Files:** `src/Dispatch.Web/Program.cs` (register/login endpoints, Identity
config, service registration), `appsettings.json` (rate limit + invite code
config if added).

**Acceptance criteria:**
- Hitting `/api/auth/register` or `/api/auth/login` past the configured
  threshold from one IP returns `429 Too Many Requests`.
- 5 consecutive bad passwords for one account triggers Identity lockout
  (subsequent correct password still rejected until lockout expires).
- If invite-gating is added, registration without a valid code returns
  `400`.

---

## P0-2. Admin role bootstrap — done (`47f8b70`)

**Problem:** Nothing in the codebase creates the `Admin` `IdentityRole` or
assigns it to any user — `grep -n "RoleManager\|IdentityRole<Guid>(" src/Dispatch.Web/Program.cs`
only shows the role type being registered with Identity, never seeded. Since
recent changes gate feed start/stop/delete/archive behind
`RequireRole("Admin")`, a fresh deployment has **no way to become admin**
except manual SQLite editing.

**Steps:**
1. Add a startup seeding block (near the existing `db.Database.Migrate()`
   scope in `Program.cs`) that:
   - Ensures the `Admin` role exists (`RoleManager<IdentityRole<Guid>>`).
   - Optionally promotes a configured bootstrap email
     (`appsettings.json` → `Bootstrap:AdminEmail`) to Admin on startup if
     that user exists and isn't already in the role.
2. Document the bootstrap flow in `README.md` (how to become the first
   admin on a fresh deploy).

**Files:** `src/Dispatch.Web/Program.cs`, `appsettings.json`, `README.md`.

**Acceptance criteria:**
- On a fresh database, after setting `Bootstrap:AdminEmail` and registering
  that account, `GET /api/auth/me` returns `isAdmin: true` for it without
  any manual DB editing.
- Role seeding is idempotent (safe to run on every startup).

---

## P1-1. Externalize ffmpeg/whisper paths per environment — done (`622d759`)

**Problem:** `appsettings.json` hardcodes
`Decoder:FfmpegPath = /opt/homebrew/bin/ffmpeg` and
`Transcription:WhisperCliPath = /opt/homebrew/bin/whisper` — macOS Homebrew
paths that won't exist on a production Linux host/container.

**Steps:**
1. Move these two paths out of the committed `appsettings.json` defaults
   (or leave sane Linux defaults, e.g. `/usr/bin/ffmpeg`) and set the real
   values via `appsettings.Production.json` or environment variables
   (`Decoder__FfmpegPath`, `Transcription__WhisperCliPath`) in the deploy
   config.
2. Add a startup check that fails fast with a clear error if the configured
   binary path doesn't exist/isn't executable, instead of failing deep
   inside `FeedRecorder`/`WhisperCliTranscriber` on first use.

**Files:** `appsettings.json`, `appsettings.Production.json` (new, or deploy
env vars), `src/Dispatch.Web/Program.cs` (startup validation), whichever
deploy manifest/Dockerfile is used.

**Acceptance criteria:**
- Production config does not reference `/opt/homebrew/*`.
- Starting the app with a bad/missing ffmpeg or whisper path logs a clear
  startup error rather than silently failing per-feed later.

---

## P1-2. Confirm TLS termination in front of the app — done (`f7334bb`)

**Note on what was actually implemented:** the real deploy target wasn't
known at implementation time, so this shipped with the safe default:
`ForwardedHeadersOptions` trusts only a loopback proxy (ASP.NET Core's
built-in default), `UseHsts`/`UseHttpsRedirection` are enabled outside
Development, and the auth cookie's `SecurePolicy` is `Always` outside
Development (not conditioned on the forwarded scheme). **If the production
reverse proxy/load balancer is NOT on the same host as the app**, add its
address to `KnownProxies` or its subnet to `KnownNetworks` in
`Program.cs` — otherwise `X-Forwarded-For`/`-Proto` will be ignored and the
app won't see the real client IP (affecting rate-limit partitioning) or
scheme.

**Problem:** No `UseHttpsRedirection()`/`UseHsts()` in `Program.cs`. This is
fine if a reverse proxy (nginx/Caddy/Fly/etc.) terminates TLS in front of
Kestrel, but it needs to be verified for the actual deploy target — cookies
here carry auth (`ConfigureApplicationCookie`), so plaintext HTTP in
production would leak session cookies.

**Steps:**
1. Confirm the hosting setup: is there a reverse proxy terminating TLS? If
   yes, ensure `app.UseForwardedHeaders()` is configured so ASP.NET Core
   sees the original scheme (needed for secure-cookie logic to work
   correctly behind a proxy).
2. If there is no reverse proxy and Kestrel is exposed directly, add
   `UseHttpsRedirection()`/`UseHsts()` and configure a cert.
3. Set the auth cookie's `SecurePolicy` to `Always` in
   `ConfigureApplicationCookie` once HTTPS is confirmed end-to-end.

**Files:** `src/Dispatch.Web/Program.cs`, deploy/proxy config (outside this
repo, if applicable).

**Acceptance criteria:**
- Plain HTTP requests to the production URL either don't exist (proxy-only)
  or redirect to HTTPS.
- Auth cookie is issued with `Secure` set in production.

---

## P2-1. SQLite scaling watch item — already satisfied, no change made

Verified WAL mode is already active by default (EF Core's SQLite provider
enables it automatically — confirmed `PRAGMA journal_mode` returns `wal`
against a freshly created database). No code change was needed.

**Problem:** SQLite is a single-writer datastore. Fine at low/moderate
traffic, but worth having a plan before it becomes a bottleneck (feed
heartbeats, recordings, transcription writes all hit the same file).

**Steps (not urgent — revisit if traffic grows):**
1. Ensure WAL mode is enabled for better read/write concurrency
   (`PRAGMA journal_mode=WAL;` — check `DispatchDbContext`/connection setup,
   add if missing).
2. Keep an eye on request latency/lock contention once real users land; only
   migrate to Postgres if it actually becomes a problem.

**Files:** `src/Dispatch.Web/Data/DispatchDbContext.cs`.

**Acceptance criteria:** WAL mode confirmed enabled (`PRAGMA journal_mode;`
returns `wal`). No action beyond that unless metrics show contention.

---

## P2-2. Frontend UX polish — done (`8d14435`)

**Problem:** Minor rough edges, not blockers.

**Steps:**
1. Replace the `alert()` used for the admin-stopped-feed message in
   `addFeedFromDiscovery` (`src/Dispatch.Web/wwwroot/app.js`) with an inline
   toast/banner consistent with the rest of the UI.
2. SSE reconnect logic (`connectRecordingStream`/`connectFeedStream` in
   `app.js`) hard-redirects to `/login.html` after 5 consecutive failures,
   which will also fire on flaky networks with a valid session. Consider
   distinguishing "401 received" (definitely expired) from generic
   connection errors (retry with backoff, don't redirect) before assuming
   session expiry.

**Files:** `src/Dispatch.Web/wwwroot/app.js`.

**Acceptance criteria:** No blind `alert()` for expected error states;
network blips (simulated by killing/restoring the dev server) don't bounce
a logged-in user to the login page.

---

## Suggested order of work

1. P0-1 (rate limiting/lockout) and P0-2 (admin bootstrap) — do together,
   both touch `Program.cs` startup/auth sections.
2. P1-1 (binary paths) — independent, safe to parallelize.
3. P1-2 (TLS) — depends on knowing the actual deploy target; confirm with
   whoever owns hosting before writing code.
4. P2-1 and P2-2 — pick up post-launch or opportunistically.
