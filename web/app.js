/* App shell. Handles the Discord session and shows the signed-in view.
   Everything past sign-in is a blank slate — build the app on top of this. */

const $ = (id) => document.getElementById(id);
const el = {
  signin: $('signin'), signinError: $('signin-error'), app: $('app'),
  account: $('account'), avatar: $('avatar'), username: $('username'),
  tabs: $('tabs'), ready: $('ready'), playerList: $('player-list'),
  // draft
  onClock: $('on-clock'), onClockBox: $('on-clock-box'), pickNo: $('pick-no'), timer: $('timer'),
  play: $('play'), rollback: $('rollback'), abort: $('abort'), simSeason: $('sim-season'), banner: $('state-banner'),
  turn: $('turn'), turnLabel: $('turn-label'), tiers: $('tiers'), options: $('options'),
  skip: $('skip'), draftError: $('draft-error'), picks: $('picks'),
  // pre-start settings (admin)
  draftSettings: $('draft-settings'), setWeeks: $('set-weeks'), setTimeout: $('set-timeout'),
  // team page
  teamPage: $('team-page'), teamTitle: $('team-title'), teamBody: $('team-body'), teamClose: $('team-close'),
  teamIcon: $('team-icon'), teamShowdown: $('team-showdown'), teamEdit: $('team-edit'),
  teamEditForm: $('team-edit-form'), tpName: $('tp-name'), tpIcon: $('tp-icon'), tpShowdown: $('tp-showdown'),
  tpCancel: $('tp-cancel'), tpMsg: $('tp-msg'),
  // tier list
  tlSearch: $('tl-search'), tlTiers: $('tl-tiers'), tlType1: $('tl-type1'), tlType2: $('tl-type2'), tlRoles: $('tl-roles'),
  tlAvailable: $('tl-available'), tlClear: $('tl-clear'), tlCount: $('tl-count'), tlBody: $('tl-body'),
  // schedule
  schedScroll: $('sched-scroll'),
};

// ── session ────────────────────────────────────────────────────────────

function signedOut() {
  el.signin.hidden = false;
  el.app.hidden = true;
  el.tabs.hidden = true;
  el.ready.hidden = true;
  el.account.hidden = true;
  // Drop the identity itself, not just the container. Leaving it would show
  // the previous user's portrait for a frame on the next sign-in.
  el.avatar.hidden = true;
  el.avatar.removeAttribute('src');
  el.username.textContent = '';
  closeTeam();
  teardownDraft();
  statsData = null; // drop cached stats so a re-login reloads them
}

async function signedIn() {
  const user = Auth.user();
  el.signin.hidden = true;
  el.app.hidden = false;
  el.tabs.hidden = false;
  el.account.hidden = false;
  el.username.textContent = user.username;
  showView('draft'); // always land on the draft, not wherever we last were
  // Not every Discord account has an avatar, and this element survives a
  // sign-out, so the empty case has to clear it rather than leave the last one.
  if (user.avatarUrl) { el.avatar.src = user.avatarUrl; el.avatar.hidden = false; }
  else { el.avatar.hidden = true; el.avatar.removeAttribute('src'); }

  loadPlayers();
  await initDraft();
}

// ── players ──────────────────────────────────────────────────────────────
// The league roster down the left: real Discord users merged with the fixed
// dummy coaches. Each row's right-hand tag shows the coach's ready status.

async function loadPlayers() {
  const list = el.playerList;
  if (!list) return;
  list.innerHTML = '<li class="muted">Loading…</li>';

  let players;
  try {
    const res = await Auth.authFetch('/api/players');
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    players = await res.json();
  } catch (e) {
    list.innerHTML = '';
    const li = document.createElement('li');
    li.className = 'muted';
    li.textContent = `Couldn't load players — ${e.message}`;
    list.appendChild(li);
    return;
  }

  if (!players.length) {
    list.innerHTML = '<li class="muted">No players yet.</li>';
    return;
  }

  // Removing a player deletes an account, so the control is admin-only and the
  // server enforces the same — this just hides a button that would 403.
  const me = Auth.user();
  const canRemove = !!me?.isAdmin;

  list.replaceChildren(...players.map((p) => {
    const li = document.createElement('li');
    li.className = 'player';
    // Clicking a player opens their team page over the draft.
    li.tabIndex = 0;
    li.onclick = () => openTeam(p.discordId, p.username);
    li.onkeydown = (e) => { if (e.key === 'Enter') openTeam(p.discordId, p.username); };

    const avatar = document.createElement('img');
    avatar.className = 'player-avatar';
    avatar.alt = '';
    if (p.avatarUrl) avatar.src = p.avatarUrl;
    else avatar.classList.add('player-avatar--blank');

    const name = document.createElement('span');
    name.className = 'player-name';
    // textContent, not innerHTML — usernames come from Discord and must not
    // be interpreted as markup.
    name.textContent = p.username;

    // The right-hand tag reports ready status (it replaced the old
    // dummy/discord source badge). Readied-up coaches show a green "ready" from
    // the latest draft state; the span is always present so it holds the grid
    // column and keeps the remove button aligned to the far right.
    const tag = document.createElement('span');
    tag.className = 'player-ready';
    if (draft?.ready?.includes(p.discordId)) tag.textContent = 'ready';

    li.append(avatar, name, tag);

    // Never yourself — the server rejects self-removal and it would strand
    // your session.
    if (canRemove && p.discordId !== me.discordId) {
      const rm = document.createElement('button');
      rm.className = 'player-remove';
      rm.textContent = '×';
      rm.title = `Remove ${p.username}`;
      rm.setAttribute('aria-label', `Remove ${p.username}`);
      // Don't let the remove click bubble up and open the team page.
      rm.onclick = (e) => { e.stopPropagation(); removePlayer(p); };
      li.append(rm);
    }
    return li;
  }));
}

async function removePlayer(p) {
  if (!confirm(`Remove ${p.username}? This deletes their account and ends every session.`)) return;
  try {
    const res = await Auth.authFetch(`/api/players/${encodeURIComponent(p.discordId)}`, { method: 'DELETE' });
    if (!res.ok) {
      const msg = res.status === 403 ? 'Only admins can remove players' : `Failed (${res.status})`;
      throw new Error(msg);
    }
    await loadPlayers();
  } catch (e) {
    alert(`Couldn't remove ${p.username} — ${e.message}`);
  }
}

// ── team page ──────────────────────────────────────────────────────────
// Clicking a name in the roster opens a full-cover page of that player's
// drafted team, each mon with its types, abilities and base stats.

const STAT_ROWS = [['HP', 'hp'], ['Atk', 'atk'], ['Def', 'def'], ['SpA', 'spAtk'], ['SpD', 'spDef'], ['Spe', 'speed']];

let teamPageId = null; // discordId of the team currently open

async function openTeam(discordId, username) {
  teamPageId = discordId;
  el.teamTitle.textContent = username;
  el.teamEditForm.hidden = true;
  el.teamBody.replaceChildren(Object.assign(document.createElement('p'), { className: 'muted', textContent: 'Loading…' }));
  el.teamPage.hidden = false;
  try {
    const res = await Auth.authFetch(`/api/players/${encodeURIComponent(discordId)}/team`);
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    renderTeam(await res.json());
  } catch (e) {
    el.teamBody.replaceChildren(Object.assign(document.createElement('p'), { className: 'muted', textContent: `Couldn't load team — ${e.message}` }));
  }
}

function closeTeam() {
  if (!el.teamPage) return;
  el.teamPage.hidden = true;
  el.teamEditForm.hidden = true;
  el.teamBody.replaceChildren();
  teamPageId = null;
}

// pickMvp lives in pool-logic.js (loaded before this file) so it can be
// unit-tested headlessly; it's a global here.

