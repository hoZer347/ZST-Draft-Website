'use strict';

// The coach's login in the self-hosted client, inlined into the page by scripts/serve-client.js.

(function (root, factory) {
  if (typeof module === 'object' && module.exports) module.exports = factory();
  else root.zstLogin = factory();
})(typeof self !== 'undefined' ? self : this, function () {
  const NAME_KEY = 'zst_name';
  const RECONNECTS_KEY = 'zst_reconnects';
  const TICK_MS = 250;
  const RESEND_MS = 1500;
  const MAX_SENDS = 80;
  const PROBE_MS = 5000;
  const MAX_PROBE_MS = 5 * 60 * 1000;
  const HTTPS_PORT = 443;
  // Not Config.sockjsprefix: the client blanks that when a page loads with the server down.
  const INFO_PATH = '/showdown/info';

  function read(win, store, key) {
    try { return win[store].getItem(key) || ''; } catch (e) { return ''; }
  }

  function write(win, store, key, value) {
    try { win[store].setItem(key, value); } catch (e) {}
  }

  // localStorage reads first so a tab's older sessionStorage copy can't restore a replaced name.
  function rememberName(win) {
    let name = '';
    try { name = new URLSearchParams(win.location.search).get('name') || ''; } catch (e) {}
    name = name || read(win, 'localStorage', NAME_KEY) || read(win, 'sessionStorage', NAME_KEY);
    if (name) {
      win.__zstName = name;
      write(win, 'localStorage', NAME_KEY, name);
      write(win, 'sessionStorage', NAME_KEY, name);
    }
    return name;
  }

  function infoUrl(server) {
    const scheme = server.port === HTTPS_PORT || server.https ? 'https' : 'http';
    return scheme + '://' + server.host + ':' + server.port + INFO_PATH;
  }

  function startAutoLogin(win, name) {
    const clean = name.replace(/[|,;]+/g, '');
    const wantId = clean.toLowerCase().replace(/[^a-z0-9]/g, '');
    let challstr = '';
    let sends = 0;
    let lastSend = -Infinity;
    let loggedIn = false;
    let nextProbe = 0;
    let probing = false;

    function settle(app) {
      sends = 0;
      if (loggedIn) return;
      loggedIn = true;
      write(win, 'sessionStorage', RECONNECTS_KEY, '0');
      // The topbar's own 'change' handler can miss an update made before it was built.
      if (app.topbar && app.topbar.updateUserbar) app.topbar.updateUserbar();
    }

    // A new challstr is a new connection, whose userid is stale until |updateuser| arrives.
    function keepName(app) {
      const current = app.user.challstr;
      if (!current) return;
      const fresh = current !== challstr;
      if (fresh) {
        challstr = current;
        sends = 0;
        lastSend = -Infinity;
      }
      if (!fresh && app.user.get('userid') === wantId) {
        settle(app);
        return;
      }
      loggedIn = false;
      if (sends >= MAX_SENDS || Date.now() - lastSend < RESEND_MS) return;
      sends++;
      lastSend = Date.now();
      app.send('/trn ' + clean + ',0,');
    }

    // Doubles per failed reload, so a server that answers /info but refuses sockets can't spin the page.
    function reconnect() {
      if (probing || !win.Config || !win.Config.server) return;
      const reconnects = Number(read(win, 'sessionStorage', RECONNECTS_KEY)) || 0;
      const wait = Math.min(PROBE_MS * Math.pow(2, reconnects), MAX_PROBE_MS);
      if (!nextProbe) nextProbe = Date.now() + wait;
      if (Date.now() < nextProbe) return;
      probing = true;
      nextProbe = Date.now() + PROBE_MS;
      win.fetch(infoUrl(win.Config.server), { cache: 'no-store' }).then(function (response) {
        probing = false;
        if (!response.ok) return;
        write(win, 'sessionStorage', RECONNECTS_KEY, String(reconnects + 1));
        win.location.reload();
      }, function () {
        probing = false;
      });
    }

    setInterval(function () {
      try {
        const app = win.app;
        if (!app || !app.user) return;
        if (app.isDisconnected) reconnect();
        else keepName(app);
      } catch (e) { /* never break the client over auto-login */ }
    }, TICK_MS);
  }

  return {
    rememberName: rememberName,
    startAutoLogin: startAutoLogin,
    infoUrl: infoUrl,
    PROBE_MS: PROBE_MS,
    MAX_PROBE_MS: MAX_PROBE_MS,
    RESEND_MS: RESEND_MS,
    TICK_MS: TICK_MS,
    MAX_SENDS: MAX_SENDS,
  };
});
