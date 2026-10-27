'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const { seriesKey, visibleBracket, pickGame, gameLabel } = require('../playoff-logic.js');

const team = (teamId) => ({ teamId, name: `T${teamId}`, seed: teamId });
const game = (matchId, n, result, homeScore = null, awayScore = null) =>
  ({ matchId, game: n, result, homeScore, awayScore });
const opened = (...ids) => (id) => ids.includes(id);
const none = opened();

// Shaped like Playoffs.SyncAsync's output: a decided series holds only its played
// games, a live one its played games plus the next. Seeds 1 and 2 have byes; 3 beat 6
// in two games and 4 beat 5 in three. In the semis 3 leads 1 after one game, and
// 2 v 4 hasn't started.
const rounds = [
  {
    round: 1, name: 'Round 1', slots: [
      { index: 0, bye: true, home: team(1), away: null, winnerTeamId: 1, games: [] },
      { index: 1, bye: false, home: team(3), away: team(6), winnerTeamId: 3, games: [
        game(11, 1, 'HomeWin', 3, 0), game(12, 2, 'HomeWin', 1, 0),
      ] },
      { index: 2, bye: true, home: team(2), away: null, winnerTeamId: 2, games: [] },
      { index: 3, bye: false, home: team(4), away: team(5), winnerTeamId: 4, games: [
        game(21, 1, 'HomeWin', 2, 0), game(22, 2, 'AwayWin', 0, 1), game(23, 3, 'HomeWin', 4, 0),
      ] },
    ],
  },
  {
    round: 2, name: 'Semifinals', slots: [
      { index: 0, bye: false, home: team(1), away: team(3), winnerTeamId: null, games: [
        game(31, 1, 'AwayWin', 0, 2), game(32, 2, 'Pending'),
      ] },
      { index: 1, bye: false, home: team(2), away: team(4), winnerTeamId: null, games: [
        game(41, 1, 'Pending'),
      ] },
    ],
  },
];
const allOpen = () => true;
const view = (isOpen, round, index) => visibleBracket(rounds, isOpen).get(seriesKey(round, index));

test('with nothing revealed a series shows its teams and replays but no score or winner', () => {
  const v = view(none, 1, 1);
  assert.deepEqual([v.home, v.away], [team(3), team(6)]);
  assert.deepEqual([v.homeWins, v.awayWins, v.winnerTeamId], [null, null, null]);
  assert.deepEqual(v.games.map((g) => g.game), [1]);
});

test('later games appear one at a time as the earlier ones are revealed', () => {
  assert.deepEqual(view(none, 1, 3).games.map((g) => g.game), [1]);
  assert.deepEqual(view(opened(21), 1, 3).games.map((g) => g.game), [1, 2]);
  assert.deepEqual(view(opened(21, 22), 1, 3).games.map((g) => g.game), [1, 2, 3]);
});

test('a sweep and a decider look the same until the second game is revealed', () => {
  assert.deepEqual(view(opened(11), 1, 1).games.map((g) => g.game), [1, 2]);
  assert.deepEqual(view(opened(21), 1, 3).games.map((g) => g.game), [1, 2]);
});

test('the next unplayed game of a live series shows once the played ones are revealed', () => {
  assert.deepEqual(view(opened(11, 12), 2, 0).games.map((g) => g.game), [1]);
  assert.deepEqual(view(opened(11, 12, 31), 2, 0).games.map((g) => g.game), [1, 2]);
});

test('the score counts revealed games for each side', () => {
  const v = view(opened(21, 22), 1, 3);
  assert.deepEqual([v.homeWins, v.awayWins, v.winnerTeamId], [1, 1, null]);
});

test('the winner shows once every played game is revealed', () => {
  const v = view(opened(11, 12), 1, 1);
  assert.deepEqual([v.homeWins, v.awayWins, v.winnerTeamId, v.settled], [2, 0, 3, true]);
});

test('a revealed game can still hide an unrevealed one before it', () => {
  const v = view(opened(12), 1, 1);
  assert.deepEqual(v.games.map((g) => g.game), [1]);
  assert.equal(v.homeWins, null);
});

test('a bye is settled from the start', () => {
  const v = view(none, 1, 0);
  assert.equal(v.settled, true);
  assert.equal(v.winnerTeamId, 1);
});

test('a team from an unsettled series is hidden in the next round, with that series locked', () => {
  const v = view(none, 2, 0);
  assert.deepEqual(v.home, team(1));
  assert.equal(v.away, null);
  assert.deepEqual(v.games, []);
  assert.deepEqual([v.homeWins, v.awayWins], [null, null]);
});

