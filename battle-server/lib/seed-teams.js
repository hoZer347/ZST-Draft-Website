// Pure seeding logic for the self-hosted Showdown teambuilder's localStorage
// (`showdown_teams`). Kept DOM-free and side-effect-free so it can be BOTH:
//   • injected verbatim into the client page by serve-client.js (it attaches
//     `seedTeams` to window), where a tiny wrapper reads the URL + localStorage,
//     calls it, and writes the result back; and
//   • unit-tested headlessly by the Node runner (test/seed-teams.test.js).
//
// The rule it enforces (do not "fix" without updating the tests):
//   • Matchup weeks ("ZST Season 4 / Week N vs <opp>") are seeded BLANK, one empty
//     team per matchup, for the coach to fill. Never pre-filled. Existing entries
//     (a team the coach already edited) are left untouched.
//   • Demo teams ("Demo teams / <player>") are FILLED, one packed team per player,
//     seeded only when demo data is passed. Re-seeding OVERWRITES (they're
//     regeneratable examples), so a stale entry from an older build can't linger.
//
// Written in ES5 (var/function) because it runs in the vendored classic client page.
(function (root, factory) {
  'use strict';
  var api = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = api; // Node
  for (var k in api) if (Object.prototype.hasOwnProperty.call(api, k)) root[k] = api[k]; // browser global
})(typeof globalThis !== 'undefined' ? globalThis : (typeof window !== 'undefined' ? window : this), function () {
  'use strict';

  var FORMAT = 'gen9zstseason4';
  var MATCHUP_FOLDER = 'ZST Season 4';
  var DEMO_FOLDER = 'Demo teams';

  // `|` and `/` are the packed-line delimiters, so strip them from any name that
  // becomes a folder/team segment. Blank collapses to a caller-supplied fallback.
  function clean(s) {
    return String(s == null ? '' : s).split('|').join(' ').split('/').join(' ').trim();
  }

  // A packed line is "FORMAT]folder/name|packedTeam". Its key is "folder/name".
  function keyOf(line) {
    var bracket = line.indexOf(']');
    var bar = line.indexOf('|');
    return bar < 0 ? null : line.slice(bracket + 1, bar);
  }

  // Seed matchup weeks (blank) and demo teams (filled) into the raw `showdown_teams`
  // string. Returns { text, changed }: `text` is the new value to store, `changed`
  // is false when nothing needed adding/updating (so the caller can skip the write).
  function seedTeams(rawTeams, opts) {
    opts = opts || {};
    var matchups = opts.matchups || [];
    var demo = opts.demo || [];
    var lines = rawTeams ? rawTeams.split('\n') : [];
    var changed = false;

    // Existing "folder/name" -> line index, so we ensure-exist / overwrite by key.
    var idx = {};
    for (var i = 0; i < lines.length; i++) {
      var k = keyOf(lines[i]);
      if (k !== null) idx[k] = i;
    }

    // ── matchup weeks: ensure-exists, always BLANK, never overwrite ──
    for (var j = 0; j < matchups.length; j++) {
      var opp = clean(matchups[j].o) || 'TBD';
      var key = MATCHUP_FOLDER + '/' + 'Week ' + matchups[j].w + ' vs ' + opp;
      if (Object.prototype.hasOwnProperty.call(idx, key)) continue; // leave coach edits alone
      idx[key] = lines.length;
      lines.push(FORMAT + ']' + key + '|'); // trailing '|' + nothing = a blank team
      changed = true;
    }

    // ── demo teams: one FILLED team per player, overwrite on re-seed ──
    for (var d = 0; d < demo.length; d++) {
      var player = clean(demo[d].player) || 'Player';
      var packed = (typeof demo[d].team === 'string') ? demo[d].team : '';
      var dkey = DEMO_FOLDER + '/' + player;
      var line = FORMAT + ']' + dkey + '|' + packed;
      if (Object.prototype.hasOwnProperty.call(idx, dkey)) {
        if (lines[idx[dkey]] !== line) { lines[idx[dkey]] = line; changed = true; }
      } else {
        idx[dkey] = lines.length;
        lines.push(line);
        changed = true;
      }
    }

    return { text: lines.join('\n'), changed: changed };
  }

  return {
    seedTeams: seedTeams,
    FORMAT: FORMAT,
    MATCHUP_FOLDER: MATCHUP_FOLDER,
    DEMO_FOLDER: DEMO_FOLDER,
  };
});
