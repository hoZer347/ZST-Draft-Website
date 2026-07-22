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
// The .NET API, for the same-origin roster proxy below (avoids the teambuilder
// having to reach the API cross-origin). Same host the report plugin posts to.
const API_BASE = (process.env.DRAFT_REPORT_URL || 'http://localhost:5211/api/showdown/report')
  .replace(/\/api\/showdown\/report\/?$/, '');

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
// Inlined into the page so the browser and the Node tests share the exact roster ->
// buildable-species mapping (see ROSTER_RESTRICT below and test/roster-teambuilder.test.js).
const ROSTER_LIB = fs.readFileSync(path.join(__dirname, '..', 'lib', 'roster-teambuilder.js'), 'utf8');

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

// Restrict the format pickers to the league's two formats and default the Battle!
// button to ZST Season 4. The client parses the server's |formats| list into
// window.BattleFormats, each entry carrying searchShow / challengeShow / tournamentShow
// flags the format popups filter on. We wrap app.parseFormats so that after every
// parse only the allowed ids keep those flags (everything else is hidden from the
// Battle! / find-a-battle picker, the challenge picker, and tournaments), and the
// main-menu search button is pointed at ZST (the stock default is a random battle,
// which we've hidden). Both allowed formats (ZST + the "[Gen 9] Scrims" doubles
// custom game) are defined in showdown-config/custom-formats.js; the self-hosted
// client only surfaces our custom formats + random battles, so a built-in custom
// game would never appear here. The teambuilder's own format list is unaffected.
const FORMAT_ALLOW = ['gen9zstseason4', 'gen9scrims'];
const FORMAT_DEFAULT = 'gen9zstseason4';
const FORMAT_FILTER = `<script>
(function(){
  var ALLOW = ${JSON.stringify(FORMAT_ALLOW.reduce((o, id) => (o[id] = 1, o), {}))};
  var DEFAULT = ${JSON.stringify(FORMAT_DEFAULT)};
  function sanitize(){
    var BF = window.BattleFormats; if (!BF) return;
    for (var id in BF){ var f = BF[id]; if (!f) continue;
      var ok = !!ALLOW[id]; f.searchShow = ok; f.challengeShow = ok; f.tournamentShow = ok; }
  }
  // Point the main-menu search (Battle!) format button at the default. renderFormats
  // otherwise falls back to a random battle, which we've hidden; set the button's
  // value and re-render so the label, team picker and the search all use ZST.
  function setDefault(){
    var mm = window.app && app.rooms && app.rooms[''];
    if (!mm || mm.__zstDefaulted || !window.BattleFormats || !BattleFormats[DEFAULT]) return false;
    var $btns = mm.$ ? mm.$('button[name=format]') : null;
    if (!$btns || !$btns.length) return false;
    mm.curFormat = DEFAULT;
    $btns.each(function(i, el){ el.value = DEFAULT; });
    if (mm.updateFormats) mm.updateFormats();
    mm.__zstDefaulted = true;
    return true;
  }
  function hook(){
    if (!window.app || typeof app.parseFormats !== 'function') return false;
    if (!app.__zstFormatFilter){
      var orig = app.parseFormats;
      app.parseFormats = function(){ var r = orig.apply(this, arguments); sanitize(); try { setDefault(); } catch(e){} return r; };
      app.__zstFormatFilter = true;
    }
    sanitize(); // formats may already have been parsed before we hooked
    var done = false; try { done = setDefault(); } catch(e){}
    // Keep polling until the main-menu button has actually been defaulted (the room
    // may render after the first parse); the wrap above handles any later re-parse.
    return !!(window.app && app.rooms && app.rooms[''] && app.rooms[''].__zstDefaulted);
  }
  var iv = setInterval(function(){ if (hook()) clearInterval(iv); }, 150);
  setTimeout(function(){ clearInterval(iv); }, 30000);
})();
</script>`;