test('settling a series reveals its winner in the next round and that series opens', () => {
  const v = view(opened(11, 12), 2, 0);
  assert.deepEqual(v.away, team(3));
  assert.deepEqual(v.games.map((g) => g.game), [1]);
});

test('every game revealed shows the whole bracket', () => {
  const v = view(allOpen, 2, 0);
  assert.deepEqual([v.home, v.away, v.homeWins, v.awayWins], [team(1), team(3), 0, 1]);
  assert.deepEqual(v.games.map((g) => g.game), [1, 2]);
  assert.deepEqual(view(allOpen, 2, 1).games.map((g) => g.game), [1]);
});

// Seed 9 upset 8 (revealed); 4 v 5 is either decided for 4 but unrevealed, or still live.
const upset = (other, next) => [
  { round: 1, slots: [
    { index: 0, bye: false, home: team(4), away: team(5), ...other },
    { index: 1, bye: false, home: team(8), away: team(9), winnerTeamId: 9, games: [game(51, 1, 'AwayWin', 0, 1), game(52, 2, 'AwayWin', 0, 2)] },
  ] },
  { round: 2, slots: [{ index: 0, bye: false, winnerTeamId: null, ...next }] },
];

test('a hidden opponent sits in the same row whether or not its series is decided', () => {
  const isOpen = opened(51, 52);
  const decided = upset(
    { winnerTeamId: 4, games: [game(61, 1, 'HomeWin', 1, 0), game(62, 2, 'HomeWin', 1, 0)] },
    { home: team(4), away: team(9), games: [game(71, 1, 'Pending')] },
  );
  const live = upset(
    { winnerTeamId: null, games: [game(61, 1, 'HomeWin', 1, 0), game(62, 2, 'Pending')] },
    { home: team(9), away: null, games: [] },
  );
  const at = (rounds) => visibleBracket(rounds, isOpen).get(seriesKey(2, 0));
  assert.deepEqual([at(decided).home, at(decided).away], [team(9), null]);
  assert.deepEqual([at(live).home, at(live).away], [team(9), null]);
  assert.deepEqual(at(decided).games, []);
});

test('an unstarted series between visible teams lists its games with no score', () => {
  const v = view(opened(21, 22, 23), 2, 1);
  assert.deepEqual([v.home, v.away], [team(2), team(4)]);
  assert.deepEqual([v.homeWins, v.awayWins], [null, null]);
});

test('no bracket gives no views', () => {
  assert.equal(visibleBracket(null, none).size, 0);
});

test('keys keep round and index apart', () => {
  assert.notEqual(seriesKey(1, 12), seriesKey(11, 2));
});

const games = [game(1, 1, 'HomeWin', 1, 0), game(2, 2, 'AwayWin', 0, 1), game(3, 3, 'Pending')];

test('the picked game is the one asked for', () => {
  assert.equal(pickGame(games, 3, none), games[2]);
});

test('with no pick the next game to watch is the first hidden one', () => {
  assert.equal(pickGame(games, null, none), games[0]);
  assert.equal(pickGame(games, null, opened(1)), games[1]);
});

test('with nothing left to watch, or a pick that no longer exists, it falls back to the first', () => {
  assert.equal(pickGame(games, null, allOpen), games[0]);
  assert.equal(pickGame(games.slice(0, 2), 3, allOpen), games[0]);
});

test('a series without games picks nothing', () => {
  assert.equal(pickGame([], 1, none), null);
});

test('a revealed game shows its score from the home side', () => {
  assert.equal(gameLabel(game(1, 1, 'HomeWin', 5, 0), true), 'G1 5–0');
  assert.equal(gameLabel(game(1, 2, 'AwayWin', 0, 2), true), 'G2 0–2');
});

test('a revealed draw says so', () => {
  assert.equal(gameLabel(game(1, 2, 'Draw', 0, 0), true), 'G2 draw');
});

test('a hidden game shows only its number', () => {
  assert.equal(gameLabel(game(1, 1, 'HomeWin', 5, 0), false), 'G1');
  assert.equal(gameLabel(game(1, 2, 'Draw', 0, 0), false), 'G2');
});

test('an unplayed game, or one without a score, shows only its number', () => {
  assert.equal(gameLabel(game(1, 3, 'Pending'), true), 'G3');
  assert.equal(gameLabel(game(1, 1, 'HomeWin'), true), 'G1');
});
