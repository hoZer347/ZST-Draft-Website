/* Discord login for the browser, using Authorization Code + PKCE.
 *
 * This page is a public client — anything shipped here is readable by anyone,
 * so it never sees the Discord client secret. It obtains a `code` from Discord
 * and hands it to our API, which does the secret-bearing exchange and returns
 * our own tokens.
 *
 * The previous version of this site used response_type=token (the implicit
 * flow): the access token landed in the URL fragment, with no PKCE and no
 * refresh. That flow is deprecated and isn't used here.
 */

const Auth = (() => {
  const { apiBase } = window.DRAFT_CONFIG;

  // localStorage survives the redirect to Discord and back; sessionStorage
  // would too, but tokens should persist across tabs and restarts.
  const STORE = 'draft.auth';
  const VERIFIER = 'draft.pkce.verifier';
  const STATE = 'draft.oauth.state';
  // While an admin is "viewing as" a dummy coach, their own session is stashed
  // here so they can pop straight back without re-logging-in.
  const IMPERSONATOR = 'draft.auth.impersonator';

  const load = () => { try { return JSON.parse(localStorage.getItem(STORE)) ?? null; } catch { return null; } };
  const save = (v) => localStorage.setItem(STORE, JSON.stringify(v));
  const clear = () => localStorage.removeItem(STORE);

  // ── PKCE ─────────────────────────────────────────────────────────────

  const b64url = (bytes) =>
    btoa(String.fromCharCode(...new Uint8Array(bytes)))
      .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

  const randomString = () => b64url(crypto.getRandomValues(new Uint8Array(32)));

  // SubtleCrypto (crypto.subtle) only exists in a secure context — HTTPS or
  // localhost. Opening the dev site on a phone over the LAN (plain http://<ip>)
  // leaves crypto.subtle undefined, and `crypto.subtle.digest(...)` threw
  // "Cannot read properties of undefined (reading 'digest')" during login. This
  // pure-JS SHA-256 is the fallback so the S256 PKCE challenge still works there;
  // production runs over HTTPS and uses the native digest.
  function sha256(bytes) {
    const K = new Uint32Array([
      0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
      0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
      0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
      0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
      0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
      0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
      0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
      0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2]);
    let h0 = 0x6a09e667, h1 = 0xbb67ae85, h2 = 0x3c6ef372, h3 = 0xa54ff53a,
      h4 = 0x510e527f, h5 = 0x9b05688c, h6 = 0x1f83d9ab, h7 = 0x5be0cd19;
    const l = bytes.length, bitLen = l * 8, withOne = l + 1;
    const total = withOne + (56 - (withOne % 64) + 64) % 64 + 8;
    const m = new Uint8Array(total);
    m.set(bytes);
    m[l] = 0x80;
    const dv = new DataView(m.buffer);
    dv.setUint32(total - 8, Math.floor(bitLen / 0x100000000));
    dv.setUint32(total - 4, bitLen >>> 0);
    const w = new Uint32Array(64), rotr = (x, n) => (x >>> n) | (x << (32 - n));
    for (let off = 0; off < total; off += 64) {
      for (let i = 0; i < 16; i++) w[i] = dv.getUint32(off + i * 4);
      for (let i = 16; i < 64; i++) {
        const s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >>> 3);
        const s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >>> 10);
        w[i] = (w[i - 16] + s0 + w[i - 7] + s1) >>> 0;
      }
      let a = h0, b = h1, c = h2, d = h3, e = h4, f = h5, g = h6, h = h7;
      for (let i = 0; i < 64; i++) {
        const S1 = rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25);
        const ch = (e & f) ^ (~e & g);
        const t1 = (h + S1 + ch + K[i] + w[i]) >>> 0;
        const S0 = rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22);
        const maj = (a & b) ^ (a & c) ^ (b & c);
        const t2 = (S0 + maj) >>> 0;
        h = g; g = f; f = e; e = (d + t1) >>> 0; d = c; c = b; b = a; a = (t1 + t2) >>> 0;
      }
      h0 = (h0 + a) >>> 0; h1 = (h1 + b) >>> 0; h2 = (h2 + c) >>> 0; h3 = (h3 + d) >>> 0;
      h4 = (h4 + e) >>> 0; h5 = (h5 + f) >>> 0; h6 = (h6 + g) >>> 0; h7 = (h7 + h) >>> 0;
    }
    const out = new Uint8Array(32), od = new DataView(out.buffer);
    [h0, h1, h2, h3, h4, h5, h6, h7].forEach((h, i) => od.setUint32(i * 4, h));
    return out;
  }

  async function challengeFor(verifier) {
    const data = new TextEncoder().encode(verifier);
    const digest = crypto.subtle
      ? await crypto.subtle.digest('SHA-256', data)
      : sha256(data);
    return b64url(digest);
  }

  /** Must exactly match a URI registered in the Discord portal AND in the
   *  server's Discord:RedirectUris. Discord compares it byte-for-byte. */
  const redirectUri = () => `${location.origin}/`;

  // ── login / logout ───────────────────────────────────────────────────

  async function login() {
    // Guard the response: when the API is down/restarting, the Cloudflare tunnel
    // returns an HTML error page, and a bare .json() throws the cryptic
    // "Unexpected token '<'". Turn that into a clear, retryable message.
    let cfg;
    try {
      const res = await fetch(`${apiBase}/api/auth/config`);
      if (!res.ok) throw new Error(`server returned ${res.status}`);
      cfg = await res.json();
    } catch {
      throw new Error('Server is unavailable right now — please try again in a moment.');
    }
    if (!cfg.configured) {
      throw new Error('Discord login is not configured on the server (see AUTH_SETUP.md)');
    }

    const verifier = randomString();
    const state = randomString();
    sessionStorage.setItem(VERIFIER, verifier);
    sessionStorage.setItem(STATE, state);

    const params = new URLSearchParams({
      client_id: cfg.clientId,
      redirect_uri: redirectUri(),
      response_type: 'code',
      scope: cfg.scopes,
      state,
      code_challenge: await challengeFor(verifier),
      code_challenge_method: 'S256',
    });
    location.assign(`${cfg.authorizeUrl}?${params}`);
  }

  // ── dev sign-in (localhost only) ─────────────────────────────────────
  // Sign in as an existing account without Discord, for local testing. All of
  // this 404s on a deployed server (the /dev/* routes only exist in Development).

  /** Existing accounts you can sign in as: the admin plus every coach. */
  async function devAccounts() {
    const res = await fetch(`${apiBase}/dev/accounts`);
    if (!res.ok) throw new Error('Dev accounts need the API running in Development');
    return res.json();
  }

  /** Sign in as `discordId` (dev bypass). Stores a session shaped like a real login. */
  async function devSignInAs(discordId, admin = false) {
    const res = await fetch(`${apiBase}/dev/token/${encodeURIComponent(discordId)}?admin=${admin}`, { method: 'POST' });
    if (!res.ok) throw new Error('Could not sign in as that account');
    const body = await res.json();
    save({
      accessToken: body.accessToken,
      refreshToken: body.refreshToken,
      expiresAt: body.accessExpiresAt,
      user: body.user,
    });
  }

  // ── admin: view as another user ──────────────────────────────────────

  /** Admin-only: the users an admin can browse as (everyone in the roster). */
  async function loadDummies() {
    const res = await authFetch('/api/admin/dummies');
    if (!res.ok) throw new Error('Could not load dummy accounts');
    return res.json();
  }

  /**
   * Swap the current admin session for a dummy coach's, stashing the admin one so
   * stopImpersonating() can restore it. Only the first swap stashes, so a chain of
   * view-as jumps still returns to the real admin.
   */
  async function impersonate(discordId) {
    const res = await authFetch('/api/admin/impersonate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ discordId }),
    });
    if (!res.ok) {
      const err = await res.json().catch(() => ({}));
      throw new Error(err.error ?? 'Could not view as that account');
    }
    if (!localStorage.getItem(IMPERSONATOR)) {
      const cur = localStorage.getItem(STORE);
      if (cur) localStorage.setItem(IMPERSONATOR, cur);
    }
    save(await res.json());
  }

  /** The stashed admin's user, if currently viewing as a dummy — else null. */
  function impersonator() {
    try { return JSON.parse(localStorage.getItem(IMPERSONATOR))?.user ?? null; } catch { return null; }
  }

  /** Restore the stashed admin session. No network call. */
  function stopImpersonating() {
    const stash = localStorage.getItem(IMPERSONATOR);
    if (stash) { localStorage.setItem(STORE, stash); localStorage.removeItem(IMPERSONATOR); }
  }

  async function logout() {
    localStorage.removeItem(IMPERSONATOR);
    const s = load();
    if (s?.refreshToken) {
      // Best-effort: the local session goes regardless.
      try {
        await fetch(`${apiBase}/api/auth/logout`, {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ refreshToken: s.refreshToken }),
        });
      } catch { /* offline is fine */ }
    }
    clear();
  }

  /**
   * Completes the redirect back from Discord, if this load is one.
   * Returns true when a login just happened.
   */
  async function handleRedirect() {
    const url = new URL(location.href);
    const code = url.searchParams.get('code');
    const state = url.searchParams.get('state');
    const error = url.searchParams.get('error');

    if (error) {
      cleanUrl();
      throw new Error(`Discord refused the login: ${error}`);
    }
    if (!code) return false;

    const expected = sessionStorage.getItem(STATE);
    const verifier = sessionStorage.getItem(VERIFIER);
    sessionStorage.removeItem(STATE);
    sessionStorage.removeItem(VERIFIER);
    cleanUrl();

    // Without this check a third party could hand you a code of their choosing
    // and have you log in as them.
    if (!expected || state !== expected) throw new Error('Login state mismatch — try again');
    if (!verifier) throw new Error('Login verifier missing — try again');

    const res = await fetch(`${apiBase}/api/auth/discord`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        code,
        codeVerifier: verifier,
        redirectUri: redirectUri(),
        deviceLabel: 'Web',
      }),
    });
    if (!res.ok) throw new Error('Login failed — the server rejected the code');

    save(await res.json());
    return true;
  }

  /** Strip ?code/?state so a refresh doesn't retry a spent code. */
  function cleanUrl() {
    history.replaceState({}, '', location.pathname);
  }

  // ── session ──────────────────────────────────────────────────────────

  const session = () => load();
  const user = () => load()?.user ?? null;
  const isLoggedIn = () => !!load()?.accessToken;
  const accessToken = () => load()?.accessToken ?? null;

  async function refresh() {
    const s = load();
    if (!s?.refreshToken) return false;

    let res;
    try {
      res = await fetch(`${apiBase}/api/auth/refresh`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken: s.refreshToken }),
      });
    } catch {
      // Network error / server unreachable — transient. Keep the session so a
      // later call can retry; the local dev API restarts constantly (watchdog),
      // and clearing here logged the user in and out every time it blipped.
      return classifyRefresh({ networkError: true }) === 'save';
    }

    // classifyRefresh keeps the login-logout-flicker rule in one testable place:
    // only a definitive 401/403 clears; a 5xx (server restarting) is transient.
    const decision = classifyRefresh({ status: res.status, ok: res.ok });
    if (decision === 'clear') { clear(); return false; }
    if (decision !== 'save') return false;

    save(await res.json());
    return true;
  }

  /**
   * fetch with the bearer token attached. On a 401 it refreshes once and
   * retries — access tokens are deliberately short-lived, so this happens
   * during normal use, not just at the edges.
   */
  async function authFetch(path, options = {}, retry = true) {
    const token = accessToken();
    const headers = { ...(options.headers ?? {}) };
    if (token) headers.Authorization = `Bearer ${token}`;

    const res = await fetch(`${apiBase}${path}`, { ...options, headers });

    if (res.status === 401 && retry && (await refresh())) {
      return authFetch(path, options, false);
    }
    return res;
  }

  /**
   * Synchronously drop the stored session — no network call, unlike logout().
   * Used by the hard-reload reset, which has to finish before the browser
   * navigates away.
   */
  function forget() {
    clear();
    localStorage.removeItem(IMPERSONATOR);
    sessionStorage.removeItem(VERIFIER);
    sessionStorage.removeItem(STATE);
  }

  return { login, devAccounts, devSignInAs, logout, forget, handleRedirect, session, user, isLoggedIn, accessToken, refresh, authFetch, loadDummies, impersonate, impersonator, stopImpersonating };
})();
