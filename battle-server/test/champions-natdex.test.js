'use strict';

// The league runs on the `champions` mod, which CUT content the National Dex keeps:
// ~194 gen-9 moves are "Past" there and per-species learnsets were trimmed (Archaludon
// lost Body Press, Garchomp lost Aqua Tail), and whole species were dropped. The ZST
// formats put all of that back through NatDex Mod plus the two National Dex hooks in
// showdown-config/custom-formats.js. These tests pin that down, in both directions: the
// cut content must be legal for us, and must still be illegal in a stock Champions
// format (otherwise the test proves nothing about our hooks).
//
// They also pin the live format and its simulator twin to the SAME rules, since a
// simulated season only predicts real games while those agree.

const { test } = require('node:test');
const assert = require('node:assert');
const { Dex, TeamValidator } = require('../lib/engine').requireEngine();
const { movePool } = require('../lib/random-team');

const LIVE = 'gen9championsnatdexzstseason4';
const SIM = 'gen9championsnatdexzstsim';
const STOCK_CHAMPIONS = 'gen9championsvgc2026regmb';

// Champions spreads: at most 32 Stat Points per stat, 66 total.
const set = (o) => Object.assign({
  name: '', species: '', item: '', ability: '', moves: [], nature: 'Serious',
  evs: { hp: 32, atk: 32, def: 2, spa: 0, spd: 0, spe: 0 }, level: 50, teraType: '',
}, o);

// validateSet skips the format's validateTeam, so these exercise legality alone, with
// no drafted-roster fetch involved.
const check = (formatid, s) => TeamValidator.get(formatid).validateSet(s, {}) || [];

test('Champions really did cut Body Press from Archaludon', () => {
  const champions = Dex.mod('champions');
  const learnset = (champions.data.Learnsets['archaludon'] || {}).learnset || {};
  assert.ok(!learnset['bodypress'], 'the champions learnset has no Body Press');
  const natdex = (Dex.mod('gen9').data.Learnsets['archaludon'] || {}).learnset || {};
  assert.ok(natdex['bodypress'], 'the National Dex learnset still has it');
});

test('a move Champions cut from a learnset is legal in the league format', () => {
  const s = set({ species: 'Archaludon', ability: 'Stamina', moves: ['Body Press', 'Flash Cannon', 'Dragon Tail', 'Protect'] });
  assert.deepEqual(check(LIVE, { ...s }), [], 'legal in ZST Season 4');
  assert.deepEqual(check(SIM, { ...s }), [], 'legal in ZST Sim');
  const stock = check(STOCK_CHAMPIONS, { ...s });
  assert.ok(stock.some((p) => /can't learn Body Press/i.test(p)),
    `stock Champions should still reject it, got: ${stock.join(' | ')}`);
});

test('moves Champions marked Past are legal again (Garchomp keeps Aqua Tail)', () => {
  const s = set({ species: 'Garchomp', ability: 'Rough Skin', moves: ['Aqua Tail', 'Earthquake', 'Dual Chop', 'Protect'] });
  assert.deepEqual(check(LIVE, { ...s }), []);
  assert.ok(check(STOCK_CHAMPIONS, { ...s }).length, 'stock Champions rejects them');
});

test('a species Champions dropped entirely is legal in the league format', () => {
  const s = set({ species: 'Blissey', ability: 'Natural Cure', moves: ['Seismic Toss', 'Soft-Boiled', 'Calm Mind', 'Protect'] });
  assert.deepEqual(check(LIVE, { ...s }), []);
  assert.ok(check(STOCK_CHAMPIONS, { ...s }).some((p) => /does not exist in Gen 9/i.test(p)));
});

test("a Champions-only mega still validates (the hooks don't break new content)", () => {
  const s = set({ species: 'Falinks', item: 'Falinksite', ability: 'Defiant', moves: ['Close Combat', 'No Retreat', 'Protect', 'Rock Slide'] });
  assert.deepEqual(check(LIVE, { ...s }), []);
});

test('the generated-team movepool draws from the National Dex, not the Champions trim', () => {
  const pool = movePool(Dex.mod('champions').species.get('Archaludon'));
  assert.ok(pool.includes('bodypress'), 'Body Press is available to a generated set');
});

test('ZST Sim resolves to the same rules as ZST Season 4', () => {
  const keys = (id) => [...Dex.formats.getRuleTable(Dex.formats.get(id)).keys()].sort();
  assert.deepEqual(keys(SIM), keys(LIVE), 'same clauses and bans');
  const live = Dex.formats.get(LIVE), sim = Dex.formats.get(SIM);
  assert.equal(sim.mod, live.mod);
  assert.equal(sim.gameType, 'doubles');
  // The two deliberate differences: the sim has no coach, so no roster check and no
  // roster-driven tera restriction. It keeps the tera-restoring actions object.
  assert.ok(!sim.validateTeam && !sim.onBegin, 'sim drops the draft-roster hooks');
  assert.ok(live.validateTeam && live.onBegin, 'the live format keeps them');
  assert.equal(sim.actions.canTerastallize, live.actions.canTerastallize);
});

test('the sim format is hidden from every picker', () => {
  const sim = Dex.formats.get(SIM);
  assert.equal(sim.searchShow, false);
  assert.equal(sim.challengeShow, false);
  assert.equal(sim.tournamentShow, false);
});