// Auto-fill a C-tier pick's drafted Tera type in the teambuilder. Each coach draws
// a fixed Tera type for their C-tier mons; picking one of those species in the
// builder should load it in already teratyped. We fetch the coach's roster (by the
// captured login name, via the same-origin /zst-roster proxy) into a species->tera
// map, then wrap TeambuilderRoom.setPokemon so that right after it sets the species
// (which resets teraType), we re-apply the drafted Tera and re-render. Non-C picks,
// and species the coach didn't draft, are left untouched.
const AUTO_TERA = `<script>
(function () {
  var TERA = {};
  function id(s) { return ('' + (s || '')).toLowerCase().replace(/[^a-z0-9]/g, ''); }
  function load() {
    var name = window.__zstName;
    if (!name) { try { name = sessionStorage.getItem('zst_name'); } catch (e) {} }
    if (!name) return;
    fetch('/zst-roster?name=' + encodeURIComponent(name)).then(function (r) { return r.json(); }).then(function (d) {
      if (!d || !d.found || !d.mons) return;
      d.mons.forEach(function (m) {
        if (m.tier === 'C' && m.tera) { if (m.name) TERA[id(m.name)] = m.tera; if (m.slug) TERA[id(m.slug)] = m.tera; }
      });
    }).catch(function () {});
  }
  function patch() {
    var R = window.TeambuilderRoom;
    if (!R || !R.prototype || typeof R.prototype.setPokemon !== 'function') return false;
    if (R.prototype.__zstAutoTera) return true;
    var orig = R.prototype.setPokemon;
    R.prototype.setPokemon = function (val, selectNext) {
      orig.call(this, val, selectNext);
      try {
        var set = this.curSet;
        if (set && set.species) {
          var t = TERA[id(set.species)];
          if (t && set.teraType !== t) { set.teraType = t; this.updateSetTop(); this.save(); }
        }
      } catch (e) {}
    };
    R.prototype.__zstAutoTera = true;
    return true;
  }
  load();
  var iv = setInterval(function () { if (patch()) clearInterval(iv); }, 150);
  setTimeout(function () { clearInterval(iv); }, 30000);
})();
</script>`;