function renderTeam(data) {
  // Discord name, with the custom team name beside it if set.
  el.teamTitle.textContent = data.teamName ? `${data.username} — ${data.teamName}` : data.username;

  // Square team icon.
  if (data.teamIcon) { el.teamIcon.src = data.teamIcon; el.teamIcon.hidden = false; }
  else { el.teamIcon.hidden = true; el.teamIcon.removeAttribute('src'); }

  // Showdown handle.
  if (data.showdownName) { el.teamShowdown.textContent = `Showdown: ${data.showdownName}`; el.teamShowdown.hidden = false; }
  else { el.teamShowdown.hidden = true; }

  // Only the owner can customise their own team.
  const mine = data.discordId === Auth.user()?.discordId;
  el.teamEdit.hidden = !mine;
  if (mine) {
    el.tpName.value = data.teamName ?? '';
    el.tpIcon.value = data.teamIcon ?? '';
    el.tpShowdown.value = data.showdownName ?? '';
  }

  if (!data.mons.length) {
    el.teamBody.replaceChildren(Object.assign(document.createElement('p'), {
      className: 'muted', textContent: 'No mons drafted yet.',
    }));
    return;
  }
  // Presence needs the team's total battle turns as its denominator.
  for (const m of data.mons) if (m.stats) m.stats.teamTurns = data.teamTurns ?? 0;

  // Highlight the team MVP from the battle stats (if any games are logged).
  const mvp = pickMvp(data.mons);
  for (const m of data.mons) m.__mvp = m === mvp;

  // Two colour-grouped columns: left runs S, A, A; right runs B, B, B; the four
  // C's split two per column so the blue rows line up at the bottom.
  const by = { S: [], A: [], B: [], C: [] };
  for (const m of data.mons) (by[m.tier] ??= []).push(m);
  const cs = by.C ?? [];
  const half = Math.ceil(cs.length / 2);
  const left = [...(by.S ?? []), ...(by.A ?? []), ...cs.slice(0, half)];
  const right = [...(by.B ?? []), ...cs.slice(half)];

  const col = (mons) => {
    const c = document.createElement('div');
    c.className = 'tm-col';
    c.append(...mons.map(monCard));
    return c;
  };
  el.teamBody.replaceChildren(col(left), col(right));
}

function monCard(m) {
  const card = document.createElement('div');
  card.className = `tm-card tm--${m.tier}` + (m.__mvp ? ' mvp' : '');

  const top = document.createElement('div');
  top.className = 'tm-top';

  const img = document.createElement('img');
  img.className = 'tm-sprite';
  img.alt = '';
  img.loading = 'lazy';
  applySprite(img, m);

  const id = document.createElement('div');
  id.className = 'tm-id';

  const name = document.createElement('div');
  name.className = 'tm-name';
  name.textContent = m.name;
  const badge = document.createElement('span');
  badge.className = `tier-badge tier-badge--${m.tier}`;
  badge.textContent = m.tier;
  name.append(' ', badge);
  // Team MVP badge (highest KO differential, per pickMvp).
  if (m.__mvp) {
    const mvp = document.createElement('span');
    mvp.className = 'mvp-badge';
    mvp.textContent = '★ MVP';
    mvp.title = `Team MVP — ${m.stats.k}/${m.stats.d} KO/faint (${m.stats.k - m.stats.d >= 0 ? '+' : ''}${m.stats.k - m.stats.d})`;
    name.append(' ', mvp);
  }
  // C-tier mons were drafted with a Tera type — show it on the team page.
  if (m.teraType) name.append(' ', teraTag(m.teraType));

  const types = document.createElement('div');
  types.className = 'tm-types';
  for (const t of [m.type1, m.type2]) {
    if (!t) continue;
    const chip = document.createElement('span');
    chip.className = `type-chip type--${t.toLowerCase()}`;
    chip.textContent = t;
    types.append(chip);
  }

  const abils = document.createElement('div');
  abils.className = 'tm-abilities';
  abils.textContent = [m.ability1, m.ability2, m.hiddenAbility].filter(Boolean).join(' · ');

  id.append(name, types, abils);

  const bst = document.createElement('div');
  bst.className = 'tm-bst';
  const bstNum = document.createElement('span');
  bstNum.className = 'tm-bst-num';
  bstNum.textContent = m.bst;
  const bstLabel = document.createElement('span');
  bstLabel.className = 'tm-bst-label';
  bstLabel.textContent = 'BST';
  bst.append(bstNum, bstLabel);

  top.append(img, id, bst);

  const stats = document.createElement('div');
  stats.className = 'tm-stats';
  for (const [label, key] of STAT_ROWS) {
    const row = document.createElement('div');
    row.className = 'tm-stat';
    const l = document.createElement('span');
    l.className = 'tm-stat-label';
    l.textContent = label;
    const v = document.createElement('span');
    v.className = 'tm-stat-val';
    v.textContent = m[key];
    const bar = document.createElement('span');
    bar.className = 'tm-bar';
    const fill = document.createElement('span');
    fill.className = `tm-bar-fill ${statClass(m[key])}`;
    // 200 is a generous cap for a base stat; anything higher just pins full.
    fill.style.width = `${Math.min(100, Math.round((m[key] / 200) * 100))}%`;
    bar.append(fill);
    row.append(l, v, bar);
    stats.append(row);
  }

  const main = document.createElement('div');
  main.className = 'tm-main';
  main.append(top, stats);

  card.append(main);
  // Battle stats scraped from the season's replays, down the right side.
  if (m.stats && m.stats.gp > 0) card.append(battlePanel(m.stats));
  return card;
}

// The right-hand battle-stats panel on a team-page mon card. Four rows:
// W/L (+ win rate), KOs/Faints/(+/-), damage ratio, and presence.
function battlePanel(s) {
  const b = document.createElement('div');
  b.className = 'tm-battle';

  const head = document.createElement('div');
  head.className = 'tm-battle-head';
  head.textContent = `${s.gp} ${s.gp === 1 ? 'game' : 'games'}`;
  b.append(head);

  const row = (label, ...parts) => {
    const r = document.createElement('div');
    r.className = 'tm-brow';
    const l = document.createElement('span');
    l.className = 'tm-brow-l';
    l.textContent = label;
    r.append(l);
    for (const p of parts) {
      const v = document.createElement('span');
      v.className = 'tm-brow-v';
      if (p && typeof p === 'object') { v.textContent = p.text; if (p.cls) v.classList.add(p.cls); }
      else v.textContent = p;
      r.append(v);
    }
    return r;
  };
  const sign = (n) => (n >= 0 ? `+${n}` : `${n}`);
  const wr = s.w + s.l > 0 ? Math.round((100 * s.w) / (s.w + s.l)) : 0;
  const diff = s.k - s.d;
  const ratio = s.taken > 0 ? (s.dealt / s.taken).toFixed(2) : (s.dealt > 0 ? '∞' : '—');
  const presence = s.teamTurns > 0 ? `${Math.round((100 * s.activeTurns) / s.teamTurns)}%` : '—';

  b.append(
    row('W / L', `${s.w} – ${s.l}`, { text: `${wr}%`, cls: 'tm-brow-sub' }),
    row('KO / Faint', `${s.k} / ${s.d}`, { text: sign(diff), cls: diff >= 0 ? 'good' : 'bad' }),
    row('Dmg ratio', { text: ratio, cls: s.dealt >= s.taken ? 'good' : 'bad' }),
    row('Presence', presence),
  );
  return b;
}

// Green ≥100, red ≤75, amber in between.
const statClass = (v) => (v >= 100 ? 'stat--hi' : v <= 75 ? 'stat--lo' : 'stat--mid');

// ── draft ──────────────────────────────────────────────────────────────
// Snake draft. Each turn the coach on the clock opens a tier, is offered a
// random sample of that tier's remaining pool (S 3 / A 4 / B 5 / C 7), and
// picks one. The server is authoritative for all of it — the UI only enables
// controls it expects to succeed and re-reads state after every change.

const TIERS = ['S', 'A', 'B', 'C']; // index === the tier ordinal the API wants

let draftId = null;
let leagueId = null;   // the draft's league — the schedule tab hangs off this
let draft = null;      // last state from the server
let myTeamId = null;   // which team (if any) this user coaches in this draft
let deadline = null;   // wall-clock ms when the current pick expires
let pollTimer = null;
let conn = null;

