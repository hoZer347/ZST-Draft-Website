'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const {
  TEAMBUILDER_BASE, buildTeambuilderUrl, openTeambuilderWith,
} = require('../teambuilder.js');

// These guard the auto-login of the self-hosted Showdown teambuilder, which
// regressed twice: once the ?name was read too late, once openTeambuilder
// opened about:blank and navigated a cross-origin window (leaving it blank, so
// login never happened). The invariant: opening the teambuilder produces the
// real teambuilder URL, in one window.open, carrying ?name.

// ── buildTeambuilderUrl ────────────────────────────────────────────────────

test('URL carries ?name so the client auto-logs in', () => {
  const url = buildTeambuilderUrl('CoachA', null);
  assert.ok(url.startsWith(TEAMBUILDER_BASE), 'is the real teambuilder URL');
  assert.match(url, /[?&]name=CoachA(&|$)/);
  assert.doesNotMatch(url, /about:blank/);
});

test('name is URL-encoded (spaces, &, unicode)', () => {
  const url = buildTeambuilderUrl('a b&c', null);
  assert.match(url, /[?&]name=a%20b%26c(&|$)/);
  // round-trips back to the original
  const got = new URL(url).searchParams.get('name');
  assert.equal(got, 'a b&c');
});

test('no name → no name param, still the real URL (never about:blank)', () => {
  const url = buildTeambuilderUrl(undefined, null);
  assert.equal(url, TEAMBUILDER_BASE);
  assert.doesNotMatch(url, /about:blank/);
});

test('matchups are appended alongside name and round-trip as JSON', () => {
  const mus = [{ w: 1, o: 'Alice' }, { w: 2, o: 'Bob' }, { w: 3, o: 'Alice' }];
  const url = buildTeambuilderUrl('Coach', mus);
  assert.match(url, /[?&]name=Coach(&|$)/, 'name still present with matchups');
  const raw = new URL(url).searchParams.get('matchups');
  assert.ok(raw, 'matchups param present');
  assert.deepEqual(JSON.parse(raw), mus);
});

test('empty / missing matchups → no matchups param', () => {
  assert.doesNotMatch(buildTeambuilderUrl('Coach', []), /matchups=/);
  assert.doesNotMatch(buildTeambuilderUrl('Coach', null), /matchups=/);
});

// ── pre-built teams (one packed team per matchup slot) ──────────────────────

test('pre-built teams are appended and round-trip as JSON', () => {
  const teams = ['Charizard||||||||||', 'Garchomp||||||||||'];
  const url = buildTeambuilderUrl('Coach', [{ w: 1, o: 'Alice' }], teams);
  const raw = new URL(url).searchParams.get('teams');
  assert.ok(raw, 'teams param present');
  assert.deepEqual(JSON.parse(raw), teams);
});

test('empty / missing pre-built teams → no teams param', () => {
  assert.doesNotMatch(buildTeambuilderUrl('Coach', null, []), /[?&]teams=/);
  assert.doesNotMatch(buildTeambuilderUrl('Coach', null, null), /[?&]teams=/);
});

// ── demo teams (admin: one per player, seeded on the admin's device) ────────

test('demo teams are appended and round-trip as [{player, team}] JSON', () => {
  const demo = [
    { player: 'Alice', team: 'Charizard||||||||||' },
    { player: 'Bob', team: 'Garchomp||||||||||' },
  ];
  const url = buildTeambuilderUrl('Admin', null, null, demo);
  const raw = new URL(url).searchParams.get('demo');
  assert.ok(raw, 'demo param present');
  // The seeding block in serve-client.js reads .player and .team by name — the
  // shape must survive the round-trip exactly (a field rename there broke it once).
  assert.deepEqual(JSON.parse(raw), demo);
});

test('empty / missing demo → no demo param', () => {
  assert.doesNotMatch(buildTeambuilderUrl('Admin', null, null, []), /demo=/);
  assert.doesNotMatch(buildTeambuilderUrl('Admin', null, null, null), /demo=/);
});

test('name, matchups, teams and demo can all coexist on one URL', () => {
  const mus = [{ w: 1, o: 'Alice' }];
  const teams = ['Charizard||||||||||'];
  const demo = [{ player: 'Alice', team: 'Garchomp||||||||||' }];
  const u = new URL(buildTeambuilderUrl('Coach', mus, teams, demo));
  assert.equal(u.searchParams.get('name'), 'Coach');
  assert.deepEqual(JSON.parse(u.searchParams.get('matchups')), mus);
  assert.deepEqual(JSON.parse(u.searchParams.get('teams')), teams);
  assert.deepEqual(JSON.parse(u.searchParams.get('demo')), demo);
});

// ── openTeambuilderWith ─────────────────────────────────────────────────────

test('opens the real teambuilder URL in a SINGLE window.open, not about:blank', () => {
  const calls = [];
  const openFn = (url, target, features) => { calls.push({ url, target, features }); };

  openTeambuilderWith(openFn, 'Coach', [{ w: 1, o: 'Alice' }]);

  assert.equal(calls.length, 1, 'exactly one window.open (the about:blank two-step was the bug)');
  const { url, target, features } = calls[0];
  assert.ok(url.startsWith(TEAMBUILDER_BASE), 'opens the teambuilder, not about:blank');
  assert.notEqual(url, 'about:blank');
  assert.match(url, /[?&]name=Coach(&|$)/, 'the opened URL carries the login name');
  assert.equal(target, 'draft-teambuilder', 'reuses the one named tab');
  // noopener would make the named target behave like _blank, opening a NEW tab
  // each time; the old tab keeps the coach's name and the new one can't log in.
  assert.ok(!features || !String(features).includes('noopener'),
    'no noopener — it breaks named-tab reuse and causes name conflicts');
});

test('opens with name even when there are no matchups', () => {
  let opened = null;
  openTeambuilderWith((url) => { opened = url; }, 'SoloCoach', []);
  assert.match(opened, /[?&]name=SoloCoach(&|$)/);
});
