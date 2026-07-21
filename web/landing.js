// Pure, DOM-free logic for where to land after sign-in.
//
// Extracted from app.js so the ONE invariant that must never break, the
// remembered tab is only shown AFTER the draft has initialised, is unit-testable.
// (It regressed once: the saved tab was restored before initDraft ran, so the
// schedule / scoreboard / draft-stats tabs, which read leagueId that initDraft
// sets, failed to load on a refresh.)
//
// Loaded two ways, like teambuilder.js:
//   • the browser loads it as a plain <script> BEFORE app.js, so the exports land
//     on globalThis and app.js uses them unqualified;
//   • the Node test suite require()s it (tests/landing.test.js).
(function (root, factory) {
  const api = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = api; // Node
  for (const k in api) root[k] = api[k]; // browser: expose as globals for app.js
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  // The tab to land on: the one remembered from last session if it's still a real
  // view, otherwise the fallback. `saved` is the stored tab name (or null);
  // `viewExists(name)` reports whether the DOM has a `view-<name>` element.
  function pickSavedTab(saved, viewExists, fallback = 'draft') {
    return saved && viewExists(saved) ? saved : fallback;
  }

  // Reveal the landing tab, but ONLY after `init` has resolved. The schedule /
  // scoreboard / draft-stats tabs read leagueId, which init sets, so showing a
  // remembered tab before init leaves those tabs unable to load. Awaiting init
  // first is exactly what keeps that from regressing.
  //
  // `init` returns a promise; `show(name)` reveals a tab; `saved()` returns the
  // remembered tab name (or null); `viewExists(name)` checks the DOM.
  async function landOnSavedTab({ init, show, saved, viewExists, fallback = 'draft' }) {
    await init();
    show(pickSavedTab(saved(), viewExists, fallback));
  }

  return { pickSavedTab, landOnSavedTab };
});
