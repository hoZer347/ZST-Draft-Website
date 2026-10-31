'use strict';

// Checks the live Showdown server for server/deploy-live.ps1.
//
//   node scripts/live-probe.js battles [url]          # prints the live battle count
//   node scripts/live-probe.js hold <seconds> [url]   # exit 1 if a socket through the tunnel closes

const LOCAL_URL = 'ws://127.0.0.1:8787/showdown/websocket';
const PUBLIC_URL = 'wss://showdown.loomhozer.ca/showdown';
const HELD_SOCKETS = 8;
const BATTLES_TIMEOUT_MS = 8000;
const CONNECT_TIMEOUT_MS = 15000;
const CONNECT_RETRY_MS = 1000;
const BATTLES_MARKER = '|queryresponse|livebattles|';

function fail(message) {
  console.error(message);
  process.exit(1);
}

function battles(url = LOCAL_URL) {
  const ws = new WebSocket(url);
  ws.onmessage = (event) => {
    const data = String(event.data);
    if (data.includes('|challstr|')) ws.send('|/livebattles');
    if (!data.includes(BATTLES_MARKER)) return;
    console.log(Number(data.split(BATTLES_MARKER)[1]));
    process.exit(0);
  };
  ws.onerror = () => fail(`could not reach ${url}`);
  setTimeout(() => fail(`no answer from ${url}`), BATTLES_TIMEOUT_MS);
}

function holdSocket(url, index, startedAt) {
  const connecting = setTimeout(() => fail(`socket ${index} never connected`), CONNECT_TIMEOUT_MS);
  const connect = () => {
    const session = Math.random().toString(36).slice(2, 10);
    const ws = new WebSocket(`${url}/${index}/${session}/websocket`);
    let opened = false;
    ws.onopen = () => {
      opened = true;
      clearTimeout(connecting);
    };
    ws.onclose = () => {
      if (opened) fail(`socket ${index} closed after ${Math.round((Date.now() - startedAt) / 1000)}s`);
      else setTimeout(connect, CONNECT_RETRY_MS);
    };
  };
  connect();
}

function hold(seconds, url = PUBLIC_URL) {
  const startedAt = Date.now();
  for (let i = 0; i < HELD_SOCKETS; i++) holdSocket(url, i, startedAt);
  setTimeout(() => {
    console.log(`held ${HELD_SOCKETS} sockets for ${seconds}s`);
    process.exit(0);
  }, seconds * 1000);
}

const [, , command, ...rest] = process.argv;
if (command === 'battles') battles(rest[0]);
else if (command === 'hold') hold(Number(rest[0]), rest[1]);
else fail('usage: live-probe.js battles [url] | hold <seconds> [url]');
