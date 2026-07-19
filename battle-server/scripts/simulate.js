'use strict';

// Headless proof that two drafted rosters produce a real, playable battle.
// Builds a team for each side, drops a random-move AI on both, runs the battle
// to completion, and prints the winner. This de-risks the league-specific core
// — species naming and team packing — before we stand up the interactive server.
//
//   node scripts/simulate.js

const { BattleStream, getPlayerStreams } = require('pokemon-showdown');
const { RandomPlayerAI } = require('pokemon-showdown/dist/sim/tools/random-player-ai');
const { packDraftTeam } = require('../lib/pack');

// Two sample "drafted" rosters, using our pool's species slugs.
const TEAM_A = ['charizard-megay', 'venusaur', 'blastoise-mega', 'butterfree', 'beedrill-mega', 'pidgeot-mega'];
const TEAM_B = ['venusaur-mega', 'charizard', 'wartortle', 'blastoise', 'charizard-megax', 'weedle'];

async function main() {
  const streams = getPlayerStreams(new BattleStream());

  const spec = { formatid: 'gen9customgame' };
  const p1spec = { name: 'Team Alpha', team: packDraftTeam(TEAM_A) };
  const p2spec = { name: 'Team Beta', team: packDraftTeam(TEAM_B) };

  new RandomPlayerAI(streams.p1).start();
  new RandomPlayerAI(streams.p2).start();

  let winner = null;
  let turns = 0;
  const done = (async () => {
    for await (const chunk of streams.omniscient) {
      for (const line of chunk.split('\n')) {
        if (line.startsWith('|turn|')) turns = Number(line.slice(6)) || turns;
        if (line.startsWith('|win|')) winner = line.slice(5);
      }
    }
  })();

  void streams.omniscient.write(
    `>start ${JSON.stringify(spec)}\n` +
    `>player p1 ${JSON.stringify(p1spec)}\n` +
    `>player p2 ${JSON.stringify(p2spec)}`
  );

  await done;

  if (!winner) throw new Error('Battle ended with no winner — something is wrong.');
  console.log(`Battle ran ${turns} turns. Winner: ${winner}`);
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
