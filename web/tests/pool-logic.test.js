'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { pickMvp, poolMatches, rolesOf, ROLES, ALL_TYPES } = require('../pool-logic.js');
const { randomPool, randomTeam, mvpBetter } = require('./helpers.js');

// ── MVP: the exact tiebreak ladder, one level per test ─────────────────────

const mon = (tier, gp, k, d, w, l) => ({ tier, stats: { gp, k, d, w, l } });

test('MVP: nobody has played → null', () => {
  assert.equal(pickMvp([mon('S', 0, 0, 0, 0, 0), mon('A', 0, 0, 0, 0, 0)]), null);
  assert.equal(pickMvp([]), null);
});

test('MVP: only mons with a logged game are eligible', () => {
  // The unplayed S-tier has a gaudy record on paper but gp:0, the played
  // C-tier wins by default.
  const played = mon('C', 3, 5, 1, 2, 1);
  const benched = { tier: 'S', stats: { gp: 0, k: 99, d: 0, w: 0, l: 0 } };
  assert.equal(pickMvp([benched, played]), played);
});

test('MVP: highest KO differential wins first', () => {
  const hi = mon('C', 4, 10, 2, 1, 3); // diff +8
  const lo = mon('S', 4, 12, 8, 4, 0); // diff +4 but better winrate & KOs
  assert.equal(pickMvp([lo, hi]), hi);
});

test('MVP: equal KO diff → win rate breaks the tie', () => {
  const winner = mon('B', 4, 6, 2, 4, 0); // diff +4, 100% wr
  const loser = mon('S', 4, 5, 1, 1, 3); // diff +4, 25% wr
  assert.equal(pickMvp([loser, winner]), winner);
});

test('MVP: equal diff + win rate → raw KOs break the tie', () => {
  const winner = mon('A', 4, 9, 5, 2, 2); // diff +4, 50% wr, 9 KOs
  const loser = mon('S', 4, 6, 2, 2, 2);  // diff +4, 50% wr, 6 KOs
  assert.equal(pickMvp([loser, winner]), winner);
});

test('MVP: all else equal → lower tier wins (C over S)', () => {
  const c = mon('C', 4, 6, 2, 2, 2);
  const s = mon('S', 4, 6, 2, 2, 2);
  assert.equal(pickMvp([s, c]), c);
  // Ordering among the middle tiers too.
  assert.equal(pickMvp([mon('A', 4, 6, 2, 2, 2), mon('B', 4, 6, 2, 2, 2)]).tier, 'B');
});

test('MVP: winner is a true maximum across many random teams (property)', () => {
  for (let seed = 1; seed <= 200; seed++) {
    const team = randomTeam(seed, 10);
    const mvp = pickMvp(team);
    const eligible = team.filter((m) => m.stats.gp > 0);
    if (eligible.length === 0) { assert.equal(mvp, null); continue; }
    assert.ok(mvp && mvp.stats.gp > 0, `seed ${seed}: MVP must be an eligible mon`);
    // No eligible mon is strictly better than the chosen MVP.
    for (const m of eligible) {
      if (m === mvp) continue;
      assert.ok(mvpBetter(mvp, m) <= 0, `seed ${seed}: found a mon better than the MVP`);
    }
  }
});

// ── roles derived from base stats ──────────────────────────────────────────

test('roles: each threshold classifies correctly', () => {
  assert.deepEqual(rolesOf({ atk: 100, spAtk: 0, speed: 0, hp: 0, def: 0, spDef: 0 }), ['Physical']);
  assert.deepEqual(rolesOf({ atk: 99, spAtk: 100, speed: 0, hp: 0, def: 0, spDef: 0 }), ['Special']);
  assert.deepEqual(rolesOf({ atk: 0, spAtk: 0, speed: 100, hp: 0, def: 0, spDef: 0 }), ['Fast']);
  assert.deepEqual(rolesOf({ atk: 0, spAtk: 0, speed: 0, hp: 100, def: 0, spDef: 0 }), ['Bulky']);
  assert.deepEqual(rolesOf({ atk: 0, spAtk: 0, speed: 0, hp: 0, def: 110, spDef: 0 }), ['Wall']);
  assert.deepEqual(rolesOf({ atk: 0, spAtk: 0, speed: 0, hp: 0, def: 0, spDef: 110 }), ['Wall']);
});