const teamName = (id) => draft?.teams.find((t) => t.Id === id || t.id === id)?.name ?? `Team ${id}`;

// Prefer the sheet's Showdown slug (megas/regional forms share a dex, so a bare
// dex lookup shows the wrong sprite); fall back to a dex-based sprite.
const spriteUrl = (mon) =>
  !mon.sprite
    ? `https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/${mon.dexNumber}.png`
    // A full URL in the sprite field is used as-is — lets a specific mon override
    // the default Showdown gen-5 sprite (e.g. forms with no good gen-5 art).
    : /^https?:\/\//.test(mon.sprite)
      ? mon.sprite
      : `https://play.pokemonshowdown.com/sprites/gen5/${mon.sprite}.png`;

// Serebii's Legends Z-A artwork URL for a mega form, keyed by national dex +
// mega suffix (-mx / -my / -mz / -m). This is the source of the actual mega
// art for the Champions/Z-A megas that Showdown has no gen-5 sprite for — so a
// missing mega shows its real mega form, never the base forme.
function serebiiMega(mon) {
  // Tolerate both row shapes: the pool/pick rows use name/dexNumber, the stats
  // rows use pokemon/dex.
  const name = mon.name || mon.pokemon || '';
  const dex = mon.dexNumber ?? mon.dex;
  const slug = mon.sprite || '';
  const isMega = name.startsWith('M-') || /-mega[xyz]?$/.test(slug);
  if (!isMega || !dex) return null;
  const suffix = /-X$/.test(name) || /-megax$/.test(slug) ? '-mx'
    : /-Y$/.test(name) || /-megay$/.test(slug) ? '-my'
    : /-Z$/.test(name) || /-megaz$/.test(slug) ? '-mz'
    : '-m';
  return `https://www.serebii.net/legendsz-a/pokemon/${String(dex).padStart(3, '0')}${suffix}.png`;
}

// Set an <img>'s sprite with graceful fallback. Old megas have a Showdown gen-5
// pixel sprite (kept, so they match the rest of the pool). The newer Z-A megas
// (M-Barbaracle, M-Raichu-X, M-Tatsugiri…) don't — so on a load error we fall
// back to Serebii's real mega artwork rather than the base forme, then to a
// PokéAPI sprite by dex number, then give up.
function applySprite(img, mon) {
  const chain = [];
  const serebii = serebiiMega(mon);
  if (serebii) chain.push(serebii);
  const dex = mon.dexNumber ?? mon.dex;
  if (dex) chain.push(`https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/${dex}.png`);
  let i = 0;
  img.onerror = () => { if (i < chain.length) img.src = chain[i++]; else img.onerror = null; };
  img.src = spriteUrl(mon);
}

function monImg(mon) {
  const img = document.createElement('img');
  img.className = 'mon-img';
  img.alt = '';
  img.loading = 'lazy';
  applySprite(img, mon);
  return img;
}

// A small type-coloured pill for a C-tier pick's Tera type, e.g. "Tera Grass".
// Only C-tier options/picks carry a teraType; everything else passes null here.
function teraTag(teraType) {
  const s = document.createElement('span');
  s.className = `type-chip tera type--${teraType.toLowerCase()}`;
  s.textContent = `Tera ${teraType}`;
  return s;
}

const showDraftError = (msg) => { el.draftError.textContent = msg || ''; el.draftError.hidden = !msg; };

async function initDraft() {
  teardownDraft();
  try {
    const res = await Auth.authFetch('/api/drafts');
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    const drafts = await res.json();
    if (!drafts.length) { showDraftError('No draft configured.'); return; }

    // One draft in the mock. Teams are built at Start, so a viewer needn't own
    // a team to open it — the server tells us our team id once it exists.
    draftId = drafts[0].id;
    leagueId = drafts[0].leagueId ?? null;

    await refreshDraft();
    // Now that draft state (incl. the ready set) is loaded, re-render the roster
    // so ready markers show on first paint.
    loadPlayers();
    await connect();
  } catch (e) {
    showDraftError(`Can't load the draft — ${e.message}`);
  }
}

function teardownDraft() {
  if (conn) { try { conn.stop(); } catch { /* already down */ } conn = null; }
  clearInterval(pollTimer);
  pollTimer = null;
  draftId = null;
  leagueId = null;
  draft = null;
  myTeamId = null;
  deadline = null;
  scheduleData = null;
  renderedPicks = 0; // force a clean rebuild of the feed for the next draft
}

// Coalesce concurrent refreshes: making a pick awaits its own refresh, and the
// SignalR 'pickMade' broadcast fires one too — without this they'd both GET and
// both re-render for the same change.
let refreshInFlight = null;
function refreshDraft() {
  if (draftId == null) return Promise.resolve();
  if (refreshInFlight) return refreshInFlight;
  refreshInFlight = (async () => {
    try {
      const res = await Auth.authFetch(`/api/drafts/${draftId}`);
      if (res.status === 401) return signedOut();
      if (!res.ok) { showDraftError(`Can't reach the draft (${res.status})`); return; }
      render(await res.json());
      showDraftError('');
    } finally {
      refreshInFlight = null;
    }
  })();
  return refreshInFlight;
}

function render(s) {
  draft = s;
  // Which team is mine is decided by the server (teams are created at Start),
  // never config or the cached session.
  myTeamId = s.myTeamId ?? null;

  const mine = myTeamId != null && s.onClockTeamId === myTeamId;

  el.onClock.textContent = s.onClockTeamId == null ? '—' : teamName(s.onClockTeamId);
  el.onClock.classList.toggle('mine', mine);
  // Once the draft is done there's no one on the clock — hide that box for
  // non-admins (admins keep it for oversight).
  el.onClockBox.hidden = s.state === 'Complete' && !s.isAdmin;
  el.pickNo.textContent = s.state === 'Complete' ? 'done' : `${s.pickNumber} / ${s.totalPicks}`;

  // Pre-start settings (admin only). Populate from state but never clobber an
  // input the admin is actively editing.
  const showSettings = s.state === 'NotStarted' && s.isAdmin;
  el.draftSettings.hidden = !showSettings;
  if (showSettings) {
    if (document.activeElement !== el.setWeeks) el.setWeeks.value = s.weeks;
    if (document.activeElement !== el.setTimeout) el.setTimeout.value = s.pickTimerSeconds / 3600;
  }

  // Ready-up toggle (header): only a coach, only before the draft starts.
  if (s.canReady) {
    el.ready.hidden = false;
    el.ready.textContent = s.myReady ? 'Leave draft' : '✓ Ready up';
    el.ready.classList.toggle('ready-in', !!s.myReady);
  } else {
    el.ready.hidden = true;
  }

  deadline = s.secondsRemaining == null ? null : Date.now() + s.secondsRemaining * 1000;
  renderTimer();

  // Banner for any non-running state.
  if (s.state !== 'Running') {
    el.banner.hidden = false;
    el.banner.textContent = s.state === 'Complete'
      ? 'Draft complete — every pick is in.'
      : s.state === 'NotStarted' ? 'Waiting for an admin to start the draft.'
      : `Draft is ${s.state.toLowerCase()}.`;
  } else {
    el.banner.hidden = true;
  }

  // Play is admin-only. Undo is for the admin or whoever made the last pick.
  // isAdmin comes from the draft state (live DB), not the cached login token —
  // a demoted coach must not keep seeing admin controls.
  const isAdmin = !!s.isAdmin;
  const lastPick = s.picks[s.picks.length - 1];
  const canUndo = s.picks.length > 0 && s.state !== 'NotStarted'
    && (isAdmin || (myTeamId != null && lastPick.teamId === myTeamId));
  el.play.hidden = !(isAdmin && (s.state === 'NotStarted' || s.state === 'Paused'));
  el.rollback.hidden = !canUndo;
  // Abort is admin-only and only meaningful once the draft has begun.
  el.abort.hidden = !(isAdmin && s.state !== 'NotStarted');
  // Dev-only season simulator: same context as Start (admin, pre-start), and
  // only on localhost since the endpoint is Development-only.
  el.simSeason.hidden = !(isLocal && isAdmin && s.state === 'NotStarted');

  renderTurn(mine, isAdmin, s);
  renderPicks(s);
}

