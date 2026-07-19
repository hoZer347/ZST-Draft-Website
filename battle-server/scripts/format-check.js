'use strict';

// Develop + verify the league format. Resolves lib/format.js through its guard
// (so a dropped/invalid rule fails loudly, not silently) and prints the exact
// rule table the sim will enforce. Run this after editing DRAFT_RULES.
//
//   node scripts/format-check.js

const { Dex } = require('pokemon-showdown');
const { DRAFT_FORMAT_ID, DRAFT_RULES, resolve } = require('../lib/format');

function main() {
  console.log(`format id: ${DRAFT_FORMAT_ID}\n`);

  const format = resolve(); // throws if the sim dropped or no-op'd any rule
  const table = Dex.formats.getRuleTable(format);

  console.log(`declared rules (${DRAFT_RULES.length}):`);
  for (const r of DRAFT_RULES) console.log(`  • ${r}`);

  const bans = [...table.keys()].filter((k) => k.startsWith('-'));
  const clauses = [...table.keys()].filter((k) => !/^[-+*!]/.test(k));
  console.log(`\nresolved rule table (${table.size} entries):`);
  console.log(`  clauses/limits: ${clauses.join(', ') || '(none)'}`);
  console.log(`  bans:           ${bans.join(', ') || '(none)'}`);

  console.log('\nOK — every declared rule applied.');
}

try { main(); }
catch (e) { console.error(`\nFORMAT INVALID: ${e.message}`); process.exit(1); }
