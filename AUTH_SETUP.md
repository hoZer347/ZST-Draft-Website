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
3. Client posts `{code, codeVerifier, redirectUri}` to `POST /api/auth/discord`.
4. Server checks `redirectUri` is in `Discord:RedirectUris`, exchanges the code with
   the secret + verifier, upserts a `User` keyed by Discord snowflake, and issues a
   30-minute JWT + 30-day refresh token.
5. Client sends `Authorization: Bearer <jwt>`; on 401 it refreshes once. Refresh
   tokens **rotate** (each use revokes the old one) and are stored as SHA-256 hashes.

## Identity and admin

The **Discord snowflake is the system-wide identity**: `Team.CoachId`,
`DeviceRegistration.UserId`, and `NotificationRecord.UserId` all hold it. Usernames are
cached for display only (they change and get reused). A user gets a team only once a
`Team` row carries their snowflake in `CoachId`; until then they sign in and watch.

Admin is granted on sign-in to any id in `Admin:DiscordIds` (appsettings). In
Development, `POST /dev/token/<discordId>?admin=true` mints an admin token for any id
without Discord; the reserved id `admin` is an admin who is deliberately not a player.
