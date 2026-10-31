'use strict';

const { test, mock } = require('node:test');
const assert = require('node:assert');
const {
  rememberName, startAutoLogin, infoUrl, PROBE_MS, MAX_PROBE_MS, RESEND_MS, TICK_MS, MAX_SENDS,
} = require('../lib/auto-login');

const WANT_ID = 'coachone';
const LOGIN_LINE = '/trn Coach One,0,';
const TWO_MINUTES_MS = 2 * 60 * 1000;

function store(values = {}) {
  const data = new Map(Object.entries(values));
  return {
    getItem: (key) => (data.has(key) ? data.get(key) : null),
    setItem: (key, value) => data.set(key, String(value)),
  };
}

function blocked() {
  const refuse = () => { throw new Error('storage blocked'); };
  return { getItem: refuse, setItem: refuse };
}

function page(search, local = store(), session = store()) {
  return { location: { search }, localStorage: local, sessionStorage: session };
}

function connectedPage() {
  const sent = [];
  const values = { userid: 'guest1' };
  const win = {
    sessionStorage: store(),
    Config: { server: { host: 'showdown.test', port: 443 } },
    location: { reload: mock.fn() },
    fetch: mock.fn(() => Promise.resolve({ ok: true })),
    app: {
      user: { challstr: '', get: (key) => values[key] },
      topbar: { updateUserbar: mock.fn() },
      send: (line) => sent.push(line),
    },
  };
  return { win, sent, values };
}

function withClock(body) {
  return async () => {
    mock.timers.enable({ apis: ['Date', 'setInterval'] });
    try {
      await body();
    } finally {
      mock.timers.reset();
    }
  };
}

async function advance(ms) {
  mock.timers.tick(ms);
  await new Promise((resolve) => setImmediate(resolve));
}

test('a ?name= is saved on the device', () => {
  const win = page('?name=Coach');
  assert.strictEqual(rememberName(win), 'Coach');
  assert.strictEqual(win.localStorage.getItem('zst_name'), 'Coach');
  assert.strictEqual(win.__zstName, 'Coach');
});

test('a load without ?name= uses the saved name', () => {
  const win = page('', store({ zst_name: 'Coach' }));
  assert.strictEqual(rememberName(win), 'Coach');
  assert.strictEqual(win.__zstName, 'Coach');
});

test('a new ?name= replaces the saved one', () => {
  const win = page('?name=Second', store({ zst_name: 'First' }), store({ zst_name: 'First' }));
  assert.strictEqual(rememberName(win), 'Second');
  assert.strictEqual(win.localStorage.getItem('zst_name'), 'Second');
});

test('an older tab reloading takes the replaced name, not its own copy', () => {
  const win = page('', store({ zst_name: 'Second' }), store({ zst_name: 'First' }));
  assert.strictEqual(rememberName(win), 'Second');
  assert.strictEqual(win.sessionStorage.getItem('zst_name'), 'Second');
});

test('an empty ?name= keeps the saved name', () => {
  assert.strictEqual(rememberName(page('?name=', store({ zst_name: 'Coach' }))), 'Coach');
});

test('nobody saved and no ?name= leaves no name', () => {
  const win = page('');
  assert.strictEqual(rememberName(win), '');
  assert.strictEqual(win.__zstName, undefined);
});

test('blocked localStorage falls back to the tab', () => {
  assert.strictEqual(rememberName(page('', blocked(), store({ zst_name: 'Coach' }))), 'Coach');
});

test('blocked storage still takes a ?name=', () => {
  assert.strictEqual(rememberName(page('?name=Coach', blocked(), blocked())), 'Coach');
});

test('the /info url follows the server and ignores a blanked prefix', () => {
  assert.strictEqual(infoUrl({ host: 'showdown.test', port: 443 }), 'https://showdown.test:443/showdown/info');
  assert.strictEqual(infoUrl({ host: '127.0.0.1', port: 8787 }), 'http://127.0.0.1:8787/showdown/info');
});

test('sends once a connection is up, and not before', withClock(async () => {
  const { win, sent } = connectedPage();
  startAutoLogin(win, 'Coach One');
  await advance(TICK_MS);
  assert.deepStrictEqual(sent, []);
  win.app.user.challstr = 'one';
  await advance(TICK_MS);
  assert.deepStrictEqual(sent, [LOGIN_LINE]);
}));

test('a connection slower than two minutes still signs in', withClock(async () => {
  const { win, sent } = connectedPage();
  startAutoLogin(win, 'Coach One');
  for (let elapsed = 0; elapsed < TWO_MINUTES_MS * 2; elapsed += TICK_MS) await advance(TICK_MS);
  win.app.user.challstr = 'late';
  await advance(TICK_MS);
  assert.deepStrictEqual(sent, [LOGIN_LINE]);
}));

