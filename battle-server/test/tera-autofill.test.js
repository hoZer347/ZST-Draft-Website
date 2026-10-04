'use strict';

// The C-tier Tera lock as the VALIDATOR applies it (applyDraftedTera in
// showdown-config/custom-formats.js), against the real champions dex the live format
// uses. Complements test/roster-tera.test.js, which covers the teambuilder side.
//
// The bug these tests pin down: a C-tier set can reach validation with no teraType at
// all (the classic client deletes it on every species change, an imported or pasted set
// never had one, and the champions mod strips it during base validation), restoreTeraTypes
// then falls back to the species' PRIMARY type exactly as vanilla gen 9 does, and
// Aerodactyl drafted Tera Flying arrives as Tera ROCK. checkRoster, correctly, refused
// the team, and the coach could not start their match. The Tera type of a C-tier pick is
// decided at draft time and cannot be chosen, so it is now stamped on instead.

const { test } = require('node:test');
const assert = require('node:assert');
const { Dex } = require('../lib/engine').requireEngine();
const {
  applyDraftedTera, checkRoster, restoreTeraTypes, teraAllowedBaseIds, applyTeraRestriction, baseFormeId,
} = require('../showdown-config/custom-formats');

const dex = Dex.mod('champions');

// CAMKART100's real Season 4 roster, straight out of /api/showdown/roster.
const ROSTER = {
  found: true,
  mons: [
    { tier: 'S', tera: null, name: 'Zygarde-50', slug: 'zygarde' },
    { tier: 'A', tera: null, name: 'M-Barbaracle', slug: 'barbaracle-mega' },
    { tier: 'B', tera: null, name: 'Kommo-o', slug: 'kommoo' },
    { tier: 'B', tera: null, name: 'Weezing-G', slug: 'weezing-galar' },
    { tier: 'A', tera: null, name: 'Jirachi', slug: 'jirachi' },
    { tier: 'C', tera: 'Fire', name: 'Corsola-G', slug: 'corsola-galar' },
    { tier: 'C', tera: 'Grass', name: 'Bastiodon', slug: 'bastiodon' },
    { tier: 'B', tera: null, name: 'Meowscarada', slug: 'meowscarada' },
    { tier: 'C', tera: 'Flying', name: 'Aerodactyl', slug: 'aerodactyl' },
    { tier: 'C', tera: 'Poison', name: 'Butterfree', slug: 'butterfree' },
  ],
};

const set = (o) => Object.assign({ species: '', name: '', item: '' }, o);

// What validateTeam does, minus the roster fetch: snapshot the declared Tera types, let
// the base validation strip them, restore, stamp the drafted ones, then check the roster.
function validate(team, roster = ROSTER) {
  const declared = team.map((s) => s && s.teraType);
  for (const s of team) if (s) delete s.teraType; // the champions mod strips it
  const problems = restoreTeraTypes(team, declared, dex);
  applyDraftedTera(team, roster, dex);
  return problems.concat(checkRoster(team, roster, dex));
}

// ── the regression ──────────────────────────────────────────────────────────

test('REGRESSION: an Aerodactyl set with no Tera type validated as Rock and was rejected', () => {
  // Exactly the old pipeline: restore, then check, with no stamp in between.
  const team = [set({ species: 'Aerodactyl' })];
  restoreTeraTypes(team, [undefined], dex);
  assert.equal(team[0].teraType, 'Rock', 'the vanilla fallback is the primary type');
  const problems = checkRoster(team, ROSTER, dex);
  assert.ok(problems.some((p) => /C-tier.*Flying.*got Rock/i.test(p)), problems.join(' | '));
});

test('the same set now validates, on Flying', () => {
  const team = [set({ species: 'Aerodactyl' })];
  assert.deepEqual(validate(team), []);
  assert.equal(team[0].teraType, 'Flying');
});

test('a C-tier set that declares the wrong Tera type is corrected, not rejected', () => {
  const team = [set({ species: 'Aerodactyl', teraType: 'Rock' })];
  assert.deepEqual(validate(team), []);
  assert.equal(team[0].teraType, 'Flying');
});

test("CAMKART100's full team validates with no Tera types declared anywhere", () => {
  const team = [
    set({ species: 'Zygarde' }),
    set({ species: 'Barbaracle', item: 'Barbaraclite' }), // drafted mega, built as base + stone
    set({ species: 'Kommo-o' }),
    set({ species: 'Corsola-Galar' }),
    set({ species: 'Bastiodon' }),
    set({ species: 'Aerodactyl' }),
  ];
  assert.deepEqual(validate(team), []);
  assert.deepEqual(team.map((s) => s.teraType), [
    'Dragon',  // non-C: the vanilla primary-type fallback, and it can never tera anyway
    'Rock',
    'Dragon',
    'Fire',    // C: drafted
    'Grass',   // C: drafted
    'Flying',  // C: drafted
  ]);
});

// ── what it must not touch ──────────────────────────────────────────────────

test('a non-C pick keeps the type restoreTeraTypes gave it', () => {
  const team = [set({ species: 'Jirachi' }), set({ species: 'Meowscarada', teraType: 'Fire' })];
  assert.deepEqual(applyDraftedTera(team, ROSTER, dex), []);
});

test('a drafted mega is never stamped (megas are Tera-barred at draft, so tera is null)', () => {
  const team = [set({ species: 'Barbaracle', item: 'Barbaraclite', teraType: 'Rock' })];
  assert.deepEqual(applyDraftedTera(team, ROSTER, dex), []);
  assert.equal(team[0].teraType, 'Rock');
});

