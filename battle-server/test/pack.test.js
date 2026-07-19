'use strict';

const { test } = require('node:test');
const assert = require('node:assert');
const { Teams, toID } = require('pokemon-showdown');
const { packDraftTeam, FILLER_MOVES } = require('../lib/pack');

test('packs a team of known species into a valid packed string', () => {
  const packed = packDraftTeam(['charizard', 'venusaur', 'blastoise']);
  assert.equal(typeof packed, 'string');
  assert.ok(packed.length > 0);
  // Round-trips back to the same three species the sim recognises.
  const unpacked = Teams.unpack(packed);
  assert.equal(unpacked.length, 3);
  assert.deepEqual(unpacked.map((s) => s.species).sort(), ['Blastoise', 'Charizard', 'Venusaur']);
});

test('packs mega and regional-form slugs (the pool\'s sprite keys)', () => {
  const packed = packDraftTeam(['charizard-megay', 'blastoise-mega', 'venusaur-mega']);
  const unpacked = Teams.unpack(packed);
  assert.equal(unpacked.length, 3);
  // The megay slug normalises to the real Showdown species name.
  assert.ok(unpacked.some((s) => s.species === 'Charizard-Mega-Y'));
});

test('throws a clear error on an unknown species', () => {
  assert.throws(() => packDraftTeam(['not-a-real-pokemon']), /Unknown species/);
});

test('every set gets the four filler coverage moves', () => {
  assert.equal(FILLER_MOVES.length, 4);
  const unpacked = Teams.unpack(packDraftTeam(['snorlax']));
  // unpack returns display names ("Body Slam"); compare by normalised id.
  assert.deepEqual(unpacked[0].moves.map(toID), FILLER_MOVES);
});
