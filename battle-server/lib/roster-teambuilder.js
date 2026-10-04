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

// Formes that are the SAME draft pick as their base, so a coach who drafted either may
// build any of them, matching baseFormeId in showdown-config/custom-formats.js, which
// collapses the family to one id server-side. Two kinds:
//   - a forme a mon flips into IN BATTLE by carrying a signature move (Keldeo-Resolute
//     via Secret Sword): requiredMove + changesFrom, no battleOnly;
//   - a forme its base gets by holding a type-changing item (Silvally + Water Memory,
//     Genesect + Douse Drive, Arceus + a plate): a required item + changesFrom, no
//     battleOnly, and the base's own stats and abilities, so only the type moves.
// Mega / primal / Pirouette formes carry battleOnly and stay their own pick, and so
// does an item forme that is really a different mon: Giratina-Origin, Necrozma-Ultra
// and Zygarde-Mega restat, and the Ogerpon masks and Venomicon-Epilogue change ability.
//
// The CLIENT's Species objects drop requiredMove, so read the raw BattlePokedex row the
// page already has and fall back to the species object itself (the Node dex the tests
// run against, which carries the field).
function formeData(sp) {
  var raw = null;
  try {
    if (typeof BattlePokedex !== 'undefined' && BattlePokedex) raw = BattlePokedex[tbID(sp && (sp.id || sp.name))] || null;
  } catch (e) {}
  return raw || sp;
}

// Shallow equality for the flat maps compared below (baseStats, abilities), which key
// order can differ on.
function sameMap(a, b) {
  var ka = Object.keys(a || {});
  if (ka.length !== Object.keys(b || {}).length) return false;
  for (var i = 0; i < ka.length; i++) if (a[ka[i]] !== b[ka[i]]) return false;
  return true;
}

function isMoveForme(sp) {
  var d = formeData(sp);
  return !!(d && d.requiredMove && d.changesFrom && typeof d.battleOnly !== 'string');
}

function isItemForme(sp, getSpecies) {
  var d = formeData(sp);
  if (!d || !d.changesFrom || typeof d.battleOnly === 'string') return false;
  if (!d.requiredItem && !(d.requiredItems && d.requiredItems.length)) return false;
  var base = safeGet(getSpecies, d.changesFrom);
  if (!base || base.exists === false) return false;
  return sameMap(d.baseStats, base.baseStats) && sameMap(d.abilities, base.abilities);
}

// The ids a drafted pick shares its identity with: its base, plus every forme that
// collapses onto that base. Empty for a pick that stands alone.
function samePickIds(sp, getSpecies) {
  var out = [];
  if (!sp || sp.exists === false) return out;
  var base = sp;
  if (isMoveForme(sp) || isItemForme(sp, getSpecies)) {
    base = safeGet(getSpecies, formeData(sp).changesFrom);
    if (!base || base.exists === false) return out;
    out.push(tbID(base.id || base.name));
  }
  var formes = base.otherFormes || [];
  for (var i = 0; i < formes.length; i++) {
    var f = safeGet(getSpecies, formes[i]);
    if (f && f.exists !== false && (isMoveForme(f) || isItemForme(f, getSpecies))) {
      out.push(tbID(f.id || f.name || formes[i]));
    }
  }
  return out;
}

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
    // Formes that are the same pick as this one (see samePickIds) unlock with it.
    for (const extra of samePickIds(s, getSpecies)) ids[extra] = true;
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
  module.exports = { rosterSpeciesIds, filterPokemonResults, samePickIds, tbID };
}
// The browser injection (serve-client.js) inlines this SOURCE, so expose the same
// functions on window there, exactly as it runs in the Node tests.
if (typeof window !== 'undefined') {
  window.rosterSpeciesIds = rosterSpeciesIds;
  window.filterPokemonResults = filterPokemonResults;
  window.zstTbID = tbID;
}
