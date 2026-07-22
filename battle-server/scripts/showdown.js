'use strict';

// Runs a REAL Pokémon Showdown server (the full app: chat rooms, ladder,
// teambuilder, the works) using the bundled `pokemon-showdown` package. The
// server resolves its config/data relative to the package dir, so we launch it
// with that as the cwd while keeping this launcher in our own repo.
//
//   node scripts/showdown.js          # port 8787
//   PORT=8000 node scripts/showdown.js
//
// Connect the official web client to it at:
//   https://play.pokemonshowdown.com/~~localhost:<PORT>/
//
// (The server's own http://localhost:<PORT> is just the backend endpoint, not
// the game UI, the client above talks to it.)

const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

const psDir = path.dirname(require.resolve('pokemon-showdown/package.json'));
const port = String(process.env.PORT || 8787);

// Local-dev config: allow same-IP matchmaking. Showdown's ladder refuses to
// pair two searchers from the same IP (anti-self-laddering), so on one machine
// you can never "search" for a battle against your own second tab, both are
// 127.0.0.1. `noipchecks` bypasses that guard. We patch the bundled config here,
// idempotently, so the setting survives an `npm install` that resets
// node_modules (rather than hand-editing a file inside node_modules).
function ensureLocalDevConfig() {
  const cfgPath = path.join(psDir, 'config', 'config.js');
  let cfg;
  try { cfg = fs.readFileSync(cfgPath, 'utf8'); }
  catch { return; } // no config yet, server will create/complain on its own

  // Idempotently turn a boolean Config setting on. Flips an existing
  // `exports.x = false;`, or appends `exports.x = true;` if it's absent.
  function ensureOn(name, note) {
    if (new RegExp(`exports\\.${name}\\s*=\\s*true`).test(cfg)) return false;
    const off = new RegExp(`exports\\.${name}\\s*=\\s*false\\s*;`);
    cfg = off.test(cfg)
      ? cfg.replace(off, `exports.${name} = true;`)
      : cfg + `\n// [draft-league] ${note}\nexports.${name} = true;\n`;
    return true;
  }

  // noipchecks: allow same-IP matchmaking (both tabs are 127.0.0.1 on one box).
  // noguestsecurity: accept a chosen name without a login-server assertion, so the
  // league app can log a coach in as their Discord username (the teambuilder
  // auto-login sends /trn <name>,0, with an empty token). Non-trusted userids
  // only, trusted/admin names still require a real token (see users.js
  // validateToken).
  const a = ensureOn('noipchecks', 'local dev: allow same-IP matchmaking');
  const b = ensureOn('noguestsecurity', 'accept Discord-name logins without a loginserver');
  // nothrottle: lift the unregistered-rename cap (default 3 per 2 min). The
  // teambuilder auto-login resends /trn until the rename lands, because a coach
  // reopening the tab often collides with their just-closed session that the
  // server still has registered (users.js handleRename refuses the merge with
  // |nametaken| until that stale connection drops). Without nothrottle those
  // retries hit the rate limit and the coach is left a Guest, not shown joined.
  const c = ensureOn('nothrottle', 'dev: no rename/chat throttle so /trn auto-login can retry until a stale same-name session clears');

  // Force a SINGLE worker. Showdown defaults to one worker per CPU core (~8 on
  // this box); incoming connections round-robin across them, and on a heavily
  // loaded dev machine some workers stall, that connection then never gets its
  // |challstr|/|formats|, so the client's format list, match-queue button, and
  // "Choose name" button hang on "Loading". One worker is plenty for a league
  // and behaves deterministically. Idempotent.
  let workersChanged = false;
  if (/exports\.workers\s*=\s*\d+/.test(cfg)) {
    if (!/exports\.workers\s*=\s*1\b/.test(cfg)) {
      cfg = cfg.replace(/exports\.workers\s*=\s*\d+\s*;?/, 'exports.workers = 1;');
      workersChanged = true;
    }
  } else {
    cfg += `\n// [draft-league] single worker: reliable on a loaded dev box\nexports.workers = 1;\n`;
    workersChanged = true;
  }

  if (a || b || c || workersChanged) {
    fs.writeFileSync(cfgPath, cfg);
    console.log('[showdown] patched dev config (noipchecks, noguestsecurity, nothrottle, workers=1)');
  }
}

ensureLocalDevConfig();

// Install our custom formats into the bundled server. Showdown loads them from
// dist/config/custom-formats.js (the --skip-build server runs from dist) and
// merges them into the format list the teambuilder + ladder show. We copy our
// repo file over it on every start, so this repo stays the source of truth and
// the format survives an `npm install` that resets node_modules.
function installCustomFormats() {
  const src = path.join(__dirname, '..', 'showdown-config', 'custom-formats.js');
  const dest = path.join(psDir, 'dist', 'config', 'custom-formats.js');
  try {
    fs.copyFileSync(src, dest);
    console.log('[showdown] installed custom formats → dist/config/custom-formats.js');
  } catch (e) {
    console.warn('[showdown] could not install custom formats:', e.message);
  }
}

