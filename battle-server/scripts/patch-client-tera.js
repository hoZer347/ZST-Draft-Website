'use strict';

// Puts the teambuilder's Tera Type control back for the league's formats.
//
// The classic teambuilder decides a format is a Champions format by string-matching
// "champions" in its id, and Champions has no Terastallization, so it hides the Tera
// Type row, refuses to read the <select>, and deletes set.teraType on every edit. Our
// formats ARE champions formats (they need its dex and Stat Points), but the league
// keeps tera as a C-tier-only mechanic: a C-tier pick is locked to the Tera type it
// was drafted with, and the format's validateTeam rejects the team if it doesn't match
// (showdown-config/custom-formats.js). With the row hidden, no coach could ever set one.
//
// So we re-enable exactly the tera sites, by rewriting the built client, the same
// approach scripts/patch-client-tiers.js takes with the data files. Nothing else about
// Champions mode is touched: Stat Points, the disabled Level field and the Champions
// dex all still key off the same `isChampions` flag.
//
// Re-run after every `node build` of the client (the build overwrites js/ from src/).
// Idempotent: each rewrite is a no-op once the guard token is present.
//
//   node scripts/patch-client-tera.js

const fs = require('fs');
const path = require('path');

const FILE = path.join(__dirname, '..', 'client', 'play.pokemonshowdown.com', 'js', 'oldclient', 'client-teambuilder.js');

// "zst" and "scrims" are the two tokens that tell the league's format ids apart from
// every stock champions format (see showdown-config/custom-formats.js).
const LEAGUE = `(this.curTeam.format.includes('zst') || this.curTeam.format.includes('scrims'))`;

// [what, from, to]. Every `from` is a tera gate and nothing else; `to` adds "…or it's
// a league format" so the row survives Champions mode.
const REWRITES = [
  // The details-form Tera Type <select>, and the two "Tera Type" summary cells that
  // render next to it (one in the set list, one after an edit).
  ['tera row + summary cells',
    `if (this.curTeam.gen === 9 && !isChampions) {`,
    `if (this.curTeam.gen === 9 && (!isChampions || ${LEAGUE})) {`],
  // detailsChange: read the <select> back into the set instead of deleting teraType.
  ['tera type is saved on edit',
    `if (!isChampions && Dex.types.isName(teraType)) {`,
    `if ((!isChampions || ${LEAGUE}) && Dex.types.isName(teraType)) {`],
  // Importing a Smogon/box sample set: keep the sample's tera type.
  ['sample sets keep their tera type',
    `if (this.curSet.gen === 9 && !this.curTeam.format.includes('champions')) {`,
    `if (this.curSet.gen === 9 && (!this.curTeam.format.includes('champions') || ${LEAGUE})) {`],
];

let text = fs.readFileSync(FILE, 'utf8');
let changed = 0;

for (const [what, from, to] of REWRITES) {
  const already = text.split(to).length - 1;
  const hits = text.split(from).length - 1;
  if (!hits && !already) {
    console.error(`FAILED: could not find the site for "${what}". The client build changed; ` +
      `re-check the tera gates in src/oldclient/client-teambuilder.js.`);
    process.exit(1);
  }
  if (hits) {
    text = text.split(from).join(to);
    changed += hits;
  }
  console.log(`${what.padEnd(34)} ${hits ? `patched ${hits} site(s)` : `already patched (${already})`}`);
}

if (changed) fs.writeFileSync(FILE, text);
console.log(changed ? `patched ${FILE}` : 'nothing to do');
