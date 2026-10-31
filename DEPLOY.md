# Deploying

The whole stack is self-hosted on one Windows box and exposed through a single
named Cloudflare tunnel. There is no cloud host; "deploy" means "promote a build on
that box and keep the processes up."

## What runs, and where it's exposed

| Process | Local | Public (Cloudflare tunnel `loom-tunnel`) |
| --- | --- | --- |
| .NET server (web app + API + SignalR) | `:5211` | `dev.loomhozer.ca`, `zst.loomhozer.ca` |
| Showdown battle server (`battle-server`) | `:8787` | `showdown.loomhozer.ca` |
| Showdown client / Teambuilder | `:8791` | `play.loomhozer.ca` |
| pay-page (unrelated project, `C:\Users\3hoze\pay-page`) | `:8080` | `justfuckingpayme.com`, `www.justfuckingpayme.com` |
| personal site (unrelated project, `C:\Users\3hoze\Desktop\Website`) | `:8082` | `loomhozer.ca`, `www.loomhozer.ca` |

The .NET server serves the web page and the API on the same origin, so production
calls are relative (`/api/...`) with no CORS. `web/config.js` selects the API and
Showdown targets by hostname (localhost, `*.loomhozer.ca`, else legacy).

## Keeping it up

`server/keep-server-up.ps1` is the watchdog, launched at boot (before sign-in) by the
`DraftLeagueBoot` scheduled task (install with `server/install-boot-task.ps1`) and at
sign-in from the user Startup folder.
Every ~5s it relaunches anything down: the .NET server, `cloudflared`, both Node
servers, the pay-page server on :8080 and the personal-site server on :8082. It never kills a live
process: a Node server that stops answering is logged and left running, and is relaunched
once it has exited. Each Showdown launch writes `server/showdown-logs/<start time>.out.log`
and `.err.log`, and the newest 40 files are kept. `restart-node.ps1` clears a hung :8787 or :8791; a hung :8080 or :8082
needs killing by hand. Three failed connects in a row count as down. Ports are checked by connecting, because Windows' TCP table has omitted live
listeners since 2026-10-30. It also enforces an always-awake power
profile so the draft clock keeps firing while unattended. Manage it by its
`.watchdog.pid` / `.watchdog.alive` files, never by matching command lines (a match
would kill the watchdog itself).

Relaunch it with the script path quoted, or the space in "Pokemon Draft League"
splits the argument and PowerShell exits immediately (silently leaving nothing
supervised):

```powershell
$s = 'C:\Users\3hoze\Desktop\Pokemon Draft League\server\keep-server-up.ps1'
Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$s`"" -WindowStyle Hidden
```

Tunnel config lives in `%USERPROFILE%\.cloudflared\config.yml`.

## Promoting a new build

The live server runs `server/run/DraftLeagueLive.exe`, a renamed copy, not the build
output. A build does not go live on its own:

```powershell
cd server
dotnet build
powershell -File deploy-run.ps1   # pauses + kills live, copies bin/Debug/net9.0 to run/, re-creates the exe, unpauses
```

The watchdog relaunches from `run/` within ~5s. Config/appsettings-only changes still
need `deploy-run.ps1` (config lives in `run/` too) but no `dotnet build`. See CLAUDE.md
for the full handshake and the `.server-paused` caveat.

## Secrets

Never commit secrets; this is a public repo. Supply out of band (see AUTH_SETUP.md):
`Discord:ClientId`, `Discord:ClientSecret`, and `Jwt:Key` via user-secrets in dev, or
environment variables (`Discord__ClientSecret`, double underscore) in production.
Production refuses to start without a real `Jwt:Key`; Development generates a throwaway
one per run.

## Legacy

`server/Dockerfile`, `server/fly.toml`, `netlify.toml`, and the `netlify.app` entries
still in `appsettings.json` / `web/config.js` are from an earlier Fly.io + Netlify plan
that was never used. They're inert. The tunnel setup above replaced them.
