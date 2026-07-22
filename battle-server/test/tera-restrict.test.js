'use strict';

// The live ZST tera rule: only C-tier picks drafted WITH a Tera type may Terastallize
// (never megas or Shedinja). teraAllowedBaseIds derives the allowed set from the roster;
// applyTeraRestriction nulls canTerastallize on every other battle mon. Both use the real
// gen9 Dex so forme handling (mega base collapse) is exercised.

const { test } = require('node:test');
const assert = require('node:assert');
const { Dex } = require('pokemon-showdown');
const { teraAllowedBaseIds, applyTeraRestriction } = require('../showdown-config/custom-formats');

const dex = Dex.mod('gen9');

// A roster with one of every relevant case. Megas/Shedinja are Tera-barred at draft, so
// their `tera` is null (that is what makes them fall out, not any special-casing here).
const ROSTER = {
  found: true,
  mons: [
    { slug: 'gengar', tier: 'C', tera: 'Ghost' },        // C + tera  -> allowed
    { slug: 'pikachu', tier: 'C', tera: 'Water' },       // C + tera  -> allowed
    { slug: 'shedinja', tier: 'C', tera: null },         // C, Tera-barred -> NOT allowed
    { slug: 'charizard-megay', tier: 'S', tera: null },  // mega (base charizard) -> NOT allowed
    { slug: 'garchomp', tier: 'B', tera: 'Ground' },     // non-C -> NOT allowed
    { slug: 'tyranitar', tier: 'A', tera: null },        // non-C -> NOT allowed
  ],
};

test('teraAllowedBaseIds keeps only C-tier picks that have a Tera type', () => {
  const ids = teraAllowedBaseIds(ROSTER, dex);
  assert.ok(ids.has('gengar') && ids.has('pikachu'), 'C-tier tera mons are allowed');
  assert.ok(!ids.has('shedinja'), 'Shedinja (Tera-barred) excluded');
  assert.ok(!ids.has('charizard'), 'mega (collapsed to base charizard) excluded');
  assert.ok(!ids.has('garchomp') && !ids.has('tyranitar'), 'non-C excluded');
});

// A stub battle side: name + pokemon each with a species and a canTerastallize the sim
// would have set (its teraType). applyTeraRestriction should null it for disallowed mons.
function side(name, speciesList) {
  return {
    name,
    pokemon: speciesList.map((sp) => {
      const s = dex.species.get(sp);
      return { species: { name: s.name }, teraType: s.types[0], canTerastallize: s.types[0] };
    }),
  };
}

test('applyTeraRestriction disables tera for everyone except allowed C-tier mons', () => {
  const s = side('coach', ['Gengar', 'Charizard', 'Shedinja', 'Garchomp', 'Pikachu']);
  // Charizard here stands for the mega (built as base Charizard holding its stone).
  applyTeraRestriction([s], dex, () => ROSTER);
  const byName = Object.fromEntries(s.pokemon.map((p) => [p.species.name, p.canTerastallize]));
  assert.ok(byName['Gengar'], 'Gengar (C+tera) can still tera');
  assert.ok(byName['Pikachu'], 'Pikachu (C+tera) can still tera');
  assert.strictEqual(byName['Charizard'], null, 'mega base cannot tera');
  assert.strictEqual(byName['Shedinja'], null, 'Shedinja cannot tera');
  assert.strictEqual(byName['Garchomp'], null, 'non-C cannot tera');
});

test('fails OPEN for a side whose roster fetch throws (leaves tera intact)', () => {
  const s = side('coach', ['Garchomp']);
  const before = s.pokemon[0].canTerastallize;
  applyTeraRestriction([s], dex, () => { throw new Error('roster server down'); });
  assert.strictEqual(s.pokemon[0].canTerastallize, before, 'tera left as-is on fetch failure');
});

test('an unregistered side (no roster) is left untouched', () => {
  const s = side('coach', ['Garchomp']);
  const before = s.pokemon[0].canTerastallize;
  applyTeraRestriction([s], dex, () => ({ found: false }));
  assert.strictEqual(s.pokemon[0].canTerastallize, before);
});
