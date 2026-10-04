# Discord login setup

Sign-in is Discord OAuth. The site is a public client (its JS is readable), so it
uses **Authorization Code + PKCE**: the browser gets a `code` from Discord and posts
it to our server, which does the secret-bearing exchange and issues its own JWTs.

## Setup by hand

### 1. Discord Developer Portal

<https://discord.com/developers/applications> → your app → OAuth2 → Redirects.
Register each origin exactly (Discord compares byte-for-byte, trailing slash
included). The client redirects back to its own origin, so register the ones you use:

```text
http://localhost:5211/
https://dev.loomhozer.ca/
https://zst.loomhozer.ca/
```

Discord accepts only http(s) redirects (custom schemes are refused). Scope: `identify`
only. These must also be listed in the server's `Discord:RedirectUris` (appsettings),
or the login is refused before Discord is contacted.

### 2. Give the server the secret (never commit it)

```powershell
cd server
dotnet user-secrets set "Discord:ClientId" "<app id>"
dotnet user-secrets set "Discord:ClientSecret" "<the secret>"
dotnet user-secrets set "Jwt:Key" "<32+ random bytes, base64>"
```

User-secrets live outside the repo. In production, supply the same keys as environment
variables (`Discord__ClientSecret`, double underscore maps to nested config). The
frontend reads the client id from `/api/auth/config`, so it isn't duplicated in
`web/config.js`.

## The two keys, and their blast radius

- **`Discord:ClientSecret`**: lets anyone exchange codes as your app.
- **`Jwt:Key`**: signs our access tokens. Anyone with it can mint a token for **any
  user, including an admin**. The more dangerous of the two. Development generates a
  throwaway key per startup (so the app runs with no setup, and every restart
  invalidates old tokens); **production refuses to start without a real one.**

## The flow

1. Client makes a PKCE `code_verifier` + `code_challenge`, sends the user to Discord.
2. Discord redirects back with a `code`.
3. Client posts `{code, codeVerifier, redirectUri, invite}` to
   `POST /api/auth/discord` (`invite` only when one was stashed from a link).
4. Server checks `redirectUri` is in `Discord:RedirectUris`, exchanges the code with
   the secret + verifier, requires a valid invite if this snowflake is new, upserts a
   `User` keyed by it, and issues a 30-minute JWT + 30-day refresh token.
5. Client sends `Authorization: Bearer <jwt>`; it renews a token it knows is spent
   and refreshes once on a 401. Refresh tokens **rotate** (each use revokes the old
   one) and are stored as SHA-256 hashes.

A rotated token keeps working for `Jwt:RefreshGraceSeconds` (default 60). Tabs share
one stored token and can refresh simultaneously; without the grace the loser got a
401, which cleared the session and forced a fresh Discord login. The client also
single-flights its own refreshes. Set the grace to 0 for strict single use.

## Three ways in: visitor, player, admin

**Visitor** is the default and needs nothing. Opening the site posts to
`POST /api/auth/visitor` and gets back a short-lived access token that carries no
`discord_id` claim and no refresh token. That is the whole security model: every
write path resolves its caller through `ClaimsPrincipal.DiscordId()`, and
`IsAdminAsync` / `OwnsTeamAsync` both return false when it is null, so a visitor
reads everything and can change nothing. The token exists only because the read
endpoints sit behind `RequireAuthorization`. It lasts `Jwt:VisitorTokenHours`
(default 12) and the page just mints another when it lapses, there is nothing to
refresh. `VisitorTests` pins both halves: what a visitor may read, and that every
write is refused.

**Player** takes an invite. An admin hits **Copy player invite** on the draft tab
(`POST /api/admin/invite`) and hands out the resulting
`https://<host>/?invite=<token>` link. Opening it stashes the token and shows the
Discord sign-in card instead of dropping you into visitor mode; the exchange sends
the token along, and the server requires a valid one before it will create a
**new** `User`. An account that already exists is never asked for an invite, so
signing out cannot lock a coach out of a league they are already in.

The invite is a JWT signed with `Jwt:Key`, carrying a `purpose=invite` claim and
an expiry (`Invite:ValidDays`, default 14) and nothing else. No invite table: the
signature is the record. The purpose claim is what stops a live access token
being spent as an invite, since both are signed with the same key. It is reusable
until it expires, so treat a leaked link as a leaked invite. Set
`Invite:RequireForNewUsers` to `false` to reopen open signup.

**Admin** is unchanged, see below.

## Identity and admin

The **Discord snowflake is the system-wide identity**: `Team.CoachId`,
`DeviceRegistration.UserId`, and `NotificationRecord.UserId` all hold it. Usernames are
cached for display only (they change and get reused). A user gets a team only once a
`Team` row carries their snowflake in `CoachId`; until then they sign in and watch.

Admin is granted on sign-in to any id in `Admin:DiscordIds` (appsettings). In
Development, `POST /dev/token/<discordId>?admin=true` mints an admin token for any id
without Discord; the reserved id `admin` is an admin who is deliberately not a player.
