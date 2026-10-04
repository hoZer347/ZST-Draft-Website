'use strict';

// The C-tier Tera lock, teambuilder side. Pure, so the browser injection
// (serve-client.js AUTO_TERA) and the Node tests run identical logic: the page inlines
// this SOURCE, the tests require it.
//
// Each coach draws a fixed Tera type for every C-tier pick, and the format's validator
// enforces it (showdown-config/custom-formats.js). The coach therefore has no choice to
// make, so the builder should never make them make one: any C-tier set in the team gets
// its drafted type stamped on, whatever route it arrived by (species picked, team
// imported, set pasted, sample set loaded), and whatever the classic client did to
// set.teraType on the way (it DELETES it on every species change, and blanks it in
// Champions mode unless patch-client-tera.js has run).
//
// Without this a set reaches the server with no Tera type, the validator falls back to
// the species' PRIMARY type, and a mon whose drafted type is its second type is silently
// retyped: Aerodactyl (Rock/Flying) drafted Tera Flying turns up as Tera Rock and the
// team is rejected. The server now stamps the drafted type too (applyDraftedTera), so
// this side is about what the coach SEES while building, and about the team they export.

function teraID(s) { return ('' + (s || '')).toLowerCase().replace(/[^a-z0-9]/g, ''); }

// roster.mons -> { speciesid: 'Flying' } for C-tier picks that were drafted with a Tera
// type. Keyed by BOTH the display name ("Corsola-G") and the sprite slug
// ("corsola-galar") in id form, since the builder's set.species can match either
// spelling depending on which dex named it. Non-C picks and C-tier picks drafted
// Tera-barred (megas, Shedinja: `tera` is null) are absent, so they are never touched.
function draftedTeraMap(mons) {
  const map = {};
  const list = Array.isArray(mons) ? mons : [];
  for (const m of list) {
    if (!m || String(m.tier).toUpperCase() !== 'C' || !m.tera) continue;
    if (m.name) map[teraID(m.name)] = m.tera;
    if (m.slug) map[teraID(m.slug)] = m.tera;
  }
  return map;
}

// Stamp the drafted Tera type onto every set in a teambuilder set list that is a C-tier
// pick. Mutates the sets in place and returns the indices it changed, so a caller can
// skip re-rendering when nothing moved (and so the tests can assert idempotence).
// Sets whose species isn't a C-tier pick are left exactly as they are: a non-C mon can't
// Terastallize at all, so inventing a type for it would only be noise.
function applyDraftedTera(sets, teraMap) {
  const changed = [];
  const list = Array.isArray(sets) ? sets : [];
  if (!teraMap) return changed;
  for (let i = 0; i < list.length; i++) {
    const set = list[i];
    if (!set || !set.species) continue;
    const tera = teraMap[teraID(set.species)];
    if (!tera || set.teraType === tera) continue;
    set.teraType = tera;
    changed.push(i);
  }
  return changed;
}

if (typeof module !== 'undefined' && module.exports) {
  module.exports = { draftedTeraMap, applyDraftedTera, teraID };
}
// The browser injection inlines this SOURCE, so expose the same functions on window
// there, exactly as they run in the Node tests.
if (typeof window !== 'undefined') {
  window.zstDraftedTeraMap = draftedTeraMap;
  window.zstApplyDraftedTera = applyDraftedTera;
  window.zstTeraID = teraID;
}
