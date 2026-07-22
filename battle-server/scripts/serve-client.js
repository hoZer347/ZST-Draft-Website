'use strict';

// Static file server for our self-hosted Showdown client
// (battle-server/client/play.pokemonshowdown.com), exposed at play.loomhozer.ca
// through the Cloudflare tunnel. Sprites/dex data still load from the official
// CDN (see Config.routes in client/config/config.js); we only serve the client
// itself + our tier-patched data files.
//
// gzips compressible responses (the tier data is ~15 MB raw, ~1.7 MB gzipped)
// and caches the compressed bytes in memory, so it stays fast over a home
// connection without a CDN.
//
//   PORT=8791 node scripts/serve-client.js

const http = require('http');
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const ROOT = path.join(__dirname, '..', 'client', 'play.pokemonshowdown.com');
const PORT = Number(process.env.PORT || 8791);

const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8',
  '.png': 'image/png', '.gif': 'image/gif', '.jpg': 'image/jpeg', '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon', '.woff': 'font/woff', '.woff2': 'font/woff2', '.map': 'application/json',
  '.wav': 'audio/wav', '.mp3': 'audio/mpeg', '.txt': 'text/plain; charset=utf-8',
};
const COMPRESSIBLE = new Set(['.html', '.js', '.css', '.json', '.svg', '.map', '.txt']);

// Auto-login the coach into the self-hosted Showdown client when the draft app's
// Teambuilder tab opens it with ?name=<coach>. Our server runs noguestsecurity,
// so a chosen name needs no login-server assertion, we send `/trn <name>,0,`.
// Kept in our launcher, not the vendored client, so it survives a client rebuild.
//
// Two parts, because timing matters:
//  - CAPTURE_HEAD runs FIRST (injected right after <head>), before any client
//    script. The classic client strips the query string during init
//    (history.replaceState in dispatchFragment); on our same-origin dev host that
//    init now runs immediately, so a bottom-of-page read would find ?name already
//    gone. Capturing it up top, into window + sessionStorage, beats that race.
//  - AUTOLOGIN runs LAST (appended after the client scripts, once `app` exists)
//    and drives the login off the captured name.
// The team-seeding logic lives in lib/seed-teams.js (pure + unit-tested). We inline
// its SOURCE into the page so the browser and the Node tests run the identical code
//, the file attaches `seedTeams` to window. A second script then reads the URL +
// localStorage, calls it, and writes the result back.
const SEED_LIB = fs.readFileSync(path.join(__dirname, '..', 'lib', 'seed-teams.js'), 'utf8');

const CAPTURE_HEAD = `<script>
(function () {
  var q = new URLSearchParams(location.search);
  try {
    var n = q.get('name');
    if (n) { window.__zstName = n; try { sessionStorage.setItem('zst_name', n); } catch (e) {} }
  } catch (e) {}
})();
</script>
<script>${SEED_LIB}</script>
<script>
(function () {
  // Seed matchup weeks (blank) + demo teams (filled) into localStorage before the
  // client loads teams from it. All the rules live in seedTeams (lib/seed-teams.js).
  // ?matchups = [{w: week, o: opponentName}]; ?demo = [{player, team}].
  try {
    var q = new URLSearchParams(location.search);
    var matchups = []; try { var mp = q.get('matchups'); if (mp) matchups = JSON.parse(mp) || []; } catch (e) {}
    var demo = []; try { var dm = q.get('demo'); if (dm) demo = JSON.parse(dm) || []; } catch (e) {}
    if ((matchups.length || demo.length) && typeof seedTeams === 'function') {
      var raw = ''; try { raw = localStorage.getItem('showdown_teams') || ''; } catch (e) {}
      var res = seedTeams(raw, { matchups: matchups, demo: demo });
      if (res.changed) { try { localStorage.setItem('showdown_teams', res.text); } catch (e) {} }
    }
  } catch (e) {}
})();
</script>`;

const AUTOLOGIN = `
<script>
(function () {
  try {
    var name = window.__zstName;
    if (!name) { try { name = sessionStorage.getItem('zst_name'); } catch (e) {} }
    if (!name) return;
    var clean = name.replace(/[|,;]+/g, '');
    var wantId = clean.toLowerCase().replace(/[^a-z0-9]/g, '');
    var tries = 0, lastSend = -999;
    var iv = setInterval(function () {
      if (++tries > 480) { clearInterval(iv); return; } // ~120s then give up
      // Wait for the classic client's app + a received challstr.
      if (!window.app || !app.user || !app.user.challstr) return;
      var uid = app.user.get && app.user.get('userid');
      if (uid === wantId) {
        // Logged in as the desired name. Force the top-right userbar to re-render
        // (its 'change' handler can miss the first update if the topbar wasn't
        // built yet), then stop.
        if (app.topbar && app.topbar.updateUserbar) app.topbar.updateUserbar();
        clearInterval(iv);
        return;
      }
      // Keep resending /trn (empty token; our server runs noguestsecurity) every
      // ~1.5s until the rename lands. The common failure is a coach reopening the
      // teambuilder while their just-closed tab's same-name session is still
      // registered server-side: handleRename refuses the merge with |nametaken|
      // until that stale connection drops, after which the same-IP merge
      // succeeds. The server runs with nothrottle on, so repeated renames aren't
      // rate-limited. (This used to cap at 3 sends and gave up while the old
      // session was still lingering, leaving the coach an unnamed Guest.)
      if (tries - lastSend >= 6) {
        app.send('/trn ' + clean + ',0,');
        lastSend = tries;
      }
    }, 250);
  } catch (e) { /* never break the client over auto-login */ }
})();
</script>`;

