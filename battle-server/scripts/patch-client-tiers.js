'use strict';

// Rewrites the built Showdown client's tier data so the teambuilder shows OUR
// league tiers, Banned (X) / S / A / B / C / Bad (Z), from data/tiers.json,
// instead of Showdown's OU/Uber/etc.
//
// The teambuilder reads a mon's tier from BattleTeambuilderTable.overrideTier
// (the tag shown + searched) and groups the browse list from .tiers (a flat
// array of ["header", label] markers followed by species ids). We rewrite both.
//
// UNRANKED = ILLEGAL: a mon with no tier in tiers.json is left OUT of .tiers
// entirely. The teambuilder marks every dex mon that isn't in the browse list as
// Illegal and drops it into the greyed "Illegal results" section (see
// battle-dex-search.ts getResults: any id not in baseResults → illegalReasons),
// so unranked mons are non-selectable rather than a normal browsable tier.
//
// Re-run after every `node build` of the client (the build regenerates these
// files). Idempotent, headers are dropped and regrouped each run.

const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const tiers = require(path.join(ROOT, 'data', 'tiers.json')); // { speciesid: "S"|"A"|"B"|"C"|"X"|"Z" }
const tablePath = path.join(ROOT, 'client', 'play.pokemonshowdown.com', 'data', 'teambuilder-tables.js');
const { BattleTeambuilderTable: T } = require(tablePath);

// Tier order for the browse list, and the header label each group gets. The
// per-mon tag stays the short code (X/S/A/B/C/Z) so it's easy to search. There is
// no Unranked group: an untiered mon is dropped from the list and the teambuilder
// then treats it as Illegal (see the header comment).
const ORDER = ['S', 'A', 'B', 'C', 'X', 'Z'];
const HEADER = { X: 'Banned (X)', S: 'S', A: 'A', B: 'B', C: 'C', Z: 'Bad (Z)' };

