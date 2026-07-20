'use strict';

const { test } = require('node:test');
const assert = require('node:assert');
const { Teams, Dex } = require('pokemon-showdown');
const { buildRandomTeam, randomSet } = require('../lib/random-team');

// randomSet builds the fully-random-but-legal sets the demo/sim teams are made of.
// The load-bearing rule these guard: a MEGA is fielded as its BASE form holding
// the mega stone (it mega-evolves in battle), never the mega forme with no item.

test('a mega slug becomes the BASE species holding its mega stone', () => {
  const set = randomSet('charizard-megay');
  assert.equal(set.species, 'Charizard', 'fields the base form, not Charizard-Mega-Y');
  assert.equal(set.item, 'Charizardite Y', 'holds the mega stone');
  // Its ability + moves must be legal for the BASE form (what it has until it megas).
  const base = Dex.species.get('Charizard');
  assert.ok(Object.values(base.abilities).includes(set.ability), 'base-form ability');
});

test('every known official mega maps base + its own stone', () => {
  for (const [slug, base, stone] of [
    ['venusaur-mega', 'Venusaur', 'Venusaurite'],
    ['blastoise-mega', 'Blastoise', 'Blastoisinite'],
    ['gardevoir-mega', 'Gardevoir', 'Gardevoirite'],
    ['charizardmegax', 'Charizard', 'Charizardite X'],
  ]) {
    const set = randomSet(slug);
    assert.equal(set.species, base, `${slug} -> ${base}`);
    assert.equal(set.item, stone, `${slug} holds ${stone}`);
  }
});

test('a non-mega keeps its own species and gets a held item', () => {
  const set = randomSet('garchomp');
  assert.equal(set.species, 'Garchomp');
  assert.ok(set.item && set.item.length > 0, 'a non-mega carries a random item');
});

test('a stone-less mega (Mega Rayquaza) stays the forme with no item', () => {
  // Rayquaza megas via a move (Dragon Ascent), not a stone — there is no stone to
  // hold, so it can only be fielded as the forme itself, itemless.
  const set = randomSet('rayquaza-mega');
  assert.equal(set.species, 'Rayquaza-Mega');
  assert.equal(set.item, '');
});

test('the packed mega team round-trips to base species holding the stone', () => {
  const packed = buildRandomTeam(['charizard-megay'], 1);
  const [mon] = Teams.unpack(packed);
  assert.equal(mon.species, 'Charizard');
  assert.equal(mon.item, 'Charizardite Y');
});

test('buildRandomTeam samples `count` from a larger roster and skips unknowns', () => {
  const roster = ['garchomp', 'dragonite', 'not-a-real-pokemon', 'greninja', 'kingambit', 'corviknight', 'toxapex'];
  const packed = buildRandomTeam(roster, 6);
  const team = Teams.unpack(packed);
  assert.equal(team.length, 6, 'brings exactly 6');
  assert.ok(team.every((s) => Dex.species.get(s.species).exists), 'no unknown species slip in');
});