// Lock coaches to their Discord identity. The teambuilder auto-logs a coach in
// as their Discord name (see AUTOLOGIN), so the client's name controls must not
// let them log out (which drops them to an anonymous Guest and breaks their
// identity across battles/teambuilder) or rename to something else. The
// OptionsPopup (the cog / name popup) renders a "Change name" and a "Log out"
// button in its buttonbar; hide both. Everything else in that popup, the
// avatar/portrait picker, sound, timestamps and other prefs, is untouched, so
// coaches can still customise their trainer sprite.
//
// Scoped to `.ps-popup` so only the popup's controls are hidden: the userbar's
// own "Choose name" button (same name="login") stays as a recovery path if
// auto-login ever fails. "Log out" only appears in the popup anyway. The
// LoginPopup's submit is an unnamed type="submit", so it is unaffected.
const LOCK_IDENTITY = `<style>.ps-popup button[name="logout"], .ps-popup button[name="login"] { display: none !important; }</style>`;

// Some of the league's custom megas (see showdown-config/custom-megas.js) only have
// a FRONT sprite on the CDN and no back sprite, so they'd render blank from the
// player's own side. As a fallback, we inject a small client patch: for those mons
// only, a request for the BACK sprite returns the FRONT sprite instead (their own
// mega art, just facing the camera, never the base form). Which mons lack a back
// sprite is computed against the CDN at startup, so this self-heals as the CDN adds
// the real back sprites (a mon that gains one drops off the list on the next start).
const CUSTOM_MEGAS = (() => { try { return require('../showdown-config/custom-megas.js'); } catch { return { Pokedex: {} }; } })();
const spriteToID = (s) => ('' + (s || '')).toLowerCase().replace(/[^a-z0-9]+/g, '');
let MEGA_BACK_FALLBACK = '';
async function computeMegaBackFallback() {
  const CDN = 'https://play.pokemonshowdown.com/sprites/';
  const has = async (p) => { try { return (await fetch(CDN + p, { method: 'HEAD' })).ok; } catch { return false; } };
  const noBack = {};
  await Promise.all(Object.values(CUSTOM_MEGAS.Pokedex || {}).map(async (sp) => {
    const id = spriteToID(sp.baseSpecies) + '-' + spriteToID(sp.forme); // Showdown sprite id, e.g. malamar-mega
    if (!(await has(`ani-back/${id}.gif`)) && !(await has(`gen5-back/${id}.png`))) noBack[id] = 1;
  }));
  const n = Object.keys(noBack).length;
  // Patch the ModdedDex PROTOTYPE once (covers Dex and every modded dex, incl. the
  // champions mod battles use). getSpriteData is a prototype method; wrapping it so a
  // back request for a no-back mon re-calls for the front sprite.
  MEGA_BACK_FALLBACK = n === 0 ? '' : `<script>
(function(){var NB=${JSON.stringify(noBack)};function patch(){var D=window.Dex;if(!D)return false;var pr=Object.getPrototypeOf(D);if(!pr||typeof pr.getSpriteData!=='function')return false;if(pr.__megaBackFallback)return true;var orig=pr.getSpriteData;pr.getSpriteData=function(pokemon,isFront,options){if(!isFront){try{var forme=(window.Pokemon&&pokemon instanceof window.Pokemon)?pokemon.getSpeciesForme():pokemon;var sid=this.species.get(forme).spriteid;if(NB[sid])return orig.call(this,pokemon,true,options);}catch(e){}}return orig.call(this,pokemon,isFront,options);};pr.__megaBackFallback=true;return true;}var iv=setInterval(function(){if(patch())clearInterval(iv);},150);setTimeout(function(){clearInterval(iv);},30000);})();
</script>`;
  console.log(`[serve-client] custom-mega back-sprite fallback: ${n} mon(s) will use their front sprite`);
}

// path -> { mtimeMs, raw, gz }, files don't change at runtime, so cache the
// gzipped bytes after the first hit instead of recompressing 15 MB per request.
const cache = new Map();

