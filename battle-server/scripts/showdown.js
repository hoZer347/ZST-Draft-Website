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
// the game UI — the client above talks to it.)

const { spawn } = require('child_process');
const fs = require('fs');
const path = require('path');

const psDir = path.dirname(require.resolve('pokemon-showdown/package.json'));
const port = String(process.env.PORT || 8787);

// Local-dev config: allow same-IP matchmaking. Showdown's ladder refuses to
// pair two searchers from the same IP (anti-self-laddering), so on one machine
// you can never "search" for a battle against your own second tab — both are
// 127.0.0.1. `noipchecks` bypasses that guard. We patch the bundled config here,
// idempotently, so the setting survives an `npm install` that resets
// node_modules (rather than hand-editing a file inside node_modules).
function ensureLocalDevConfig() {
  const cfgPath = path.join(psDir, 'config', 'config.js');
  let cfg;
  try { cfg = fs.readFileSync(cfgPath, 'utf8'); }
  catch { return; } // no config yet — server will create/complain on its own
  if (/exports\.noipchecks\s*=\s*true/.test(cfg)) return; // already on
  const patched = /exports\.noipchecks\s*=\s*false\s*;/.test(cfg)
    ? cfg.replace(/exports\.noipchecks\s*=\s*false\s*;/, 'exports.noipchecks = true;')
    : cfg + '\n// [draft-league] local dev: allow same-IP matchmaking\nexports.noipchecks = true;\n';
  fs.writeFileSync(cfgPath, patched);
  console.log('[showdown] enabled noipchecks for local same-IP matchmaking');
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

// The bundled server has optional chat plugins (youtube, mafia, seasons, …) that
// persist JSON into config/chat-plugins/. That dir doesn't ship, so they log
// noisy (non-fatal) ENOENT "CRASH" lines on write. Create it so they stay quiet.
try { fs.mkdirSync(path.join(psDir, 'config', 'chat-plugins'), { recursive: true }); } catch { /* best effort */ }

const child = spawn(process.execPath, ['pokemon-showdown', 'start', port, '--skip-build'], {
  cwd: psDir,
  stdio: 'inherit',
});

child.on('exit', (code) => process.exit(code ?? 0));
for (const sig of ['SIGINT', 'SIGTERM']) {
  process.on(sig, () => child.kill(sig));
}
