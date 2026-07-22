'use strict';

// Builds fully-random-but-LEGAL battle teams from a drafted roster, for the headless
// test-season simulator. Every choice that can be made is made randomly:
//   • a random nature (any of the 25),
//   • a random legal EV spread (4-point blocks, each stat ≤ 252, total ≤ 510),
//   • a random legal IV spread (each stat 0–31),
//   • a random ability from the species' legal set,
//   • a random 4 moves from the species' movepool (own learnset + prevo chain),
//   • a random held item, EXCEPT megas (which carry their form, not an item).
// Then every set is run through Showdown's own TeamValidator against National Dex
// Doubles, the exact engine behind the Teambuilder's "Validate" button, and anything
// it rejects (an unlearnable move, an incompatible move pair, a Gen 1-2 VC move that
// needs perfect IVs) is repaired until it passes (see the "Legality gate" below). So a
// synthetic team is a legal Nat Dex Doubles team, restricted just like a coach's real
// one, not merely a random one. The result is a packed team string, what the sim accepts.

const { Teams, Dex, toID, TeamValidator } = require('pokemon-showdown');

function pick(arr) { return arr[Math.floor(Math.random() * arr.length)]; }

function sampleN(arr, n) {
  const a = arr.slice();
  for (let i = a.length - 1; i > 0; i--) {
    const j = Math.floor(Math.random() * (i + 1));
    [a[i], a[j]] = [a[j], a[i]];
  }
  return a.slice(0, n);
}

// The six stat ids, in canonical order.
const STATS = ['hp', 'atk', 'def', 'spa', 'spd', 'spe'];

// Every nature the game has (25), by name; one is picked at random per set.
const NATURES = Dex.natures.all().map((n) => n.name);

// A random legal EV spread. EVs are handed out in 4-point blocks (the granularity
// that actually moves a stat) across the stats in a random order, each capped at
// 252 and the whole spread at 510. Most sets spend near the full 510, but which
// stats get the investment, and how much, varies every game.
function randomEvs() {
  const evs = { hp: 0, atk: 0, def: 0, spa: 0, spd: 0, spe: 0 };
  let remaining = 510;
  for (const s of sampleN(STATS, STATS.length)) {
    if (remaining < 4) break;
    const maxBlocks = Math.floor(Math.min(252, remaining) / 4);
    const blocks = Math.floor(Math.random() * (maxBlocks + 1));
    evs[s] = blocks * 4;
    remaining -= blocks * 4;
  }
  return evs;
}

// A random legal IV spread: each stat independently 0–31.
function randomIvs() {
  const ivs = {};
  for (const s of STATS) ivs[s] = Math.floor(Math.random() * 32);
  return ivs;
}

// The league's competitive format. The sim actually BATTLES under custom-game
// (which bans nothing), so we apply this format's option-level restrictions here,
// at team-build time, the same restrictions the Showdown team builder enforces.
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
// anything the format bans (e.g. Bright Powder, Lax Incense, the type gems) and any
// form-locked / species-locked item, which does nothing on a random mon and, worse,
// fails the ZST validator on the wrong holder: mega stones (also, via isMegaStone,
// they'd float their non-mega holder to the lead), primal orbs, Z-crystals, forme
// forcers, Poke Balls (not real held items), and itemUser items (Rusted Sword/Shield,
// origin orbs, memories, Light Ball/Thick Club…).
const USABLE_ITEMS = Dex.items.all()
  .filter((i) => i.name && !i.isNonstandard && i.gen <= 9 && !RESTRICTIONS.items.has(i.id) &&
    !i.megaStone && !i.onPrimal && !i.zMove && !i.forcedForme && !i.isPokeball && !i.itemUser)
  .map((i) => i.name);

// A forme's learnset: its OWN if it has one, otherwise the base forme's. A regional
// / alternate forme (Arcanine-Hisui, Tauros-Paldea-*, Zapdos-Galar, Ursaluna-Bloodmoon)
// carries a complete own learnset and must NOT inherit the Kanto base's moves, doing so
// used to hand it moves it can't legally learn (Arcanine-Hisui "learning" Dragon Breath),
// which National Dex validation rightly rejects. Only formes with NO own learnset
// (cosmetics, mega formes, Rotom appliances) fall through to the base's.
function learnsetFor(species) {
  let s = species;
  const seen = new Set();
  while (s && s.exists && !seen.has(s.id)) {
    seen.add(s.id);
    const ls = Dex.data.Learnsets[s.id];
    if (ls && ls.learnset) return ls.learnset;
    if (s.baseSpecies && s.baseSpecies !== s.name) s = Dex.species.get(s.baseSpecies);
    else return null;
  }
  return null;
}

