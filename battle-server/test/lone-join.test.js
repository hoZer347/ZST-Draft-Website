'use strict';

const { test, before, after } = require('node:test');
const assert = require('node:assert');
const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');
const { engineDir, hasChampions } = require('../lib/engine');

const START_TIMEOUT_MS = 60000;
const MESSAGE_TIMEOUT_MS = 20000;
const QUIET_MS = 1000;
const LEAVE_MS = 200;
const PLUGIN = 'lone-join.js';
const skip = hasChampions(engineDir()) ? false : 'no Champions engine built';

let server;
let url;

function freePort() {
  return new Promise((resolve) => {
    const probe = net.createServer();
    probe.listen(0, '127.0.0.1', () => {
      const { port } = probe.address();
      probe.close(() => resolve(port));
    });
  });
}

function connect() {
  const ws = new WebSocket(url);
  const lines = [];
  const waiters = new Set();
  const notify = () => {
    for (const waiter of waiters) {
      if (lines.slice(waiter.from).some(waiter.match)) waiter.resolve();
    }
  };
  ws.onmessage = (event) => {
    lines.push(...String(event.data).split('\n'));
    notify();
  };
  const closed = new Promise((resolve) => { ws.onclose = resolve; });
  return {
    lines,
    send: (text) => ws.send(`|${text}`),
    close: async () => {
      ws.close();
      await closed;
      await sleep(LEAVE_MS);
    },
    until: (match, label, from = 0) => new Promise((resolve, reject) => {
      const waiter = {
        match,
        from,
        resolve: () => {
          clearTimeout(timer);
          waiters.delete(waiter);
          resolve();
        },
      };
      const timer = setTimeout(() => {
        waiters.delete(waiter);
        reject(new Error(`no ${label} in ${MESSAGE_TIMEOUT_MS}ms`));
      }, MESSAGE_TIMEOUT_MS);
      waiters.add(waiter);
      notify();
    }),
  };
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));
const isJoin = (name) => (line) => /^\|[jJ]\|/.test(line) && line.slice(3).trim() === name;
const joinsAfter = (page, from, name) => page.lines.slice(from).filter(isJoin(name));

async function openPage() {
  const page = connect();
  await page.until((line) => line.startsWith('|challstr|'), 'challstr');
  page.send('/autojoin');
  await page.until((line) => line === '|init|chat', 'lobby');
  return page;
}

async function logIn(page, name) {
  page.send(`/trn ${name},0,`);
  await page.until((line) => line.startsWith(`|updateuser| ${name}|1|`), 'login');
}

async function joinLobby(name) {
  const page = await openPage();
  const from = page.lines.length;
  await logIn(page, name);
  await page.until(isJoin(name), 'join line', from);
  return page;
}

before(async () => {
  if (skip) return;
  const dir = engineDir();
  fs.copyFileSync(path.join(__dirname, '..', 'showdown-config', 'chat-plugins', PLUGIN), path.join(dir, 'dist', 'server', 'chat-plugins', PLUGIN));
  const port = await freePort();
  url = `ws://127.0.0.1:${port}/showdown/websocket`;
  server = spawn(process.execPath, ['pokemon-showdown', 'start', String(port), '--skip-build'], { cwd: dir, stdio: ['ignore', 'pipe', 'ignore'] });
  await new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error('the engine did not start')), START_TIMEOUT_MS);
    server.stdout.on('data', (chunk) => {
      if (!String(chunk).includes('now listening')) return;
      clearTimeout(timer);
      resolve();
    });
  });
}, { timeout: START_TIMEOUT_MS + MESSAGE_TIMEOUT_MS });

after(() => {
  if (server) spawnSync('taskkill', ['/pid', String(server.pid), '/T', '/F']);
});

test('a coach refreshing alone in the lobby is told they joined', { skip }, async () => {
  const first = await joinLobby('LoneCoach');
  await first.close();
  const refreshed = await openPage();
  assert.ok(refreshed.lines.includes('|users|0'));
  const from = refreshed.lines.length;
  await logIn(refreshed, 'LoneCoach');
  await refreshed.until(isJoin('LoneCoach'), 'join line', from);
  await refreshed.close();
});

test('a coach refreshing beside someone else joins once', { skip }, async () => {
  const other = await joinLobby('OtherCoach');
  const first = await joinLobby('SharedCoach');
  await first.close();
  const refreshed = await openPage();
  const from = refreshed.lines.length;
  const otherFrom = other.lines.length;
  await logIn(refreshed, 'SharedCoach');
  await refreshed.until(isJoin('SharedCoach'), 'join line', from);
  await sleep(QUIET_MS);
  assert.strictEqual(joinsAfter(refreshed, from, 'SharedCoach').length, 1);
  assert.strictEqual(joinsAfter(other, otherFrom, 'SharedCoach').length, 1);
  await refreshed.close();
  await other.close();
});

test('a first login in an empty lobby is told once', { skip }, async () => {
  const page = await openPage();
  const from = page.lines.length;
  await logIn(page, 'FirstCoach');
  await sleep(QUIET_MS);
  assert.strictEqual(joinsAfter(page, from, 'FirstCoach').length, 1);
  await page.close();
});

test('a second tab joining a coach in the room adds no join line', { skip }, async () => {
  const first = await joinLobby('TabCoach');
  const from = first.lines.length;
  const second = await openPage();
  await logIn(second, 'TabCoach');
  await sleep(QUIET_MS);
  assert.strictEqual(joinsAfter(first, from, 'TabCoach').length, 0);
  await first.close();
  await second.close();
});

test('a coach joining a room from a fresh socket is told once by the user list', { skip }, async () => {
  const first = await joinLobby('LateCoach');
  await first.close();
  const page = connect();
  await page.until((line) => line.startsWith('|challstr|'), 'challstr');
  await logIn(page, 'LateCoach');
  const from = page.lines.length;
  page.send('/autojoin');
  await page.until((line) => line === '|init|chat', 'lobby');
  await sleep(QUIET_MS);
  assert.deepStrictEqual(page.lines.slice(from).filter((line) => line.startsWith('|users|')), ['|users|1, LateCoach']);
  assert.strictEqual(page.lines.slice(from, page.lines.indexOf('|init|chat', from)).filter(isJoin('LateCoach')).length, 0);
  await page.close();
});