test('roles: a stat-stuffed mon gets every role; a weakling gets none', () => {
  assert.deepEqual(rolesOf({ atk: 130, spAtk: 130, speed: 130, hp: 130, def: 130, spDef: 130 }),
    ROLES.map(([n]) => n));
  assert.deepEqual(rolesOf({ atk: 50, spAtk: 50, speed: 50, hp: 50, def: 50, spDef: 50 }), []);
});

// ── tier-list filtering (poolMatches) ──────────────────────────────────────

const none = { search: '', availableOnly: false, tiers: [], type1: '', type2: '', roles: [] };
const water = { id: 1, name: 'Vaporeon', tier: 'B', type1: 'Water', type2: null,
  hp: 130, atk: 65, def: 60, spAtk: 110, spDef: 95, speed: 65, drafted: false };
const dragon = { id: 2, name: 'Garchomp', tier: 'S', type1: 'Dragon', type2: 'Ground',
  hp: 108, atk: 130, def: 95, spAtk: 80, spDef: 85, speed: 102, drafted: true };

test('filter: empty criteria passes everything', () => {
  assert.ok(poolMatches(water, none));
  assert.ok(poolMatches(dragon, none));
});

test('filter: search matches name case-insensitively, substring', () => {
  assert.ok(poolMatches(water, { ...none, search: 'vapor' }));
  assert.ok(poolMatches(water, { ...none, search: 'PORE' }));
  assert.ok(!poolMatches(water, { ...none, search: 'chomp' }));
});

test('filter: availableOnly hides drafted mons', () => {
  assert.ok(!poolMatches(dragon, { ...none, availableOnly: true }));
  assert.ok(poolMatches(water, { ...none, availableOnly: true }));
});

test('filter: tier is an OR set', () => {
  assert.ok(poolMatches(dragon, { ...none, tiers: ['S', 'A'] }));
  assert.ok(!poolMatches(water, { ...none, tiers: ['S', 'A'] }));
  assert.ok(poolMatches(water, { ...none, tiers: [] })); // empty = no constraint
});

test('filter: type1/type2 each match either of the mon\'s types', () => {
  assert.ok(poolMatches(dragon, { ...none, type1: 'Ground' })); // secondary type
  assert.ok(poolMatches(dragon, { ...none, type1: 'Dragon', type2: 'Ground' }));
  assert.ok(!poolMatches(dragon, { ...none, type1: 'Dragon', type2: 'Fire' }));
});

test('filter: roles require ALL selected roles', () => {
  // Garchomp: atk 130 (Physical), speed 102 (Fast), but not Special.
  assert.ok(poolMatches(dragon, { ...none, roles: ['Physical', 'Fast'] }));
  assert.ok(!poolMatches(dragon, { ...none, roles: ['Physical', 'Special'] }));
});

test('filter: skip omits exactly one facet (facet-count basis)', () => {
  const c = { ...none, tiers: ['S'], type1: 'Fire' };
  // Water fails both tier and type; skipping tier still fails on type…
  assert.ok(!poolMatches(water, c, 'tier'));
  // …but skipping BOTH-relevant type facet lets it through when tier is skipped
  // and type is the only other constraint.
  assert.ok(poolMatches(water, { ...none, tiers: ['S'] }, 'tier'));
});

// ── consistency between the module and app.js expectations ─────────────────

test('exports: ALL_TYPES has the 18 canonical types', () => {
  assert.equal(ALL_TYPES.length, 18);
  assert.ok(ALL_TYPES.includes('Fairy'));
  assert.equal(new Set(ALL_TYPES).size, 18);
});

test('filter over a random pool never contradicts its own facet-skip', () => {
  // For any mon that fully passes, it must also pass with any single facet
  // skipped (skipping a constraint can only ever admit more).
  const pool = randomPool(42, 500);
  const crit = { search: 'mon1', availableOnly: true, tiers: ['S', 'B'], type1: 'Water', type2: '', roles: ['Fast'] };
  for (const m of pool) {
    if (poolMatches(m, crit)) {
      for (const skip of ['search', 'avail', 'tier', 'type1', 'type2', 'roles']) {
        assert.ok(poolMatches(m, crit, skip), 'skipping a facet must not drop a passing mon');
      }
    }
  }
});
