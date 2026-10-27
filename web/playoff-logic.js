// Pure, DOM-free pieces of the playoff bracket (see renderPlayoffs in app.js).
//
// Loaded two ways, like pool-logic.js / auth-logic.js:
//   * the browser loads it as a plain <script> BEFORE app.js, so the exports
//     land on globalThis and app.js uses them unqualified;
//   * the Node test suite require()s it (tests/playoff-logic.test.js).
(function (root, factory) {
  const api = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = api; // Node
  for (const k in api) root[k] = api[k]; // browser: expose as globals for app.js
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  // A series' identity in the bracket: its round and its position in that round.
  function seriesKey(round, index) {
    return `${round}:${index}`;
  }

  const played = (g) => g.result !== 'Pending';

  // What a viewer sees of the bracket without spoiling anything, keyed by seriesKey.
  // A played game stays hidden until isOpen(matchId). A series lists its games only
  // up to its first hidden one, since a later game's existence gives the earlier
  // result away; its score counts revealed games, and its winner shows once every
  // game is revealed. A team that advanced from a series not yet settled that way is
  // hidden in the next round, and so are that series' games.
  function visibleBracket(rounds, isOpen) {
    const views = new Map();
    let prev = [];
    for (const r of rounds ?? []) {
      prev = r.slots.map((s) => {
        const feeders = prev.filter((p) => Math.floor(p.index / 2) === s.index);
        const hidden = (e) => e != null && feeders.some((p) => !p.settled
          && (p.series.home?.teamId === e.teamId || p.series.away?.teamId === e.teamId));
        const homeHidden = hidden(s.home);
        const awayHidden = hidden(s.away);
        const games = [];
        if (!homeHidden && !awayHidden)
          for (const g of s.games) {
            games.push(g);
            if (played(g) && !isOpen(g.matchId)) break;
          }
        const revealed = games.filter((g) => played(g) && isOpen(g.matchId));
        const wins = (result) => (revealed.length ? revealed.filter((g) => g.result === result).length : null);
        const settled = !homeHidden && !awayHidden
          && (s.bye || (s.winnerTeamId != null && revealed.length === s.games.filter(played).length));
        // One side hidden: the known team goes on top, as the server lays out an
        // undecided opponent, since keeping the seed order would leak the result.
        const home = homeHidden ? null : s.home;
        const away = awayHidden ? null : s.away;
        const view = {
          round: r, index: s.index, series: s, settled, games,
          home: home ?? away,
          away: home ? away : null,
          homeWins: wins('HomeWin'), awayWins: wins('AwayWin'),
          winnerTeamId: settled ? s.winnerTeamId : null,
        };
        views.set(seriesKey(r.round, s.index), view);
        return view;
      });
    }
    return views;
  }

  // The game a series dialog shows: the one numbered n, else the next one to watch
  // (the first played game still hidden), else the first.
  function pickGame(games, n, isOpen) {
    return games.find((g) => g.game === n)
      ?? games.find((g) => played(g) && !isOpen(g.matchId))
      ?? games[0] ?? null;
  }

  // One game's chip: "G1 5–0" or "G2 draw" once revealed, just "G3" while unplayed or hidden.
  function gameLabel(g, open) {
    const name = `G${g.game}`;
    if (!played(g) || !open) return name;
    if (g.result === 'Draw') return `${name} draw`;
    return g.homeScore != null && g.awayScore != null ? `${name} ${g.homeScore}–${g.awayScore}` : name;
  }

  return { seriesKey, visibleBracket, pickGame, gameLabel };
});
