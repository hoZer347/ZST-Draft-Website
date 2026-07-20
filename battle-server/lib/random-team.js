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

const { Teams, Dex } = require('pokemon-showdown');

function pick(arr) { return arr[Math.floor(Math.random() * arr.length)]; }

function sampleN(arr, n) {
  const a = arr.slice();
  for (let i = a.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [a[i], a[j]] = [a[j], a[i]];
  }
  return a.slice(0, n);
}

// Held items a mon can plausibly carry in gen 9 (drop past-gen / nonstandard).
const USABLE_ITEMS = Dex.items.all()
  .filter((i) => i.name && !i.isNonstandard && i.gen <= 9)
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
    return mv.exists && !mv.isNonstandard && mv.id !== 'struggle';
  });
}

// One random set for a pool species key (our `sprite` slug, e.g. "charizard-megay").
function randomSet(key) {
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

  const abilities = Object.values(species.abilities).filter(Boolean);
  const pool = movePool(species);
  const moves = pool.length ? sampleN(pool, Math.min(4, pool.length)) : ['tackle'];

  return {
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
}

// A packed battle team: a random `count` of the roster's species, each a random
// set. count defaults to 6 (a singles "bring"), so a 10-mon roster fields a
// different six most games. Species Showdown doesn't know (e.g. the league's
// custom Champions megas) are skipped — they simply can't take the field.
function buildRandomTeam(speciesKeys, count = 6) {
  const valid = speciesKeys.filter((k) => Dex.species.get(k).exists);
  const chosen = sampleN(valid, Math.min(count, valid.length));
  if (!chosen.length) throw new Error('roster has no Showdown-known species');
  return Teams.pack(chosen.map(randomSet));
}

module.exports = { buildRandomTeam, randomSet, movePool, USABLE_ITEMS };
