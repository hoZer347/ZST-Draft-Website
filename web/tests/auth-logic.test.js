'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { classifyRefresh } = require('../auth-logic.js');

// These guard the "permanent loading / login-logout flicker" fix. The local dev
// API restarts constantly (watchdog), and while it's down the tunnel returns
// network errors / 5xx. If refresh() drops the session on those, boot() logs the
// user back in and the app churns login/logout/loading forever. The invariant:
// ONLY a definitive 401/403 clears the session; everything transient keeps it.

test('network error keeps the session (server blip, not a bad token)', () => {
  assert.equal(classifyRefresh({ networkError: true }), 'keep');
});

test('no outcome at all is treated as transient, so keep', () => {
  assert.equal(classifyRefresh(undefined), 'keep');
  assert.equal(classifyRefresh(null), 'keep');
});

test('401 clears: the refresh token is definitively rejected', () => {
  assert.equal(classifyRefresh({ status: 401, ok: false }), 'clear');
});

test('403 clears: also a definitive auth rejection', () => {
  assert.equal(classifyRefresh({ status: 403, ok: false }), 'clear');
});

test('5xx keeps the session: the server is restarting, not rejecting us', () => {
  for (const status of [500, 502, 503, 504]) {
    assert.equal(classifyRefresh({ status, ok: false }), 'keep', `status ${status}`);
  }
});

test('a 4xx that is not 401/403 is still transient: keep, do not log out', () => {
  // e.g. a 400/404 from a mis-routed request while the tunnel flaps must not
  // nuke a valid session.
  assert.equal(classifyRefresh({ status: 400, ok: false }), 'keep');
  assert.equal(classifyRefresh({ status: 404, ok: false }), 'keep');
});

test('a 2xx saves the returned tokens', () => {
  assert.equal(classifyRefresh({ status: 200, ok: true }), 'save');
});
