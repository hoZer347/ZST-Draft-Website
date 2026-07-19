# Pokemon Draft League

A hosting service for Pokemon draft leagues.

```text
web/      Static frontend — deployed to Netlify (zst-league.netlify.app)
server/   ASP.NET Core 9 — draft engine, REST API, SignalR hub
```

> A Flutter mobile/desktop notification client used to live in `app/`. It was
> removed; the last commit that contains it is recoverable from git history
> (`git log -- app/`). The server's notification pipeline stays — it can feed a
> web client later.

## Deployment: two hosts, not one

**Netlify serves `web/` only.** It cannot host `server/` — there is no .NET
runtime there, the draft clock needs an always-on process, the SignalR hub needs
a persistent connection, and SQLite needs a writable disk. Netlify offers none
of those.

So the server deploys somewhere that runs .NET — Azure App Service, Fly.io,
Render, a VPS — and the frontend calls it cross-origin. See `DEPLOY.md`; the
Fly config is written but blocked on Fly wanting a card, and the account-free
Cloudflare tunnel route was tried and does not work from this machine.

Two consequences:

1. **`web/config.js` must point at the deployed API.** It ships with a
   `REPLACE-ME` placeholder; until it's set, the live site loads but shows
   "Can't reach the API".
2. **The frontend's origin must be in the server's `Cors:Origins`.** Otherwise
   the browser blocks every request. `Program.cs` defaults to
   `http://localhost:8000` and `https://zst-league.netlify.app`; override via
   config for any other domain.

## Status

The **backend draft engine and auth work and are verified end to end** against a
running server.

Sign-in is Discord OAuth (Authorization Code + PKCE) for the website. Every
`/api` route is authenticated and the caller's identity comes from the token,
never the request body. Setup — including the client secret, which must never be
committed to this public repo — is in `AUTH_SETUP.md`.

## Running it locally

Two processes. The API:

```powershell
cd server
dotnet run --urls http://localhost:5203
```

Creates `draftleague.db` (SQLite) and, in Development, seeds a two-team test
league with a snake draft. Then the frontend, from another shell:

```powershell
cd web
python -m http.server 8000
```

Open `http://localhost:8000`. `config.js` points at `localhost:5203`
automatically when served from localhost.

Dev-only endpoints. **These are auth bypasses and only exist in Development:**

| Endpoint | Does |
| --- | --- |
| `POST /dev/token/{discordId}?admin=true` | Mints a token for a seeded coach, so the draft can be driven without standing up Discord |
| `POST /dev/drafts/1/expire` | Forces the clock to expire, triggering auto-pick |

Starting and rolling back are real admin routes now: `POST /api/admin/drafts/1/start`
and `/rollback`, both requiring the `admin` role.

The seeded league has coaches `coach-1` (Team Alpha) and `coach-2` (Team Beta):

```powershell
# a token for coach-1, as an admin
curl -X POST "http://localhost:5211/dev/token/coach-1?admin=true"
```

## The draft rules

Carried over from the previous Python implementation:

| Tier | Options offered | Slots per team |
| --- | --- | --- |
| S | 3 | 1 |
| A | 4 | 2 |
| B | 5 | 3 |
| C | 7 | 4 |

A coach picks a **tier**, not a pokemon. The server offers a random sample from
that tier's remaining pool and they choose one of those.

Three rules are load-bearing and are covered by the verification below:

- **Options are cached per turn.** Re-requesting returns the same set, so a
  coach cannot refresh until the sample contains something they like.
- **One tier per turn.** Opening C then trying S is rejected.
- **Auto-pick walks C → B → A → S.** An idle coach loses their least valuable
  slot, never their S pick.

Tiers are per-league rows (`TierRule`), not constants, so a league can run its
own format without a code change.

## What changed from the Python version

- **Database is the source of truth.** State used to live in an in-memory dict,
  so a restart mid-draft lost every tier count and offered option.
- **Picks serialize on a lock.** A coach's pick could previously interleave with
  the timer's auto-pick and burn two slots for one turn.
- **One clock for all drafts.** Was a thread per draft sleeping in 1s steps,
  with the remaining time held in memory. Now a single background service
  compares each draft's persisted deadline to the wall clock, so it survives
  restarts and doesn't scale threads with leagues.
- **Rollback returns the pokemon to the pool.** That step used to depend on a
  Google Sheets write; if the write failed the pokemon stayed locked forever.
- **Google Sheets is gone.** Sheets was the record; it's now SQLite via EF Core.

## Verified

Driven against a live server, not just unit tests:

- Offer before start → rejected; wrong team → "Not your turn"
- C tier offers exactly 7 options, matching the tier rule
- Re-offering returns an identical set (no reroll-by-refresh)
- Second tier in one turn → rejected; picking a non-offered mon → rejected
- A pick advances the snake, clears options, records history
- Rollback restores the pick number, the clock and the pool
- Clock expiry auto-picks a C-tier mon, flags `WasAutoPick`, advances the snake
- A coach doesn't get notified of their own pick
- Muting a kind via `/api/preferences` suppresses it

Two bugs were found this way and fixed — neither was visible at compile time:

1. **SQLite cannot `ORDER BY` a `DateTimeOffset`.** Threw on the notification
   list and the push queue drain. Fixed with a value converter storing UTC ticks.
2. **`OrderBy(_ => Guid.NewGuid())` is untranslatable for SQLite.** It's a SQL
   Server `NEWID()` idiom. Auto-pick threw on every clock tick, so a draft would
   stall forever once a coach went idle — the exact failure the timer prevents.

## Not built yet

- **Linking a Discord account to a team.** Auth works, but there's no admin UI
  to put someone's Discord id in `Team.CoachId`. Until a row exists, a user
  signs in and sees "no team". Today that means editing the database by hand.
- **The frontend has never been opened in a browser.** `web/` was verified only
  at the wiring level: assets serve, the API answers cross-origin, the sprite
  URLs resolve, and the auth endpoints accept the right calls. The rendering,
  the login round trip and the SignalR reconnect path are unexercised.
- **The API isn't deployed.** `web/config.js` still says `REPLACE-ME`.
- **`server/Pages`** is still the stock Razor template, now unused — the real UI
  is `web/`. It's harmless but worth deleting.
- **Push delivery is a stub.** `LoggingPushSender` logs instead of sending; the
  notification pipeline is kept so a future client can consume it, but no real
  transport is wired.
- **`EnsureCreated()` instead of migrations.** Fine for dev; schema changes will
  need a real migration before any league data exists.