// Restrict the ZST Season 4 teambuilder's species picker to the coach's drafted
// roster. We fetch the roster (same-origin /zst-roster proxy) and map each pick's slug
// to the base species the builder offers (rosterSpeciesIds, inlined from ROSTER_LIB).
//
// The classic teambuilder's "add pokemon" (+) list AND its species text search both
// flow through DexSearch.find (oldclient/search.js -> engine.find(query) -> reads
// engine.results): an empty query is the browse list, a typed query is the text
// search. So we wrap DexSearch.find and, for a pokemon search in the ZST format, keep
// only drafted mons in this.results (dropping undrafted mons AND the ability/move/type
// filter chips, since the coach should see just their team). Filtering the OUTPUT each
// call sidesteps the per-search result cache. Fail OPEN: no roster / no match / a fetch
// error leaves the normal list, because the format's validateTeam blocks any undrafted
// mon at battle time regardless of the picker.
const ROSTER_RESTRICT = `<script>${ROSTER_LIB}</script>
<script>
(function () {
  var TAG = '[zst-roster]';
  function dbg(msg) { try { console.log(TAG, msg); } catch (e) {} } // console breadcrumbs
  // Two allow-sets, chosen by the team's format in the search hook below:
  //  ALLOWED_OWN    - the coach's OWN drafted mons (ZST Season 4, a league team).
  //  ALLOWED_SEASON - every mon owned by ANY coach (Scrims, free practice).
  var ALLOWED_OWN = null, ALLOWED_SEASON = null;
  var MONS = null;        // coach's own roster mons, held until window.Dex loads (CDN, async)
  var MONS_SEASON = null; // season-wide owned mons
  var tried = {};         // name -> 1, so each candidate is fetched at most once
  function isGuest(x) { x = ('' + x).toLowerCase(); return !x || x === 'guest' || /^guest[0-9]/.test(x); }
  function nameCandidates() {
    // Try EVERY name we can see, because Showdown sanitises the login (drops '.', etc.)
    // so the logged-in name, the ?name= value, and the DB's stored name can all differ.
    // Whichever one the roster endpoint matches wins.
    var c = [];
    try { if (window.app && app.user && app.user.get) { var u = app.user.get('userid'), n = app.user.get('name'); if (!isGuest(u)) c.push(u); if (!isGuest(n)) c.push(n); } } catch (e) {}
    if (window.__zstName) c.push(window.__zstName);
    try { var s = sessionStorage.getItem('zst_name'); if (s) c.push(s); } catch (e) {}
    return c.filter(function (x, i) { return x && !isGuest(x) && c.indexOf(x) === i; });
  }
  function fetchRosters() {
    if (!MONS) {
      var cands = nameCandidates();
      if (!fetchRosters._logged) { fetchRosters._logged = 1; if (cands.length) dbg('name candidates: ' + JSON.stringify(cands)); }
      cands.forEach(function (name) {
        if (tried[name]) return;
        tried[name] = 1;
        fetch('/zst-roster?name=' + encodeURIComponent(name), { cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (d) {
          dbg('roster for "' + name + '" -> found=' + (d && d.found) + ' mons=' + (d && d.mons ? d.mons.length : 0));
          if (!MONS && d && d.found && d.mons && d.mons.length) MONS = d.mons; // first match wins
        }).catch(function (e) { delete tried[name]; dbg('fetch failed for "' + name + '" ' + e); });
      });
    }
    if (!MONS_SEASON && !fetchRosters._season) {
      fetchRosters._season = 1; // season roster needs no name; fetch once
      fetch('/zst-season-roster', { cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (d) {
        dbg('season roster -> mons=' + (d && d.mons ? d.mons.length : 0));
        if (d && d.mons && d.mons.length) MONS_SEASON = d.mons;
      }).catch(function (e) { fetchRosters._season = 0; dbg('season fetch failed ' + e); });
    }
  }
  function build(mons) {
    if (!mons || !window.Dex || !window.Dex.species || !window.rosterSpeciesIds) return null;
    var ids = window.rosterSpeciesIds(mons, function (slug) { return window.Dex.species.get(slug); });
    return (ids && Object.keys(ids).length) ? ids : null;
  }
  function computeAllowed() {
    if (!ALLOWED_OWN && MONS) { var a = build(MONS); if (a) { ALLOWED_OWN = a; window.__zstAllowed = a; dbg('own restriction ACTIVE (' + Object.keys(a).length + ' mons)'); } }
    if (!ALLOWED_SEASON && MONS_SEASON) { var b = build(MONS_SEASON); if (b) { ALLOWED_SEASON = b; window.__zstAllowedSeason = b; dbg('season restriction ACTIVE (' + Object.keys(b).length + ' mons)'); } }
  }
  function isScrims(fmt) { return ('' + (fmt || '')).toLowerCase().indexOf('scrims') >= 0; }
  function installHook() {
    var D = window.DexSearch;
    if (!D || !D.prototype || typeof D.prototype.find !== 'function') return false;
    if (D.prototype.__zstRoster) return true;
    var origFind = D.prototype.find;
    var logged = {};
    D.prototype.find = function (query) {
      var ret = origFind.call(this, query);
      try {
        var ts = this.typedSearch;
        if (ts && ts.searchType === 'pokemon') {
          window.__zstFmt = ts.format;
          // Scrims picks from the season-wide owned pool; every other league format
          // (ZST) is limited to the coach's own team. Filtering the OUTPUT each call
          // sidesteps the per-search result cache.
          var scrims = isScrims(ts.format);
          var allowed = scrims ? ALLOWED_SEASON : ALLOWED_OWN;
          var before = Array.isArray(this.results) ? this.results.length : -1;
          if (allowed && Array.isArray(this.results)) {
            this.results = window.filterPokemonResults(this.results, allowed);
          }
          if (!logged[ts.format || '']) { logged[ts.format || ''] = 1; dbg('pokemon search: format=' + JSON.stringify(ts.format) + ' scrims=' + scrims + ' restricting=' + !!allowed + ' rows ' + before + '->' + (Array.isArray(this.results) ? this.results.length : -1)); }
        }
      } catch (e) { dbg('filter error ' + e); }
      return ret;
    };
    D.prototype.__zstRoster = true;
    dbg('DexSearch hook installed');
    return true;
  }
  // Poll: (re)fetch as the live login name resolves (auto-login runs async, esp. after
  // a hard refresh), build the allow-sets once rosters + Dex are ready, install the hook.
  var iv = setInterval(function () {
    fetchRosters();
    computeAllowed();
    var hooked = installHook();
    if (hooked && ALLOWED_OWN && ALLOWED_SEASON) clearInterval(iv);
  }, 300);
  setTimeout(function () { clearInterval(iv); }, 120000);
})();
</script>`;