test('resends until the rename lands, then stops', withClock(async () => {
  const { win, sent, values } = connectedPage();
  win.app.user.challstr = 'one';
  startAutoLogin(win, 'Coach One');
  await advance(TICK_MS);
  await advance(RESEND_MS);
  assert.strictEqual(sent.length, 2);
  values.userid = WANT_ID;
  await advance(RESEND_MS * 2);
  assert.strictEqual(sent.length, 2);
  assert.strictEqual(win.app.topbar.updateUserbar.mock.callCount(), 1);
}));

test('a new connection sends even while the old userid still matches', withClock(async () => {
  const { win, sent, values } = connectedPage();
  win.app.user.challstr = 'one';
  values.userid = WANT_ID;
  startAutoLogin(win, 'Coach One');
  await advance(TICK_MS);
  await advance(TICK_MS);
  assert.strictEqual(sent.length, 1);
  win.app.user.challstr = 'two';
  await advance(TICK_MS);
  assert.strictEqual(sent.length, 2);
}));

test('dropping to a guest while connected signs back in', withClock(async () => {
  const { win, sent, values } = connectedPage();
  win.app.user.challstr = 'one';
  values.userid = WANT_ID;
  startAutoLogin(win, 'Coach One');
  await advance(TICK_MS);
  await advance(RESEND_MS);
  values.userid = 'guest2';
  await advance(RESEND_MS);
  assert.strictEqual(sent.length, 2);
}));

test('a name the server keeps refusing stops at the cap, and a new connection retries', withClock(async () => {
  const { win, sent } = connectedPage();
  win.app.user.challstr = 'one';
  startAutoLogin(win, 'Coach One');
  for (let i = 0; i < MAX_SENDS + 5; i++) await advance(RESEND_MS);
  assert.strictEqual(sent.length, MAX_SENDS);
  win.app.user.challstr = 'two';
  await advance(TICK_MS);
  assert.strictEqual(sent.length, MAX_SENDS + 1);
}));

test('a disconnected page sends nothing into the client queue', withClock(async () => {
  const { win, sent } = connectedPage();
  win.app.user.challstr = 'one';
  win.app.isDisconnected = true;
  startAutoLogin(win, 'Coach One');
  await advance(RESEND_MS * 2);
  assert.deepStrictEqual(sent, []);
}));

test('a disconnected page reloads once the server answers', withClock(async () => {
  const { win } = connectedPage();
  win.app.isDisconnected = true;
  let up = false;
  win.fetch = mock.fn(() => (up ? Promise.resolve({ ok: true }) : Promise.reject(new Error('down'))));
  startAutoLogin(win, 'Coach One');
  await advance(TICK_MS);
  await advance(PROBE_MS);
  await advance(TICK_MS);
  assert.strictEqual(win.fetch.mock.callCount(), 1);
  assert.strictEqual(win.fetch.mock.calls[0].arguments[0], 'https://showdown.test:443/showdown/info');
  assert.strictEqual(win.location.reload.mock.callCount(), 0);
  up = true;
  await advance(PROBE_MS);
  await advance(TICK_MS);
  assert.strictEqual(win.location.reload.mock.callCount(), 1);
}));

test('a tunnel error page is not the server coming back', withClock(async () => {
  const { win } = connectedPage();
  win.app.isDisconnected = true;
  win.fetch = mock.fn(() => Promise.resolve({ ok: false }));
  startAutoLogin(win, 'Coach One');
  await advance(TICK_MS);
  await advance(PROBE_MS);
  await advance(TICK_MS);
  assert.strictEqual(win.fetch.mock.callCount(), 1);
  assert.strictEqual(win.location.reload.mock.callCount(), 0);
}));

test('reloads that never sign in back off up to the cap', withClock(async () => {
  const { win } = connectedPage();
  win.app.isDisconnected = true;
  win.sessionStorage.setItem('zst_reconnects', '20');
  startAutoLogin(win, 'Coach One');
  await advance(TICK_MS);
  await advance(MAX_PROBE_MS - TICK_MS * 2);
  assert.strictEqual(win.fetch.mock.callCount(), 0);
  await advance(TICK_MS * 2);
  assert.strictEqual(win.fetch.mock.callCount(), 1);
}));

test('signing in clears the backoff', withClock(async () => {
  const { win, values } = connectedPage();
  win.sessionStorage.setItem('zst_reconnects', '4');
  win.app.user.challstr = 'one';
  values.userid = WANT_ID;
  startAutoLogin(win, 'Coach One');
  await advance(TICK_MS);
  await advance(TICK_MS);
  assert.strictEqual(win.sessionStorage.getItem('zst_reconnects'), '0');
}));

test('a throwing page never escapes the interval', withClock(async () => {
  const { win } = connectedPage();
  win.app.user.get = () => { throw new Error('client not ready'); };
  win.app.user.challstr = 'one';
  win.app.send = () => { throw new Error('client not ready'); };
  startAutoLogin(win, 'Coach One');
  await advance(TICK_MS);
  await advance(TICK_MS);
}));
