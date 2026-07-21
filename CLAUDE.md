# Draft League: project notes

## Never use em dashes

Do not write em dashes (the long dash) anywhere: UI copy, tooltips, code comments,
commit messages, prose. Use a comma, parentheses, a colon, or two sentences instead.

## Always keep the dev server running

The .NET API + web app runs at **http://localhost:5211** (the web `config.js` and the
Cloudflare tunnels `dev.loomhozer.ca` / `zst.loomhozer.ca` all point there). A
watchdog, `server/keep-server-up.ps1`, relaunches it (and cloudflared + the Showdown
servers) whenever it's down.

**After any change, restart the server and leave it running; never end a turn with
:5211 down.** Verify with `curl -s -o /dev/null -w "%{http_code}" http://localhost:5211/dev/slots`
(expect `200`).

**The live server runs `server/run/DraftLeagueLive.exe`, a renamed COPY, NOT the
build output** (`bin/Debug/net9.0`). A fresh `dotnet build` does NOT go live on its
own; you must promote it. The process name is `DraftLeagueLive` (kill that, never
`DraftLeague.Web`).

Rebuild handshake:

1. `dotnet build` in `server/`.
2. `powershell -File server/deploy-run.ps1`: sets `.server-paused`, kills the live
   server, robocopies `bin/Debug/net9.0` to `run/`, re-creates `DraftLeagueLive.exe`,
   clears the flag. Watchdog relaunches from `run/` within ~5s.

**Never leave `server/.server-paused` behind**: it silently keeps the server down.
If `:5211` is off, first check for a stale `server/.server-paused` and delete it.

Config/appsettings-only changes still need `deploy-run.ps1` (the config lives in
`run/` too) but no rebuild. No schema change means don't recreate the DB.

## Restart the Showdown/battle servers after changing their scripts

The Node servers in `battle-server/` (the Showdown battle server `scripts/showdown.js`,
port 8787, and the self-hosted client static server `scripts/serve-client.js`, port
8791, which serves the Teambuilder at `play.loomhozer.ca` / `127.0.0.1:8791`) load their
code once at process start. **After editing any `battle-server/scripts/*.js` or the
libs they require, kill the affected process and let the watchdog relaunch it** (~5s),
or the old code keeps serving:

- Client server (8791): `Stop-Process -Id (Get-NetTCPConnection -LocalPort 8791 -State Listen).OwningProcess -Force`
- Battle server (8787): same with port 8787.

Verify the client server picked up an edit by curling the served page, e.g.
`curl -s http://127.0.0.1:8791/teambuilder | grep <your-new-token>`.

## Verifying without disrupting the running server

**Always run ad-hoc endpoint tests against an isolated localhost instance on a
spare port with its own DB; never drive the live :5211 server.** Hitting :5211
mutates the real dev data (starts drafts, deletes/reseeds the DB) and leaves it in
a confusing state. Spin up a throwaway instead:

```bash
cd server
ASPNETCORE_ENVIRONMENT=Development ConnectionStrings__Default="Data Source=verify.db" \
  ./bin/Debug/net9.0/DraftLeague.Web.exe --urls http://localhost:5199 &
```

- Build first (`dotnet build server/DraftLeague.Web.csproj`) so `bin/Debug` is
  current. Running the exe directly tests your latest code WITHOUT promoting it
  live (no `deploy-run.ps1` needed to iterate).
- Dev auth: `POST /dev/token/<discordId>?admin=true`, then use the `accessToken` as a
  Bearer. A fresh `verify.db` seeds a NotStarted draft, so you control the whole
  flow (ready coaches, start, pick, skip, and so on).
- Tear down by killing only that port, then delete the DB:
  `Stop-Process -Id (Get-NetTCPConnection -LocalPort 5199 -State Listen).OwningProcess -Force`
  then `rm -f verify.db*`. (Kill by port, not by process name; the name matches
  the live server.)

## Web assets

Bump the `?v=NN` query on every asset in `web/index.html` after editing web files, or
browsers serve stale CSS/JS.

## Tier-coloured Pokemon entry standard (`.tier-fill` / `.tier-entry`)

Any element representing a drafted Pokemon (Scoreboard leaderboards, Draft Stats
lists, the draft **pick feed**, the schedule's per-match **battle-stats rows**) uses
one shared CSS standard in `web/style.css`. Reuse it rather than re-styling rows; that
is what keeps every mon list looking the same. It has two composable pieces:

- **`.tier-fill`** is the COLOURING, and is display-agnostic (works on a flex row, a
  CSS-grid pick, a battle-stats row): a tier-coloured left lip + a 90-degree wash,
  strongest under the sprite/name and fading toward the value. Compose as
  `tier-fill tier--X [tier-fill--mine]`.
- **`.tier-entry`** is `.tier-fill` PLUS a ready-made flex sprite/name/value ROW
  LAYOUT, for the simple lists. Compose as `tier-entry tier--X [tier-entry--mine]`.
  It reads `--entry-sprite` (default 34px), the **scalable-height** knob: set it on
  the caller (e.g. `.leader-row { --entry-sprite: 40px }`, or smaller in a mobile
  media query) and the row + sprite scale together, no new rules.

Shared bits:

- `tier--S/A/B/C` set `--tc` (the tier colour, from the `--tier-s/a/b/c` root vars).
  Omit the `tier--X` class for a mon with no tier (neutral row).
- `tier-fill--mine` / `tier-entry--mine` is the **boolean highlight**: add it only for
  the signed-in (or viewed-as) coach's own mon to overlay a Poke-red accent ring +
  faint wash; it stacks with the tier lip (both inset box-shadows). Resolve "mine"
  from the module-global `myTeamId` (draft/schedule) or `data.myTeamId` (scoreboard).

In JS, build a row like:

```js
const mine = myTeamId != null && p.teamId === myTeamId;
// grid pick row (keeps its own .pick layout, borrows the colouring):
li.className = `pick tier-fill tier--${p.tier}${mine ? ' tier-fill--mine' : ''}`;
// simple flex row (colouring + layout from the standard):
li.className = `leader-row tier-entry${e.tier ? ` tier--${e.tier}` : ''}${mine ? ' tier-entry--mine' : ''}`;
```

Two exceptions:

- The **Stats TABLE** re-implements the same look per-cell (tier tint on the `<tr>`,
  lip on `td:first-child`, `.stats-row--mine` accent), because box-shadow on a `<tr>`
  is unreliable and its first two columns are sticky/opaque. Same visual, table-safe.
- **Standings** rows are coloured by RANK (the OPGB scheme, `.opgb-0..3`), not by tier,
  so they use `.standings-row--mine` (the same accent-ring idea, layered with the OPGB
  lip) rather than `.tier-fill`.