function load(file) {
  const stat = fs.statSync(file); // throws if missing → caught by caller
  const hit = cache.get(file);
  if (hit && hit.mtimeMs === stat.mtimeMs) return hit;
  let raw = fs.readFileSync(file);
  const ext = path.extname(file);
  // The SPA shell is served for every client-side route. This shell has no
  // <head>/<body>, it goes straight to <meta>/<script> tags. Inject CAPTURE_HEAD
  // before the FIRST <script> so it grabs ?name before any client script can
  // strip the query string, and append AUTOLOGIN after the client scripts so it
  // runs last, once `app` exists.
  if (path.basename(file) === 'index-old.html') {
    let html = raw.toString('utf8');
    // Insert before the first EXTERNAL script (`<script ... src=`). The very
    // first <script> in this shell is inside an `<!--[if lte IE 8]>` conditional
    // comment, so a naive `<script` match would bury CAPTURE_HEAD in a block
    // modern browsers never run. The first external script is the earliest real
    // client JS, so running just before it still beats the query-string strip.
    html = html.replace(/<script[^>]*\bsrc=/i, CAPTURE_HEAD + '\n$&') + AUTOLOGIN + '\n' + LOCK_IDENTITY + '\n' + MEGA_BACK_FALLBACK + '\n';
    // Cache-bust our LOCAL patched data files by their mtime. Their ?v= in the shell
    // is a hardcoded constant that never moves when patch-client-tiers.js rewrites the
    // file, so browsers (and Cloudflare) would serve the stale bytes forever. Stamping
    // the version with the file's mtime makes every re-patch a new URL, so a fix lands
    // on the next load with no manual version bump.
    html = html.replace(/\/data\/(search-index|teambuilder-tables)\.js\?v=\d+/g, (whole, name) => {
      try { return `/data/${name}.js?v=${Math.floor(fs.statSync(path.join(ROOT, 'data', `${name}.js`)).mtimeMs)}`; }
      catch { return whole; }
    });
    raw = Buffer.from(html, 'utf8');
  }
  const gz = COMPRESSIBLE.has(ext) ? zlib.gzipSync(raw, { level: 6 }) : null;
  const entry = { mtimeMs: stat.mtimeMs, raw, gz, ext };
  cache.set(file, entry);
  return entry;
}

// Probe the CDN for missing back sprites, then drop the cached shell so the next
// request re-injects the patch with the populated list. Best-effort and off the
// hot path, the server starts serving immediately.
computeMegaBackFallback()
  .catch((e) => console.warn('[serve-client] mega back-sprite fallback failed:', e.message))
  .finally(() => cache.delete(path.join(ROOT, 'index-old.html')));

http.createServer((req, res) => {
  let url = decodeURIComponent((req.url || '/').split('?')[0]);

  // Stand in for the Showdown login server's action.php. We don't run one; the
  // battle server uses `noguestsecurity`, so coaches log in by name via the
  // teambuilder's `/trn <name>,0,` auto-login. But the classic client, on every
  // `|challstr|`, POSTs `act=upkeep` here and sets `app.user.loaded = true` ONLY
  // in that request's SUCCESS callback (client.js receiveChallstr). With no
  // login server the POST 404s, `loaded` never flips, and the top-left userbar
  // is stuck on a disabled "Loading…" button forever (client-topbar.js:
  // `if (!app.user.loaded) buf = 'Loading…'`). Reply with a minimal "no session"
  // upkeep body so `loaded` flips true and the userbar renders; the `/trn`
  // auto-login then names the coach. The leading `]` is the anti-JSON-hijack
  // prefix the client's Storage.safeJSON strips. A non-empty body is required:
  // safeJSON bails on an empty response, which would leave `loaded` false.
  if (url.endsWith('/action.php')) {
    const body = ']' + JSON.stringify({ username: '', loggedin: false, assertion: '' });
    res.writeHead(200, {
      'content-type': 'application/json; charset=utf-8',
      'cache-control': 'no-cache',
      'access-control-allow-origin': '*',
    });
    res.end(body);
    return;
  }

  if (url.endsWith('/')) url += 'index-old.html';
  const file = path.join(ROOT, url);
  if (!file.startsWith(ROOT)) { res.writeHead(403).end(); return; }

  let entry;
  try { entry = load(file); }
  catch {
    // Extensionless paths (e.g. /teambuilder) are client-side routes → serve the
    // SPA (index-old.html). Missing assets (with an extension) are a real 404.
    if (!path.extname(url)) {
      try { entry = load(path.join(ROOT, 'index-old.html')); }
      catch { console.log('404', req.url); res.writeHead(404).end(); return; }
    } else {
      console.log('404', req.url); res.writeHead(404).end(); return;
    }
  }
  console.log('200', req.url);

  const headers = {
    'content-type': MIME[entry.ext] || 'application/octet-stream',
    // No-cache while we're actively patching the client, otherwise Cloudflare
    // caches our edited files for an hour and changes don't show. Raise this to a
    // long max-age once the client is finalised.
    'cache-control': 'no-cache',
  };
  const acceptsGzip = /\bgzip\b/.test(req.headers['accept-encoding'] || '');
  if (entry.gz && acceptsGzip) {
    headers['content-encoding'] = 'gzip';
    headers['vary'] = 'Accept-Encoding';
    res.writeHead(200, headers);
    res.end(entry.gz);
  } else {
    res.writeHead(200, headers);
    res.end(entry.raw);
  }
}).listen(PORT, '127.0.0.1', () => console.log(`[serve-client] http://127.0.0.1:${PORT} -> ${ROOT} (gzip on)`));