function patchTable(tbl) {
  if (!tbl || !Array.isArray(tbl.tiers)) return null;

  // Species ids are the plain strings in .tiers; arrays are ["header", ...].
  const ids = tbl.tiers.filter((e) => typeof e === 'string');

  const groups = Object.fromEntries(ORDER.map((t) => [t, []]));
  const override = { ...tbl.overrideTier };
  const seen = new Set();
  let illegal = 0;
  for (const id of ids) {
    const t = tiers[id];
    if (!t) {
      // Untiered → leave OUT of the browse list so the teambuilder marks it Illegal.
      // Drop any stale 'Unranked' tag a previous run of this script left behind.
      if (override[id] === 'Unranked') delete override[id];
      illegal++;
      continue;
    }
    groups[t].push(id);
    override[id] = t;
    seen.add(id);
  }

  // Add tiered mons the client's browse list didn't already carry — the league's
  // CUSTOM megas (Floette-Mega etc.) live only in overrideTier as Illegal, never in
  // .tiers, so they'd never show. The client dex learns their species at runtime (see
  // CUSTOM_DEX in serve-client.js), so listing them here makes them browsable/pickable.
  let added = 0;
  for (const id of Object.keys(tiers)) {
    if (seen.has(id) || !ORDER.includes(tiers[id])) continue;
    groups[tiers[id]].push(id);
    override[id] = tiers[id];
    added++;
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
  // the whole set, nothing gets cut. Safe: only our format uses these tables now.
  tbl.formatSlices = {};

  return { ...Object.fromEntries(ORDER.map((t) => [t, groups[t].length])), Illegal: illegal, Added: added };
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
// doubles the file, that turned a ~2 MB file into 18 MB.)
const LS = String.fromCharCode(0x2028); // line separator, legal in JSON, not in a JS string literal
const PS = String.fromCharCode(0x2029); // paragraph separator, same
const json = JSON.stringify(T)
  .split('\\').join('\\\\')
  .split("'").join("\\'")
  .split(LS).join('\\u2028')
  .split(PS).join('\\u2029');
fs.writeFileSync(tablePath, "exports.BattleTeambuilderTable = JSON.parse('" + json + "');\n");

// Make our tier codes typeable in the teambuilder's Pokémon search: add each as
// a "tier" token to the (sorted) search index, so typing S / A / X / etc. offers
// a tier filter just like typing OU/Uber does. Idempotent.
//
// search-index.js exports FOUR things: BattleSearchIndex plus the parallel
// BattleSearchIndexOffset (per-entry typeahead metadata) and BattleSearchCountIndex
// / BattleArticleTitles. TWO invariants must hold or the Pokémon search breaks:
//
//  1. Re-emit ALL four exports. (An earlier version emitted only BattleSearchIndex,
//     so the offset table vanished and the search threw "BattleSearchIndexOffset is
//     not defined" on every keystroke.) We keep each offset paired with its entry.
//
//  2. ALIAS entries ([aliasid, type, originalIndex, matchStart], length > 2) hold a
//     POSITIONAL pointer (originalIndex) into BattleSearchIndex, and the row the
//     search DISPLAYS is BattleSearchIndex[originalIndex] (see battle-dex-search.ts).
//     Inserting our tier tokens shifts positions, so every alias pointer MUST be
//     remapped to its target's NEW index, or aliases resolve to the wrong row: typing
//     "dark" showed the item "darkmemory" (and Regenerator, G-Max moves, Dark Pulse…)
//     as 0-stat "?" mons in the Pokémon list. We remap by OBJECT IDENTITY: capture
//     each alias's target row before re-sorting, then look up its new position. No
//     coordinate arithmetic. (Assumes a consistent input, a freshly built index or
//     one this patch produced; the old buggy output was one-time repaired to pristine.)
const idxPath = path.join(ROOT, 'client', 'play.pokemonshowdown.com', 'data', 'search-index.js');
const searchMod = require(idxPath);
const idx = searchMod.BattleSearchIndex;
const off = searchMod.BattleSearchIndexOffset || [];
const TOKENS = ['s', 'a', 'b', 'c', 'x', 'z'];

// Capture each alias's target row object while the index is still self-consistent.
const targetOf = new Map();
for (const e of idx) if (e.length > 2 && typeof e[2] === 'number') targetOf.set(e, idx[e[2]]);

let pairs = idx.map((e, i) => ({ e, o: off[i] != null ? off[i] : '' }));
pairs = pairs.filter((p) => !(p.e[1] === 'tier' && TOKENS.includes(p.e[0])));
// A tier token is one word, so its offset string is all zeros (one per char).
for (const t of TOKENS) pairs.push({ e: [t, 'tier'], o: '0'.repeat(t.length) });
pairs.sort((a, b) => (a.e[0] < b.e[0] ? -1 : a.e[0] > b.e[0] ? 1 : 0)); // index stays sorted by id

// Remap each alias pointer to its captured target's NEW position.
const newPos = new Map();
pairs.forEach((p, i) => newPos.set(p.e, i));
for (const p of pairs) {
  const t = targetOf.get(p.e);
  if (t) { const ni = newPos.get(t); if (ni != null) p.e[2] = ni; }
}

let out = 'exports.BattleSearchIndex = ' + JSON.stringify(pairs.map((p) => p.e)) + ';\n';
out += 'exports.BattleSearchIndexOffset = ' + JSON.stringify(pairs.map((p) => p.o)) + ';\n';
if (searchMod.BattleSearchCountIndex) out += 'exports.BattleSearchCountIndex = ' + JSON.stringify(searchMod.BattleSearchCountIndex) + ';\n';
if (searchMod.BattleArticleTitles) out += 'exports.BattleArticleTitles = ' + JSON.stringify(searchMod.BattleArticleTitles) + ';\n';
fs.writeFileSync(idxPath, out);

for (const [k, counts] of Object.entries(report)) {
  if (counts) console.log(k.padEnd(18), JSON.stringify(counts));
}
console.log('patched', tablePath, '(' + (fs.statSync(tablePath).size / 1048576).toFixed(1) + ' MB)');