test('a C-tier pick drafted Tera-barred (tera null) is not stamped', () => {
  const roster = { found: true, mons: [{ slug: 'shedinja', tier: 'C', tera: null }] };
  const team = [set({ species: 'Shedinja', teraType: 'Bug' })];
  assert.deepEqual(applyDraftedTera(team, roster, dex), []);
  assert.equal(team[0].teraType, 'Bug');
});

test('an undrafted species is not stamped', () => {
  const team = [set({ species: 'Pikachu', teraType: 'Electric' })];
  assert.deepEqual(applyDraftedTera(team, ROSTER, dex), []);
  assert.equal(team[0].teraType, 'Electric');
});

test("a species with a locked Tera type keeps its own (Ogerpon-Wellspring is Water)", () => {
  const roster = { found: true, mons: [{ slug: 'ogerpon-wellspring', tier: 'C', tera: 'Grass' }] };
  const team = [set({ species: 'Ogerpon-Wellspring', item: 'Wellspring Mask' })];
  const problems = restoreTeraTypes(team, [undefined], dex);
  assert.deepEqual(applyDraftedTera(team, roster, dex), [], 'the drafted type never overrides a required one');
  assert.equal(team[0].teraType, 'Water');
  assert.deepEqual(problems, []);
});

test('a Tera type the dex does not know is left alone and still reported', () => {
  const roster = { found: true, mons: [{ slug: 'aerodactyl', tier: 'C', tera: 'Sparkle' }] };
  const team = [set({ species: 'Aerodactyl' })];
  restoreTeraTypes(team, [undefined], dex);
  assert.deepEqual(applyDraftedTera(team, roster, dex), []);
  const problems = checkRoster(team, roster, dex);
  assert.ok(problems.some((p) => /C-tier.*Sparkle/i.test(p)), problems.join(' | '));
});

// ── forme handling, shared with the rest of the roster rules ────────────────

test('a move-triggered forme is stamped from its base pick (Keldeo-Resolute)', () => {
  const roster = { found: true, mons: [{ slug: 'keldeo', tier: 'C', tera: 'Fairy' }] };
  const team = [set({ species: 'Keldeo-Resolute', moves: ['Secret Sword'] })];
  assert.equal(baseFormeId(dex, 'Keldeo-Resolute'), 'keldeo');
  assert.deepEqual(applyDraftedTera(team, roster, dex), ['Keldeo-Resolute: Fairy']);
  assert.deepEqual(checkRoster(team, roster, dex), []);
});

test('a regional forme is its own pick and is stamped from its own draft entry', () => {
  const team = [set({ species: 'Corsola-Galar' })];
  assert.deepEqual(applyDraftedTera(team, ROSTER, dex), ['Corsola-Galar: Fire']);
  const other = [set({ species: 'Corsola' })]; // the base was not drafted
  assert.deepEqual(applyDraftedTera(other, ROSTER, dex), []);
});

// ── shape, robustness, and the battle rule downstream ───────────────────────

test('it reports what it changed, and a second pass changes nothing', () => {
  const team = [set({ species: 'Aerodactyl' }), set({ species: 'Butterfree' }), set({ species: 'Jirachi' })];
  assert.deepEqual(applyDraftedTera(team, ROSTER, dex), ['Aerodactyl: Flying', 'Butterfree: Poison']);
  assert.deepEqual(applyDraftedTera(team, ROSTER, dex), []);
});

test('a missing or empty roster stamps nothing and does not throw', () => {
  const team = [set({ species: 'Aerodactyl' })];
  for (const roster of [null, undefined, {}, { found: false }, { found: true, mons: [] }]) {
    assert.deepEqual(applyDraftedTera(team, roster, dex), []);
  }
  assert.equal(team[0].teraType, undefined);
});

test('holes in the team are skipped', () => {
  assert.deepEqual(applyDraftedTera([null, undefined, set({ species: '' })], ROSTER, dex), []);
  assert.deepEqual(applyDraftedTera(null, ROSTER, dex), []);
});

test('the stamped mons are exactly the ones allowed to Terastallize in battle', () => {
  const team = [
    set({ species: 'Aerodactyl' }), set({ species: 'Butterfree' }),
    set({ species: 'Corsola-Galar' }), set({ species: 'Bastiodon' }),
    set({ species: 'Jirachi' }), set({ species: 'Barbaracle', item: 'Barbaraclite' }),
  ];
  const stamped = applyDraftedTera(team, ROSTER, dex).map((s) => s.split(':')[0]);
  const allowed = teraAllowedBaseIds(ROSTER, dex);
  for (const s of team) {
    const canTera = allowed.has(baseFormeId(dex, s.species));
    assert.equal(canTera, stamped.includes(s.species), `${s.species}: stamped iff allowed to tera`);
  }

  // And the battle-start rule agrees: only the stamped mons keep canTerastallize.
  const side = {
    name: 'CAMKART100',
    pokemon: team.map((s) => ({ species: dex.species.get(s.species), canTerastallize: s.teraType || 'Rock' })),
  };
  applyTeraRestriction([side], dex, () => ROSTER);
  const keeps = side.pokemon.filter((p) => p.canTerastallize).map((p) => p.species.name);
  assert.deepEqual(keeps.sort(), ['Aerodactyl', 'Bastiodon', 'Butterfree', 'Corsola-Galar']);
});
