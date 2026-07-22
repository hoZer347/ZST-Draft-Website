'use strict';

// Regenerate data/tiers.json from the league's Google Sheet, the single source of
// truth for tiers (the same sheet PokedexSync reads on the .NET side). The sheet
// carries EVERY pool row, including Z/X rows and the custom megas, whereas the .NET
// draft only imports draftable S/A/B/C. tiers.json (used by patch-client-tiers.js to
// colour + group the teambuilder) had gone stale and was missing the custom megas.
//
// Keys are Showdown species ids (toID of the sheet's "Raw (Showdown)" column); values
// are the tier letter. Only S/A/B/C/X/Z rows are kept.
//
//   node scripts/sync-tiers.js
// Re-run whenever the sheet's tiers change, then re-run patch-client-tiers.js.

const fs = require('fs');
const path = require('path');

// Same sheet as server/appsettings.json (SheetCsvUrl).
const SHEET_CSV = 'https://docs.google.com/spreadsheets/d/1JTOAxHBwtHT5bkAUGbFAz9SIk2TTi_aBb5RhxDDYDZw/export?format=csv';
const OUT = path.join(__dirname, '..', 'data', 'tiers.json');
const VALID = new Set(['S', 'A', 'B', 'C', 'X', 'Z']);

const toID = (s) => ('' + (s || '')).toLowerCase().replace(/[^a-z0-9]/g, '');

// Minimal RFC-4180-ish CSV splitter (handles quoted fields with commas/newlines).
function parseCsv(text) {
  const rows = [];
  let row = [], field = '', inQ = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (inQ) {
      if (c === '"') { if (text[i + 1] === '"') { field += '"'; i++; } else inQ = false; }
      else field += c;
    } else if (c === '"') inQ = true;
    else if (c === ',') { row.push(field); field = ''; }
    else if (c === '\r') { /* skip */ }
    else if (c === '\n') { row.push(field); rows.push(row); row = []; field = ''; }
    else field += c;
  }
  if (field.length || row.length) { row.push(field); rows.push(row); }
  return rows;
}

async function main() {
  const res = await fetch(SHEET_CSV, { redirect: 'follow' });
  if (!res.ok) throw new Error(`sheet fetch HTTP ${res.status}`);
  const rows = parseCsv(await res.text());
  if (!rows.length) throw new Error('empty sheet');

  const header = rows[0].map((h) => h.trim());
  const tierCol = header.indexOf('Tier');
  const spriteCol = header.indexOf('Raw (Showdown)');
  if (tierCol < 0 || spriteCol < 0) throw new Error(`missing Tier/Raw columns; header: ${header.join('|')}`);

  const tiers = {};
  const counts = {};
  for (let i = 1; i < rows.length; i++) {
    const tier = (rows[i][tierCol] || '').trim().toUpperCase();
    const id = toID(rows[i][spriteCol]);
    if (!id || !VALID.has(tier)) continue;
    tiers[id] = tier;                       // last row wins on a dup id
    counts[tier] = (counts[tier] || 0) + 1;
  }

  // Emit sorted by id for a stable, diff-friendly file.
  const sorted = {};
  for (const k of Object.keys(tiers).sort()) sorted[k] = tiers[k];
  fs.writeFileSync(OUT, JSON.stringify(sorted, null, 0).replace(/","/g, '",\n"').replace(/^\{/, '{\n').replace(/\}$/, '\n}') + '\n');
  console.log('wrote', OUT, '-', Object.keys(sorted).length, 'mons', JSON.stringify(counts));
}

main().catch((e) => { console.error('sync-tiers failed:', e.message); process.exit(1); });
