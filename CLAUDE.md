# Draft League: project notes

## No em dashes

Never write em dashes anywhere (UI copy, comments, commit messages, prose). Use a
comma, parentheses, a colon, or two sentences.

## Keep the docs current

README.md, DEPLOY.md, AUTH_SETUP.md, DATA_MODEL.md are meant to stay accurate. When a
change makes one wrong (a renamed route, a removed feature, a new component), update
it in the same turn. Keep them succinct; state each fact once. This file is the
exception: it holds operational habits, not user-facing docs.

## Keep the dev server running

The .NET API + web app runs at **http://localhost:5211** (the Cloudflare tunnels
`dev.loomhozer.ca` / `zst.loomhozer.ca` point there). The watchdog
`server/keep-server-up.ps1` relaunches it (plus cloudflared and the Showdown servers)
within ~5s of any going down.

**After any change, restart the server and leave it up; never end a turn with :5211
down.** Health check: `curl -s -o /dev/null -w "%{http_code}" http://localhost:5211/`
(expect `200`).

**Live runs `server/run/DraftLeagueLive.exe`, a renamed COPY, not the build output**
(`bin/Debug/net9.0`). A `dotnet build` does not go live on its own. Process name is
`DraftLeagueLive` (kill that, never `DraftLeague.Web`). To promote a build:

1. `dotnet build` in `server/`.
2. `powershell -File server/deploy-run.ps1` (sets `.server-paused`, kills live,
   robocopies to `run/`, re-creates the exe, clears the flag; watchdog relaunches).

**Never leave `server/.server-paused` behind** (it silently keeps the server down); if
:5211 is off, delete any stale one first. Config/appsettings-only changes still need
`deploy-run.ps1` (config lives in `run/` too) but no rebuild. Never recreate the DB:
it holds the live season. New columns go in `server/Data/SchemaUpgrade.cs`.

## Restart the battle servers after editing their scripts

The Node servers in `battle-server/` load their code once at start: the Showdown
battle server (`scripts/showdown.js`, :8787) and the client static server
(`scripts/serve-client.js`, :8791, serving the Teambuilder at `play.loomhozer.ca` /
`127.0.0.1:8791`). After editing any `battle-server/scripts/*.js` or a lib it requires,
kill the process and let the watchdog relaunch it, or old code keeps serving:
`Stop-Process -Id (Get-NetTCPConnection -LocalPort 8791 -State Listen).OwningProcess -Force`
(same for 8787). Confirm a client-server edit landed:
`curl -s http://127.0.0.1:8791/teambuilder | grep <new-token>`.

## The Showdown engine lives in the client cache

The league's formats need the `champions` mod, which the npm `pokemon-showdown`
package doesn't ship, so `battle-server/lib/engine.js` resolves the engine to
`battle-server/client/caches/pokemon-showdown` (the checkout the client build clones
and compiles) and everything in `battle-server/` requires Showdown through it, never
by package name. Consequences: **stop :8787 before rebuilding the client**, since the
rebuild git-pulls and recompiles the directory the server is running out of; and after
a `cd client && node build`, re-run BOTH `node scripts/patch-client-tiers.js` (league
tiers) and `node scripts/patch-client-tera.js` (the Tera Type control, which the
teambuilder hides for any format whose id contains "champions"). `SHOWDOWN_DIR`
overrides the choice.

Both patch scripts are load-bearing now: `serve-client.js` rewrites the shell so every
script we have on disk is served from OUR copy instead of hotlinked from
play.pokemonshowdown.com (see `lib/client-shell.js`). Upstream's CDN is their live
deploy, and a `data/graphics.js` they shipped against a newer `battledata.js` than ours
threw `eHTML is not defined` inside `new Battle()`, so no battle room ever opened for
anyone, players or spectators, while the server sat waiting at team preview. The same
rewrite covers the stylesheets, because their CSS drifts too: deleting the
`.select.teamselect` rules our pinned client's team buttons rely on left the team icons
wrapping out of the challenge box.

## Verify on an isolated instance, never live :5211

Hitting :5211 mutates real dev data (starts drafts, reseeds the DB). Test ad-hoc
endpoints against a throwaway with its own DB:

```bash
cd server && dotnet build DraftLeague.Web.csproj
ASPNETCORE_ENVIRONMENT=Development ConnectionStrings__Default="Data Source=verify.db" \
  ./bin/Debug/net9.0/DraftLeague.Web.exe --urls http://localhost:5199 &
```

Running the exe directly tests your latest code without promoting it live. Dev auth:
`POST /dev/token/<discordId>?admin=true`, use the `accessToken` as a Bearer. A fresh
`verify.db` seeds a NotStarted draft. Tear down by port (the name matches live):
`Stop-Process -Id (Get-NetTCPConnection -LocalPort 5199 -State Listen).OwningProcess -Force`
then `rm -f verify.db*`.

## Web assets

Bump `?v=NN` on every asset in `web/index.html` after editing web files, or browsers
serve stale CSS/JS.

## Tier-coloured Pokemon entry standard (`.tier-fill` / `.tier-entry`)

Every drafted-mon element (Scoreboard, Draft Stats, the draft pick feed, the
schedule's battle-stats rows) reuses one CSS standard in `web/style.css`. Two
composable pieces:

- **`.tier-fill`** = the colouring only, display-agnostic (flex row, grid pick,
  battle-stats row): a tier lip + a 90deg wash. Compose `tier-fill tier--X [tier-fill--mine]`.
- **`.tier-entry`** = `.tier-fill` plus a ready flex sprite/name/value row layout, for
  simple lists. Compose `tier-entry tier--X [tier-entry--mine]`. Reads `--entry-sprite`
  (default 34px): set it on the caller and row + sprite scale together.

`tier--S/A/B/C` set `--tc` from the `--tier-*` root vars (omit for a no-tier neutral
row). `*-mine` adds the Poke-red accent ring for the signed-in coach's own mon,
resolved from `myTeamId` (draft/schedule) or `data.myTeamId` (scoreboard). Example:

```js
const mine = myTeamId != null && p.teamId === myTeamId;
li.className = `pick tier-fill tier--${p.tier}${mine ? ' tier-fill--mine' : ''}`;
li.className = `leader-row tier-entry${e.tier ? ` tier--${e.tier}` : ''}${mine ? ' tier-entry--mine' : ''}`;
```

Two exceptions: the **Stats table** re-implements the look per-cell (`.stats-row--mine`),
because box-shadow on a `<tr>` is unreliable with sticky columns; **Standings** rows
are coloured by RANK (the OPGB scheme `.opgb-0..3`, `.standings-row--mine`), not tier.
