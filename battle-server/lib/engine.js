'use strict';

// Which Pokemon Showdown build the battle server (and anything that needs the same
// dex) runs on.
//
// The league's formats use the `champions` mod, which the npm `pokemon-showdown`
// package predates: its data/mods has no champions or championsregma directory, so
// "[Gen 9 Champions] NatDex ZST Season 4" simply would not load there. The
// Champions-capable build is the Showdown checkout the CLIENT's build tooling clones
// and compiles into client/caches/pokemon-showdown (see client/build-tools/build-indexes,
// which git-pulls and `npm run build`s it to generate the teambuilder data). It is a
// full server: dist/{sim,server,config,data,lib}, its own config/config.js, and its
// deps resolve up into battle-server/node_modules, so we launch it as-is.
//
// Both directories are gitignored build artifacts, so a fresh clone needs `npm install`
// AND a client build before the battle server can start. SHOWDOWN_DIR overrides the
// choice if you want to pin a different checkout.
//
// Note the client rebuild runs `git pull` in that checkout: stop the battle server
// before rebuilding the client, or Windows will refuse to replace files in use.

const fs = require('fs');
const path = require('path');

const CHAMPIONS_MOD = path.join('dist', 'data', 'mods', 'champions');

/** Does this Showdown checkout carry the Champions mod the league's formats need? */
function hasChampions(dir) {
  return !!dir && fs.existsSync(path.join(dir, CHAMPIONS_MOD));
}

/** The Champions-capable checkout the client's build tooling maintains. */
function cacheDir() {
  return path.resolve(__dirname, '..', 'client', 'caches', 'pokemon-showdown');
}

/** The npm package, kept as a fallback for the scripts that only need vanilla gen 9. */
function packageDir() {
  try { return path.dirname(require.resolve('pokemon-showdown/package.json')); }
  catch { return null; }
}

/**
 * Copy showdown-config/custom-formats.js into the engine, which loads the league's
 * formats from dist/config/custom-formats.js. The repo file stays the source of truth,
 * and every consumer gets the formats, not just a server started through
 * scripts/showdown.js: the season simulator and the tests resolve league format ids
 * straight off the Dex. Idempotent and content-compared, so repeat calls don't churn
 * the file (rewriting it does not affect an already-running server, which read it at
 * start). Best effort: a read-only engine dir shouldn't stop anything from booting.
 */
function installCustomFormats(dir) {
  const src = path.join(__dirname, '..', 'showdown-config', 'custom-formats.js');
  const dest = path.join(dir, 'dist', 'config', 'custom-formats.js');
  try {
    const want = fs.readFileSync(src, 'utf8');
    let have = null;
    try { have = fs.readFileSync(dest, 'utf8'); } catch { /* not installed yet */ }
    if (have === want) return false;
    fs.writeFileSync(dest, want);
    return true;
  } catch (e) {
    console.warn('[engine] could not install custom formats:', e.message);
    return false;
  }
}

/**
 * The engine directory to use. Prefers SHOWDOWN_DIR, then the Champions-capable
 * client cache, then the npm package. Throws if `requireChampions` is set and no
 * candidate has the mod, because the alternative is a server that starts fine and
 * then silently drops the league's formats. Syncs the league's custom formats in on
 * the way out, so a caller that just wants the Dex still finds them.
 */
function engineDir({ requireChampions = false } = {}) {
  const candidates = [process.env.SHOWDOWN_DIR, cacheDir(), packageDir()].filter(Boolean);
  const champions = candidates.find(hasChampions);
  if (champions) { installCustomFormats(champions); return champions; }
  if (requireChampions) {
    throw new Error(
      `No Pokemon Showdown build with the 'champions' mod was found (looked in: ${candidates.join(', ')}).\n` +
      `The league's formats need it. Build the client once (cd client && node build), which clones and ` +
      `compiles the checkout into client/caches/pokemon-showdown, or point SHOWDOWN_DIR at a build that has it.`
    );
  }
  const fallback = candidates.find((d) => fs.existsSync(d));
  if (!fallback) throw new Error('No Pokemon Showdown build found at all; run npm install.');
  return fallback;
}

/** require() a path inside the engine, e.g. requireEngine() or requireEngine('dist/sim/dex'). */
function requireEngine(rel, options) {
  const dir = engineDir(options);
  return require(rel ? path.join(dir, rel) : dir);
}

module.exports = { engineDir, requireEngine, hasChampions, installCustomFormats };
