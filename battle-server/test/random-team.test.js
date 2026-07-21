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

test('sets carry a random-but-legal nature, EV spread, and IV spread', () => {
  const stats = ['hp', 'atk', 'def', 'spa', 'spd', 'spe'];
  const natureNames = new Set(Dex.natures.all().map((n) => n.name));
  const seenNatures = new Set();
  // Sample many sets so the legality checks and the "not all Hardy/84/31" variety
  // claims are exercised across the random space, not one lucky draw.
  for (let i = 0; i < 300; i++) {
    const set = randomSet('garchomp');
    assert.ok(natureNames.has(set.nature), `nature ${set.nature} is a real nature`);
    seenNatures.add(set.nature);

    let total = 0;
    for (const s of stats) {
      const ev = set.evs[s];
      assert.ok(Number.isInteger(ev) && ev >= 0 && ev <= 252, `EV ${s}=${ev} in 0..252`);
      assert.equal(ev % 4, 0, `EV ${s}=${ev} is a multiple of 4`);
      total += ev;
      const iv = set.ivs[s];
      assert.ok(Number.isInteger(iv) && iv >= 0 && iv <= 31, `IV ${s}=${iv} in 0..31`);
    }
    assert.ok(total <= 510, `EV total ${total} <= 510`);
  }
  // Randomness actually varies the nature (not pinned to one value).
  assert.ok(seenNatures.size > 5, `saw ${seenNatures.size} distinct natures over 300 sets`);
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
  // Rayquaza megas via a move (Dragon Ascent), not a stone, there is no stone to
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
// custom-game, which bans nothing, so we strip these here, the same options the
// Showdown team builder would refuse to submit) ─────────────────────────────────

test('the restriction set covers evasion, OHKO, evasion items and abilities', () => {
  for (const id of ['doubleteam', 'minimize', 'fissure', 'guillotine', 'horndrill', 'sheercold', 'swagger'])
    assert.ok(RESTRICTIONS.moves.has(id), `expected ${id} to be restricted`);
  assert.ok(RESTRICTIONS.items.has('brightpowder'), 'Bright Powder should be restricted');
  assert.ok(RESTRICTIONS.abilities.has('sandveil'), 'Sand Veil should be restricted');
});

test('movePool strips a restricted move a species would otherwise learn', () => {
  // Find a real species whose raw learnset includes Double Team (an old TM move),
  // and confirm movePool drops it, proving the filter isn't vacuous.
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

// ── the league's custom "megas" (ChampionsRegMA content) ─────────────────────
// They aren't in a vanilla pokemon-showdown; scripts/showdown.js merges them into
// the bundled engine as PLAIN gen-9 mega data (species + string-format stones +
// a formats-data row, see showdown-config/custom-megas.js) so the stock engine's
// own canMegaEvo evolves them with no sim/ruleset/format changes. The data file is
// checked unconditionally; the behaviour tests need the merge (which the running
// battle server does) and SKIP on a fresh checkout rather than failing.

const CUSTOM_MEGAS = require('../showdown-config/custom-megas.js');

test('custom-megas.js is well-formed: string-format stones mapping base <-> a listed mega', () => {
  const { Pokedex, Items } = CUSTOM_MEGAS;
  assert.ok(Object.keys(Pokedex).length >= 40, `expected a full set, got ${Object.keys(Pokedex).length}`);
  const megaNames = new Set(Object.values(Pokedex).map((s) => s.name));
  const stoneNames = new Set(Object.values(Items).map((i) => i.name));
  for (const [id, item] of Object.entries(Items)) {
    // Plain strings are what the STOCK canMegaEvo reads (not the cache's object form).
    assert.equal(typeof item.megaStone, 'string', `${id}.megaStone is a string`);
    assert.equal(typeof item.megaEvolves, 'string', `${id}.megaEvolves is a string`);
    assert.ok(megaNames.has(item.megaStone), `${id} evolves into a listed species (${item.megaStone})`);
  }
  for (const sp of Object.values(Pokedex)) {
    assert.equal(sp.forme && sp.forme.startsWith('Mega'), true, `${sp.name} is a Mega forme`);
    assert.ok(stoneNames.has(sp.requiredItem), `${sp.name} needs a listed stone (${sp.requiredItem})`);
    // A formats-data row must exist, or Mix and Mega's init crashes writing to it.
    assert.ok(CUSTOM_MEGAS.FormatsData[Dex.toID(sp.name)], `${sp.name} has a formats-data row`);
  }
});

const MEGA_ENGINE = Dex.species.get('malamarmega').exists
  ? false
  : 'custom megas not merged into node_modules, start the battle server (scripts/showdown.js) once';

test('a merged custom mega is a real mega species its string stone evolves', { skip: MEGA_ENGINE }, () => {
  const sp = Dex.species.get('malamarmega');
  assert.ok(sp.isMega, 'Malamar-Mega is a mega');
  assert.equal(sp.baseSpecies, 'Malamar');
  const stone = Dex.items.get(sp.requiredItem);
  assert.equal(stone.megaEvolves, 'Malamar');
  assert.equal(stone.megaStone, 'Malamar-Mega');
});

test('a custom mega slug is fielded as its BASE form holding its stone', { skip: MEGA_ENGINE }, () => {
  const set = randomSet('malamar-mega');
  assert.equal(set.species, 'Malamar', 'base form, not Malamar-Mega');
  assert.equal(set.item, 'Malamarite', 'holds its stone');
});

test('a roster of custom megas fields a full team (drafted megas no longer vanish)', { skip: MEGA_ENGINE }, () => {
  // The reported bug: a 9-mon roster with FOUR custom megas fielded only its 5
  // non-mega mons, because the engine didn't know the megas and dropped them.
  const roster = ['froslass-mega', 'masquerain', 'scrafty-mega', 'staraptor-mega',
    'mandibuzz', 'volbeat', 'yanmega', 'tauros-paldeaaqua', 'falinks-mega'];
  assert.equal(roster.filter((s) => Dex.species.get(s).exists).length, roster.length, 'all nine are known');
  assert.equal(Teams.unpack(buildRandomTeam(roster, 6)).length, 6, 'brings a full 6, not 5');
});

test('every format still builds with the custom megas merged (Mix and Mega guard)', { skip: MEGA_ENGINE }, () => {
  // Adding mega stones used to crash Mix and Mega\'s init (it writes isNonstandard
  // to each stone\'s formats-data row); the merged formats-data rows prevent that,
  // so every format\'s rule table still builds (no server-connect crash).
  Dex.includeFormats();
  for (const f of Dex.formats.all()) {
    assert.doesNotThrow(() => Dex.formats.getRuleTable(f), `format ${f.id} should still build`);
  }
});

test('a custom mega mega-evolves in a sim battle, with C-tier tera alongside', { skip: MEGA_ENGINE, timeout: 60000 }, () => {
  // Home leads a custom-mega Malamar (holds its stone → megas turn 1) next to a
  // C-tier Pikachu (teras). One battle exercises both gimmicks: mega + tera.
  const spec = { matches: [{
    homeName: 'H', awayName: 'A',
    homeTeam: [{ s: 'malamar-mega', t: null }, { s: 'pikachu', t: 'Water' }],
    awayTeam: [{ s: 'snorlax', t: null }, { s: 'skarmory', t: null }],
  }] };
  const script = path.join(__dirname, '..', 'scripts', 'simulate-season.js');
  const res = spawnSync('node', [script], { input: JSON.stringify(spec), encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  assert.equal(res.status, 0, res.stderr);
  const log = JSON.parse(res.stdout)[0].log;
  assert.ok(/\|-mega\|[^\n]*Malamar/.test(log), `custom Malamar should mega-evolve; log had no Malamar mega`);
  assert.ok(/\|-terastallize\|[^\n]*Pikachu\|Water/.test(log), 'C-tier Pikachu should still tera to Water');
});
