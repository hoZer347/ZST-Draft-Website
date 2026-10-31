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
                schedule, playoffs, scoreboard, stats, and teambuilder tabs. Loaded by the
                .NET server above.
battle-server/  Node service wrapping a real Pokemon Showdown build (the
                Champions-capable checkout under client/caches, see lib/engine.js):
                a custom-format battle server (:8787) and a static server (:8791)
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
| `loomhozer.ca` / `www.loomhozer.ca` | `:8082` | the personal site (a separate repo, `Desktop\Website`) |

A watchdog, [server/keep-server-up.ps1](server/keep-server-up.ps1), relaunches
any of these (plus `cloudflared`) within a few seconds if it goes down, so the
draft clock and the battle server stay up unattended. The live .NET server runs
a promoted copy at `server/run/DraftLeagueLive.exe`, not the raw build output; a
new build goes live via [server/deploy-run.ps1](server/deploy-run.ps1), and the
watchdog, tunnel config and Node servers via
[server/deploy-live.ps1](server/deploy-live.ps1). See CLAUDE.md for the full
rebuild handshake.

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
`http://localhost:5211`. A page served from localhost calls the server that served
it (`web/config.js`; only the static host on :8000 goes to `localhost:5211`) and
Showdown at `localhost:8787`. `EnsureCreated` never alters an existing database, so a
new column goes in [SchemaUpgrade](server/Data/SchemaUpgrade.cs), which adds it at
startup; the database is never recreated for a schema change.

On the dev machine the whole stack (server, Cloudflare tunnel, both Showdown
servers) is kept alive by `server/keep-server-up.ps1`. `server/start-server.ps1`
boots it: it clears a stale pause flag, starts the watchdog if none is running,
then waits for `:5211` and reports what came up. It runs at boot, before anyone signs
in (the `DraftLeagueBoot` task from `server/install-boot-task.ps1`, so a Windows
Update restart doesn't leave the sites down), at logon (hidden, from
`DraftLeagueServerWatchdog.vbs` in the user's Startup folder) and on demand from
the `Start Draft League Server.cmd` desktop shortcut. Running it while the stack
is already up is a no-op, it never starts a second watchdog (one running older
code than `keep-server-up.ps1` is replaced).

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
| `POST /dev/play-out-season` | Battles every unplayed regular-season game, then the playoffs, keeping the league's teams, rosters and results |
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
(the official Showdown client pointed at our custom `gen9championsnatdexzstseason4`
format) and battle on the private server. That format's validator
(`battle-server/showdown-config/custom-formats.js`) enforces the draft per coach:
keyed by the logged-in Showdown name, it fetches the coach's roster from
`/api/showdown/roster/{user}` at validation time and requires every mon to be
drafted, form-changing items (mega stones, the origin orbs, Rusted Sword/Shield) to
unlock only a battle forme the coach drafted, and C-tier picks to Terastallize to
their drafted Tera type. That Tera type is decided at draft time, so nobody picks it:
the validator STAMPS it onto every C-tier set (`applyDraftedTera`) and the teambuilder
does the same as you build (`battle-server/lib/roster-tera.js`), because a set that
arrives without one would otherwise fall back to the species' primary type and be
rejected (Aerodactyl drafted Tera Flying validating as Tera Rock).

A forme a mon changes into mid-battle through a signature
move (Keldeo-Resolute via Secret Sword) counts as the same pick as its base, so
drafting one lets a coach bring either; so does a forme you get by choosing the
base's signature ability in the teambuilder (Greninja + Battle Bond is
Greninja-Bond, Rockruff + Own Tempo is Rockruff-Dusk). Battle Bond is the Gen 9
version: +1 Attack, Sp. Atk and Speed the first time the holder KOes something, no
Ash-Greninja form change. A whole type-item family is one pick too (Silvally + any
memory, Genesect + any drive, Arceus + any plate): stats and abilities are the
base's, so a drafted Silvally may be brought as any of its 17 formes. Item formes
that restat or change ability stay separate picks (Giratina-Origin, Necrozma-Ultra,
the Ogerpon masks).

When a battle ends, the Showdown server POSTs its log to
`/api/showdown/report` (shared-secret guarded). The server matches it to the
pending fixture, scores it, updates standings, and folds per-mon battle stats
(KOs, faints, damage dealt/taken, healing, presence, crits) into the Stats tab.
Scrims games are never recorded, by any reporting path.
[ReplayStatsScraper](server/Services/ReplayStatsScraper.cs) parses the log and
attributes every effect to a source: direct hits, delayed attacks (Future Sight,
Doom Desire), entry hazards, status chip, weather, Perish Song, bind moves,
Destiny Bond, recoil and other self-damage, and absorb-ability healing.