installCustomFormats();

// Install our auto-report chat plugin. On every finished battle it POSTs the log
// to the .NET league server, which records the scheduled match. The bundled
// server only loads plugins from dist/server/chat-plugins/, so (like the custom
// formats) we copy our repo file in on every start.
function installReportPlugin() {
  const src = path.join(__dirname, '..', 'showdown-config', 'chat-plugins', 'draft-report.js');
  const dest = path.join(psDir, 'dist', 'server', 'chat-plugins', 'draft-report.js');
  try {
    fs.copyFileSync(src, dest);
    console.log('[showdown] installed draft-report plugin → dist/server/chat-plugins/');
  } catch (e) {
    console.warn('[showdown] could not install draft-report plugin:', e.message);
  }
}

installReportPlugin();

// Merge the league's custom "megas" (ChampionsRegMA content) into the bundled
// engine's dex. They're PLAIN gen-9 mega data, species + string-format stones +
// a formats-data row (see showdown-config/custom-megas.js), so the stock engine's
// own canMegaEvo evolves them with NO sim/ruleset/format changes (an earlier
// attempt to port the teambuilder's whole custom fork broke 130+ formats; this
// additive approach doesn't). We copy the data file in and append an idempotent
// Object.assign to the three compiled data modules, re-merging after an `npm
// install` resets node_modules, exactly like the custom formats above.
//
// NOTE the target is `module.exports`, not `exports`: the data modules are esbuild
// output ending in `module.exports = __toCommonJS(...)`, so the CJS `exports` we'd
// otherwise mutate is stale and ignored.
function installCustomMegas() {
  const dataDir = path.join(psDir, 'dist', 'data');
  const src = path.join(__dirname, '..', 'showdown-config', 'custom-megas.js');
  const MARK = '[draft-league] custom megas';
  const merges = [
    ['pokedex.js', 'Pokedex'],
    ['items.js', 'Items'],
    ['formats-data.js', 'FormatsData'],
  ];
  try {
    fs.copyFileSync(src, path.join(dataDir, 'custom-megas.js'));
    let did = false;
    for (const [file, key] of merges) {
      const p = path.join(dataDir, file);
      const txt = fs.readFileSync(p, 'utf8');
      if (txt.includes(MARK)) continue; // already merged (until an npm install resets node_modules)
      fs.writeFileSync(p, txt +
        `\n// ${MARK} (additive; module.exports is the esbuild __toCommonJS object)\n` +
        `try { Object.assign(module.exports.${key}, require('./custom-megas.js').${key}); } ` +
        `catch (e) { console.error('[draft-league] mega merge failed in ${file}:', e.message); }\n`);
      did = true;
    }
    console.log(did ? '[showdown] merged custom megas into the dex' : '[showdown] custom megas already merged');
  } catch (e) {
    console.warn('[showdown] could not merge custom megas:', e.message);
  }
}

installCustomMegas();

// The bundled server has optional chat plugins (youtube, mafia, seasons, …) that
// persist JSON into config/chat-plugins/. That dir doesn't ship, so they log
// noisy (non-fatal) ENOENT "CRASH" lines on write. Create it so they stay quiet.
try { fs.mkdirSync(path.join(psDir, 'config', 'chat-plugins'), { recursive: true }); } catch { /* best effort */ }

// Showdown writes each format's ladder to config/ladders/<formatid>.tsv when a
// rated battle ends. That dir doesn't ship either, and unlike the read path (which
// swallows ENOENT into an empty ladder) the WRITE crashes the whole worker with
// ENOENT, taking the server down after the first laddered ZST game. Create it.
try { fs.mkdirSync(path.join(psDir, 'config', 'ladders'), { recursive: true }); } catch { /* best effort */ }

// Where the report plugin sends finished battles, and the shared-secret file the
// .NET server writes. Passed to the server process so the plugin (running inside
// it) can read them.
const reportEnv = {
  DRAFT_REPORT_URL: process.env.DRAFT_REPORT_URL || 'http://localhost:5211/api/showdown/report',
  DRAFT_REPORT_SECRET_FILE: path.resolve(__dirname, '..', '.report-secret'),
  // Where the ZST Season 4 team validator (custom-formats.js) fetches a coach's
  // drafted roster to enforce the draft. Inherited by the validator worker.
  DRAFT_ROSTER_URL: process.env.DRAFT_ROSTER_URL || 'http://localhost:5211/api/showdown/roster',
};

const child = spawn(process.execPath, ['pokemon-showdown', 'start', port, '--skip-build'], {
  cwd: psDir,
  stdio: 'inherit',
  env: { ...process.env, ...reportEnv },
});

child.on('exit', (code) => process.exit(code ?? 0));
for (const sig of ['SIGINT', 'SIGTERM']) {
  process.on(sig, () => child.kill(sig));
}
