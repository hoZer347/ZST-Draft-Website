'use strict';

// Unit tests for the ZST Season 4 draft-roster validator (checkRoster in
// showdown-config/custom-formats.js): every mon must be drafted, mega stones must
// sit on the species they evolve AND be a mega the coach drafted, and C-tier picks
// are locked to their drafted Tera type. Uses the stock Dex (stock megas only, since
// the custom megas are merged into node_modules at server start, not in tests).

const { test } = require('node:test');
const assert = require('node:assert');
const { Dex } = require('pokemon-showdown');
const { checkRoster } = require('../showdown-config/custom-formats');

const dex = Dex.mod('gen9');

// A drafted roster: base mons, one mega (Charizard-Mega-Y), a C-tier with a fixed
// Tera type. slug is the sprite slug the .NET endpoint returns.
const ROSTER = {
  found: true,
  mons: [
    { slug: 'venusaur', tier: 'A', tera: 'Grass' },
    { slug: 'blastoise', tier: 'B', tera: 'Water' },
    { slug: 'charizard-megay', tier: 'A', tera: 'Fire' }, // drafted the mega
    { slug: 'gengar', tier: 'C', tera: 'Ghost' },          // C-tier: Tera locked to Ghost
    { slug: 'rotom-wash', tier: 'B', tera: 'Water' },
  ],
};

const set = (o) => Object.assign({ species: '', name: '', item: '', teraType: '' }, o);

test('a team of only drafted mons with correct megas/tera passes', () => {
  const team = [
    set({ species: 'Venusaur' }),
    set({ species: 'Charizard', item: 'Charizardite Y' }), // drafted mega, right holder
    set({ species: 'Gengar', teraType: 'Ghost' }),          // C-tier, correct tera
    set({ species: 'Rotom-Wash' }),
  ];
  assert.deepEqual(checkRoster(team, ROSTER, dex), []);
});

test('an undrafted mon is flagged', () => {
  const team = [set({ species: 'Pikachu' })];
  const problems = checkRoster(team, ROSTER, dex);
  assert.ok(problems.some((p) => /Pikachu.*not on your drafted team/i.test(p)), problems.join(' | '));
});

test('a regional forme the coach did not draft is flagged (Rotom-Heat != Rotom-Wash)', () => {
  const team = [set({ species: 'Rotom-Heat' })];
  const problems = checkRoster(team, ROSTER, dex);
  assert.ok(problems.some((p) => /Rotom-Heat.*not on your drafted team/i.test(p)), problems.join(' | '));
});

test('the drafted mega on its base species is allowed', () => {
  const team = [set({ species: 'Charizard', item: 'Charizardite Y' })];
  assert.deepEqual(checkRoster(team, ROSTER, dex), []);
});

test('a mega stone whose mega was not drafted is flagged (base drafted, mega not)', () => {
  const roster = { found: true, mons: [{ slug: 'charizard', tier: 'A', tera: 'Fire' }] }; // base only
  const team = [set({ species: 'Charizard', item: 'Charizardite Y' })];
  const problems = checkRoster(team, roster, dex);
  assert.ok(problems.some((p) => /not its mega/i.test(p)), problems.join(' | '));
});

test('a mega stone on the wrong species is flagged', () => {
  const team = [set({ species: 'Blastoise', item: 'Charizardite Y' })];
  const problems = checkRoster(team, ROSTER, dex);
  assert.ok(problems.some((p) => /belongs to Charizard/i.test(p)), problems.join(' | '));
});

test('a C-tier pick on the wrong Tera type is flagged', () => {
  const team = [set({ species: 'Gengar', teraType: 'Fairy' })];
  const problems = checkRoster(team, ROSTER, dex);
  assert.ok(problems.some((p) => /C-tier.*Ghost/i.test(p)), problems.join(' | '));
});

