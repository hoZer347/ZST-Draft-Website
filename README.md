# Pokemon Draft League

A self-hosted platform for running a Pokemon draft league end to end: a snake
draft, a round-robin season, live battles on a private Pokemon Showdown server,
and per-mon stats scraped from every battle replay.

## The three components

```text
server/         ASP.NET Core 9 (net9.0). REST API + SignalR hub + the draft
                engine, and it also serves the web/ folder as static files, so
                the site and the API are same-origin in production. EF Core over
                SQLite (draftleague.db).
web/            Static frontend (plain HTML/CSS/JS, no build step): the draft,
                schedule, scoreboard, stats, and teambuilder tabs. Loaded by the
                .NET server above.
battle-server/  Node service wrapping the real pokemon-showdown package: a
                custom-format battle server (:8787) and a static server (:8791)
                that hosts the Showdown client / Teambuilder. Auto-reports every
                finished league game back to the .NET server.
```

Supporting docs: [DEPLOY.md](DEPLOY.md), [AUTH_SETUP.md](AUTH_SETUP.md),
[DATA_MODEL.md](DATA_MODEL.md), and [CLAUDE.md](CLAUDE.md) (the day-to-day
operational notes: dev-server lifecycle, rebuild handshake, cache-busting).

## How it runs in production

Everything is self-hosted on a single Windows box. One .NET process listens on
`localhost:5211` and serves both the web app and the API; the Node battle server
listens on `:8787` and the Showdown client server on `:8791`. A named Cloudflare
tunnel maps them to public subdomains:

| Public URL | Local | Serves |
| --- | --- | --- |
| `dev.loomhozer.ca` / `zst.loomhozer.ca` | `:5211` | web app + REST API (same-origin, no CORS) |
| `showdown.loomhozer.ca` | `:8787` | the Showdown battle server |
| `play.loomhozer.ca` | `:8791` | the self-hosted Showdown client + Teambuilder |

A watchdog, [server/keep-server-up.ps1](server/keep-server-up.ps1), relaunches
any of these (plus `cloudflared`) within a few seconds if it goes down, so the
draft clock and the battle server stay up unattended. The live .NET server runs
a promoted copy at `server/run/DraftLeagueLive.exe`, not the raw build output; a
new build goes live via [server/deploy-run.ps1](server/deploy-run.ps1). See
CLAUDE.md for the full rebuild handshake.

`netlify.toml` and the Netlify path in `web/config.js` are legacy: the site used
to be a static Netlify deploy calling the API cross-origin. It is now served by
the .NET server itself.

## Running it locally

The .NET server serves the web app too, so one process is enough for the core
app:

```powershell
cd server
dotnet run --urls http://localhost:5211
```

On first run it creates `draftleague.db` (SQLite, via `EnsureCreated`) and, in
Development, seeds a "Test Season 1" league: the tier rules and the full Pokedex
pool, plus an empty draft. Teams and the snake order are built from whoever is
signed in when an admin presses Start (there is no fixed coach roster). Open
`http://localhost:5211`; `web/config.js` points the frontend at `localhost:5211`
and `localhost:8787` automatically when served from localhost.

For live battles and the Teambuilder, also start the battle server:

```powershell
cd battle-server
npm run showdown        # battle server on :8787
node scripts/serve-client.js   # Showdown client / Teambuilder on :8791
```

### Dev-only endpoints

These are auth bypasses and exist **only in Development**:

| Endpoint | Does |
| --- | --- |
| `POST /dev/token/{discordId}?admin=true` | Mints a bearer token for any id, so the draft can be driven without Discord. Use the `accessToken` as `Authorization: Bearer` |
| `GET /dev/accounts` | Lists seeded/known accounts |
| `POST /dev/drafts/{id}/expire` | Forces the pick clock to expire, triggering auto-pick |
| `POST /dev/simulate-season` / `POST /dev/simulate-random-season` | Seeds and plays a full test season (real Showdown battles), then scrapes stats from the replays |
| `POST /dev/sync-pokedex` | Re-syncs the pool from the source Pokedex sheet |
| `POST /dev/snapshot-now` | Takes a season snapshot immediately |

