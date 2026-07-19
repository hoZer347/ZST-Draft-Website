# Deploying

Two pieces, two hosts. `web/` is static and already lives on Netlify. `server/`
needs a .NET host — and that is the part that isn't done.

## Why the server can't go on Netlify

Netlify serves static files and short-lived serverless functions. It has no .NET
runtime. Even setting that aside, the server needs things serverless can't give:

- `DraftClock` is an always-on background service. It fires auto-picks when a
  coach's timer expires. A function that only wakes on HTTP can't do that.
- The SignalR hub needs a persistent WebSocket.
- SQLite needs a writable disk that survives requests.

So the API is deployed separately, and `web/config.js` points at it.

## Fly.io — configured, not deployed

`server/Dockerfile` and `server/fly.toml` are written and ready. `flyctl` is
installed and the account `liam@browndomain.com` is logged in, but app creation
is blocked:

```
Error: We need your payment information to continue!
```

Fly requires a card on file before creating any app. Once that's added:

```powershell
cd server
flyctl volumes create draft_data --size 1 --region yyz   # SQLite's disk
flyctl deploy
```

Then point the frontend at it and redeploy Netlify:

```js
// web/config.js
apiBase: 'https://zst-draft-api.fly.dev',
```

`fly.toml` already lists `https://zst-league.netlify.app` in `Cors__Origins__0`.
If the API lands on a different hostname, that list is what needs updating —
CORS is what stops the browser talking to it.

Two settings in `fly.toml` are deliberate and shouldn't be "optimised" away:

- `auto_stop_machines = false` / `min_machines_running = 1`. Fly stops idle
  machines by default. That would stop the draft clock, so an idle coach's
  timer would never fire — the exact failure the timer exists to prevent.
- The `[mounts]` volume. Without it SQLite lives in the container filesystem and
  every deploy wipes the league.

## Cloudflare Quick Tunnel — tried, does not work here

The idea was to expose the locally-running API at a public `trycloudflare.com`
URL with no account. It doesn't work from this machine. Tested:

- 4 separate tunnels, each with a fresh URL
- both transports (`quic` and `--protocol http2`)
- two origins: this API, and a bare `python -m http.server`

Every request returned **404 from Cloudflare's edge** (`Server: cloudflare`,
`CF-Ray` present — the request never reached the local process). Each tunnel
registered only **1** connection where cloudflared normally opens 4.

A trivial static server failing identically rules out this codebase. It's either
Cloudflare's quick-tunnel service or something on this network. A *named* tunnel
(which needs a free Cloudflare account) may work where quick tunnels don't.

## Other options

- **Azure App Service** — F1 free tier runs ASP.NET Core. No always-on, so the
  draft clock is unreliable; B1 fixes that.
- **Render** — free tier sleeps when idle, same clock problem.
- **Any VPS** — `docker build` the existing Dockerfile and run it.

The pattern to watch for: **free tiers that sleep break the draft clock.** The
server has to stay awake, or auto-pick never fires.

## Reverting the Netlify site

If the live site needs to go back to the old prototype:

```powershell
git revert 0b2bb09   # the "Replace prototype" commit
git push
```

Netlify redeploys automatically. The old prototype's files are all still in
history.