A finished game stays spoiler-free on the Schedule tab: it renders collapsed, showing
only the week, the two coaches and the replay link, so you can watch the battle before
the score or the scoresheet reaches you. "Show result" opens one (including your own,
which also collapses the moment it finishes), and which games you've opened is kept per
league and viewer in localStorage, so a re-login brings the same cards back open.

**Playoffs.** Once every regular-season game is played, the top 12 by standings are
seeded into a single-elimination bracket ([Playoffs](server/Services/Playoffs.cs)),
padded to 16 so seeds 1-4 get first-round byes. Every matchup is a best-of-three
series. Each game is an ordinary Match, created once the one before it is played and
both teams are known, a week after the round before; it is reported, scored and
scraped like any other, keeping its replay and both teams' pastes, and counts toward
per-mon stats and KO totals but not the win/loss record. A drawn game counts for
neither side, so the series gets another game. Results stand once the next round is
played. Before seeding, the Playoffs tab shows the bracket the current standings
would make, marked Projected.

**The Champions mod.** Every league format runs on Pokemon Champions (Regulation
M-B), so megas, including the ones vanilla Showdown never shipped, are native to
the dex, and the league plays that game's mechanics: Stat Points instead of EVs
(66 per mon, 32 per stat), no IVs, level 50, PP capped at 20. Champions turns
Terastallization off; the league restores it (`actions.canTerastallize` in the
format) because C-tier picks are drafted with a Tera type. Champions also CUT
content: ~194 gen-9 moves are "Past" there and learnsets were trimmed (Archaludon
lost Body Press, Garchomp lost Aqua Tail), and whole species were dropped. `NatDex
Mod` plus the two National Dex hooks in the format put all of it back, deferring
legality to the gen-9 National Dex validator, so the league's pool is the full
National Dex with Champions mechanics on top. The npm
`pokemon-showdown` package predates the mod, so the server runs the
Champions-capable Showdown checkout the client build maintains, resolved by
[battle-server/lib/engine.js](battle-server/lib/engine.js). Icons for megas without
a Showdown sprite fall back through Serebii's Z-A artwork and PokeAPI (see
[web/sprite.js](web/sprite.js)).

Two build-artifact patches keep the vendored client in step, and both must be
re-run after `cd client && node build`:
`scripts/patch-client-tiers.js` writes the league's X/S/A/B/C/Z tiers into the
teambuilder tables, and `scripts/patch-client-tera.js` puts back the Tera Type
control the teambuilder hides for any format whose id says "champions".

**Season simulation.** The dev "Simulate season" flow builds fully random teams
(random natures, Stat Point spreads, abilities, moves, items; see
[battle-server/lib/random-team.js](battle-server/lib/random-team.js)), then runs each
one through Showdown's own TeamValidator (the engine behind the Teambuilder's
"Validate" button) and repairs anything illegal, so a synthetic team is legal exactly
where a coach's is. Both the battles and that gate use
`[Gen 9 Champions] NatDex ZST Sim`, which shares the live format's ruleset, banlist,
National Dex hooks and restored Terastallization and drops only the draft-roster
check a headless run has no coach for, so a simulated season plays by the rules real
games do. It then plays a
whole season of real Showdown battles headlessly, playoffs included, and scrapes the
results, so the schedule/scoreboard/stats/playoffs tabs can be exercised without
waiting on live games.

## Auth

Opening the site makes you a **visitor**: the whole league is readable with no
Discord prompt. Becoming a **player** takes an invite link, which an admin copies
from the draft tab. Sign-in itself is Discord OAuth (Authorization Code + PKCE).
Every `/api` route is authenticated and the caller's identity comes from the
token, never the request body. Setup, including the client secret (which must
never be committed to this public repo), is in [AUTH_SETUP.md](AUTH_SETUP.md).

## Tests

Three suites, all runnable offline:

```powershell
cd server.Tests   ; dotnet test        # xUnit: draft engine, scoring, replay scraper, endpoints, watchdog
cd web            ; npm test           # node:test: pool/MVP logic, sprite fallback, teambuilder, auth
cd battle-server  ; npm test           # node:test: team packing, formats, custom megas, sim battles
```

The replay-scraper attribution rules and the icon-fallback chain in particular
are locked down by dedicated regression tests, so a future change can't silently
mis-credit a KO or leave a mon with a missing icon.
