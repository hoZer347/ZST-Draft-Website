// Pure, DOM-free logic for opening the self-hosted Showdown teambuilder.
//
// Extracted from app.js so the ONE invariant that must never break,
// clicking Teambuilder opens the real teambuilder URL, in a single window.open,
// carrying ?name so the client auto-logs the coach in, is unit-testable.
// (It regressed twice: once the ?name was read too late, once openTeambuilder
// opened about:blank and navigated a cross-origin window, leaving it blank.)
//
// Loaded two ways, like pool-logic.js:
//   • the browser loads it as a plain <script> BEFORE app.js, so the exports
//     land on globalThis and app.js uses them unqualified;
//   • the Node test suite require()s it (tests/teambuilder.test.js).
(function (root, factory) {
  const api = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = api; // Node
  for (const k in api) root[k] = api[k]; // browser: expose as globals for app.js
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  // Where the teambuilder client is served. Local dev (localhost / 127.0.0.1)
  // hits the client on :8791 directly, bypassing the Cloudflare cache; everywhere
  // else (and Node, which has no `location`) uses the public host. Mirrors the
  // host split in config.js.
  const TEAMBUILDER_HOST =
    (typeof location !== 'undefined' &&
      (location.hostname === 'localhost' || location.hostname === '127.0.0.1'))
      ? 'http://127.0.0.1:8791'
      : 'https://play.loomhozer.ca';
  const TEAMBUILDER_BASE = TEAMBUILDER_HOST + '/teambuilder';

  // Build the URL to open. `name` is the coach's Discord username (the client
  // auto-logs in as it, see serve-client.js). `matchups` is an optional
  // [{w: week, o: opponentName}] list the teambuilder seeds a folder of blank
  // teams from. Always returns the real teambuilder URL, never about:blank.
  function buildTeambuilderUrl(name, matchups, teams, demo) {
    let url = TEAMBUILDER_BASE;
    if (name) url += '?name=' + encodeURIComponent(name);
    if (Array.isArray(matchups) && matchups.length) {
      url += (url.indexOf('?') >= 0 ? '&' : '?') +
        'matchups=' + encodeURIComponent(JSON.stringify(matchups));
    }
    // Optional pre-built teams (packed strings), one per matchup slot, the client
    // seeds each week's team with its content instead of leaving it blank.
    if (Array.isArray(teams) && teams.length) {
      url += (url.indexOf('?') >= 0 ? '&' : '?') +
        'teams=' + encodeURIComponent(JSON.stringify(teams));
    }
    // Optional demo teams, [{player, team}], the admin seeds a folder per player
    // onto their own device.
    if (Array.isArray(demo) && demo.length) {
      url += (url.indexOf('?') >= 0 ? '&' : '?') +
        'demo=' + encodeURIComponent(JSON.stringify(demo));
    }
    return url;
  }

  // The one named tab the teambuilder reuses, so repeated opens navigate the
  // same tab instead of piling up.
  const TEAMBUILDER_TAB = 'draft-teambuilder';

  // Open the teambuilder in ONE window.open call, at the URL that carries the
  // login name. `openFn` is injected (window.open in the browser, a spy in
  // tests) so this can't silently regress to a two-step about:blank navigation.
  //
  // NOTE: no `noopener`. Per the HTML spec, window.open with noopener treats a
  // named target like _blank, so every open makes a NEW tab instead of reusing
  // TEAMBUILDER_TAB. The old tab then stays connected holding the coach's name,
  // and the new tab's auto-login collides with it (nametaken → never logs in).
  // Reusing the tab closes the previous session first, which is what lets login
  // succeed. Do not add noopener back.
  function openTeambuilderWith(openFn, name, matchups, teams, demo) {
    return openFn(buildTeambuilderUrl(name, matchups, teams, demo), TEAMBUILDER_TAB);
  }

  return { TEAMBUILDER_BASE, buildTeambuilderUrl, openTeambuilderWith };
});
