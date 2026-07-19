'use strict';

const { test } = require('node:test');
const assert = require('node:assert');
const { Dex } = require('pokemon-showdown');
const { resolve, buildFormatId, DRAFT_RULES, DRAFT_FORMAT_ID, validateTeam } = require('../lib/format');
const { packDraftTeam } = require('../lib/pack');

test('DRAFT_FORMAT_ID is customgame plus the declared rules', () => {
  assert.match(DRAFT_FORMAT_ID, /^gen9customgame@@@/);
  for (const rule of DRAFT_RULES) assert.ok(DRAFT_FORMAT_ID.includes(rule), `missing ${rule}`);
});

test('buildFormatId with no rules is the bare base', () => {
  assert.equal(buildFormatId([]), 'gen9customgame');
});

test('resolve() applies every declared rule to the rule table', () => {
  const format = resolve();
  assert.ok(format.exists);
  assert.equal(format.customRules.length, DRAFT_RULES.length);
  const table = Dex.formats.getRuleTable(format);
  assert.ok(table.has('speciesclause'));
  assert.ok(table.has('evasionclause'));
  assert.ok(table.has('ohkoclause'));
});

// The silent-null footgun: the sim drops ALL custom rules if any one is bad.
// resolve() must turn that into a loud throw, not a rule-less battle.
test('resolve() throws when a rule is invalid (Item Clause is not gen9-valid)', () => {
  assert.throws(() => resolve([...DRAFT_RULES, 'Item Clause']), /dropped|missing/i);
});

test('resolve() throws on a duplicate rule', () => {
  assert.throws(() => resolve([...DRAFT_RULES, 'Species Clause']), /dropped|missing/i);
});

test('resolve() throws when re-declaring a rule the base already has (Team Preview)', () => {
  assert.throws(() => resolve([...DRAFT_RULES, 'Team Preview']), /dropped|missing/i);
});

test('validateTeam returns an array (legal team → no problems)', () => {
  const team = packDraftTeam(['charizard', 'venusaur', 'blastoise', 'pikachu', 'snorlax', 'gengar']);
  const problems = validateTeam(team);
  assert.ok(Array.isArray(problems));
  assert.equal(problems.length, 0);
});

test('validateTeam flags a Species Clause violation (two of the same mon)', () => {
  const team = packDraftTeam(['charizard', 'charizard', 'blastoise']);
  const problems = validateTeam(team);
  assert.ok(problems.some((p) => /Species Clause/i.test(p)), problems.join(' | '));
});
