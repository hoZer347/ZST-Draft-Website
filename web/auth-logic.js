// Pure, DOM-free auth decisions extracted from auth.js so the ONE invariant
// that caused the "permanent loading / login-logout flicker" bug is
// unit-testable.
//
// The bug: the local dev API restarts constantly (the watchdog), and its
// Cloudflare tunnel returns 5xx / connection errors while it's down. The old
// refresh() cleared the stored session on ANY failed refresh, so every server
// blip logged the user out (and boot() then tried to sign them back in),
// producing an endless login/logout/loading churn.
//
// The fix, captured here: only a DEFINITIVE auth rejection (401/403) means the
// refresh token is actually bad and the session must be dropped. A network
// error or a 5xx is transient, so keep the session and let a later call retry.
//
// Loaded two ways, like pool-logic.js / teambuilder.js:
//   * the browser loads it as a plain <script> BEFORE auth.js, so the exports
//     land on globalThis and auth.js uses them unqualified;
//   * the Node test suite require()s it (tests/auth-logic.test.js).
(function (root, factory) {
  const api = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = api; // Node
  for (const k in api) root[k] = api[k]; // browser: expose as globals for auth.js
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  // Decide what refresh() should do with the stored session, given the outcome
  // of the refresh POST. Returns one of:
  //   'keep':  leave the session in place; the failure is transient (network
  //            error / server restarting). A later call can retry.
  //   'clear': drop the session; the refresh token was definitively rejected.
  //   'save':  the refresh succeeded; store the returned tokens.
  //
  // `outcome` is either { networkError: true } (fetch threw, server
  // unreachable) or { status, ok } mirroring the Response.
  function classifyRefresh(outcome) {
    if (!outcome || outcome.networkError) return 'keep';
    if (outcome.status === 401 || outcome.status === 403) return 'clear';
    if (!outcome.ok) return 'keep'; // 5xx / tunnel error while the API restarts
    return 'save';
  }

  return { classifyRefresh };
});