test('a C-tier pick on its drafted Tera type passes', () => {
  const team = [set({ species: 'Gengar', teraType: 'Ghost' })];
  assert.deepEqual(checkRoster(team, ROSTER, dex), []);
});

test('a non-C pick is not Tera-locked', () => {
  const team = [set({ species: 'Venusaur', teraType: 'Steel' })]; // A-tier, any tera
  assert.deepEqual(checkRoster(team, ROSTER, dex), []);
});

// ── Regional formes are distinct draft picks (Alolan Muk != Kanto Muk) ──────

test('drafting Alolan Muk does NOT allow bringing Kanto Muk', () => {
  const roster = { found: true, mons: [{ slug: 'muk-alola', tier: 'B', tera: null }] };
  const problems = checkRoster([set({ species: 'Muk' })], roster, dex);
  assert.ok(problems.some((p) => /Muk is not on your drafted team/i.test(p)), problems.join(' | '));
});

test('drafting Kanto Muk does NOT allow bringing Alolan Muk', () => {
  const roster = { found: true, mons: [{ slug: 'muk', tier: 'B', tera: null }] };
  const problems = checkRoster([set({ species: 'Muk-Alola' })], roster, dex);
  assert.ok(problems.some((p) => /Muk-Alola is not on your drafted team/i.test(p)), problems.join(' | '));
});

test('the drafted regional forme is allowed on its exact forme', () => {
  const roster = { found: true, mons: [{ slug: 'muk-alola', tier: 'B', tera: null }] };
  assert.deepEqual(checkRoster([set({ species: 'Muk-Alola' })], roster, dex), []);
});

test('two distinct formes drafted are each allowed independently', () => {
  const roster = {
    found: true,
    mons: [{ slug: 'rotom-wash', tier: 'B', tera: null }, { slug: 'rotom-heat', tier: 'B', tera: null }],
  };
  assert.deepEqual(checkRoster([set({ species: 'Rotom-Wash' }), set({ species: 'Rotom-Heat' })], roster, dex), []);
});

test('an undrafted battle forme sibling is flagged (Urshifu-Single vs drafted Rapid-Strike)', () => {
  const roster = { found: true, mons: [{ slug: 'urshifu-rapidstrike', tier: 'A', tera: null }] };
  // Base Urshifu is the Single-Strike style; only Rapid-Strike was drafted.
  const problems = checkRoster([set({ species: 'Urshifu' })], roster, dex);
  assert.ok(problems.some((p) => /Urshifu is not on your drafted team/i.test(p)), problems.join(' | '));
});

// ── Mega X / Y are separate megas; the base does not grant either ───────────

test('the wrong mega stone (X) is flagged when only the Y mega was drafted', () => {
  // ROSTER drafts charizard-megay; Charizardite X evolves the same base but is a
  // different mega the coach never drafted.
  const problems = checkRoster([set({ species: 'Charizard', item: 'Charizardite X' })], ROSTER, dex);
  assert.ok(problems.some((p) => /not its mega \(Charizard-Mega-X\)/i.test(p)), problems.join(' | '));
});

test('the base of a drafted mega is allowed with no stone at all', () => {
  // Drafting M-Charizard Y lets you bring plain Charizard (no mega) too.
  assert.deepEqual(checkRoster([set({ species: 'Charizard' })], ROSTER, dex), []);
});

test('a roster entry with no slug falls back to matching by name', () => {
  // The .NET endpoint returns slug = PokemonEntry.Sprite, which can be null; the
  // validator then keys off the pokemon name instead.
  const roster = { found: true, mons: [{ slug: null, name: 'Snorlax', tier: 'B', tera: null }] };
  assert.deepEqual(checkRoster([set({ species: 'Snorlax' })], roster, dex), []);
  const problems = checkRoster([set({ species: 'Gengar' })], roster, dex);
  assert.ok(problems.some((p) => /Gengar is not on your drafted team/i.test(p)), problems.join(' | '));
});
