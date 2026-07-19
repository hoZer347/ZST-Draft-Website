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

  const load = () => { try { return JSON.parse(localStorage.getItem(STORE)) ?? null; } catch { return null; } };
  const save = (v) => localStorage.setItem(STORE, JSON.stringify(v));
  const clear = () => localStorage.removeItem(STORE);

  // ── PKCE ─────────────────────────────────────────────────────────────

  const b64url = (bytes) =>
    btoa(String.fromCharCode(...new Uint8Array(bytes)))
      .replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

  const randomString = () => b64url(crypto.getRandomValues(new Uint8Array(32)));

  async function challengeFor(verifier) {
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
    return b64url(digest);
  }

  /** Must exactly match a URI registered in the Discord portal AND in the
   *  server's Discord:RedirectUris. Discord compares it byte-for-byte. */
  const redirectUri = () => `${location.origin}/`;

  // ── login / logout ───────────────────────────────────────────────────

  async function login() {
    const cfg = await fetch(`${apiBase}/api/auth/config`).then((r) => r.json());
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

  // ── debug slots (dev only) ───────────────────────────────────────────
  // Four fixed dummy players either client can claim instead of a Discord
  // login. The server tracks claims and shares them across clients, so the web
  // app and the phone see the same slots as taken. All of this 404s against a
  // deployed server — the endpoints only exist in Development.

  const CLIENT = 'draft.debug.client';

  /** A stable id for this browser, so "claimed by web-abc123" means something. */
  function clientId() {
    let id = localStorage.getItem(CLIENT);
    if (!id) { id = 'web-' + randomString().slice(0, 6); localStorage.setItem(CLIENT, id); }
    return id;
  }

  async function devSlots() {
    const res = await fetch(`${apiBase}/dev/slots`);
    if (!res.ok) throw new Error('Debug slots need the API running in Development');
    return res.json();
  }

  /**
   * Claim a slot and sign in as it. Stores a session shape-identical to a real
   * Discord login, plus the slot index so logout can release it. Returns the
   * server's body (notably previousHolder — who, if anyone, held it before).
   */
  async function claimSlot(index) {
    const res = await fetch(`${apiBase}/dev/slots/${index}/claim`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ client: clientId() }),
    });
    if (!res.ok) throw new Error('Could not claim that slot — is the API in Development?');

    const body = await res.json();
    save({
      accessToken: body.accessToken,
      refreshToken: body.refreshToken,
      expiresAt: body.accessExpiresAt,
      user: body.user,
      slotIndex: index,
    });
    return body;
  }

  /**
   * Sign in as the reserved admin — no Discord, no slot, and not listed as a
   * player. Stores a session shaped like a real login (no slotIndex, so logout
   * just revokes the token). Dev-only: the endpoint 404s on a deployed server.
   */
  async function adminLogin() {
    const res = await fetch(`${apiBase}/dev/admin`, { method: 'POST' });
    if (!res.ok) throw new Error('Admin sign-in needs the API running in Development');
    save(await res.json());
  }

  async function logout() {
    const s = load();
    // Free the debug slot first so the other clients see it open again.
    if (s?.slotIndex != null) {
      try { await fetch(`${apiBase}/dev/slots/${s.slotIndex}/release`, { method: 'POST' }); } catch { /* stale claim is harmless */ }
    }
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

    const res = await fetch(`${apiBase}/api/auth/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: s.refreshToken }),
    });
    if (!res.ok) { clear(); return false; }

    // The refresh response has no slotIndex; carry the debug slot across so
    // logout can still release it after a token rotation.
    const fresh = await res.json();
    if (s.slotIndex != null) fresh.slotIndex = s.slotIndex;
    save(fresh);
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
    sessionStorage.removeItem(VERIFIER);
    sessionStorage.removeItem(STATE);
  }

  return { login, devSlots, claimSlot, adminLogin, clientId, logout, forget, handleRedirect, session, user, isLoggedIn, accessToken, refresh, authFetch };
})();
