'use strict';
// Shared fixtures for the pool-logic tests + benchmark. No dependencies.

// Deterministic PRNG (mulberry32) so a "randomly generated season" is
// reproducible: same seed → same mons → same assertions, every run and machine.
function rng(seed) {
  let a = seed >>> 0;
  return function () {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

const TYPES = ['Normal', 'Fire', 'Water', 'Electric', 'Grass', 'Ice', 'Fighting', 'Poison',
  'Ground', 'Flying', 'Psychic', 'Bug', 'Rock', 'Ghost', 'Dragon', 'Dark', 'Steel', 'Fairy'];
const TIERS = ['S', 'A', 'B', 'C'];

// One random pool mon shaped exactly like an /api/pool row.
function randomMon(rand, i) {
  const stat = () => 40 + Math.floor(rand() * 115); // 40..154
  const t1 = TYPES[Math.floor(rand() * TYPES.length)];
  // ~55% of mons are mono-type; the rest get a distinct second type.
  let t2 = null;
  if (rand() < 0.45) { do { t2 = TYPES[Math.floor(rand() * TYPES.length)]; } while (t2 === t1); }
  return {
    id: i,
    name: `Mon${i}`,
    tier: TIERS[Math.floor(rand() * TIERS.length)],
    type1: t1,
    type2: t2,
    hp: stat(), atk: stat(), def: stat(), spAtk: stat(), spDef: stat(), speed: stat(),
    drafted: rand() < 0.35,
  };
}

function randomPool(seed, n) {
  const rand = rng(seed);
  return Array.from({ length: n }, (_, i) => randomMon(rand, i));
}

// One drafted mon with a battle-stat row, as the team endpoint returns it,
// the shape pickMvp consumes. `gp` 0 means "never played" (ineligible).
function randomTeam(seed, size) {
  const rand = rng(seed);
  return Array.from({ length: size }, (_, i) => {
    const gp = Math.floor(rand() * 6); // 0..5 games
    const k = Math.floor(rand() * 12);
    const d = Math.floor(rand() * 12);
    const w = Math.floor(rand() * (gp + 1));
    return {
      name: `Mon${i}`,
      tier: TIERS[Math.floor(rand() * TIERS.length)],
      stats: gp === 0 ? { gp: 0, k: 0, d: 0, w: 0, l: 0 } : { gp, k, d, w, l: gp - w },
    };
  });
}

// The MVP comparator, as a standalone reference, so tests can assert that
// pickMvp's winner is a true maximum under the documented ordering. Mirrors the
// spec in pool-logic.js: KO diff > win rate > KOs > lower tier.
function mvpBetter(a, b) {
  const rank = { C: 0, B: 1, A: 2, S: 3 };
  const wr = (s) => (s.w + s.l > 0 ? s.w / (s.w + s.l) : 0);
  const da = a.stats.k - a.stats.d, db = b.stats.k - b.stats.d;
  if (da !== db) return da > db ? -1 : 1;
  const wa = wr(a.stats), wb = wr(b.stats);
  if (wa !== wb) return wa > wb ? -1 : 1;
  if (a.stats.k !== b.stats.k) return a.stats.k > b.stats.k ? -1 : 1;
  return (rank[a.tier] ?? 9) - (rank[b.tier] ?? 9);
}

module.exports = { rng, randomPool, randomTeam, mvpBetter, TYPES, TIERS };
