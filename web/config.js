// Where the .NET API lives. Netlify serves this folder as static files and has
// no build step, so this is plain runtime config — edit it, commit, redeploy.
//
// The API cannot be hosted on Netlify (no .NET runtime, and the draft clock and
// SignalR hub both need a long-lived process). Point this at wherever the
// server is deployed — Azure App Service, Fly.io, Render, a VPS. See DEPLOY.md.
//
// Whatever origin serves this page must also appear in the server's
// Cors:Origins config, or the browser will block every request.
window.DRAFT_CONFIG = {
  apiBase:
    location.hostname === 'localhost' || location.hostname === '127.0.0.1'
      ? 'http://localhost:5211'
      : 'https://REPLACE-ME.example.com', // <-- set once the API is deployed

  // Our self-hosted Showdown server, as host:port. The Teambuilder tab opens the
  // official Showdown client pointed at it (play.pokemonshowdown.com/~~<server>),
  // which is how you reach a custom server without hosting the client too.
  showdownServer:
    location.hostname === 'localhost' || location.hostname === '127.0.0.1'
      ? 'localhost:8787'
      : 'REPLACE-ME.example.com:8787', // <-- set once the battle server is deployed
};

// There is deliberately no teamId or userId here. Identity comes from the
// signed-in Discord session; a value in this file would be a suggestion any
// visitor could edit.
