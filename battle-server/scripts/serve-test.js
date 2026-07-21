'use strict';

// End-to-end smoke test for the server + format: boots the real BattleServer,
// connects two WebSocket clients (Node's built-in WebSocket), creates a match in
// the league format, auto-plays both sides through the wire protocol, and asserts
// a winner comes back. This exercises the exact path the browser will use.
//
//   node scripts/serve-test.js

const { BattleServer } = require('../lib/server');
const { packDraftTeam } = require('../lib/pack');

const TEAM_A = ['charizard-megay', 'venusaur', 'blastoise-mega', 'butterfree', 'beedrill-mega', 'pidgeot-mega'];
const TEAM_B = ['venusaur-mega', 'charizard', 'wartortle', 'blastoise', 'charizard-megax', 'weedle'];

// A client that joins a side and answers every non-wait request with a legal
// default action, enough to play a battle to its end over the socket.
function autoClient(url, matchId, side) {
  const ws = new WebSocket(url);
  ws.addEventListener('open', () => ws.send(JSON.stringify({ type: 'join', matchId, side })));
  ws.addEventListener('message', (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.type !== 'protocol') return;
    for (const line of msg.chunk.split('\n')) {
      if (!line.startsWith('|request|')) continue;
      const json = line.slice('|request|'.length);
      if (!json || json === 'null') continue;
      if (JSON.parse(json).wait) continue; // other side's turn
      ws.send(JSON.stringify({ type: 'choose', decision: 'default' }));
    }
  });
  return ws;
}

async function main() {
  const server = new BattleServer();
  const { host, port } = await server.listen(0); // ephemeral port
  const url = `ws://${host}:${port}`;
  console.log(`[serve-test] server up on ${url}, format: ${server.format}`);

  const matchId = 'match-smoke';

  // A control socket creates the room and waits for the result.
  const control = new WebSocket(url);
  const done = new Promise((resolve, reject) => {
    control.addEventListener('open', () => {
      control.send(JSON.stringify({
        type: 'create',
        matchId,
        p1: { name: 'Team Alpha', team: packDraftTeam(TEAM_A) },
        p2: { name: 'Team Beta', team: packDraftTeam(TEAM_B) },
      }));
    });
    control.addEventListener('message', (ev) => {
      const msg = JSON.parse(ev.data);
      if (msg.type === 'created') {
        // Now that the room exists, join it as a spectator to watch for the end,
        // and seat both auto-players.
        control.send(JSON.stringify({ type: 'join', matchId, side: 'spectator' }));
        autoClient(url, matchId, 'p1');
        autoClient(url, matchId, 'p2');
      } else if (msg.type === 'end') {
        resolve(msg.winner);
      } else if (msg.type === 'error') {
        reject(new Error(msg.error));
      }
    });
    control.addEventListener('error', (e) => reject(e.error || new Error('socket error')));
  });

  const winner = await done;
  console.log(`[serve-test] battle ended. Winner: ${winner || '(tie)'}`);
  await server.close();
  if (winner === null || winner === undefined) throw new Error('No winner reported over the wire.');
  console.log('[serve-test] PASS');
}

main().catch((e) => { console.error('[serve-test] FAIL', e); process.exit(1); });
