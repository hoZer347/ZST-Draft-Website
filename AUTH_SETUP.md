# Discord login setup

The website signs in with Discord. It doesn't hold the client secret — it's a
public client (anyone can read the site's JS), so it uses **Authorization Code +
PKCE**: the client gets a `code` from Discord and posts it to our server, which
does the secret-bearing exchange and issues its own tokens.

The old site used `response_type=token` (the implicit flow). That put the access
token in the URL fragment with no PKCE and no refresh, and it isn't used here.

## What you have to do by hand

### 1. Discord Developer Portal

<https://discord.com/developers/applications> → your app → OAuth2 → Redirects.

Register these exactly — Discord compares byte-for-byte, trailing slash
included:

```text
http://localhost:8000/
https://zst-league.netlify.app/
```

**Discord only accepts http(s) redirects.** A custom scheme is refused with
"Invalid OAuth2 redirect_uri" and cannot be saved at all — the web client
redirects back to its own origin.

Scope needed: `identify` only. Don't grant `email` or guild scopes — nothing
reads them.

The previous project's app (`1342220973679579138`, "ZST Website") had its secret
rotated, and the value left in the old repo is dead — Discord returns 401 for
that pair. Make a new app rather than trying to recover it.

### 2. Give the server the secret

**Never commit it.** `ZST-Draft-Website` is a *public* repo. The old project kept
a Discord secret in a tracked `config.json`; that repo was private, so it wasn't
exposed, but the same mistake here would be.

Local development:

```powershell
cd server
dotnet user-secrets init
dotnet user-secrets set "Discord:ClientId" "1342220973679579138"
dotnet user-secrets set "Discord:ClientSecret" "<the secret>"
dotnet user-secrets set "Jwt:Key" "<32+ random bytes, base64>"
```

User-secrets live outside the repo, so they can't be committed by accident.

Production (Fly):

```powershell
flyctl secrets set Discord__ClientId=... Discord__ClientSecret=... Jwt__Key=...
```

Double underscore, not colon — that's how env vars map to nested config.

### 3. Point the frontend at the API

`web/config.js` → `apiBase`. The frontend reads the client id from
`/api/auth/config`, so it isn't duplicated there.

## Keys and what they're worth

- **`Discord:ClientSecret`** — lets anyone exchange codes as your app.
- **`Jwt:Key`** — signs our access tokens. Anyone holding it can mint a token
  for **any user, including an admin**. It's the more dangerous of the two.

In Development a throwaway `Jwt:Key` is generated at startup, so the app runs
with no setup. It changes on every restart, which invalidates existing tokens —
that's the intended trade. **Production refuses to start without a real key**
rather than silently signing with something guessable.

## How the flow works

1. Client makes a PKCE `code_verifier` + `code_challenge`, sends the user to
   Discord with the challenge.
2. Discord redirects back with a `code`.
3. Client posts `{code, codeVerifier, redirectUri}` to `POST /api/auth/discord`.
4. Server checks `redirectUri` is in `Discord:RedirectUris`, then exchanges the
   code with Discord using the secret + verifier.
5. Server upserts a `User` keyed by Discord snowflake, issues a 30-minute JWT
   and a 30-day refresh token.
6. Client sends `Authorization: Bearer <jwt>`. On 401 it refreshes once.

Refresh tokens **rotate**: each use revokes the old one, so a stolen token is
only good until the real client next refreshes. They're stored as SHA-256
hashes — a database leak doesn't hand over live sessions.

## What identity means here

The **Discord snowflake is the identity** for the whole system. `Team.CoachId`,
`DeviceRegistration.UserId` and `NotificationRecord.UserId` all hold it.

Usernames are cached for display only. Discord usernames change and get reused —
identifying anyone by username would eventually hand someone else's team over.

A user only gets a team if a `Team` row has their snowflake in `CoachId`. Until
then they can sign in and watch, but the site shows "no team". There's no admin
UI for linking yet — see "Not built yet" in the README.

## Verified

Driven against a running server, and against Discord's real token endpoint:

- Every `/api` route returns 401 unauthenticated; a forged bearer is rejected.
- **Coach B sending coach A's `teamId` gets 403.** The engine's "not your turn"
  was never a security control — this is.
- Own team but out of turn is still 400 "Not your turn" — the two failures stay
  distinct.
- Admin routes: 403 for a normal user, 200 for an admin.
- SignalR: 401 with no token, 200 via header or `access_token` query param —
  and that query param is refused on non-hub paths, so it isn't a bypass.
- Refresh rotates; replaying the old token returns 401; identity survives.
- Notifications are scoped to the caller — the route no longer takes a userId.
- An unregistered `redirect_uri` is refused before Discord is ever contacted.
- With no secret configured, login fails closed rather than open.

## Not verified

- **No browser has run this.** `web/auth.js` and the login UI are unexercised;
  the PKCE round trip through Discord has never been completed end to end.