function renderTimer() {
  if (deadline == null) { el.timer.textContent = '—'; el.timer.classList.remove('low'); return; }
  const left = Math.max(0, Math.round((deadline - Date.now()) / 1000));
  const pad = (n) => String(n).padStart(2, '0');
  const h = Math.floor(left / 3600);
  const m = Math.floor((left % 3600) / 60);
  const sec = left % 60;
  // Show hours only when the timeout is long (a 24h default reads as "23:59:12").
  el.timer.textContent = h > 0 ? `${h}:${pad(m)}:${pad(sec)}` : `${m}:${pad(sec)}`;
  el.timer.classList.toggle('low', left <= 30);
}

// The tier buttons + offered options. Shown to the coach on the clock (who can
// act) and to the admin (read-only) — nobody else sees the live pick controls.
function renderTurn(mine, isAdmin, s) {
  const canAct = mine;
  if (!(mine || isAdmin) || s.state !== 'Running') { el.turn.hidden = true; el.skip.hidden = true; return; }
  el.turn.hidden = false;
  el.turn.classList.toggle('readonly', !canAct);

  const openTier = s.offered.length ? s.offered[0].tier : null;
  const onClockName = teamName(s.onClockTeamId);

  // Skip is the on-clock coach's own choice, and only once they've opened a tier
  // and are looking at options — not during tier selection. The pick still comes
  // back to them in a later cycle; they don't lose the slot.
  const mySkips = s.teams.find((t) => t.id === myTeamId)?.skipsRemaining ?? 0;
  el.skip.hidden = !(canAct && mySkips > 0 && openTier !== null);
  el.skip.textContent = `Skip pick (${mySkips} left)`;

  // Remaining slots per tier = allowed − already picked by the on-clock team.
  const rule = (t) => s.tierRules.find((r) => r.tier === t) ?? { slotsPerTeam: 0 };
  const used = (t) => s.picks.filter((p) => p.teamId === s.onClockTeamId && p.tier === t).length;

  el.turnLabel.textContent = canAct
    ? (openTier ? `Your pick — ${openTier} tier` : 'Your pick — choose a tier')
    : (openTier ? `${onClockName} is picking — ${openTier} tier` : `${onClockName} is on the clock`);

  el.tiers.replaceChildren(...TIERS.map((key, ordinal) => {
    const remaining = rule(key).slotsPerTeam - used(key);
    const b = document.createElement('button');
    b.className = `tier tier--${key}`;
    b.innerHTML = `<span class="tier-key">${key}</span><span class="tier-left">${remaining} left</span>`;
    // Only the coach on the clock can act; the admin sees the same panel but
    // read-only. The server enforces this too — disabling avoids a doomed click.
    b.disabled = !canAct || remaining <= 0 || (openTier !== null && openTier !== key);
    b.classList.toggle('open', openTier === key);
    if (canAct) b.onclick = () => offer(ordinal);
    return b;
  }));

  if (!s.offered.length) {
    el.options.replaceChildren(Object.assign(document.createElement('p'), {
      className: 'muted',
      textContent: canAct ? 'Pick a tier to see your options.' : 'Waiting for a tier to be opened.',
    }));
    return;
  }
  el.options.replaceChildren(...s.offered.map((o) => {
    const card = document.createElement('button');
    card.className = 'mon';
    card.disabled = !canAct;
    card.append(monImg(o));
    const name = document.createElement('span');
    name.className = 'mon-name';
    name.textContent = o.name;
    const t = document.createElement('span');
    t.className = 'mon-tier';
    t.textContent = o.tier;
    card.append(name, t);
    // C-tier options carry a rolled Tera type, shown underneath the option.
    if (o.teraType) card.append(teraTag(o.teraType));
    if (canAct) card.onclick = () => pick(o.pokemonEntryId);
    return card;
  }));
}

// The running feed, newest first.
// Build one row of the pick feed. Rows never change once made (a pick is
// immutable; a rollback removes the last one), so the feed can be rendered
// incrementally — see renderPicks.
function pickRow(p) {
  const li = document.createElement('li');
  li.className = `pick pick--${p.tier}`;

  // Column 1 — the draft pick itself: number, sprite, name (+ C-tier Tera). Left.
  const main = document.createElement('div');
  main.className = 'pick-main';
  const num = document.createElement('span');
  num.className = 'pick-num';
  num.textContent = `#${p.pickNumber}`;
  const name = document.createElement('span');
  name.className = 'pick-name';
  name.textContent = p.name;
  if (p.teraType) name.append(' ', teraTag(p.teraType));
  main.append(num, monImg(p), name);

  // Column 2 — the options offered but passed on this turn (dimmed sprites). Left.
  const passed = document.createElement('div');
  passed.className = 'pick-others';
  let others = [];
  try { others = p.otherOptions ? JSON.parse(p.otherOptions) : []; } catch { /* ignore bad json */ }
  if (others.length) {
    const lbl = document.createElement('span');
    lbl.className = 'pick-others-label';
    lbl.textContent = 'passed';
    passed.append(lbl);
    for (const o of others) {
      const img = document.createElement('img');
      img.className = 'pick-other-img';
      img.alt = o.name;
      img.title = o.name;
      img.loading = 'lazy';
      applySprite(img, o);
      passed.append(img);
    }
  }

  // Column 3 — who drafted it and the tier (+ auto badge). Right.
  const end = document.createElement('div');
  end.className = 'pick-end';
  const who = document.createElement('span');
  who.className = 'pick-team';
  who.textContent = teamName(p.teamId);
  const badge = document.createElement('span');
  badge.className = `tier-badge tier-badge--${p.tier}`;
  badge.textContent = p.tier;
  end.append(who, badge);
  if (p.wasAutoPick) {
    const auto = document.createElement('span');
    auto.className = 'auto';
    auto.textContent = 'auto';
    end.append(auto);
  }

  li.append(main, passed, end);
  return li;
}

// The running feed, newest first. Rendered incrementally: a new pick just
// prepends its row. The old code rebuilt the whole list every refresh, which
// re-created and re-fetched every sprite in it — so each pick got slower as the
// feed grew. A full rebuild only happens when the list shrinks (rollback/abort).
let renderedPicks = 0;
function renderPicks(s) {
  const picks = s.picks;
  if (!picks.length) {
    if (renderedPicks !== 0 || !el.picks.firstElementChild) {
      el.picks.innerHTML = '<li class="muted">No picks yet.</li>';
      renderedPicks = 0;
      sizePicks();
    }
    return;
  }
  if (picks.length < renderedPicks || renderedPicks === 0) {
    // Fresh feed, or the list shrank — rebuild from scratch.
    el.picks.replaceChildren(...[...picks].reverse().map(pickRow));
    renderedPicks = picks.length;
    sizePicks();
  } else if (picks.length > renderedPicks) {
    // Only the new picks need rows; prepend so the newest lands on top and the
    // existing rows (and their already-loaded sprites) are left untouched.
    for (let i = renderedPicks; i < picks.length; i++) el.picks.prepend(pickRow(picks[i]));
    renderedPicks = picks.length;
    sizePicks();
  }
}

// Grow the picks feed down to the bottom of the window, then let it scroll.
// Recomputed on every render, on tab switches, and on resize, because what sits
// above it — the turn panel especially — changes height.
function sizePicks() {
  const view = document.getElementById('view-draft');
  if (!el.picks || !view || view.hidden) return;
  const card = el.picks.closest('.card');
  if (!card) return;
  // Anchor the *card's* bottom to the viewport, not the list's top. The card wraps
  // the list in a label + padding + border, and main adds its own bottom padding;
  // sizing off the list's top alone ignores all of that and lets the list grow ~a
  // card-padding taller than the space, spilling past the window and adding a page
  // scrollbar. `chrome` is everything in the card that isn't the list, measured live
  // so it stays correct if the styling changes.
  const cardRect = card.getBoundingClientRect();
  const chrome = cardRect.height - el.picks.getBoundingClientRect().height;
  const pageGap = 20; // main's bottom padding / a little breathing room
  const avail = window.innerHeight - cardRect.top - chrome - pageGap;
  el.picks.style.maxHeight = `${Math.max(0, avail)}px`;
}

