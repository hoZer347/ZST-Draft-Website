'use strict';

const { test } = require('node:test');
const assert = require('node:assert');
const { spawn } = require('node:child_process');
const { createHash } = require('node:crypto');
const http = require('node:http');
const path = require('node:path');

const PROBE = path.join(__dirname, '..', 'scripts', 'live-probe.js');
const WS_GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';
const TEXT_FRAME = 0x81;
const HOLD_SECONDS = '1';
const CLOSE_AFTER_MS = 200;
const textFrame = (text) => Buffer.concat([Buffer.from([TEXT_FRAME, text.length]), Buffer.from(text)]);

function accept(req, socket) {
  const key = createHash('sha1').update(req.headers['sec-websocket-key'] + WS_GUID).digest('base64');
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${key}\r\n\r\n`);
}

function serve(onUpgrade) {
  const server = http.createServer();
  server.on('upgrade', (req, socket) => {
    socket.on('error', () => {});
    onUpgrade(req, socket);
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve({
    url: `ws://127.0.0.1:${server.address().port}/showdown`,
    stop: () => { server.closeAllConnections(); server.close(); },
  })));
}

function probe(...args) {
  return new Promise((resolve) => {
    const child = spawn(process.execPath, [PROBE, ...args]);
    let out = '';
    child.stdout.on('data', (chunk) => { out += chunk; });
    child.on('close', (code) => resolve({ code, out: out.trim() }));
  });
}

test('battles prints the count the plugin answers with', async () => {
  const fake = await serve((req, socket) => {
    accept(req, socket);
    socket.write(textFrame('|challstr|4|abc'));
    socket.once('data', () => socket.write(textFrame('|queryresponse|livebattles|3')));
  });
  const { code, out } = await probe('battles', fake.url);
  fake.stop();
  assert.strictEqual(code, 0);
  assert.strictEqual(out, '3');
});

test('hold passes when every socket stays open', async () => {
  const fake = await serve(accept);
  const { code } = await probe('hold', HOLD_SECONDS, fake.url);
  fake.stop();
  assert.strictEqual(code, 0);
});

test('hold fails when an open socket closes', async () => {
  const fake = await serve((req, socket) => {
    accept(req, socket);
    setTimeout(() => socket.destroy(), CLOSE_AFTER_MS);
  });
  const { code } = await probe('hold', HOLD_SECONDS, fake.url);
  fake.stop();
  assert.strictEqual(code, 1);
});

test('hold retries a socket refused before it opened', async () => {
  let refused = false;
  const fake = await serve((req, socket) => {
    if (!refused) {
      refused = true;
      socket.end('HTTP/1.1 503 Service Unavailable\r\n\r\n');
      return;
    }
    accept(req, socket);
  });
  const { code } = await probe('hold', '2', fake.url);
  fake.stop();
  assert.strictEqual(code, 0);
});

test('livebattles counts started battles, even with a player gone', () => {
  const { commands } = require('../showdown-config/chat-plugins/live-battles');
  global.Rooms = { rooms: new Map([
    ['lobby', {}],
    ['battle-a', { battle: { started: true, ended: false, playerCount: 1 } }],
    ['battle-b', { battle: { started: true, ended: true } }],
    ['battle-c', { battle: { started: false, ended: false } }],
  ]) };
  let sent = '';
  commands.livebattles('', null, null, { send: (text) => { sent = text; } });
  delete global.Rooms;
  assert.strictEqual(sent, '|queryresponse|livebattles|1');
});
