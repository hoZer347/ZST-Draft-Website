# Draft League — project notes

## Never use em dashes

Do not write em dashes (—) anywhere: UI copy, tooltips, code comments, commit
messages, prose. Use a comma, parentheses, a colon, or two sentences instead.

## Always keep the dev server running

The .NET API + web app runs at **http://localhost:5211** (the web `config.js` and the
Cloudflare tunnels `dev.loomhozer.ca` / `zst.loomhozer.ca` all point there). A
watchdog, `server/keep-server-up.ps1`, relaunches it (and cloudflared + the Showdown
servers) whenever it's down.

**After any change, restart the server and leave it running — never end a turn with
:5211 down.** Verify with `curl -s -o /dev/null -w "%{http_code}" http://localhost:5211/dev/slots`
(expect `200`).

**The live server runs `server/run/DraftLeagueLive.exe`, a renamed COPY — NOT the
build output** (`bin/Debug/net9.0`). A fresh `dotnet build` does NOT go live on its
own; you must promote it. The process name is `DraftLeagueLive` (kill that, never
`DraftLeague.Web`).

Rebuild handshake:

1. `dotnet build` in `server/`.
2. `powershell -File server/deploy-run.ps1` — sets `.server-paused`, kills the live
   server, robocopies `bin/Debug/net9.0` → `run/`, re-creates `DraftLeagueLive.exe`,
   clears the flag. Watchdog relaunches from `run/` within ~5s.

**Never leave `server/.server-paused` behind** — it silently keeps the server down.
If `:5211` is off, first check for a stale `server/.server-paused` and delete it.

Config/appsettings-only changes still need `deploy-run.ps1` (the config lives in
`run/` too) but no rebuild. No schema change → don't recreate the DB.

## Restart the Showdown/battle servers after changing their scripts

The Node servers in `battle-server/` — the Showdown battle server (`scripts/showdown.js`,
port 8787) and the self-hosted client static server (`scripts/serve-client.js`, port
8791, serves the Teambuilder at `play.loomhozer.ca` / `127.0.0.1:8791`) — load their
code once at process start. **After editing any `battle-server/scripts/*.js` or the
libs they require, kill the affected process and let the watchdog relaunch it** (~5s),
or the old code keeps serving:

- Client server (8791): `Stop-Process -Id (Get-NetTCPConnection -LocalPort 8791 -State Listen).OwningProcess -Force`
- Battle server (8787): same with port 8787.

Verify the client server picked up an edit by curling the served page, e.g.
`curl -s http://127.0.0.1:8791/teambuilder | grep <your-new-token>`.

## Verifying without disrupting the running server

To test changes without touching the live server, run an isolated instance:
`ASPNETCORE_ENVIRONMENT=Development ConnectionStrings__Default="Data Source=verify.db" dotnet run --no-build --no-launch-profile --urls http://localhost:5199`
(delete `verify.db*` after). Dev auth: `POST /dev/token/<discordId>?admin=true`.

## Web assets

Bump the `?v=NN` query on every asset in `web/index.html` after editing web files, or
browsers serve stale CSS/JS.