// ── draft actions ──────────────────────────────────────────────────────

async function postDraft(path, body) {
  const res = await Auth.authFetch(path, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: body ? JSON.stringify(body) : undefined,
  });
  if (res.ok) return null;
  if (res.status === 403) return "That isn't your team";
  try { return (await res.json()).error ?? `Failed (${res.status})`; }
  catch { return `Failed (${res.status})`; }
}

async function offer(tierOrdinal) {
  showDraftError(await postDraft(`/api/drafts/${draftId}/offer`, { teamId: myTeamId, tier: tierOrdinal }) ?? '');
  await refreshDraft();
}

async function pick(pokemonEntryId) {
  showDraftError(await postDraft(`/api/drafts/${draftId}/pick`, { teamId: myTeamId, pokemonEntryId }) ?? '');
  await refreshDraft();
}

async function skip() {
  showDraftError(await postDraft(`/api/drafts/${draftId}/skip`, { teamId: myTeamId }) ?? '');
  await refreshDraft();
}

// ── live connection ────────────────────────────────────────────────────

function setConn(up) {
  // Poll only while the socket is down, so a hub outage degrades to a slower
  // page instead of a dead one.
  clearInterval(pollTimer);
  pollTimer = up ? null : setInterval(refreshDraft, 5000);
}

async function connect() {
  if (typeof signalR === 'undefined') { setConn(false); return; }
  const { apiBase } = window.DRAFT_CONFIG;
  conn = new signalR.HubConnectionBuilder()
    // A browser can't set headers on the WebSocket handshake, so SignalR sends
    // the token as a query param. Re-read per attempt for post-refresh reconnects.
    .withUrl(`${apiBase}/hubs/draft`, { accessTokenFactory: () => Auth.accessToken() })
    .withAutomaticReconnect()
    .build();

  ['turnChanged', 'pickMade', 'optionsOffered', 'pickSkipped', 'pickRolledBack', 'draftStateChanged']
    .forEach((evt) => conn.on(evt, () => refreshDraft()));

  // Roster changes (someone signed in / was removed) fan out to all clients.
  conn.on('playersChanged', () => loadPlayers());

  // Someone readied up or left before the draft — update the button + markers.
  conn.on('readyChanged', async () => { await refreshDraft(); loadPlayers(); });

  // A match was scored or the schedule was regenerated — refresh the tab if it's
  // been opened (scheduleData is set on first view) so results land live.
  conn.on('scheduleChanged', () => { if (scheduleData) ensureSchedule(); });

  conn.onreconnected(async () => { setConn(true); await conn.invoke('JoinDraft', draftId); await loadPlayers(); await refreshDraft(); });
  conn.onclose(() => setConn(false));

  try {
    await conn.start();
    await conn.invoke('JoinDraft', draftId);
    setConn(true);
  } catch {
    setConn(false);
  }
}

// ── tabs ───────────────────────────────────────────────────────────────
// Draft is the default view; Teambuilder embeds the Showdown builder. The
// iframe is loaded on first open, not at sign-in, so the draft view stays snappy.

// ── tier list ────────────────────────────────────────────────────────────
// A browse view of the whole pool, grouped by tier, with type + role filters.
// Roles are derived from base stats (we have no move data), so they're rough
// signposts, not a full competitive classification.

// ALL_TYPES, ROLES and rolesOf live in pool-logic.js (loaded first) as globals,
// so the filter logic can be unit-tested and benchmarked headlessly.

let poolCache = null;
let tlBuilt = false;

async function ensureTierList() {
  if (!tlBuilt) buildTierFilters();
  if (poolCache == null) {
    el.tlBody.replaceChildren(Object.assign(document.createElement('p'), { className: 'muted', textContent: 'Loading…' }));
    try {
      const res = await Auth.authFetch('/api/pool');
      if (!res.ok) throw new Error(`Failed (${res.status})`);
      poolCache = await res.json();
    } catch (e) {
      el.tlBody.replaceChildren(Object.assign(document.createElement('p'), { className: 'muted', textContent: `Couldn't load the pool — ${e.message}` }));
      return;
    }
  }
  renderTierList();
}

function tlCheckbox(value, cls) {
  const label = document.createElement('label');
  label.className = 'tl-check';
  const cb = document.createElement('input');
  cb.type = 'checkbox';
  cb.value = value;
  if (cls) cb.className = cls;
  cb.onchange = renderTierList;
  label.append(cb, document.createTextNode(' ' + value));
  return label;
}

function buildTierFilters() {
  tlBuilt = true;
  el.tlTiers.replaceChildren(...TIERS.map((t) => tlCheckbox(t, `tl-tier-cb tl-tier-cb--${t}`)));
  for (const sel of [el.tlType1, el.tlType2]) sel.onchange = renderTierList; // options filled per render
  el.tlRoles.replaceChildren(...ROLES.map(([name]) => tlCheckbox(name)));
  el.tlSearch.oninput = renderTierList;
  el.tlAvailable.onchange = renderTierList;
  el.tlClear.onclick = () => {
    el.tlSearch.value = '';
    el.tlType1.value = '';
    el.tlType2.value = '';
    el.tlAvailable.checked = false;
    [...el.tlTiers.querySelectorAll('input'), ...el.tlRoles.querySelectorAll('input')]
      .forEach((c) => { c.checked = false; });
    renderTierList();
  };
}

const tlChecked = (container) => [...container.querySelectorAll('input:checked')].map((c) => c.value);

// Snapshot the current filter controls into a plain criteria object, so the
// actual filtering is done by the pure poolMatches (in pool-logic.js).
function tlCriteria() {
  return {
    search: el.tlSearch.value,
    availableOnly: el.tlAvailable.checked,
    tiers: tlChecked(el.tlTiers),
    type1: el.tlType1.value,
    type2: el.tlType2.value,
    roles: tlChecked(el.tlRoles),
  };
}

// Passes every active filter except the one named — the basis for facet counts.
const tlPasses = (m, skip) => poolMatches(m, tlCriteria(), skip);

function tlTypesPresent(skip) {
  const s = new Set();
  for (const m of poolCache) if (tlPasses(m, skip)) { if (m.type1) s.add(m.type1); if (m.type2) s.add(m.type2); }
  return s;
}

function tlFillSelect(sel, avail) {
  const cur = sel.value;
  sel.replaceChildren(new Option('Any type', ''), ...ALL_TYPES.filter((t) => avail.has(t)).map((t) => new Option(t, t)));
  sel.value = (cur === '' || avail.has(cur)) ? cur : '';
}

// Resize every facet's options to only what's still reachable given the others.
function tlRefreshFacets() {
  tlFillSelect(el.tlType1, tlTypesPresent('type1'));
  const t2 = tlTypesPresent('type2');
  if (el.tlType1.value) t2.delete(el.tlType1.value); // no picking the same type twice
  tlFillSelect(el.tlType2, t2);

  const tierBase = poolCache.filter((m) => tlPasses(m, 'tier'));
  for (const label of el.tlTiers.children) {
    const cb = label.querySelector('input');
    const ok = cb.checked || tierBase.some((m) => m.tier === cb.value);
    cb.disabled = !ok;
    label.classList.toggle('off', !ok);
  }
  const full = poolCache.filter((m) => tlPasses(m, null));
  for (const label of el.tlRoles.children) {
    const cb = label.querySelector('input');
    const ok = cb.checked || full.some((m) => rolesOf(m).includes(cb.value));
    cb.disabled = !ok;
    label.classList.toggle('off', !ok);
  }
}

