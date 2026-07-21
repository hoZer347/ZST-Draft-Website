'use strict';

const { test } = require('node:test');
const assert = require('node:assert');
const { spawnSync } = require('node:child_process');
const path = require('node:path');
const { Teams, Dex, TeamValidator, toID } = require('pokemon-showdown');
const { buildRandomTeam, buildTeamWithTera, randomSet, movePool, RESTRICTIONS, RESTRICTION_FORMAT } = require('../lib/random-team');

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

// ── Tera type: only C-tier mons carry one (from the draft), and the runner teras
// exactly those, ASAP. The set + team-build side of that is guarded here; the AI
// side is guarded by the battle integration test.

test('a Tera type is baked onto the set only when given one (C-tier mons)', () => {
  assert.equal(randomSet({ s: 'garchomp', t: 'Steel' }).teraType, 'Steel', 'C-tier: teras to its drafted type');
  assert.equal(randomSet({ s: 'garchomp', t: null }).teraType, undefined, 'non-C: no Tera type');
  assert.equal(randomSet('garchomp').teraType, undefined, 'a bare slug is treated as non-C');
});

test('buildTeamWithTera reports exactly the mons that carry a Tera type', () => {
  const { team, teraNames } = buildTeamWithTera(
    [{ s: 'pikachu', t: 'Water' }, { s: 'garchomp', t: null }, { s: 'snorlax', t: 'Fairy' }], 6);
  assert.deepEqual([...teraNames].sort(), ['Pikachu', 'Snorlax'], 'the two tera-typed mons, not Garchomp');
  const packed = Teams.unpack(team);
  assert.equal(packed.find((m) => m.species === 'Pikachu').teraType, 'Water');
  assert.equal(packed.find((m) => m.species === 'Garchomp').teraType || '', '');
});

test('in a real battle, the C-tier mon teras to its type and non-C mons never do', () => {
  // Home leads a C-tier Pikachu (Water) alongside a non-C Garchomp; the away side
  // is all non-C. Pikachu is active from turn 1, so it teras that turn.
  const spec = { matches: [{
    homeName: 'H', awayName: 'A',
    homeTeam: [{ s: 'pikachu', t: 'Water' }, { s: 'garchomp', t: null }],
    awayTeam: [{ s: 'snorlax', t: null }, { s: 'skarmory', t: null }],
  }] };
  const script = path.join(__dirname, '..', 'scripts', 'simulate-season.js');
  const res = spawnSync('node', [script], { input: JSON.stringify(spec), encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  assert.equal(res.status, 0, res.stderr);

  const teras = JSON.parse(res.stdout)[0].log.split('\n').filter((l) => l.startsWith('|-terastallize|'));
  assert.ok(teras.some((l) => /Pikachu\|Water/.test(l)), `C-tier Pikachu should tera to Water; got ${JSON.stringify(teras)}`);
  assert.ok(!teras.some((l) => l.includes('p2')), 'the all-non-C away side never teras');
  assert.ok(!teras.some((l) => l.includes('Garchomp')), 'the non-C Garchomp never teras');
}, { timeout: 60000 });

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

// ── format restrictions applied at team-build time (the sim battles under
// custom-game, which bans nothing, so we strip these here — the same options the
// Showdown team builder would refuse to submit) ─────────────────────────────────

test('the restriction set covers evasion, OHKO, evasion items and abilities', () => {
  for (const id of ['doubleteam', 'minimize', 'fissure', 'guillotine', 'horndrill', 'sheercold', 'swagger'])
    assert.ok(RESTRICTIONS.moves.has(id), `expected ${id} to be restricted`);
  assert.ok(RESTRICTIONS.items.has('brightpowder'), 'Bright Powder should be restricted');
  assert.ok(RESTRICTIONS.abilities.has('sandveil'), 'Sand Veil should be restricted');
});

test('movePool strips a restricted move a species would otherwise learn', () => {
  // Find a real species whose raw learnset includes Double Team (an old TM move),
  // and confirm movePool drops it — proving the filter isn't vacuous.
  let found = null;
  for (const sp of Dex.species.all()) {
    const ls = Dex.data.Learnsets[sp.id];
    if (ls && ls.learnset && ls.learnset.doubleteam) { found = sp; break; }
  }
  assert.ok(found, 'expected some species to learn Double Team');
  assert.ok(!movePool(found).includes('doubleteam'), `${found.name}'s movePool still had Double Team`);
});

test('generated sets never carry a restricted move, item or ability', () => {
  const roster = ['garchomp', 'dragonite', 'clefable', 'snorlax', 'gengar', 'togekiss',
    'porygon2', 'tyranitar', 'gyarados', 'blissey'];
  for (let i = 0; i < 20; i++) { // sweep, since sets are random
    for (const set of Teams.unpack(buildRandomTeam(roster, 6))) {
      for (const m of set.moves) assert.ok(!RESTRICTIONS.moves.has(toID(m)), `restricted move ${m}`);
      if (set.item) assert.ok(!RESTRICTIONS.items.has(toID(set.item)), `restricted item ${set.item}`);
      assert.ok(!RESTRICTIONS.abilities.has(toID(set.ability)), `restricted ability ${set.ability}`);
    }
  }
});

test('Showdown refuses to submit a team that carries an evasion move', () => {
  // The strong guarantee: the format's own validator (what the team builder runs
  // on submit) rejects an evasion move, so it can never reach a battle.
  const validator = new TeamValidator(RESTRICTION_FORMAT);
  const set = randomSet('garchomp');
  const bad = { ...set, moves: ['earthquake', 'doubleteam', 'dragonclaw', 'protect'] };
  const errors = validator.validateTeam([bad]);
  assert.ok(errors && errors.some((e) => /evasion|double\s*team/i.test(e)),
    `expected an evasion rejection, got: ${errors}`);
});

test('a generated set passes the format\'s evasion / OHKO / swagger clauses', () => {
  // Our built teams carry none of the restricted options, so a validator running
  // exactly those clauses accepts them (no evasion/OHKO/swagger complaint).
  const validator = new TeamValidator('gen9doublescustomgame@@@Evasion Clause, OHKO Clause, Swagger Clause');
  const team = ['garchomp', 'dragonite', 'tyranitar'].map(randomSet);
  const errors = validator.validateTeam(team) || [];
  assert.ok(!errors.some((e) => /evasion|OHKO|swagger/i.test(e)), `unexpected clause error: ${errors}`);
});

test('buildRandomTeam samples `count` from a larger roster and skips unknowns', () => {
  const roster = ['garchomp', 'dragonite', 'not-a-real-pokemon', 'greninja', 'kingambit', 'corviknight', 'toxapex'];
  const packed = buildRandomTeam(roster, 6);
  const team = Teams.unpack(packed);
  assert.equal(team.length, 6, 'brings exactly 6');
  assert.ok(team.every((s) => Dex.species.get(s.species).exists), 'no unknown species slip in');
});
