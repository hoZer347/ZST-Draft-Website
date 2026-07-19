'use strict';

// Rewrites the built Showdown client's tier data so the teambuilder shows OUR
// league tiers — Banned (X) / S / A / B / C / Bad (Z) / Unranked, from
// data/tiers.json — instead of Showdown's OU/Uber/etc.
//
// The teambuilder reads a mon's tier from BattleTeambuilderTable.overrideTier
// (the tag shown + searched) and groups the browse list from .tiers (a flat
// array of ["header", label] markers followed by species ids). We rewrite both.
//
// Re-run after every `node build` of the client (the build regenerates these
// files). Idempotent — headers are dropped and regrouped each run.

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const tiers = require(path.join(ROOT, 'data', 'tiers.json')); // { speciesid: "S"|"A"|"B"|"C"|"X"|"Z" }
const tablePath = path.join(ROOT, 'client', 'play.pokemonshowdown.com', 'data', 'teambuilder-tables.js');
const { BattleTeambuilderTable: T } = require(tablePath);

// Tier order for the browse list, and the header label each group gets. The
// per-mon tag stays the short code (X/S/A/B/C/Z/Unranked) so it's easy to search.
const ORDER = ['S', 'A', 'B', 'C', 'X', 'Z', 'Unranked'];
const HEADER = { X: 'Banned (X)', S: 'S', A: 'A', B: 'B', C: 'C', Z: 'Bad (Z)', Unranked: 'Unranked' };

function patchTable(tbl) {
  if (!tbl || !Array.isArray(tbl.tiers)) return null;

  // Species ids are the plain strings in .tiers; arrays are ["header", ...].
  const ids = tbl.tiers.filter((e) => typeof e === 'string');

  const groups = Object.fromEntries(ORDER.map((t) => [t, []]));
  const override = { ...tbl.overrideTier };
  for (const id of ids) {
    const t = tiers[id] || 'Unranked';
    groups[t].push(id);
    override[id] = t;
  }

  const newTiers = [];
  for (const t of ORDER) {
    if (!groups[t].length) continue;
    newTiers.push(['header', HEADER[t]]);
    for (const id of groups[t]) newTiers.push(id);
  }
  tbl.tiers = newTiers;
  tbl.overrideTier = override;
  // formatSlices map standard tier boundaries (Uber/OU/…) onto the ORIGINAL tier
  // order; after we reorder, they'd slice off the top of our list (S/A, which are
  // all megas). Clear them so `tierSet.slice(slices.X)` becomes slice(undefined) =
  // the whole set — nothing gets cut. Safe: only our format uses these tables now.
  tbl.formatSlices = {};

  return Object.fromEntries(ORDER.map((t) => [t, groups[t].length]));
}

// Patch the top-level table (gen9 singles) plus the gen9 doubles/natdex tables,
// so whichever one the client resolves our custom format to shows our tiers.
const report = {};
report['(top-level)'] = patchTable(T);
for (const k of ['gen9doubles', 'gen9natdex', 'gen9natdexdoubles']) {
  if (T[k]) report[k] = patchTable(T[k]);
}

// Write it back in Showdown's own compact form: JSON.parse('<json>') with a
// SINGLE-quoted string, so the JSON's double-quotes don't need escaping.
// (Double-stringifying with JSON.stringify escapes every " as \" and roughly
// doubles the file — that turned a ~2 MB file into 18 MB.)
const LS = String.fromCharCode(0x2028); // line separator — legal in JSON, not in a JS string literal
const PS = String.fromCharCode(0x2029); // paragraph separator — same
const json = JSON.stringify(T)
  .split('\\').join('\\\\')
  .split("'").join("\\'")
  .split(LS).join('\\u2028')
  .split(PS).join('\\u2029');
fs.writeFileSync(tablePath, "exports.BattleTeambuilderTable = JSON.parse('" + json + "');\n");

// Make our tier codes typeable in the teambuilder's Pokémon search: add each as
// a "tier" token to the (sorted) search index, so typing S / A / X / etc. offers
// a tier filter just like typing OU/Uber does. Idempotent.
const idxPath = path.join(ROOT, 'client', 'play.pokemonshowdown.com', 'data', 'search-index.js');
const { BattleSearchIndex: idx } = require(idxPath);
const TOKENS = ['s', 'a', 'b', 'c', 'x', 'z'];
const cleaned = idx.filter((e) => !(e[1] === 'tier' && TOKENS.includes(e[0])));
for (const t of TOKENS) cleaned.push([t, 'tier']);
cleaned.sort((a, b) => (a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0)); // index is sorted by id
fs.writeFileSync(idxPath, 'exports.BattleSearchIndex = ' + JSON.stringify(cleaned) + ';\n');

for (const [k, counts] of Object.entries(report)) {
  if (counts) console.log(k.padEnd(18), JSON.stringify(counts));
}
console.log('patched', tablePath, '(' + (fs.statSync(tablePath).size / 1048576).toFixed(1) + ' MB)');