// Every move the species can learn, walking its prevo chain (regional prevos included)
// and applying the own-else-base rule per link, so a fully-evolved mon draws on its
// whole line's pool without borrowing a sibling forme's moves. Filtered to moves usable
// in gen 9 and not format-restricted.
function movePool(species) {
  const moves = new Set();
  const seen = new Set();
  let cur = species;
  while (cur && cur.exists && !seen.has(cur.id)) {
    seen.add(cur.id);
    const ls = learnsetFor(cur);
    if (ls) for (const m in ls) moves.add(m);
    cur = cur.prevo ? Dex.species.get(cur.prevo) : null;
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
  // battle), NOT the mega forme with no item, that's how Showdown represents one.
  // Set the item + swap `species` back to the base BEFORE reading abilities/moves,
  // so the set is legal for the base (the ability/moves it has until it megas).
  let item;
  const isMega = (species.forme || '').startsWith('Mega');
  if (isMega && species.requiredItem) {
    item = species.requiredItem;                       // e.g. "Charizardite Y"
    species = Dex.species.get(species.baseSpecies);    // e.g. Charizard
  } else if (isMega) {
    item = '';                                         // stone-less mega (e.g. Mega Rayquaza)
  } else if (species.requiredItem) {
    item = species.requiredItem;                       // origin/crowned/etc: the forme is
  } else if (species.requiredItems && species.requiredItems.length) {
    item = species.requiredItems[0];                   // this exact item, so keep the forme
  } else {                                             // and hand it the item it needs
    item = pick(USABLE_ITEMS);
  }

  // Drop abilities the format bans (e.g. Sand Veil, Snow Cloak); keep at least one.
  const allAbilities = Object.values(species.abilities).filter(Boolean);
  const legalAbilities = allAbilities.filter((a) => !RESTRICTIONS.abilities.has(toID(a)));
  const abilities = legalAbilities.length ? legalAbilities : allAbilities;
  const pool = movePool(species);
  const moves = pool.length ? sampleN(pool, Math.min(4, pool.length)) : ['tackle'];

  // Showdown's dex spells Sirfetch'd / Farfetch'd with a curly apostrophe (U+2019).
  // Some clients render its UTF-8 bytes as mojibake ("SirfetchΓÇÖd"), so use a
  // straight apostrophe for the display nickname; species keeps the canonical name.
  const set = {
    name: species.name.replace(/’/g, "'"),
    species: species.name,
    ability: abilities.length ? pick(abilities) : (species.abilities['0'] || 'No Ability'),
    item,
    moves,
    nature: pick(NATURES),   // a random one of the 25
    gender: '',
    level: 100,
    evs: randomEvs(),        // random legal spread (≤252/stat, ≤510 total)
    ivs: randomIvs(),        // random legal spread (0–31/stat)
  };
  if (teraType) set.teraType = teraType; // C-tier: teras to its drafted type
  return set;
}

// ── Legality gate ────────────────────────────────────────────────────────────
// The sim battles under a custom game (no learnset checks), but the league plays
// National Dex Doubles, so a synthetic team should be a LEGAL Nat Dex Doubles team,
// not merely a random one. We validate every generated set with Showdown's own
// TeamValidator, the exact engine behind the Teambuilder's "Validate" button, and
// repair anything it rejects before the battle runs.
//
// The gate governs moves and builds/IVs only, NOT species membership: the DRAFT
// decides which species are legal (the pool is hand-picked), so a drafted mon that
// Standard NatDex would ban for being unreleased (Floette-Eternal) is unbanned per
// roster below. The league's evasion/OHKO/gem/etc. option bans are already stripped
// at build time (RESTRICTIONS), so they need no repeating here.
const VALIDATION_FORMAT = 'gen9doublescustomgame@@@Standard NatDex';

// One TeamValidator per distinct unban set (usually the empty one). Built lazily and
// cached, since a validator's rule table is a bit expensive to construct.
const _validatorCache = new Map();
function validatorForSets(sets) {
  const unbans = new Set();
  for (const set of sets) {
    const sp = Dex.species.get(set.species);
    if (sp.exists && sp.isNonstandard) unbans.add('+' + sp.id);
    const it = Dex.items.get(set.item);
    if (it && it.exists && it.isNonstandard) { unbans.add('+pokemontag:custom'); unbans.add('+' + it.id); }
  }
  const key = [...unbans].sort().join(',');
  if (!_validatorCache.has(key)) {
    _validatorCache.set(key, new TeamValidator(key ? `${VALIDATION_FORMAT},${key}` : VALIDATION_FORMAT));
  }
  return _validatorCache.get(key);
}

// Reshuffle a set's moves (and, when needed, perfect its IVs) until the validator
// accepts it, driven by the validator's own complaints:
//   • "<Mon> can't learn <Move>." (or a gen-legality reject) → ban that move from the
//     resample pool, so it can't be redrawn,
//   • "…must have at least N perfect IVs…" (a Gen 1-2 VC move) → max the IVs,
//   • an incompatible move pair → just draw a fresh random 4.
// A forme that transforms via a move (Mega Rayquaza + Dragon Ascent, species.requiredMove)
// always keeps that move. Returns true if the set ends up legal, false if it couldn't be
// legalised in `rounds` tries (the caller logs and proceeds; the battle still runs).
function legalizeSet(set, validator, rounds = 14) {
  const species = Dex.species.get(set.species);
  const requiredMove = species.requiredMove || null;
  const requiredId = requiredMove ? toID(requiredMove) : null;
  const banned = new Set();
  const draw = () => {
    const pool = movePool(species).filter((m) => !banned.has(toID(m)) && toID(m) !== requiredId);
    const picks = requiredMove ? [requiredMove] : [];
    for (const m of sampleN(pool, pool.length)) {
      if (picks.length >= 4) break;
      picks.push(m);
    }
    set.moves = picks.length ? picks : ['tackle'];
  };
  for (let r = 0; r < rounds; r++) {
    const problems = validator.validateSet(set, {}) || [];
    if (!problems.length) return true;
    for (const p of problems) {
      const m = /can't learn ([^.]+?)\.?$/.exec(p);
      if (m && toID(m[1]) !== requiredId) banned.add(toID(m[1]));
      if (/perfect IVs?/i.test(p)) for (const s of STATS) set.ivs[s] = 31;
    }
    if (r >= Math.floor(rounds / 2)) for (const s of STATS) set.ivs[s] = 31; // escalate
    draw();
  }
  return (validator.validateSet(set, {}) || []).length === 0;
}

// Validate a whole packed team (or set array) against the Nat Dex Doubles gate. Returns
// an array of problem strings, empty means legal. Used by the sim as the final check
// before a battle (see simulate-season.js).
function validateTeam(team) {
  const sets = typeof team === 'string' ? Teams.unpack(team) : team;
  return validatorForSets(sets).validateTeam(sets) || [];
}

// A random `count` of the roster's mons, each a random set. count defaults to 6 (a
// singles "bring"), so a 10-mon roster fields a different six most games. Species
// Showdown doesn't know (e.g. the league's custom Champions megas) are skipped,
// they simply can't take the field. `mons` items are either a slug string or
// { s: slug, t: teraType|null }.
//
// True if `item` is a mega stone (holds the mega-evolution wiring). Used to spot a
// set that will mega-evolve so it can be led (see chooseSets).
function isMegaStone(item) {
  if (!item) return false;
  const it = Dex.items.get(item);
  return !!(it && it.megaStone);
}

function chooseSets(mons, count) {
  const keyOf = (m) => (typeof m === 'string' ? m : m.s);
  const valid = mons.filter((m) => Dex.species.get(keyOf(m)).exists);
  const chosen = sampleN(valid, Math.min(count, valid.length));
  if (!chosen.length) throw new Error('roster has no Showdown-known species');
  const sets = chosen.map(randomSet);
  // Legalise each set against Nat Dex Doubles rules (the Validate button's engine)
  // before it ever reaches a battle, so the sim plays legal teams, not just random
  // ones. Most sets already pass; legalizeSet repairs the rest (see the gate above).
  // validateSet defaults a missing teraType to the species' primary type, which would
  // wrongly mark a non-C mon as tera-able, so snapshot the intended teraType (set only
  // on C-tier picks) and restore it afterwards.
  const validator = validatorForSets(sets);
  for (const set of sets) {
    const tera = set.teraType;
    legalizeSet(set, validator);
    if (tera) set.teraType = tera; else delete set.teraType;
  }
  // Lead the mega. Team preview brings the packed team "default" (the first slots
  // are the leads), and the AI only mega-evolves a mon that is ACTUALLY on the
  // field; a mega stuck in the back that the random AI never switches in just sits
  // there as its un-evolved base form (the "Floette that never becomes Mega Floette"
  // bug). Floating any stone-holder to the front makes a drafted mega reliably come
  // in and transform. A stable sort keeps everything else in its sampled order.
  sets.sort((a, b) => (isMegaStone(b.item) ? 1 : 0) - (isMegaStone(a.item) ? 1 : 0));
  return sets;
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

module.exports = {
  buildRandomTeam, buildTeamWithTera, randomSet, movePool, learnsetFor,
  legalizeSet, validateTeam, validatorForSets, VALIDATION_FORMAT,
  USABLE_ITEMS, RESTRICTIONS, RESTRICTION_FORMAT,
};