// Land a coach in the TEAMBUILDER, not the text lobby, when the draft app's
// Teambuilder tab opens this client at /teambuilder. On desktop the teambuilder is a
// SIDE room shown alongside the main menu, so you always see it. On a narrow (mobile)
// screen only ONE room is visible at a time, and the classic client (on a self-hosted
// host) adds a background `lobby` chat room at boot; a focus that lands there just
// after the route is dispatched leaves the coach staring at an empty lobby instead of
// the builder they tapped. So: capture that we were opened FOR the teambuilder, force
// it focused once it exists, and hold that focus through the brief boot window against
// any non-user steal, then stop the moment the coach navigates themselves (e.g. tapping
// Home to queue a battle), so we never trap them. Keyed off the initial pathname since
// later navigation rewrites it.
const FOCUS_TEAMBUILDER = `<script>
/* [zst-focus-teambuilder] */
(function () {
  var openedForTeambuilder;
  try { openedForTeambuilder = /(^|\\/)teambuilder(\\/|$)/.test(location.pathname); } catch (e) { return; }
  if (!openedForTeambuilder) return; // opened for something else (a battle) — leave routing alone
  var focused = false, interacted = false;
  // Any deliberate tap/keypress inside the client means the coach is driving now; stop
  // re-asserting so we don't yank them back off the main menu when they want to battle.
  ['pointerdown', 'touchstart', 'keydown'].forEach(function (ev) {
    try { document.addEventListener(ev, function () { interacted = true; }, { capture: true, once: true }); } catch (e) {}
  });
  var iv = setInterval(function () {
    if (!window.app || !app.rooms || !app.rooms['teambuilder']) return;
    var tb = app.rooms['teambuilder'];
    var visible = (app.curRoom === tb || app.curSideRoom === tb);
    if (!focused) { if (!visible) app.focusRoom('teambuilder'); focused = true; return; } // initial landing
    if (interacted) { clearInterval(iv); return; }        // respect the coach's own navigation
    if (!visible) app.focusRoom('teambuilder');           // undo a boot autojoin steal (mobile)
  }, 150);
  setTimeout(function () { clearInterval(iv); }, 8000);
})();
</script>`;

// The league's custom megas (see showdown-config/custom-megas.js) are the Pokémon
// Legends: Z-A mega evolutions. Showdown's CDN has no usable battle sprite for most of
// them (the classic client falls to gen5/<id>.png, which 404s → a broken-image icon the
// moment the mega evolves). Serebii hosts the official Z-A mega ARTWORK for every one,
// keyed by national dex + a mega suffix, the SAME image the league web app already shows
// (web/sprite.js serebiiMega). So we inject a getSpriteData override that renders each
// custom mega as its real Z-A mega art, front and back, never a broken icon and never
// the base form. See computeMegaSprites.
const CUSTOM_MEGAS = (() => { try { return require('../showdown-config/custom-megas.js'); } catch { return { Pokedex: {} }; } })();
const spriteToID = (s) => ('' + (s || '')).toLowerCase().replace(/[^a-z0-9]+/g, '');

