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
const ORDER = ['X', 'S', 'A', 'B', 'C', 'Z', 'Unranked'];
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

  return Object.fromEntries(ORDER.map((t) => [t, groups[t].length]));
}

// Patch the top-level table (gen9 singles) plus the gen9 doubles/natdex tables,
// so whichever one the client resolves our custom format to shows our tiers.
const report = {};
report['(top-level)'] = patchTable(T);
for (const k of ['gen9doubles', 'gen9natdex', 'gen9natdexdoubles']) {
  if (T[k]) report[k] = patchTable(T[k]);
}

const out = 'exports.BattleTeambuilderTable = JSON.parse(' + JSON.stringify(JSON.stringify(T)) + ');\n';
fs.writeFileSync(tablePath, out);

for (const [k, counts] of Object.entries(report)) {
  if (counts) console.log(k.padEnd(18), JSON.stringify(counts));
}
console.log('patched', tablePath);
