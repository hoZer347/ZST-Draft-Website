'use strict';

// Builds fully-random-but-legal battle teams from a drafted roster, for the
// headless test-season simulator. Every choice that can be made is made
// randomly, within the constraints the league wants for these synthetic games:
//   • EVs are spread evenly across all six stats (84 each), neutral nature —
//     so the numbers come from the mon, not from tuning.
//   • a random ability from the species' legal set,
//   • a random 4 moves from the species' full movepool (base + prevo forms),
//   • a random held item, EXCEPT megas (which carry their form, not an item).
// The result is a packed team string, the format the sim accepts.

const { Teams, Dex, toID } = require('pokemon-showdown');

function pick(arr) { return arr[Math.floor(Math.random() * arr.length)]; }

function sampleN(arr, n) {
  const a = arr.slice();
  for (let i = a.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [a[i], a[j]] = [a[j], a[i]];
  }
  return a.slice(0, n);
}

// The league's competitive format. The sim actually BATTLES under custom-game
// (which bans nothing), so we apply this format's option-level restrictions here,
// at team-build time — the same restrictions the Showdown team builder enforces.
const RESTRICTION_FORMAT = 'gen9zstseason4';

// Move / item / ability ids this format forbids, read straight from Showdown's own
// rule table (evasion moves like Double Team & Minimize, Swagger, evasion items &
// abilities, ...) plus one-hit-KO moves when an OHKO Clause is in force. Built once.
// So a generated set can never carry an option the format wouldn't let you submit.
const RESTRICTIONS = (() => {
  const moves = new Set(), items = new Set(), abilities = new Set();
  try {
    const rt = Dex.formats.getRuleTable(Dex.formats.get(RESTRICTION_FORMAT));
    for (const key of rt.keys()) {
      if (key.startsWith('-move:')) moves.add(key.slice('-move:'.length));
      else if (key.startsWith('-item:')) items.add(key.slice('-item:'.length));
      else if (key.startsWith('-ability:')) abilities.add(key.slice('-ability:'.length));
    }
    // OHKO Clause bans one-hit-KO moves through validation, not a -move: entry.
    if ([...rt.keys()].some((k) => k.includes('ohko'))) {
      for (const m of Dex.moves.all()) if (m.ohko) moves.add(m.id);
    }
  } catch (e) { /* unknown format → no extra restrictions */ }
  return { moves, items, abilities };
})();

// Held items a mon can plausibly carry in gen 9 (drop past-gen / nonstandard), minus
// anything the format bans (e.g. Bright Powder, Lax Incense, the type gems).
const USABLE_ITEMS = Dex.items.all()
  .filter((i) => i.name && !i.isNonstandard && i.gen <= 9 && !RESTRICTIONS.items.has(i.id))
  .map((i) => i.name);

// Every move the species can learn, walking its prevo chain and base forme so a
// fully-evolved mon (or a mega/regional form) draws on the whole line's pool.
// Filtered to moves usable in gen 9.
function movePool(species) {
  const moves = new Set();
  const seen = new Set();
  let cur = species;
  while (cur && cur.exists && !seen.has(cur.id)) {
    seen.add(cur.id);
    const ls = Dex.data.Learnsets[cur.id];
    if (ls && ls.learnset) for (const m in ls.learnset) moves.add(m);
    if (cur.prevo) cur = Dex.species.get(cur.prevo);
    else if (cur.baseSpecies && cur.baseSpecies !== cur.name) cur = Dex.species.get(cur.baseSpecies);
    else break;
  }
  return [...moves].filter((m) => {
    const mv = Dex.moves.get(m);
    return mv.exists && !mv.isNonstandard && mv.id !== 'struggle'
      && !RESTRICTIONS.moves.has(mv.id); // drop evasion / OHKO / other format-banned moves
  });
}

// One random set for a pool entry. `mon` is either a slug string, or
// { s: slug, t: teraType|null } where a non-null teraType (only C-tier mons carry
// one, from the draft) is baked onto the set so the mon teras to exactly that type.
function randomSet(mon) {
  const key = typeof mon === 'string' ? mon : mon.s;
  const teraType = typeof mon === 'string' ? null : (mon.t || null);
  let species = Dex.species.get(key);
  if (!species.exists) throw new Error(`Unknown species: ${key}`);

  // A mega is fielded as its BASE form holding the mega stone (it mega-evolves in
  // battle), NOT the mega forme with no item — that's how Showdown represents one.
  // Set the item + swap `species` back to the base BEFORE reading abilities/moves,
  // so the set is legal for the base (the ability/moves it has until it megas).
  let item;
  const isMega = (species.forme || '').startsWith('Mega');
  if (isMega && species.requiredItem) {
    item = species.requiredItem;                       // e.g. "Charizardite Y"
    species = Dex.species.get(species.baseSpecies);    // e.g. Charizard
  } else if (isMega) {
    item = '';                                         // stone-less mega (e.g. Mega Rayquaza)
  } else {
    item = pick(USABLE_ITEMS);
  }

  // Drop abilities the format bans (e.g. Sand Veil, Snow Cloak); keep at least one.
  const allAbilities = Object.values(species.abilities).filter(Boolean);
  const legalAbilities = allAbilities.filter((a) => !RESTRICTIONS.abilities.has(toID(a)));
  const abilities = legalAbilities.length ? legalAbilities : allAbilities;
  const pool = movePool(species);
  const moves = pool.length ? sampleN(pool, Math.min(4, pool.length)) : ['tackle'];

  const set = {
    name: species.name,
    species: species.name,
    ability: abilities.length ? pick(abilities) : (species.abilities['0'] || 'No Ability'),
    item,
    moves,
    nature: 'Hardy',   // neutral: no stat is boosted or cut
    gender: '',
    level: 100,
    evs: { hp: 84, atk: 84, def: 84, spa: 84, spd: 84, spe: 84 }, // evenly spread
    ivs: { hp: 31, atk: 31, def: 31, spa: 31, spd: 31, spe: 31 },
  };
  if (teraType) set.teraType = teraType; // C-tier: teras to its drafted type
  return set;
}

// A random `count` of the roster's mons, each a random set. count defaults to 6 (a
// singles "bring"), so a 10-mon roster fields a different six most games. Species
// Showdown doesn't know (e.g. the league's custom Champions megas) are skipped —
// they simply can't take the field. `mons` items are either a slug string or
// { s: slug, t: teraType|null }.
//
function chooseSets(mons, count) {
  const keyOf = (m) => (typeof m === 'string' ? m : m.s);
  const valid = mons.filter((m) => Dex.species.get(keyOf(m)).exists);
  const chosen = sampleN(valid, Math.min(count, valid.length));
  if (!chosen.length) throw new Error('roster has no Showdown-known species');
  return chosen.map(randomSet);
}

// The packed team string.
function buildRandomTeam(mons, count = 6) {
  return Teams.pack(chooseSets(mons, count));
}

// Like buildRandomTeam, but also reports the set names of the mons that carry a
// Tera type (the C-tier mons), so the runner can terastallize them ASAP.
function buildTeamWithTera(mons, count = 6) {
  const sets = chooseSets(mons, count);
  return { team: Teams.pack(sets), teraNames: sets.filter((s) => s.teraType).map((s) => s.name) };
}

module.exports = { buildRandomTeam, buildTeamWithTera, randomSet, movePool, USABLE_ITEMS, RESTRICTIONS, RESTRICTION_FORMAT };
