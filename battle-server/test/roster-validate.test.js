'use strict';

// Unit tests for the ZST Season 4 draft-roster validator (checkRoster in
// showdown-config/custom-formats.js): every mon must be drafted, mega stones must
// sit on the species they evolve AND be a mega the coach drafted, and C-tier picks
// are locked to their drafted Tera type. Runs against the champions dex the live
// format uses, so the Champions-only megas and its megaStone MAP format (rather
// than the older plain-string one) are exercised for real.

const { test } = require('node:test');
const assert = require('node:assert');
const { Dex } = require('../lib/engine').requireEngine();
const { checkRoster, restoreTeraTypes, baseFormeId } = require('../showdown-config/custom-formats');

const dex = Dex.mod('champions');

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
  assert.ok(problems.some((p) => /didn't draft its Mega-Y forme/i.test(p)), problems.join(' | '));
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
  assert.ok(problems.some((p) => /didn't draft its Mega-X forme \(Charizard-Mega-X\)/i.test(p)), problems.join(' | '));
});

test('the base of a drafted mega is allowed with no stone at all', () => {
  // Drafting M-Charizard Y lets you bring plain Charizard (no mega) too.
  assert.deepEqual(checkRoster([set({ species: 'Charizard' })], ROSTER, dex), []);
});

// ── Battle-only formes reached via a held item (crowned) and origin formes ──

test('a crowned forme built as base + rusted item is allowed when the crowned forme was drafted', () => {
  const roster = { found: true, mons: [{ slug: 'zamazenta-crowned', tier: 'S', tera: null }] };
  // Built as base Zamazenta holding Rusted Shield (it transforms to Crowned in battle).
  assert.deepEqual(checkRoster([set({ species: 'Zamazenta', item: 'Rusted Shield' })], roster, dex), []);
});

test('base + rusted item is flagged when only the base (not the crowned forme) was drafted', () => {
  const roster = { found: true, mons: [{ slug: 'zamazenta', tier: 'S', tera: null }] }; // base only
  const problems = checkRoster([set({ species: 'Zamazenta', item: 'Rusted Shield' })], roster, dex);
  assert.ok(problems.some((p) => /didn't draft its Crowned forme \(Zamazenta-Crowned\)/i.test(p)), problems.join(' | '));
});

test('a rusted item on the wrong species is flagged', () => {
  const roster = { found: true, mons: [{ slug: 'garchomp', tier: 'A', tera: null }] };
  const problems = checkRoster([set({ species: 'Garchomp', item: 'Rusted Shield' })], roster, dex);
  assert.ok(problems.some((p) => /belongs to Zamazenta/i.test(p)), problems.join(' | '));
});

test('an origin forme must be drafted (base validation rewrites base+orb to the Origin species)', () => {
  // By the time checkRoster runs, base validation has already set species to
  // Giratina-Origin; a coach who only drafted Altered Giratina is flagged.
  const roster = { found: true, mons: [{ slug: 'giratina', tier: 'S', tera: null }] };
  const problems = checkRoster([set({ species: 'Giratina-Origin', item: 'Griseous Core' })], roster, dex);
  assert.ok(problems.some((p) => /Giratina-Origin is not on your drafted team/i.test(p)), problems.join(' | '));
});

test('the drafted origin forme is allowed', () => {
  const roster = { found: true, mons: [{ slug: 'giratina-origin', tier: 'S', tera: null }] };
  assert.deepEqual(checkRoster([set({ species: 'Giratina-Origin', item: 'Griseous Core' })], roster, dex), []);
});

// ── Champions megas: native to the mod, and their stones map base -> mega ──

test('a Champions-only mega is allowed on its base holding the stone', () => {
  const roster = { found: true, mons: [{ slug: 'falinks-mega', tier: 'B', tera: null }] };
  assert.deepEqual(checkRoster([set({ species: 'Falinks', item: 'Falinksite' })], roster, dex), []);
});

test("a Champions mega's stone on the wrong species is flagged", () => {
  const roster = { found: true, mons: [{ slug: 'garchomp', tier: 'A', tera: null }] };
  const problems = checkRoster([set({ species: 'Garchomp', item: 'Falinksite' })], roster, dex);
  assert.ok(problems.some((p) => /belongs to Falinks/i.test(p)), problems.join(' | '));
});

// ── restoreTeraTypes: Champions strips teraType, the league puts it back ────

test('restoreTeraTypes puts a declared Tera type back on the set', () => {
  const team = [set({ species: 'Gengar' })]; // the base validation deleted teraType
  assert.deepEqual(restoreTeraTypes(team, ['Ghost'], dex), []);
  assert.equal(team[0].teraType, 'Ghost');
});

test('restoreTeraTypes defaults an undeclared type to the species primary type', () => {
  const team = [set({ species: 'Garchomp' })];
  assert.deepEqual(restoreTeraTypes(team, [undefined], dex), []);
  assert.equal(team[0].teraType, 'Dragon');
});

test('restoreTeraTypes rejects a type that does not exist', () => {
  const team = [set({ species: 'Gengar' })];
  const problems = restoreTeraTypes(team, ['Sound'], dex);
  assert.ok(problems.some((p) => /Terastal type \(Sound\) is invalid/i.test(p)), problems.join(' | '));
});

test('a roster entry with no slug falls back to matching by name', () => {
  // The .NET endpoint returns slug = PokemonEntry.Sprite, which can be null; the
  // validator then keys off the pokemon name instead.
  const roster = { found: true, mons: [{ slug: null, name: 'Snorlax', tier: 'B', tera: null }] };
  assert.deepEqual(checkRoster([set({ species: 'Snorlax' })], roster, dex), []);
  const problems = checkRoster([set({ species: 'Gengar' })], roster, dex);
  assert.ok(problems.some((p) => /Gengar is not on your drafted team/i.test(p)), problems.join(' | '));
});

// ── Move-triggered formes: Keldeo-Resolute is the same pick as Keldeo ────────

test('a drafted Keldeo may be brought as Keldeo-Resolute', () => {
  const roster = { found: true, mons: [{ slug: 'keldeo', tier: 'A', tera: null }] };
  assert.deepEqual(checkRoster([set({ species: 'Keldeo-Resolute' })], roster, dex), []);
  assert.deepEqual(checkRoster([set({ species: 'Keldeo' })], roster, dex), []);
});

test('a C-tier Keldeo keeps its drafted Tera type in Resolute forme', () => {
  const roster = { found: true, mons: [{ slug: 'keldeo', tier: 'C', tera: 'Water' }] };
  assert.deepEqual(checkRoster([set({ species: 'Keldeo-Resolute', teraType: 'Water' })], roster, dex), []);
  const problems = checkRoster([set({ species: 'Keldeo-Resolute', teraType: 'Fairy' })], roster, dex);
  assert.ok(problems.some((p) => /must use its drafted Tera type Water/i.test(p)), problems.join(' | '));
});

test('Keldeo-Resolute is still blocked for a coach who drafted neither forme', () => {
  const problems = checkRoster([set({ species: 'Keldeo-Resolute' })], ROSTER, dex);
  assert.ok(problems.some((p) => /Keldeo-Resolute is not on your drafted team/i.test(p)), problems.join(' | '));
});

test('only move-triggered formes collapse: Meloetta and Rayquaza-Mega are unaffected', () => {
  // Meloetta-Pirouette and Rayquaza-Mega also have a requiredMove, but they are
  // battle-only formes, so they keep collapsing via battleOnly, not the move rule,
  // and a distinct-forme pick (Landorus-Therian) stays its own draft pick.
  assert.equal(baseFormeId(dex, 'Meloetta-Pirouette'), 'meloetta');
  assert.equal(baseFormeId(dex, 'Rayquaza-Mega'), 'rayquaza');
  assert.equal(baseFormeId(dex, 'Landorus-Therian'), 'landorustherian');
  assert.equal(baseFormeId(dex, 'Keldeo-Resolute'), 'keldeo');
});

// ── Type-item formes: every Silvally memory is the same pick as Silvally ────

test('a drafted Silvally may be brought as any memory forme', () => {
  const roster = { found: true, mons: [{ slug: 'silvally', tier: 'B', tera: null }] };
  const team = [set({ species: 'Silvally-Water', item: 'Water Memory' })];
  assert.deepEqual(checkRoster(team, roster, dex), []);
  assert.deepEqual(checkRoster([set({ species: 'Silvally' })], roster, dex), []);
  assert.deepEqual(checkRoster([set({ species: 'Silvally-Fairy', item: 'Fairy Memory' })], roster, dex), []);
});

test('a Genesect drive forme and an Arceus plate forme collapse the same way', () => {
  assert.equal(baseFormeId(dex, 'Genesect-Douse'), 'genesect');
  assert.equal(baseFormeId(dex, 'Arceus-Water'), 'arceus');
  assert.equal(baseFormeId(dex, 'Silvally-Water'), 'silvally');
});

test('item formes that are their own mon stay distinct picks', () => {
  // Restatted (Origin, Ultra, Zygarde-Mega) or re-abilitied (Ogerpon masks,
  // Venomicon-Epilogue): a different mon, drafted on its own row.
  assert.equal(baseFormeId(dex, 'Giratina-Origin'), 'giratinaorigin');
  assert.equal(baseFormeId(dex, 'Necrozma-Ultra'), 'necrozmaultra');
  assert.equal(baseFormeId(dex, 'Ogerpon-Wellspring'), 'ogerponwellspring');
});

test('a Silvally forme is still blocked for a coach who did not draft Silvally', () => {
  const problems = checkRoster([set({ species: 'Silvally-Water', item: 'Water Memory' })], ROSTER, dex);
  assert.ok(problems.some((p) => /Silvally-Water is not on your drafted team/i.test(p)), problems.join(' | '));
});

// ── Signature-ability formes (Greninja-Bond, Rockruff-Dusk) ─────────────────
// A coach builds base Greninja and picks Battle Bond out of the teambuilder's
// "Special Event Ability" slot; Showdown's own validator then rewrites the set to
// Greninja-Bond (the only species the ability's +1 Atk/SpA/Spe on a KO fires for),
// so checkRoster sees a species the coach never literally drafted. It is the same
// pick, so it must pass.

test('Battle Bond Greninja is the same draft pick as Greninja', () => {
  const roster = { found: true, mons: [{ slug: 'greninja', tier: 'B', tera: null }] };
  assert.deepEqual(checkRoster([set({ species: 'Greninja-Bond' })], roster, dex), []);
  assert.deepEqual(checkRoster([set({ species: 'Greninja' })], roster, dex), []);
});

test('a C-tier Greninja keeps its drafted Tera type in Bond forme', () => {
  const roster = { found: true, mons: [{ slug: 'greninja', tier: 'C', tera: 'Water' }] };
  assert.deepEqual(checkRoster([set({ species: 'Greninja-Bond', teraType: 'Water' })], roster, dex), []);
  const problems = checkRoster([set({ species: 'Greninja-Bond', teraType: 'Dark' })], roster, dex);
  assert.ok(problems.some((p) => /must use its drafted Tera type Water/i.test(p)), problems.join(' | '));
});

test('Greninja-Bond is still blocked for a coach who drafted neither forme', () => {
  const problems = checkRoster([set({ species: 'Greninja-Bond' })], ROSTER, dex);
  assert.ok(problems.some((p) => /Greninja-Bond is not on your drafted team/i.test(p)), problems.join(' | '));
});

test('only signature-ability formes collapse, and only onto their own base', () => {
  assert.equal(baseFormeId(dex, 'Greninja-Bond'), 'greninja');
  assert.equal(baseFormeId(dex, 'Rockruff-Dusk'), 'rockruff');
  // Greninja-Ash is the gen-7 Battle Bond form change, a battle-only forme the league
  // never fields; it collapses via battleOnly, not this rule.
  assert.equal(baseFormeId(dex, 'Greninja-Ash'), 'greninja');
  // Zygarde-Complete's only ability is Zygarde's S slot too, but it is a battle-only
  // forme of BOTH Zygarde and Zygarde-10%, so it must not be folded onto either.
  assert.equal(baseFormeId(dex, 'Zygarde-Complete'), 'zygardecomplete');
  // A plain alternate forme with its own ability set stays its own pick.
  assert.equal(baseFormeId(dex, 'Rotom-Wash'), 'rotomwash');
});
