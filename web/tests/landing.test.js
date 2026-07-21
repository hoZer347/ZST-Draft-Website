'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { pickSavedTab, landOnSavedTab } = require('../landing.js');

// These guard the login landing tab, which regressed once: the remembered tab was
// restored BEFORE initDraft ran, so the schedule / scoreboard / draft-stats tabs
// (which read the leagueId initDraft sets) failed to load on a refresh. The
// invariant: init always finishes before the tab is shown.

// ── pickSavedTab ─────────────────────────────────────────────────────────────

test('restores the remembered tab when its view still exists', () => {
  assert.equal(pickSavedTab('scoreboard', (n) => n === 'scoreboard'), 'scoreboard');
});

test('falls back to draft when nothing is remembered', () => {
  assert.equal(pickSavedTab(null, () => true), 'draft');
  assert.equal(pickSavedTab('', () => true), 'draft');
});

test('falls back when the remembered view no longer exists', () => {
  assert.equal(pickSavedTab('gone', () => false), 'draft');
});

test('the fallback is configurable', () => {
  assert.equal(pickSavedTab(null, () => true, 'schedule'), 'schedule');
});

// ── landOnSavedTab (the ordering invariant) ─────────────────────────────────

test('init ALWAYS finishes before the tab is shown', async () => {
  const calls = [];
  let initDone = false;
  await landOnSavedTab({
    init: async () => { await Promise.resolve(); initDone = true; calls.push('init'); },
    show: (name) => { assert.ok(initDone, 'show ran before init finished'); calls.push(`show:${name}`); },
    saved: () => 'schedule',
    viewExists: () => true,
  });
  assert.deepEqual(calls, ['init', 'show:schedule']);
});

test('waits for a slow async init before showing', async () => {
  const calls = [];
  await landOnSavedTab({
    init: () => new Promise((r) => setTimeout(() => { calls.push('init'); r(); }, 15)),
    show: (name) => calls.push(`show:${name}`),
    saved: () => 'scoreboard',
    viewExists: () => true,
  });
  assert.deepEqual(calls, ['init', 'show:scoreboard']); // show never jumps ahead of a pending init
});

test('shows the remembered tab, or draft when there is none / it is gone', async () => {
  const shown = [];
  const run = (saved, exists) => landOnSavedTab({
    init: async () => {}, show: (n) => shown.push(n), saved: () => saved, viewExists: () => exists,
  });
  await run('stats', true);   // remembered + exists → stats
  await run('stats', false);  // remembered but view gone → draft
  await run(null, true);      // nothing remembered → draft
  assert.deepEqual(shown, ['stats', 'draft', 'draft']);
});
