'use strict';

// The C-tier Tera lock as the TEAMBUILDER applies it (lib/roster-tera.js, inlined into
// the self-hosted client by scripts/serve-client.js as AUTO_TERA). A coach never chooses
// a C-tier pick's Tera type, it is fixed at draft time, so every route a set can enter
// the builder by must leave it carrying the drafted type.
//
// The regression these tests exist for: Aerodactyl drafted Tera FLYING. The classic
// client deletes set.teraType whenever the species changes, and the server's fallback is
// the species' PRIMARY type, so an unstamped Aerodactyl reaches validation as Tera ROCK
// and the team is rejected. Every dual-type C-tier pick whose drafted type isn't its
// first type has the same failure mode, so the cases below use several of them.

const { test } = require('node:test');
const assert = require('node:assert');
const { draftedTeraMap, applyDraftedTera, teraID } = require('../lib/roster-tera');

// CAMKART100's real Season 4 roster shape, straight out of /api/showdown/roster: four
// C-tier picks (three of them teraing to something that is NOT their primary type), a
// drafted mega, and non-C picks with no tera at all.
const MONS = [
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
];

const MAP = draftedTeraMap(MONS);

// A teambuilder set, the shape client-teambuilder.js keeps in room.curSetList.
const set = (o) => Object.assign({
  name: '', species: '', item: '', ability: '', moves: [], nature: '', level: 50,
  evs: { hp: 11, atk: 11, def: 11, spa: 11, spd: 11, spe: 11 },
}, o);

// ── draftedTeraMap ──────────────────────────────────────────────────────────

test('the map holds only C-tier picks that were drafted with a Tera type', () => {
  assert.deepEqual(Object.keys(MAP).sort(), [
    'aerodactyl', 'bastiodon', 'butterfree', 'corsolag', 'corsolagalar',
  ].sort());
});

test('each C-tier pick is keyed by BOTH its display name and its sprite slug', () => {
  assert.equal(MAP[teraID('Corsola-G')], 'Fire');       // the name the .NET pool uses
  assert.equal(MAP[teraID('corsola-galar')], 'Fire');   // the slug the dex uses
});

test('non-C picks and Tera-barred C picks never enter the map', () => {
  const map = draftedTeraMap([
    { tier: 'S', tera: 'Fire', name: 'Charizard', slug: 'charizard' }, // non-C: no lock
    { tier: 'C', tera: null, name: 'Shedinja', slug: 'shedinja' },     // C, Tera-barred
    { tier: 'C', tera: '', name: 'Sableye', slug: 'sableye' },         // C, blank tera
  ]);
  assert.deepEqual(map, {});
});

test('a lowercase tier string still counts as C', () => {
  assert.deepEqual(draftedTeraMap([{ tier: 'c', tera: 'Ice', slug: 'lapras' }]), { lapras: 'Ice' });
});

test('junk roster entries are skipped, not thrown on', () => {
  assert.deepEqual(draftedTeraMap([null, undefined, {}, { tier: 'C' }, 'nonsense']), {});
  assert.deepEqual(draftedTeraMap(null), {});
  assert.deepEqual(draftedTeraMap(undefined), {});
});

// ── applyDraftedTera: the regression ────────────────────────────────────────

test('REGRESSION: a species change wipes teraType, and the stamp puts Flying back', () => {
  // What setPokemon leaves behind: "if (set.teraType) delete set.teraType".
  const team = [set({ species: 'Aerodactyl', name: 'Aerodactyl' })];
  assert.equal(team[0].teraType, undefined);
  assert.deepEqual(applyDraftedTera(team, MAP), [0]);
  assert.equal(team[0].teraType, 'Flying', 'drafted Flying, not the Rock the dex would default to');
});

test('a wrong Tera type left by the client default is corrected', () => {
  // The builder renders the Tera <select> defaulted to species.types[0] and saves it
  // back on any edit, which is exactly how Aerodactyl ends up "given Rock".
  const team = [set({ species: 'Aerodactyl', teraType: 'Rock' })];
  assert.deepEqual(applyDraftedTera(team, MAP), [0]);
  assert.equal(team[0].teraType, 'Flying');
});

test('every C-tier pick whose drafted type is not its primary type is fixed', () => {
  const team = [
    set({ species: 'Aerodactyl' }),    // Rock/Flying, drafted Flying
    set({ species: 'Butterfree' }),    // Bug/Flying,  drafted Poison (neither type)
    set({ species: 'Bastiodon' }),     // Rock/Steel,  drafted Grass (neither type)
    set({ species: 'Corsola-Galar' }), // Ghost,       drafted Fire
  ];
  assert.deepEqual(applyDraftedTera(team, MAP), [0, 1, 2, 3]);
  assert.deepEqual(team.map((s) => s.teraType), ['Flying', 'Poison', 'Grass', 'Fire']);
});