// Teach the CLIENT dex the league's custom megas at runtime. The client loads species
// (pokedex.js) and items (items.js) from the official CDN, which has no custom megas,
// so the teambuilder can't render "Mega Floette" et al. We Object.assign the custom
// species + mega stones (from custom-megas.js, the same data merged into the SERVER
// dex) into the client's global tables once they exist, then drop the client Dex's
// memoised lookups so the new entries resolve. Format is identical to the client's own
// pokedex/items entries (verified), so they render with real stats/sprite.
const CUSTOM_DEX = (() => {
  const pd = JSON.stringify(CUSTOM_MEGAS.Pokedex || {});
  const it = JSON.stringify(CUSTOM_MEGAS.Items || {});
  return `<script>
(function () {
  var PD = ${pd}, IT = ${it};
  function merge() {
    if (!window.BattlePokedex) return false;
    for (var k in PD) if (!window.BattlePokedex[k]) window.BattlePokedex[k] = PD[k];
    if (window.BattleItems) { for (var j in IT) if (!window.BattleItems[j]) window.BattleItems[j] = IT[j]; }
    // Drop the Dex's memoised species/items so the new entries resolve. The cache is a
    // single object with capitalised sub-tables (Dex.cache.Species / .Items); reset those
    // sub-tables, NOT Dex.cache itself (get() reads this.cache.Species[id] and would throw).
    // gen9 formats use the base Dex (Dex.mod('gen9') returns it), so this one covers them.
    try { var D = window.Dex; if (D && D.cache) { D.cache.Species = {}; D.cache.Items = {}; } } catch (e) {}
    return true;
  }
  var iv = setInterval(function () { if (merge()) clearInterval(iv); }, 100);
  setTimeout(function () { clearInterval(iv); }, 30000);
})();
</script>`;
})();
let MEGA_SPRITE_FIX = '';
async function computeMegaSprites() {
  // The Legends: Z-A mega artwork on Serebii, keyed by 3-digit national dex + a mega
  // suffix (matches web/sprite.js serebiiMega, so the battle shows the SAME image the
  // rest of the league app does). 250x250 art, rendered at a battle-field size below.
  const SUFFIX = { 'Mega-X': '-mx', 'Mega-Y': '-my', 'Mega-Z': '-mz', 'Mega': '-m' };
  const serebii = (dex, forme) =>
    `https://www.serebii.net/legendsz-a/pokemon/${String(dex).padStart(3, '0')}${SUFFIX[forme] || '-m'}.png`;
  const head = async (u) => { try { return (await fetch(u, { method: 'HEAD' })).ok; } catch { return false; } };

  const map = {};
  await Promise.all(Object.values(CUSTOM_MEGAS.Pokedex || {}).map(async (sp) => {
    // Key by the mega's toID (lowercase, punctuation stripped: "raichumegay"), NOT the
    // dashed spriteid. The client's computed spriteid is "raichu-megay" only when the
    // custom species is already merged into its dex; if the sprite is resolved before
    // that merge lands it collapses to "raichumegay". Keying (and looking up) by toID
    // matches both, so the override can never silently miss on a timing race.
    const key = spriteToID(sp.name);                                 // e.g. raichumegay
    const slug = spriteToID(sp.baseSpecies) + '-' + spriteToID(sp.forme); // Showdown sprite slug
    // The Z-A mega art first (the correct mega image); then a Showdown mega sprite if one
    // exists. NEVER the base form: a mega must render as its mega, not its base.
    const candidates = [
      serebii(sp.num, sp.forme),
      `https://play.pokemonshowdown.com/sprites/ani/${slug}.gif`,
      `https://play.pokemonshowdown.com/sprites/gen5/${slug}.png`,
    ];
    for (const url of candidates) { if (await head(url)) { map[key] = { url }; return; } }
  }));

  const n = Object.keys(map).length;
  // Patch the ModdedDex PROTOTYPE once (covers Dex and every modded dex, incl. the
  // champions mod battles use). getSpriteData is a prototype method; wrap it so a custom
  // mega's sprite (either facing) is the Z-A mega artwork, keeping the gen/cry/shiny
  // fields the original computed and just overriding the image. 120x120 scales the 250px
  // art down to a battle-field sprite; the <img> the scene builds scales it for us. The
  // lookup keys off toID(speciesForme), so it matches whether or not the merge has landed.
  MEGA_SPRITE_FIX = n === 0 ? '' : `<script>
(function(){var MS=${JSON.stringify(map)};function ID(s){return(''+(s||'')).toLowerCase().replace(/[^a-z0-9]/g,'');}function patch(){var D=window.Dex;if(!D)return false;var pr=Object.getPrototypeOf(D);if(!pr||typeof pr.getSpriteData!=='function')return false;if(pr.__megaSpriteFix)return true;var orig=pr.getSpriteData;pr.getSpriteData=function(pokemon,isFront,options){try{var forme=(window.Pokemon&&pokemon instanceof window.Pokemon)?pokemon.getSpeciesForme():pokemon;var m=MS[ID(forme)];if(m){var data=orig.call(this,pokemon,isFront,options);data.url=m.url;data.w=120;data.h=120;data.pixelated=false;data.isFrontSprite=!!isFront;return data;}}catch(e){}return orig.call(this,pokemon,isFront,options);};pr.__megaSpriteFix=true;return true;}var iv=setInterval(function(){if(patch())clearInterval(iv);},150);setTimeout(function(){clearInterval(iv);},30000);})();
</script>`;
  console.log(`[serve-client] custom-mega sprite fix: ${n} mega(s) using Legends Z-A mega artwork`);
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
    html = html.replace(/<script[^>]*\bsrc=/i, CAPTURE_HEAD + '\n$&') + AUTOLOGIN + '\n' + LOCK_IDENTITY + '\n' + CUSTOM_DEX + '\n' + MEGA_SPRITE_FIX + '\n' + AUTO_TERA + '\n' + FORMAT_FILTER + '\n' + ROSTER_RESTRICT + '\n' + FOCUS_TEAMBUILDER + '\n';
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

// Probe the CDN for each custom mega's best sprite, then drop the cached shell so the
// next request re-injects the patch with the resolved map. Best-effort and off the hot
// path, the server starts serving immediately.
computeMegaSprites()
  .catch((e) => console.warn('[serve-client] mega sprite fix failed:', e.message))
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

  // Same-origin proxy for the coach's draft roster, so the auto-Tera patch (see
  // AUTO_TERA) can fetch it without a cross-origin call to the .NET API. Public
  // data (the same picks the anonymous team page shows), server-to-server here.
  if (url === '/zst-roster') {
    const name = new URL(req.url, 'http://x').searchParams.get('name') || '';
    fetch(`${API_BASE}/api/showdown/roster/${encodeURIComponent(name)}`)
      .then((r) => r.text())
      .then((body) => {
        res.writeHead(200, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-cache' });
        res.end(body);
      })
      .catch(() => { res.writeHead(200, { 'content-type': 'application/json' }); res.end('{"found":false}'); });
    return;
  }

  // Same-origin proxy for the SEASON roster (every mon owned by any coach), for the
  // Scrims picker restriction. Same public data, server-to-server here.
  if (url === '/zst-season-roster') {
    fetch(`${API_BASE}/api/showdown/season-roster`)
      .then((r) => r.text())
      .then((body) => {
        res.writeHead(200, { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-cache' });
        res.end(body);
      })
      .catch(() => { res.writeHead(200, { 'content-type': 'application/json' }); res.end('{"mons":[]}'); });
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
