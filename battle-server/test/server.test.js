'use strict';

const { test } = require('node:test');
const assert = require('node:assert');
const { BattleServer } = require('../lib/server');

const REQ = '|request|'.length;

// Boot a fresh server on an ephemeral port for each test, and always tear it
// down. Returns { url, server }.
async function boot() {
  const server = new BattleServer();
  const { host, port } = await server.listen(0);
  return { url: `ws://${host}:${port}`, http: `http://${host}:${port}`, server };
}

// A player socket that answers every non-wait request with a legal default —
// enough to play a battle to its end over the wire.
function autoPlayer(url, matchId, side) {
  const ws = new WebSocket(url);
  ws.addEventListener('open', () => ws.send(JSON.stringify({ type: 'join', matchId, side })));
  ws.addEventListener('message', (ev) => {
    const msg = JSON.parse(ev.data);
    if (msg.type !== 'protocol') return;
    for (const line of msg.chunk.split('\n')) {
      if (!line.startsWith('|request|')) continue;
      const json = line.slice(REQ);
      if (!json || json === 'null') continue;
      if (JSON.parse(json).wait) continue;
      ws.send(JSON.stringify({ type: 'choose', decision: 'default' }));
    }
  });
  return ws;
}

// Resolve when a single JSON message matching `pred` arrives on a fresh socket
// that first sends `first`.
function once(url, first, pred) {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(url);
    ws.addEventListener('open', () => ws.send(JSON.stringify(first)));
    ws.addEventListener('message', (ev) => {
      const msg = JSON.parse(ev.data);
      if (pred(msg)) { resolve(msg); ws.close(); }
    });
    ws.addEventListener('error', reject);
    setTimeout(() => reject(new Error('timed out waiting for message')), 15000);
  });
}

test('/health reports ok and the active draft format', async () => {
  const { http, server } = await boot();
  try {
    const body = await (await fetch(`${http}/health`)).json();
    assert.equal(body.ok, true);
    assert.match(body.format, /^gen9customgame@@@/);
  } finally { await server.close(); }
});

test('serves the viewer page at /', async () => {
  const { http, server } = await boot();
  try {
    const res = await fetch(`${http}/`);
    assert.equal(res.status, 200);
    const html = await res.text();
    assert.match(html, /Battle Viewer/);
  } finally { await server.close(); }
});

test('a directory-escape path is refused', async () => {
  const { http, server } = await boot();
  try {
    const res = await fetch(`${http}/../lib/server.js`);
    assert.ok(res.status === 403 || res.status === 404);
  } finally { await server.close(); }
});

test('plays a demo battle end-to-end over WebSocket and reports a winner', async () => {
  const { url, server } = await boot();
  try {
    const matchId = 'e2e-match';
    const winner = await new Promise((resolve, reject) => {
      const control = new WebSocket(url);
      control.addEventListener('open', () => control.send(JSON.stringify({ type: 'create-demo', matchId })));
      control.addEventListener('message', (ev) => {
        const msg = JSON.parse(ev.data);
        if (msg.type === 'created') {
          control.send(JSON.stringify({ type: 'join', matchId, side: 'spectator' }));
          autoPlayer(url, matchId, 'p1');
          autoPlayer(url, matchId, 'p2');
        } else if (msg.type === 'end') {
          resolve(msg.winner);
        } else if (msg.type === 'error') {
          reject(new Error(msg.error));
        }
      });
      setTimeout(() => reject(new Error('battle timed out')), 90000);
    });
    // '' is a tie; a name is a win. null/undefined would mean no result came back.
    assert.ok(winner === '' || (typeof winner === 'string' && winner.length > 0));
  } finally { await server.close(); }
});

test('joining an unknown room errors', async () => {
  const { url, server } = await boot();
  try {
    const msg = await once(url, { type: 'join', matchId: 'ghost', side: 'p1' }, (m) => m.type === 'error');
    assert.match(msg.error, /No room/i);
  } finally { await server.close(); }
});

test('creating the same room id twice errors', async () => {
  const { url, server } = await boot();
  try {
    const matchId = 'dupe';
    await once(url, { type: 'create-demo', matchId }, (m) => m.type === 'created');
    const err = await once(url, { type: 'create-demo', matchId }, (m) => m.type === 'error');
    assert.match(err.error, /already exists/i);
  } finally { await server.close(); }
});

test('a spectator cannot submit a choice', async () => {
  const { url, server } = await boot();
  try {
    const matchId = 'spec';
    await once(url, { type: 'create-demo', matchId }, (m) => m.type === 'created');
    // One socket: join spectator, then try to choose.
    const err = await new Promise((resolve, reject) => {
      const ws = new WebSocket(url);
      let joined = false;
      ws.addEventListener('open', () => ws.send(JSON.stringify({ type: 'join', matchId, side: 'spectator' })));
      ws.addEventListener('message', (ev) => {
        const msg = JSON.parse(ev.data);
        if (msg.type === 'joined') { joined = true; ws.send(JSON.stringify({ type: 'choose', decision: 'default' })); }
        else if (msg.type === 'error' && joined) resolve(msg.error);
      });
      setTimeout(() => reject(new Error('no error came back')), 15000);
    });
    assert.match(err, /spectator/i);
  } finally { await server.close(); }
});

test('malformed JSON frame yields an error, not a crash', async () => {
  const { url, server } = await boot();
  try {
    const err = await new Promise((resolve, reject) => {
      const ws = new WebSocket(url);
      ws.addEventListener('open', () => ws.send('this is not json'));
      ws.addEventListener('message', (ev) => {
        const msg = JSON.parse(ev.data);
        if (msg.type === 'error') resolve(msg.error);
      });
      setTimeout(() => reject(new Error('no error came back')), 10000);
    });
    assert.match(err, /Malformed JSON/i);
  } finally { await server.close(); }
});