// ── applyDraftedTera: what it must NOT touch ────────────────────────────────

test('a non-C drafted mon is left with no Tera type at all', () => {
  const team = [set({ species: 'Jirachi' }), set({ species: 'Meowscarada' })];
  assert.deepEqual(applyDraftedTera(team, MAP), []);
  assert.ok(team.every((s) => s.teraType === undefined), 'no type invented for a mon that cannot tera');
});

test('a non-C mon that somehow carries a Tera type keeps it', () => {
  const team = [set({ species: 'Kommo-o', teraType: 'Steel' })];
  assert.deepEqual(applyDraftedTera(team, MAP), []);
  assert.equal(team[0].teraType, 'Steel');
});

test('a species the coach did not draft is untouched', () => {
  const team = [set({ species: 'Pikachu' }), set({ species: 'Aerodactyl-Mega' })];
  assert.deepEqual(applyDraftedTera(team, MAP), []);
  assert.ok(team.every((s) => s.teraType === undefined));
});

test('nothing but teraType is ever modified', () => {
  const before = set({ species: 'Aerodactyl', name: 'Rocky', item: 'Life Orb', ability: 'Unnerve', moves: ['Dual Wingbeat'] });
  const after = Object.assign({}, before);
  applyDraftedTera([after], MAP);
  for (const key of Object.keys(before)) {
    assert.deepEqual(after[key], before[key], `${key} must not change`);
  }
  assert.equal(after.teraType, 'Flying');
});

// ── matching rules ──────────────────────────────────────────────────────────

test('species matching ignores case, spaces and punctuation', () => {
  for (const species of ['aerodactyl', 'Aerodactyl', 'AERODACTYL', 'Aero dactyl', 'Aero-dactyl']) {
    const team = [set({ species })];
    applyDraftedTera(team, MAP);
    assert.equal(team[0].teraType, 'Flying', species);
  }
});

test('a pick is matched under either spelling of its name', () => {
  const byName = [set({ species: 'Corsola-G' })];
  const bySlug = [set({ species: 'Corsola-Galar' })];
  applyDraftedTera(byName, MAP);
  applyDraftedTera(bySlug, MAP);
  assert.equal(byName[0].teraType, 'Fire');
  assert.equal(bySlug[0].teraType, 'Fire');
});

// ── shape and idempotence ───────────────────────────────────────────────────

test('a second pass reports no changes', () => {
  const team = [set({ species: 'Aerodactyl' }), set({ species: 'Butterfree' })];
  assert.deepEqual(applyDraftedTera(team, MAP), [0, 1]);
  assert.deepEqual(applyDraftedTera(team, MAP), [], 'idempotent, so save/update hooks never redraw in a loop');
});

test('the changed indices come back, so a caller can skip the redraw', () => {
  const team = [set({ species: 'Jirachi' }), set({ species: 'Bastiodon' }), set({ species: 'Kommo-o' })];
  assert.deepEqual(applyDraftedTera(team, MAP), [1]);
});

test('holes, empty sets and a missing set list are tolerated', () => {
  assert.deepEqual(applyDraftedTera([null, undefined, {}, set({ species: '' })], MAP), []);
  assert.deepEqual(applyDraftedTera(undefined, MAP), []);
  assert.deepEqual(applyDraftedTera(null, MAP), []);
  assert.deepEqual(applyDraftedTera('not a team', MAP), []);
});

test('no roster (the fetch failed) means no stamping, never a crash', () => {
  const team = [set({ species: 'Aerodactyl' })];
  assert.deepEqual(applyDraftedTera(team, null), []);
  assert.deepEqual(applyDraftedTera(team, draftedTeraMap([])), []);
  assert.equal(team[0].teraType, undefined, 'fails open: the server stamps it too');
});

// ── the routes a set actually arrives by, end to end ────────────────────────