function renderTierList() {
  if (poolCache == null) return;
  tlRefreshFacets(); // narrow the dropdowns/checkboxes before filtering
  const filtered = poolCache.filter((m) => tlPasses(m, null));
  el.tlCount.textContent = `${filtered.length} of ${poolCache.length} shown`;

  const byTier = { S: [], A: [], B: [], C: [] };
  for (const m of filtered) (byTier[m.tier] ??= []).push(m);

  const frag = document.createDocumentFragment();
  for (const tier of TIERS) {
    const mons = byTier[tier];
    if (!mons || !mons.length) continue;
    const sec = document.createElement('section');
    sec.className = 'tl-tier';
    const head = document.createElement('div');
    head.className = `tl-tier-head tl-tier--${tier}`;
    head.innerHTML = `<span class="tl-tier-key">${tier}</span><span class="tl-tier-count">${mons.length}</span>`;
    const grid = document.createElement('div');
    grid.className = 'tl-grid';
    grid.append(...mons.map(tlCard));
    sec.append(head, grid);
    frag.append(sec);
  }
  if (!frag.childNodes.length) {
    frag.append(Object.assign(document.createElement('p'), { className: 'muted', textContent: 'No mons match those filters.' }));
  }
  el.tlBody.replaceChildren(frag);
}

function tlCard(m) {
  const card = document.createElement('div');
  card.className = `tl-mon tl-mon--${m.tier}` + (m.drafted ? ' taken' : '');
  card.title = m.owner ? `${m.name} — drafted by ${m.owner}`
    : m.drafted ? `${m.name} — already drafted` : m.name;

  const img = document.createElement('img');
  img.className = 'tl-mon-img';
  img.alt = '';
  img.loading = 'lazy';
  applySprite(img, m);

  const name = document.createElement('div');
  name.className = 'tl-mon-name';
  name.textContent = m.name;

  const types = document.createElement('div');
  types.className = 'tl-mon-types';
  for (const t of [m.type1, m.type2]) {
    if (!t) continue;
    const chip = document.createElement('span');
    chip.className = `type-chip type--${t.toLowerCase()}`;
    chip.textContent = t;
    types.append(chip);
  }

  const bst = document.createElement('span');
  bst.className = 'tl-mon-bst';
  bst.textContent = `BST ${m.bst}`;

  card.append(img, name, types, bst);

  // If it's been drafted, show which coach holds it.
  if (m.owner) {
    const owner = document.createElement('div');
    owner.className = 'tl-mon-owner';
    owner.textContent = m.owner;
    owner.title = `Drafted by ${m.owner}`;
    card.append(owner);
  }
  return card;
}

// ── schedule ─────────────────────────────────────────────────────────────
// The season's matchups. The signed-in coach's next game sits big at the top of
// the column, this week's other games under it, then future weeks; already-played
// games stack *above* the fold so you scroll up into history. Results aren't
// entered by hand — a coach pastes their Showdown replay and the server reads the
// winner and score off the battle log (see ScheduleApi / ReplayScorer).

let scheduleData = null;

async function ensureSchedule() {
  if (leagueId == null) {
    el.schedScroll.replaceChildren(muted('No league yet — the schedule appears once a draft exists.'));
    return;
  }
  el.schedScroll.replaceChildren(muted('Loading…'));
  try {
    const res = await Auth.authFetch(`/api/leagues/${leagueId}/schedule`);
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    scheduleData = await res.json();
  } catch (e) {
    el.schedScroll.replaceChildren(muted(`Couldn't load the schedule — ${e.message}`));
    return;
  }
  renderSchedule();
}

const muted = (text) => Object.assign(document.createElement('p'), { className: 'muted', textContent: text });

function renderSchedule() {
  const data = scheduleData;
  if (!data) return;

  const matches = data.matches ?? [];
  if (!matches.length) {
    // The round-robin is laid down automatically when the draft starts, so an
    // empty schedule just means the draft hasn't begun yet.
    el.schedScroll.replaceChildren(muted('No schedule yet — it appears once the draft starts.'));
    return;
  }

  const cur = data.currentWeek; // earliest week with an unplayed game, or null
  const byWeek = (a, b) => a.week - b.week;

  const played = matches.filter((m) => m.played).sort(byWeek);      // history (oldest → newest)
  const pending = matches.filter((m) => !m.played).sort(byWeek);
  // Your next game leads, whichever week it falls in.
  const myNext = pending.find((m) => m.mine) ?? null;
  const rest = pending.filter((m) => m !== myNext);
  const thisWeek = rest.filter((m) => m.week === cur);
  const future = rest.filter((m) => m.week !== cur);

  const frag = document.createDocumentFragment();

  // Past — smaller cards, yours a touch bigger. Stacked above the present anchor.
  if (played.length) {
    frag.append(schedHeading('Previous results'));
    for (const m of played) frag.append(matchCard(m, m.mine ? 'small-mine' : 'small'));
  }

  // The present: this is where we scroll to, so history sits off the top.
  const present = document.createElement('div');
  present.className = 'sched-present';
  present.id = 'sched-present';

  if (myNext) {
    present.append(schedHeading('Your next matchup'));
    present.append(matchCard(myNext, 'hero'));
  }
  if (thisWeek.length) {
    present.append(schedHeading(myNext ? 'Also this week' : 'This week'));
    for (const m of thisWeek) present.append(matchCard(m, m.mine ? 'mine' : 'normal'));
  }
  if (future.length) {
    present.append(schedHeading('Upcoming'));
    for (const m of future) present.append(matchCard(m, m.mine ? 'mine' : 'normal'));
  }
  if (!present.childNodes.length) present.append(muted('The season is complete — every game has been played.'));
  frag.append(present);

  el.schedScroll.replaceChildren(frag);

  // Land on the present, so already-played weeks require a scroll up.
  const anchor = document.getElementById('sched-present');
  if (anchor) el.schedScroll.scrollTop = anchor.offsetTop;
}

function schedHeading(text) {
  const h = document.createElement('div');
  h.className = 'sched-heading label';
  h.textContent = text;
  return h;
}

const WEEKDAY = { weekday: 'short', month: 'short', day: 'numeric' };

function matchCard(m, size) {
  const card = document.createElement('div');
  card.className = `match match--${size}` + (m.mine ? ' match--is-mine' : '');

  const head = document.createElement('div');
  head.className = 'match-head';
  const wk = document.createElement('span');
  wk.className = 'match-week';
  wk.textContent = `Week ${m.week}`;
  head.append(wk);
  if (m.scheduledFor) {
    const when = document.createElement('span');
    when.className = 'match-when';
    when.textContent = new Date(m.scheduledFor).toLocaleDateString(undefined, WEEKDAY);
    head.append(when);
  }

  // home — score — away, with the winner emphasised once it's been played.
  const homeWon = m.result === 'HomeWin';
  const awayWon = m.result === 'AwayWin';
  const row = document.createElement('div');
  row.className = 'match-row';
  row.append(
    teamSide(m.homeName, m.homeTeamId === m.myTeamId, homeWon, 'home'),
    matchMiddle(m),
    teamSide(m.awayName, m.awayTeamId === m.myTeamId, awayWon, 'away'),
  );
  card.append(head, row);

  // Your own unplayed game: submit a replay to score it.
  if (m.mine && !m.played) card.append(replayForm(m));
  else if (m.replayUrl) {
    const link = document.createElement('a');
    link.className = 'match-replay-link';
    link.href = m.replayUrl;
    link.target = '_blank';
    link.rel = 'noopener';
    link.textContent = 'Watch replay';
    card.append(link);
  }
  return card;
}

function teamSide(name, isMe, won, which) {
  const side = document.createElement('div');
  side.className = `match-team match-team--${which}` + (won ? ' won' : '') + (isMe ? ' me' : '');
  const n = document.createElement('span');
  n.className = 'match-team-name';
  n.textContent = name;
  side.append(n);
  if (isMe) {
    const you = document.createElement('span');
    you.className = 'match-you';
    you.textContent = 'you';
    side.append(you);
  }
  return side;
}

