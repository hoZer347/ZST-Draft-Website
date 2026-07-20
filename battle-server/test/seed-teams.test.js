'use strict';

const { test } = require('node:test');
const assert = require('node:assert');
const { seedTeams, FORMAT, MATCHUP_FOLDER, DEMO_FOLDER } = require('../lib/seed-teams');

// seedTeams is the single source of truth for what the Teambuilder pre-seeds into
// localStorage. The invariant these guard (the exact thing the user asked for):
//   • "Week N vs <opp>" teams are BLANK — the coach fills them, we never pre-fill.
//   • Demo teams are FILLED, and only appear when demo data is passed (i.e. the
//     admin built them). Re-seeding overwrites; coach-made teams are never touched.

// Parse a packed showdown_teams line "FORMAT]folder/name|packed" into its parts.
function parse(line) {
  const bracket = line.indexOf(']');
  const bar = line.indexOf('|');
  return { format: line.slice(0, bracket), key: line.slice(bracket + 1, bar), packed: line.slice(bar + 1) };
}
const linesOf = (res) => res.text.split('\n').filter(Boolean);

// ── matchup weeks: always blank ─────────────────────────────────────────────

test('matchup weeks seed one BLANK team per matchup', () => {
  const matchups = [{ w: 1, o: 'Alice' }, { w: 2, o: 'Bob' }];
  const res = seedTeams('', { matchups });

  assert.equal(res.changed, true);
  const lines = linesOf(res).map(parse);
  assert.equal(lines.length, 2);
  for (const l of lines) {
    assert.equal(l.format, FORMAT);
    assert.ok(l.key.startsWith(MATCHUP_FOLDER + '/Week '), 'in the season folder');
    assert.equal(l.packed, '', 'the week team is blank — never pre-filled');
  }
  assert.deepEqual(lines.map((l) => l.key), [
    `${MATCHUP_FOLDER}/Week 1 vs Alice`,
    `${MATCHUP_FOLDER}/Week 2 vs Bob`,
  ]);
});

test('re-seeding the same matchups is idempotent (no dupes, no change)', () => {
  const matchups = [{ w: 1, o: 'Alice' }];
  const first = seedTeams('', { matchups });
  const second = seedTeams(first.text, { matchups });

  assert.equal(second.changed, false, 'nothing to add the second time');
  assert.equal(linesOf(second).length, 1, 'no duplicate week folder');
});

test('a week team the coach has already filled is NEVER overwritten', () => {
  const matchups = [{ w: 1, o: 'Alice' }];
  const edited = `${FORMAT}]${MATCHUP_FOLDER}/Week 1 vs Alice|Charizard||||||||||`;
  const res = seedTeams(edited, { matchups });

  assert.equal(res.changed, false);
  assert.equal(parse(linesOf(res)[0]).packed, 'Charizard||||||||||', 'coach edit preserved');
});

test('opponent names with packed-line delimiters are sanitised', () => {
  const res = seedTeams('', { matchups: [{ w: 1, o: 'a|b/c' }] });
  assert.equal(parse(linesOf(res)[0]).key, `${MATCHUP_FOLDER}/Week 1 vs a b c`);
});

// ── demo teams: filled, only when provided ──────────────────────────────────

test('demo teams are FILLED, one per player, in the demo folder', () => {
  const demo = [
    { player: 'Alice', team: 'Charizard||||||||||' },
    { player: 'Bob', team: 'Garchomp||||||||||' },
  ];
  const res = seedTeams('', { demo });
  const lines = linesOf(res).map(parse);

  assert.equal(lines.length, 2);
  assert.deepEqual(lines.map((l) => l.key), [`${DEMO_FOLDER}/Alice`, `${DEMO_FOLDER}/Bob`]);
  assert.equal(lines[0].packed, 'Charizard||||||||||', 'demo team carries content');
  assert.equal(lines[1].packed, 'Garchomp||||||||||');
});

test('no demo data → no demo folder is seeded', () => {
  const res = seedTeams('', { matchups: [{ w: 1, o: 'Alice' }] });
  assert.ok(!linesOf(res).some((l) => parse(l).key.startsWith(DEMO_FOLDER + '/')), 'no demo teams without demo data');
});

test('re-seeding demo OVERWRITES a stale/empty entry (no lingering blanks)', () => {
  const stale = `${FORMAT}]${DEMO_FOLDER}/Alice|`; // empty from an older build
  const res = seedTeams(stale, { demo: [{ player: 'Alice', team: 'Blastoise||||||||||' }] });

  assert.equal(res.changed, true);
  const alice = linesOf(res).map(parse).filter((l) => l.key === `${DEMO_FOLDER}/Alice`);
  assert.equal(alice.length, 1, 'overwritten in place, not duplicated');
  assert.equal(alice[0].packed, 'Blastoise||||||||||');
});

// ── both together, and coach teams untouched ────────────────────────────────

test('weeks (blank) and demo (filled) coexist; unrelated teams are preserved', () => {
  const mine = `${FORMAT}]My cool team|Pikachu||||||||||`;
  const res = seedTeams(mine, {
    matchups: [{ w: 1, o: 'Alice' }],
    demo: [{ player: 'Bob', team: 'Garchomp||||||||||' }],
  });
  const byKey = Object.fromEntries(linesOf(res).map(parse).map((l) => [l.key, l.packed]));

  assert.equal(byKey['My cool team'], 'Pikachu||||||||||', 'coach team untouched');
  assert.equal(byKey[`${MATCHUP_FOLDER}/Week 1 vs Alice`], '', 'week blank');
  assert.equal(byKey[`${DEMO_FOLDER}/Bob`], 'Garchomp||||||||||', 'demo filled');
});