test('a full build session keeps every C-tier pick on its drafted type', () => {
  // The four ways a set enters the builder, in one team.
  const team = [];

  // 1. Species picked from the list: setPokemon deletes teraType.
  team.push(set({ species: 'Aerodactyl', name: 'Aerodactyl' }));
  applyDraftedTera(team, MAP);

  // 2. Team imported from text: Showdown's export format only carries a Tera line if
  //    the exporting client wrote one, and the league client used to strip it.
  team.push(set({ species: 'Butterfree', item: 'Focus Sash', moves: ['Quiver Dance'] }));
  applyDraftedTera(team, MAP);

  // 3. A set pasted from the clipboard, carrying whatever tera it was copied with.
  team.push(set({ species: 'Bastiodon', teraType: 'Rock' }));
  applyDraftedTera(team, MAP);

  // 4. A Smogon sample set loaded over the top (client-teambuilder.js forces
  //    sampleSet.teraType || species.types[0], so it lands on Ghost here).
  team.push(set({ species: 'Corsola-Galar', teraType: 'Ghost' }));
  applyDraftedTera(team, MAP);

  // 5. Plus a non-C pick, which must still have no Tera type.
  team.push(set({ species: 'Zygarde' }));
  applyDraftedTera(team, MAP);

  assert.deepEqual(team.map((s) => s.teraType), ['Flying', 'Poison', 'Grass', 'Fire', undefined]);
});

test('re-picking the same species after the stamp still ends on the drafted type', () => {
  const team = [set({ species: 'Aerodactyl' })];
  applyDraftedTera(team, MAP);
  delete team[0].teraType; // setPokemon again
  applyDraftedTera(team, MAP);
  assert.equal(team[0].teraType, 'Flying');
});

test('swapping a C-tier pick for another C-tier pick swaps the Tera type with it', () => {
  const team = [set({ species: 'Aerodactyl' })];
  applyDraftedTera(team, MAP);
  assert.equal(team[0].teraType, 'Flying');
  team[0].species = 'Butterfree';
  delete team[0].teraType; // what setPokemon does on the swap
  applyDraftedTera(team, MAP);
  assert.equal(team[0].teraType, 'Poison');
});

// ── the injected client script itself ───────────────────────────────────────
//
// The teambuilder runs this logic as a <script> serve-client.js splices into the
// self-hosted client (AUTO_TERA), so pull that exact block out of the source, run it in
// a vm against a stand-in TeambuilderRoom, and drive the same routes a coach does. This
// is what catches the injection drifting away from the library it is supposed to use.

const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const SERVE_CLIENT = fs.readFileSync(path.join(__dirname, '..', 'scripts', 'serve-client.js'), 'utf8');

function injectedTeraScript() {
  const head = 'const AUTO_TERA = `';
  const start = SERVE_CLIENT.indexOf(head);
  assert.ok(start > 0, 'serve-client.js still defines AUTO_TERA');
  const end = SERVE_CLIENT.indexOf('</script>`;', start);
  assert.ok(end > start, 'the AUTO_TERA template literal is still terminated by </script>`;');
  const body = SERVE_CLIENT.slice(start + head.length, end);
  assert.ok(body.includes('${TERA_LIB}'), 'AUTO_TERA inlines lib/roster-tera.js rather than its own copy');
  // AUTO_TERA also inlines serve-client's shared loginName() helper (LOGIN_NAME_FN);
  // substitute it the same way so the block runs standalone here.
  assert.ok(body.includes('${LOGIN_NAME_FN}'), 'AUTO_TERA inlines the shared loginName helper');
  return body
    .replace('${TERA_LIB}', fs.readFileSync(path.join(__dirname, '..', 'lib', 'roster-tera.js'), 'utf8'))
    .replace('${LOGIN_NAME_FN}', loginNameFn())
    .replace(/<\/?script>/g, '');
}

// The LOGIN_NAME_FN template literal from serve-client.js, verbatim.
function loginNameFn() {
  const head = 'const LOGIN_NAME_FN = `';
  const start = SERVE_CLIENT.indexOf(head);
  assert.ok(start > 0, 'serve-client.js still defines LOGIN_NAME_FN');
  const end = SERVE_CLIENT.indexOf('`;', start + head.length);
  assert.ok(end > start, 'the LOGIN_NAME_FN template literal is still terminated');
  return SERVE_CLIENT.slice(start + head.length, end);
}

// A stand-in for the classic client's TeambuilderRoom: only the methods AUTO_TERA hooks,
// with the one behaviour that matters (setPokemon DELETES the set's Tera type). A FRESH
// class per boot, because the injection patches the prototype and each boot must start
// from an unpatched one.
function makeRoomClass() {
  function TeambuilderRoom() {}
  TeambuilderRoom.prototype.setPokemon = function (species) {
    this.curSet = { species: species, name: species };
    this.curSetList[this.i || 0] = this.curSet;
    if (this.curSet.teraType) delete this.curSet.teraType;
  };
  TeambuilderRoom.prototype.save = function () { this.saves++; };
  TeambuilderRoom.prototype.update = function () { this.updates++; };
  TeambuilderRoom.prototype.updateSetTop = function () { this.redraws++; };
  return TeambuilderRoom;
}

