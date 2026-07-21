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

The .NET API + web app runs at **http://localhost:5211** (web `config.js` and the
Cloudflare tunnels `dev.loomhozer.ca` / `zst.loomhozer.ca` all point there). The
watchdog `server/keep-server-up.ps1` relaunches it (plus cloudflared and the Showdown
servers) within ~5s of any going down.

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
`deploy-run.ps1` (config lives in `run/` too) but no rebuild. No schema change means
don't recreate the DB.

## Restart the battle servers after editing their scripts

The Node servers in `battle-server/` load their code once at start: the Showdown
battle server (`scripts/showdown.js`, :8787) and the client static server
(`scripts/serve-client.js`, :8791, serving the Teambuilder at `play.loomhozer.ca` /
`127.0.0.1:8791`). After editing any `battle-server/scripts/*.js` or a lib it requires,
kill the process and let the watchdog relaunch it, or old code keeps serving:
`Stop-Process -Id (Get-NetTCPConnection -LocalPort 8791 -State Listen).OwningProcess -Force`
(same for 8787). Confirm a client-server edit landed:
`curl -s http://127.0.0.1:8791/teambuilder | grep <new-token>`.

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