Starting, rolling back, and other admin actions are real authenticated routes
under `/api/admin/...` requiring the `admin` role.

> Verify without disrupting the running dev server by driving a throwaway
> instance on a spare port with its own DB, never the live `:5211`. See CLAUDE.md.

## The draft

A coach picks a **tier**, not a specific Pokemon. The server offers a random
sample from that tier's remaining pool and the coach chooses one of those. Tiers
are per-league rows (`TierRule`), not constants, so a league can run its own
format without a code change. The seeded format:

| Tier | Options offered | Slots per team |
| --- | --- | --- |
| S | 3 | 1 |
| A | 4 | 2 |
| B | 5 | 3 |
| C | 7 | 4 |

Load-bearing rules, all covered by the test suite:

- **Options are cached per turn.** Re-requesting returns the same set, so a coach
  cannot refresh until the sample contains something they like.
- **One tier per turn.** Opening C then trying S is rejected.
- **Auto-pick walks C then B then A then S.** An idle coach loses their least
  valuable slot, never their S pick.
- **Pick order is shuffled at Start** and the whole draft serializes on a lock,
  so a coach's pick can't interleave with the timer's auto-pick.
- **A single background clock** ([DraftClock](server/Services/DraftClock.cs))
  compares each draft's persisted deadline to the wall clock, so it survives a
  restart and doesn't scale threads with leagues.
- **Rollback returns the Pokemon to the pool** and restores the pick number and
  clock.

## The season, battles, and stats

Once a draft finishes the server lays down a round-robin schedule
([ScheduleApi](server/Api/ScheduleApi.cs)): a full single round-robin, byes for
odd rosters, one game per pair. Coaches build their teams in the Teambuilder tab
(the official Showdown client pointed at our custom `gen9zstseason4` format, EVs
and IVs and all) and battle on the private server.

When a battle ends, the Showdown server POSTs its log to
`/api/showdown/report` (shared-secret guarded). The server matches it to the
pending fixture, scores it, updates standings, and folds per-mon battle stats
(KOs, faints, damage dealt/taken, healing, presence, crits) into the Stats tab.
[ReplayStatsScraper](server/Services/ReplayStatsScraper.cs) parses the log and
attributes every effect to a source: direct hits, entry hazards, status chip,
weather, Perish Song, bind moves, Destiny Bond, recoil and other self-damage,
and absorb-ability healing.

**Custom megas.** The league runs "Champions"-style custom mega evolutions that
vanilla Showdown doesn't ship. They are merged additively into the engine's dex
as plain gen-9 mega data (see `battle-server/showdown-config/custom-megas.js` and
`installCustomMegas` in `battle-server/scripts/showdown.js`), so the stock engine
evolves them with no ruleset fork. Icons for megas without a Showdown sprite fall
back through Serebii's Z-A artwork and PokeAPI (see [web/sprite.js](web/sprite.js)).

**Season simulation.** The dev "Simulate season" flow builds fully random-but-legal
teams (random natures, EVs, IVs, abilities, moves, items; see
[battle-server/lib/random-team.js](battle-server/lib/random-team.js)), plays a
whole season of real Showdown battles headlessly, and scrapes the results, so the
schedule/scoreboard/stats tabs can be exercised without waiting on live games.

## Auth

Sign-in is Discord OAuth (Authorization Code + PKCE). Every `/api` route is
authenticated and the caller's identity comes from the token, never the request
body. Setup, including the client secret (which must never be committed to this
public repo), is in [AUTH_SETUP.md](AUTH_SETUP.md).

## Tests

Three suites, all runnable offline:

```powershell
cd server.Tests   ; dotnet test        # xUnit: draft engine, scoring, replay scraper, endpoints
cd web            ; npm test           # node:test: pool/MVP logic, sprite fallback, teambuilder, auth
cd battle-server  ; npm test           # node:test: team packing, formats, custom megas, sim battles
```

The replay-scraper attribution rules and the icon-fallback chain in particular
are locked down by dedicated regression tests, so a future change can't silently
mis-credit a KO or leave a mon with a missing icon.