function room(sandbox, format) {
  const r = new sandbox.TeambuilderRoom();
  r.curTeam = { format: format };
  r.curSetList = [];
  r.saves = r.updates = r.redraws = 0;
  return r;
}

// Boots the injected script with the roster fetch left PENDING, so a test can check what
// happens both before and after it lands.
function boot(mons) {
  let resolve;
  const pending = new Promise((r) => { resolve = r; });
  const sandbox = {
    TeambuilderRoom: makeRoomClass(),
    app: { rooms: {} },
    sessionStorage: { getItem: () => 'CAMKART100' },
    fetch: () => pending.then(() => ({ json: () => Promise.resolve({ found: true, mons: mons }) })),
    setInterval: (fn) => { fn(); return 0; }, // patch() succeeds on the first poll
    clearInterval: () => {},
    setTimeout: () => 0,
    console: console,
  };
  sandbox.window = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(injectedTeraScript(), sandbox);
  // Let the roster land: resolve the fetch, then drain the promise chain behind it.
  const load = async () => { resolve(); await pending; await null; await null; await null; };
  return { sandbox, load };
}

test('the injected script stamps a C-tier pick the moment its species is chosen', async () => {
  const { sandbox, load } = boot(MONS);
  await load();
  const r = room(sandbox, 'gen9championsnatdexzstseason4');
  sandbox.app.rooms.teambuilder = r;

  r.setPokemon('Aerodactyl');
  assert.equal(r.curSetList[0].teraType, 'Flying');
  assert.ok(r.redraws > 0, 'the set summary is redrawn so the coach sees the type');
  assert.ok(r.saves > 0, 'and the team is saved with it');
});

test('the injected script stamps an imported team on save, and on update', async () => {
  const { sandbox, load } = boot(MONS);
  await load();

  const onSave = room(sandbox, 'gen9championsnatdexzstseason4');
  onSave.curSetList = [{ species: 'Butterfree' }, { species: 'Bastiodon', teraType: 'Rock' }];
  onSave.save();
  assert.deepEqual(onSave.curSetList.map((s) => s.teraType), ['Poison', 'Grass']);

  const onUpdate = room(sandbox, 'gen9championsnatdexzstseason4');
  onUpdate.curSetList = [{ species: 'Corsola-Galar' }];
  onUpdate.update();
  assert.equal(onUpdate.curSetList[0].teraType, 'Fire');
  assert.ok(sandbox.window.zstApplyDraftedTera, 'the shared library is what does the work');
});

test('a team open before the roster lands is stamped as soon as it does', async () => {
  const { sandbox, load } = boot(MONS);
  const r = room(sandbox, 'gen9championsnatdexzstseason4');
  r.curSetList = [{ species: 'Aerodactyl' }];
  sandbox.app.rooms.teambuilder = r;

  r.save();
  assert.equal(r.curSetList[0].teraType, undefined, 'nothing is stamped from a roster we do not have yet');

  await load();
  assert.equal(r.curSetList[0].teraType, 'Flying');
  assert.ok(r.updates > 0, 'and the open builder is redrawn');
});

test('a scrims team is left alone: no roster check there, so no Tera lock', async () => {
  const { sandbox, load } = boot(MONS);
  await load();
  const r = room(sandbox, 'gen9championsnatdexscrims');
  r.curSetList = [{ species: 'Aerodactyl', teraType: 'Rock' }];
  r.save();
  assert.equal(r.curSetList[0].teraType, 'Rock');
});

test('a failed roster fetch leaves the builder working, just unstamped', async () => {
  const sandbox = {
    TeambuilderRoom: makeRoomClass(),
    app: { rooms: {} },
    sessionStorage: { getItem: () => 'CAMKART100' },
    fetch: () => Promise.reject(new Error('offline')),
    setInterval: (fn) => { fn(); return 0; },
    clearInterval: () => {}, setTimeout: () => 0, console: console,
  };
  sandbox.window = sandbox;
  vm.createContext(sandbox);
  vm.runInContext(injectedTeraScript(), sandbox);
  await null; await null;

  const r = room(sandbox, 'gen9championsnatdexzstseason4');
  r.setPokemon('Aerodactyl');
  r.save();
  r.update();
  assert.equal(r.curSetList[0].teraType, undefined, 'the server stamps it too, so this fails open');
});