function matchMiddle(m) {
  const mid = document.createElement('div');
  mid.className = 'match-mid';
  if (m.played && m.homeScore != null && m.awayScore != null) {
    mid.classList.add('match-score');
    mid.textContent = `${m.homeScore}–${m.awayScore}`;
  } else if (m.played) {
    mid.textContent = m.result === 'Draw' ? 'draw' : 'final';
  } else {
    mid.textContent = 'vs';
  }
  return mid;
}

function replayForm(m) {
  const form = document.createElement('form');
  form.className = 'replay-form';

  const input = document.createElement('input');
  input.type = 'url';
  input.className = 'replay-input';
  input.placeholder = 'Paste your Showdown replay link…';
  input.required = true;

  const btn = document.createElement('button');
  btn.type = 'submit';
  btn.className = 'replay-btn';
  btn.textContent = 'Submit result';

  const msg = document.createElement('span');
  msg.className = 'replay-msg';

  form.onsubmit = async (e) => {
    e.preventDefault();
    btn.disabled = true;
    msg.className = 'replay-msg';
    msg.textContent = 'Reading replay…';
    const err = await postDraft(`/api/matches/${m.id}/replay`, { replayUrl: input.value.trim() });
    if (err) {
      btn.disabled = false;
      msg.className = 'replay-msg error';
      msg.textContent = err;
      return;
    }
    // The scoreboard fans out over SignalR, but refresh now so the submitter
    // sees their result without waiting on the round trip.
    await ensureSchedule();
  };

  form.append(input, btn, msg);
  return form;
}

function showView(name) {
  closeTeam(); // a view switch drops the team overlay
  for (const v of document.querySelectorAll('.view')) v.hidden = v.id !== `view-${name}`;
  for (const t of document.querySelectorAll('.tab')) t.classList.toggle('active', t.dataset.view === name);
  if (name === 'teambuilder') openTeambuilder();
  if (name === 'tierlist') ensureTierList();
  if (name === 'schedule') ensureSchedule();
  if (name === 'stats') ensureStats();
  if (name === 'draft') sizePicks();
}

// ── stats page ───────────────────────────────────────────────────────────
// A sortable league-wide table of accrued battle stats (scraped from replays),
// one row per drafted mon, with a couple of derived columns.

let statsData = null;
let statsSort = { key: 'plusminus', dir: -1 };

const STAT_COLS = [
  { key: 'pokemon', label: 'Pokémon', tip: 'The drafted Pokémon and its tier (C-tier shows its Tera type).' },
  { key: 'trainer', label: 'Trainer', tip: 'The coach who drafted it.' },
  { key: 'gp', label: 'GP', num: true, tip: 'Games played — games this mon was brought to.' },
  { key: 'presence', label: 'Presence', num: true, fmt: (v) => `${Math.round(v * 100)}%`,
    tip: "Share of the team's total battle turns this mon was on the field. 5/10 turns and 1/2 turns are the same 50%." },
  { key: 'k', label: 'KOs', num: true, tip: 'Opposing Pokémon knocked out.' },
  { key: 'd', label: 'Faints', num: true, tip: 'Times this mon was knocked out.' },
  { key: 'plusminus', label: '+/−', num: true, fmt: (v) => (v > 0 ? `+${v}` : `${v}`), tip: 'KOs minus Faints.' },
  { key: 'record', label: 'W–L', num: true, text: (r) => `${r.w}–${r.l}`, tip: 'Wins–losses of the games this mon played in.' },
  { key: 'winrate', label: 'Win%', num: true, fmt: (v) => `${Math.round(v * 100)}%`,
    tip: 'Win rate over games played. Ties are broken by most games played.' },
  { key: 'dealt', label: 'Dealt%', num: true, fmt: (v) => Math.round(v), tip: 'Total damage dealt, as a cumulative % of a full HP bar.' },
  { key: 'taken', label: 'Taken%', num: true, fmt: (v) => Math.round(v), tip: 'Total damage taken, as a cumulative % of a full HP bar.' },
  { key: 'ratio', label: 'Dmg ratio', num: true, fmt: (v) => (v === Infinity ? '∞' : v.toFixed(2)),
    tip: 'Damage dealt ÷ damage taken (∞ if it never took damage).' },
  { key: 'crits', label: 'Crits', num: true, tip: 'Critical hits landed.' },
];

async function ensureStats() {
  if (statsData) { renderStats(); return; }
  const body = $('stats-body');
  body.innerHTML = '<p class="muted">Loading…</p>';
  try {
    const res = await Auth.authFetch('/api/stats');
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    statsData = (await res.json()).map((r) => ({
      ...r,
      dexNumber: r.dex,
      // Derived: +/− is KOs minus faints; win rate over games with a result;
      // damage ratio ignores 0-taken (nothing to divide by) — a mon that dealt
      // damage but took none is ∞.
      plusminus: r.k - r.d,
      winrate: (r.w + r.l) ? r.w / (r.w + r.l) : 0,
      ratio: r.taken > 0 ? r.dealt / r.taken : (r.dealt > 0 ? Infinity : 0),
      // Presence: turns on the field ÷ the team's total battle turns.
      presence: r.teamTurns > 0 ? r.activeTurns / r.teamTurns : 0,
    }));
    renderStats();
  } catch (e) {
    body.replaceChildren(Object.assign(document.createElement('p'), {
      className: 'muted', textContent: `Couldn't load stats — ${e.message}`,
    }));
  }
}

function statVal(r, key) {
  if (key === 'record') return r.w + r.l; // sort the W–L column by games decided
  return r[key];
}

function renderStats() {
  const body = $('stats-body');
  const { key, dir } = statsSort;
  const rows = [...statsData].sort((a, b) => {
    const av = statVal(a, key), bv = statVal(b, key);
    let cmp = dir * (typeof av === 'string' ? av.localeCompare(bv) : av - bv);
    // Win% is noisy at low sample size, so break ties by most games played.
    if (cmp === 0 && key === 'winrate') cmp = b.gp - a.gp;
    return cmp;
  });

  $('stats-summary').textContent = `${statsData.length} mons · sort by any column`;

  const table = document.createElement('table');
  table.className = 'stats-table';

  const thead = document.createElement('thead');
  const hr = document.createElement('tr');
  for (const c of STAT_COLS) {
    const th = document.createElement('th');
    th.textContent = c.label + (c.key === key ? (dir < 0 ? ' ▾' : ' ▴') : '');
    if (c.tip) th.title = c.tip;
    if (c.num) th.classList.add('num');
    if (c.key === key) th.classList.add('sorted');
    th.onclick = () => {
      statsSort = statsSort.key === c.key
        ? { key: c.key, dir: -statsSort.dir }
        : { key: c.key, dir: c.num ? -1 : 1 };
      renderStats();
    };
    hr.append(th);
  }
  thead.append(hr);
  table.append(thead);

  const tbody = document.createElement('tbody');
  for (const r of rows) {
    const tr = document.createElement('tr');

    const mon = document.createElement('td');
    // Flex lives on an inner wrapper, not the <td> — display:flex on a table cell
    // drops it out of the column layout and misaligns the whole table.
    const cell = document.createElement('div');
    cell.className = 'stats-mon';
    const img = document.createElement('img');
    img.className = 'stats-sprite'; img.alt = ''; img.loading = 'lazy'; applySprite(img, r);
    const nm = document.createElement('span');
    nm.className = 'stats-name'; nm.textContent = r.pokemon;
    cell.append(img, nm);
    const tb = document.createElement('span');
    tb.className = `tier-badge tier-badge--${r.tier}`; tb.textContent = r.tier;
    cell.append(' ', tb);
    if (r.tera) cell.append(' ', teraTag(r.tera));
    mon.append(cell);
    tr.append(mon);

    const trn = document.createElement('td');
    trn.textContent = r.trainer;
    tr.append(trn);

    for (const c of STAT_COLS.slice(2)) {
      const td = document.createElement('td');
      td.className = 'num';
      td.textContent = c.text ? c.text(r) : c.fmt ? c.fmt(r[c.key]) : r[c.key];
      tr.append(td);
    }
    tbody.append(tr);
  }
  table.append(tbody);
  body.replaceChildren(table);
}

