'use strict';

// Proves the BattleRoom abstraction end-to-end through its real public surface:
// each side is driven purely via room.on(side) to receive protocol and
// room.choose(side, ...) to submit decisions — the exact contract the WebSocket
// layer and the .NET bridge will use. Here each side just answers every request
// with "default" (the sim's auto-pick), which is enough to play a battle out.
//
//   node scripts/room-test.js

const { BattleRoom } = require('../lib/battle-room');
const { packDraftTeam } = require('../lib/pack');

const TEAM_A = ['charizard-megay', 'venusaur', 'blastoise-mega', 'butterfree', 'beedrill-mega', 'pidgeot-mega'];
const TEAM_B = ['venusaur-mega', 'charizard', 'wartortle', 'blastoise', 'charizard-megax', 'weedle'];

function autoPlay(room, side) {
  room.on(side, (chunk) => {
    for (const line of chunk.split('\n')) {
      if (!line.startsWith('|request|')) continue;
      const json = line.slice('|request|'.length);
      if (!json || json === 'null') continue;
      const req = JSON.parse(json);
      if (req.wait) continue;      // it's the other side's decision
      room.choose(side, 'default'); // let the sim pick a legal default action
    }
  });
}

async function main() {
  const room = new BattleRoom({
    id: 'match-test',
    format: 'gen9customgame',
    p1: { name: 'Team Alpha', team: packDraftTeam(TEAM_A) },
    p2: { name: 'Team Beta', team: packDraftTeam(TEAM_B) },
  });

  autoPlay(room, 'p1');
  autoPlay(room, 'p2');

  const result = await new Promise((resolve) => room.on('end', resolve));
  console.log(`Room '${room.id}' ended. Winner: ${result.winner || '(tie)'}`);
  if (result.winner === null) throw new Error('No winner reported.');
}

main().catch((e) => { console.error(e); process.exit(1); });
