'use strict';

// Which teambuilder species a coach's drafted roster should unlock, for restricting
// the ZST Season 4 species picker to just their team. Pure, so the browser injection
// (serve-client.js ROSTER_RESTRICT) and the Node tests run identical logic: the
// browser passes window.Dex.species.get as the resolver, the tests pass a stub.
//
// The league lists EVERY forme as ITSELF in the teambuilder tier list: a drafted mega
// is a browsable mega forme (Charizard-Mega-Y, S-tier), not base Charizard, and picking
// it auto-fills base + stone. Regionals (Rotom-Wash) and base mons are likewise their
// own id. So a pick's SLUG (sprite id, e.g. "charizard-megay") maps straight to its own
// species id. For a CUSTOM mega the CLIENT dex doesn't know (Floette-Mega is merged only
// into the SERVER dex), the slug's own id form ("floettemega") is already the right id.
// Returns a plain object used as a Set: { speciesid: true }.

function tbID(s) { return ('' + (s || '')).toLowerCase().replace(/[^a-z0-9]/g, ''); }

function safeGet(getSpecies, slug) { try { return getSpecies(slug); } catch (e) { return null; } }

function rosterSpeciesIds(mons, getSpecies) {
  const ids = {};
  const list = Array.isArray(mons) ? mons : [];
  for (const m of list) {
    const slug = (m && (m.slug || m.name)) || '';
    if (!slug) continue;
    const s = safeGet(getSpecies, slug);
    // Resolve through the dex when known (normalises the id); otherwise the slug's own
    // id form is already correct (a custom mega the client dex lacks).
    const id = (s && s.exists !== false) ? (s.id || s.name || slug) : slug;
    ids[tbID(id)] = true;
  }
  return ids;
}

// Filter one DexSearch result list (SearchRows: [type, id, ...]) down to just the
// coach's drafted mons, for the ZST picker. Keeps allowed pokemon rows and structural
// rows (headers, the sort button, html notices); drops undrafted pokemon AND the
// ability/move/type/tier/item filter chips (the coach should see only their team), then
// removes any header left with nothing under it. `allowed` is the { speciesid: true }
// set from rosterSpeciesIds. Returns a new array.
const FILTER_ROW_TYPES = { type: 1, ability: 1, move: 1, tier: 1, egggroup: 1, item: 1, category: 1, article: 1 };

function filterPokemonResults(rows, allowed) {
  if (!Array.isArray(rows) || !allowed) return rows;
  const kept = [];
  for (const r of rows) {
    const t = r && r[0];
    if (t === 'pokemon') { if (allowed[r[1]]) kept.push(r); continue; }
    if (FILTER_ROW_TYPES[t]) continue;
    kept.push(r);
  }
  const out = [];
  for (let j = 0; j < kept.length; j++) {
    if (kept[j][0] === 'header' && (j + 1 >= kept.length || kept[j + 1][0] === 'header')) continue;
    out.push(kept[j]);
  }
  return out;
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = { rosterSpeciesIds, filterPokemonResults, tbID };
}
// The browser injection (serve-client.js) inlines this SOURCE, so expose the same
// functions on window there, exactly as it runs in the Node tests.
if (typeof window !== 'undefined') {
  window.rosterSpeciesIds = rosterSpeciesIds;
  window.filterPokemonResults = filterPokemonResults;
  window.zstTbID = tbID;
}