window.addEventListener('resize', sizePicks);

// The teambuilder lives on our own Showdown server now, not an embed. We open
// the official Showdown client pointed at our server (the ~~host:port syntax) —
// that's how you reach a custom server's rooms without hosting the client too.
function teambuilderUrl() {
  const server = window.DRAFT_CONFIG?.showdownServer || 'localhost:8787';
  return `https://play.pokemonshowdown.com/~~${server}/teambuilder`;
}

// Reuse one named tab so repeated opens focus the same window instead of piling
// up duplicates.
function openTeambuilder() {
  window.open(teambuilderUrl(), 'draft-teambuilder', 'noopener');
}

document.querySelectorAll('.tab').forEach((t) => { t.onclick = () => showView(t.dataset.view); });
{ const b = $('tb-open'); if (b) b.onclick = openTeambuilder; }

// ── boot ───────────────────────────────────────────────────────────────

setInterval(renderTimer, 1000); // tick locally; the server is authoritative

// Wire handlers defensively: one missing element (e.g. a cached HTML/JS
// mismatch) must not throw and leave every other button dead.
const on = (id, handler) => { const node = $(id); if (node) node.onclick = handler; };

on('play', async () => {
  // No separate save: the pre-start settings are read straight off their inputs
  // and handed to start, so the numbers the admin sees are the ones used. Only
  // sent while the settings panel is up (i.e. NotStarted) — a Paused resume keeps
  // whatever was already configured.
  let body;
  if (!el.draftSettings.hidden) {
    const weeks = parseInt(el.setWeeks.value, 10);
    const hours = parseFloat(el.setTimeout.value);
    if (Number.isFinite(weeks) && Number.isFinite(hours)) {
      body = { weeks, pickTimerSeconds: Math.round(hours * 3600) };
    }
  }
  showDraftError(await postDraft(`/api/admin/drafts/${draftId}/start`, body) ?? '');
  await refreshDraft();
});

on('rollback', async () => {
  if (!confirm('Undo the most recent pick?')) return;
  // Owner-or-admin route: a coach can undo their own last pick.
  showDraftError(await postDraft(`/api/drafts/${draftId}/rollback`) ?? '');
  await refreshDraft();
});

on('abort', async () => {
  if (!confirm('Abort the draft? Every pick is undone and the pool is restored.')) return;
  showDraftError(await postDraft(`/api/admin/drafts/${draftId}/abort`) ?? '');
  await refreshDraft();
});

on('skip', () => skip());

// Dev-only: replace the league with a canned, fully-played test season.
on('sim-season', async () => {
  if (!confirm('Replace this league with a simulated test season — 14 teams, full drafted rosters, and 54 played matches? This wipes the current draft.')) return;
  el.simSeason.disabled = true;
  const label = el.simSeason.textContent;
  el.simSeason.textContent = 'Simulating… (fetching replays)';
  try {
    const res = await Auth.authFetch('/dev/simulate-season', { method: 'POST' });
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    const r = await res.json();
    statsData = null; // fresh season → drop cached stats
    await refreshDraft();
    loadPlayers();
    showDraftError(`Simulated season: ${r.teams} teams, ${r.picks} picks, ${r.matches} matches.`);
  } catch (e) {
    showDraftError(`Sim failed — ${e.message}`);
  } finally {
    el.simSeason.disabled = false;
    el.simSeason.textContent = label;
  }
});

// Ready up / leave before the draft starts. The broadcast updates other clients;
// we refresh locally so the button and roster flip immediately.
on('ready', async () => {
  const action = draft?.myReady ? 'leave' : 'ready';
  showDraftError(await postDraft(`/api/drafts/${draftId}/${action}`) ?? '');
  await refreshDraft();
  loadPlayers();
});

on('team-close', closeTeam);
// Escape closes the team page (or just the edit form if it's open).
window.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  if (el.teamEditForm && !el.teamEditForm.hidden) { el.teamEditForm.hidden = true; return; }
  if (el.teamPage && !el.teamPage.hidden) closeTeam();
});

// Team customisation (own team only).
on('team-edit', () => { el.tpMsg.textContent = ''; el.teamEditForm.hidden = !el.teamEditForm.hidden; });
on('tp-cancel', () => { el.teamEditForm.hidden = true; });
el.teamEditForm.onsubmit = async (e) => {
  e.preventDefault();
  el.tpMsg.textContent = 'Saving…';
  const err = await postDraft('/api/players/me/profile', {
    teamName: el.tpName.value,
    teamIcon: el.tpIcon.value,
    showdownName: el.tpShowdown.value,
  });
  if (err) { el.tpMsg.textContent = err; return; }
  el.teamEditForm.hidden = true;
  if (teamPageId) openTeam(teamPageId, Auth.user()?.username ?? '');
};

on('login', async () => {
  try { await Auth.login(); }
  catch (e) { el.signinError.textContent = e.message; el.signinError.hidden = false; }
});

on('logout', async () => {
  await Auth.logout();
  signedOut();
});

// Debug slots are only offered on localhost; production users never see them,
// and the endpoints they drive are gone outside Development anyway.
const isLocal = location.hostname === 'localhost' || location.hostname === '127.0.0.1';
if (isLocal) { const panel = $('dev-login'); if (panel) { panel.hidden = false; loadDevSlots(); } }

on('dev-refresh', () => loadDevSlots());

on('dev-admin', async () => {
  try { await Auth.adminLogin(); await signedIn(); }
  catch (e) { el.signinError.textContent = e.message; el.signinError.hidden = false; }
});

// Render the four slots with their live claim state. A slot held by another
// client is still claimable (last-claim-wins) but warns first.
async function loadDevSlots() {
  const list = $('dev-slots');
  if (!list) return;
  list.textContent = 'Loading…';

  let slots;
  try { slots = await Auth.devSlots(); }
  catch (e) { list.textContent = e.message; return; }

  const mine = Auth.clientId();
  list.textContent = '';
  for (const s of slots) {
    const taken = s.claimedBy && s.claimedBy !== mine;

    const name = document.createElement('span');
    name.className = 'dev-slot-name';
    name.textContent = s.username + (s.isAdmin ? ' · admin' : '');

    const team = document.createElement('span');
    team.className = 'dev-slot-team';
    team.textContent = s.teamName;

    const state = document.createElement('span');
    state.className = 'dev-slot-state';
    state.textContent = s.claimedBy ? (s.claimedBy === mine ? 'you' : `taken · ${s.claimedBy}`) : 'free';

    const btn = document.createElement('button');
    btn.className = 'dev-slot' + (taken ? ' taken' : '');
    btn.append(name, team, state);
    btn.onclick = () => claimAndEnter(s.index, taken);

    const li = document.createElement('li');
    li.appendChild(btn);
    list.appendChild(li);
  }
}

async function claimAndEnter(index, taken) {
  if (taken && !confirm('Another client is on that slot. Take it over?')) return;
  try {
    await Auth.claimSlot(index);
    await signedIn();
  } catch (e) { el.signinError.textContent = e.message; el.signinError.hidden = false; }
}

// The signed-in session lives in localStorage, so a normal reload stays logged
// in. A hard reload — Ctrl+F5, or Ctrl/Cmd+Shift+R — is the deliberate "start
// clean" gesture, so wipe the session synchronously first. keydown reaches this
// handler before the browser navigates; plain F5 is left alone.
window.addEventListener('keydown', (e) => {
  const hardF5 = e.key === 'F5' && (e.ctrlKey || e.metaKey || e.shiftKey);
  const shiftR = (e.ctrlKey || e.metaKey) && e.shiftKey && (e.key === 'r' || e.key === 'R');
  if (hardF5 || shiftR) Auth.forget();
}, true);

(async function boot() {
  try {
    await Auth.handleRedirect();
  } catch (e) {
    el.signinError.textContent = e.message;
    el.signinError.hidden = false;
  }
  if (Auth.isLoggedIn()) await signedIn();
  else signedOut();
})();
