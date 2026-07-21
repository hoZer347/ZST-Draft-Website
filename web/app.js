/* App shell. Handles the Discord session and shows the signed-in view.
   Everything past sign-in is a blank slate — build the app on top of this. */

const $ = (id) => document.getElementById(id);
const el = {
  signin: $('signin'), signinError: $('signin-error'), app: $('app'),
  account: $('account'), avatar: $('avatar'), username: $('username'),
  viewAs: $('view-as'), stopViewAs: $('stop-view-as'),
  tabs: $('tabs'), ready: $('ready'), playerList: $('player-list'), addDummy: $('add-dummy'),
  readyAllDummies: $('ready-all-dummies'),
  navToggle: $('nav-toggle'), navBackdrop: $('nav-backdrop'),
  // draft
  onClock: $('on-clock'), onClockBox: $('on-clock-box'), pickNo: $('pick-no'), timer: $('timer'),
  turnCountdown: $('turn-countdown'),
  play: $('play'), rollback: $('rollback'), abort: $('abort'), simSeason: $('sim-season'),
  snapshotSeason: $('snapshot-season'), restoreSeason: $('restore-season'), restoreFile: $('restore-file'),
  simRandomSeason: $('sim-random-season'), simRandom16: $('sim-random-16'),
  simBattles: $('sim-battles'), simBattlesLabel: $('sim-battles-label'),
  demoTeams: $('demo-teams'), demoTeamsLabel: $('demo-teams-label'),
  banner: $('state-banner'),
  turn: $('turn'), turnLabel: $('turn-label'), tiers: $('tiers'), options: $('options'),
  skip: $('skip'), draftError: $('draft-error'), picks: $('picks'),
  // pre-start settings (admin)
  draftSettings: $('draft-settings'), setWeeks: $('set-weeks'), setTimeout: $('set-timeout'), weeksHint: $('weeks-hint'),
  devSignin: $('dev-signin'), devSigninSelect: $('dev-signin-select'),
  // team page
  teamPage: $('team-page'), teamTitle: $('team-title'), teamBody: $('team-body'), teamClose: $('team-close'),
  teamIcon: $('team-icon'), teamShowdown: $('team-showdown'), teamEdit: $('team-edit'),
  teamEditForm: $('team-edit-form'), tpIcon: $('tp-icon'), tpShowdown: $('tp-showdown'),
  tpCancel: $('tp-cancel'), tpMsg: $('tp-msg'),
  // tier list
  tlSearch: $('tl-search'), tlTiers: $('tl-tiers'), tlType1: $('tl-type1'), tlType2: $('tl-type2'), tlRoles: $('tl-roles'),
  tlAvailable: $('tl-available'), tlClear: $('tl-clear'), tlCount: $('tl-count'), tlBody: $('tl-body'),
  tlFilters: $('tl-filters'), tlFilterOpen: $('tl-filter-open'), tlDone: $('tl-done'),
  // schedule
  schedScroll: $('sched-scroll'), schedFilterbar: $('sched-filterbar'), schedMine: $('sched-mine'),
};

// ── session ────────────────────────────────────────────────────────────

function signedOut() {
  el.signin.hidden = false;
  el.app.hidden = true;
  el.tabs.hidden = true;
  el.ready.hidden = true;
  el.account.hidden = true;
  el.navToggle.hidden = true;
  setNav(false); // ensure the mobile drawer isn't left open across sign-out
  // Drop the identity itself, not just the container. Leaving it would show
  // the previous user's portrait for a frame on the next sign-in.
  el.avatar.hidden = true;
  el.avatar.removeAttribute('src');
  el.username.textContent = '';
  closeTeam();
  teardownDraft();
  statsData = null; // drop cached stats so a re-login reloads them
  scoreboardData = null; // and the scoreboard cache
  draftStatsData = null; // and the draft-analytics cache
  loadDevSignin(); // (localhost) repopulate the dev "Sign in as" picker
}

async function signedIn() {
  const user = Auth.user();
  el.signin.hidden = true;
  el.app.hidden = false;
  el.tabs.hidden = false;
  el.account.hidden = false;
  el.navToggle.hidden = false; // reveal the hamburger (mobile CSS decides if it shows)
  el.username.textContent = user.username;
  // Not every Discord account has an avatar, and this element survives a
  // sign-out, so the empty case has to clear it rather than leave the last one.
  if (user.avatarUrl) { el.avatar.src = user.avatarUrl; el.avatar.hidden = false; }
  else { el.avatar.hidden = true; el.avatar.removeAttribute('src'); }

  renderImpersonation(user);
  loadPlayers();

  // Land on the tab you were last on (persisted by showView). landOnSavedTab awaits
  // resolveLeague FIRST — it sets leagueId, which the schedule / scoreboard /
  // draft-stats tabs need — so a refresh on those tabs loads instead of failing.
  // resolveLeague is only a single quick fetch, so we land directly on the cached tab
  // with no draft-tab flash. No redirects: the draft tab is just the default until
  // you've navigated somewhere the first time.
  await landOnSavedTab({
    init: resolveLeague,
    show: showView,
    saved: readActiveTab,
    viewExists: (name) => !!document.getElementById(`view-${name}`),
  });

  // Now that we've landed, hydrate the draft tab (state + roster + realtime) in the
  // background. It only fills the draft view, so it never delays the tab we're on.
  hydrateDraft();
}

// The tab name persisted by showView (or null in private mode / first visit).
function readActiveTab() {
  try { return localStorage.getItem('activeTab'); } catch { return null; }
}

// Admin-only "view as a dummy coach" control. Shows the picker for a real admin;
// once viewing as someone, swaps to a "back to admin" button instead.
async function renderImpersonation(user) {
  // Defensive: a cached HTML/JS mismatch (missing #view-as) must not throw and
  // break sign-in — the impersonation controls are simply absent then.
  if (!user || !el.viewAs || !el.stopViewAs) return;
  const impersonating = !!Auth.impersonator();
  el.stopViewAs.hidden = !impersonating;
  const canPick = !impersonating && !!user.isAdmin;
  el.viewAs.hidden = !canPick;
  if (!canPick) return;
  try {
    const dummies = await Auth.loadDummies();
    el.viewAs.replaceChildren(
      new Option('View as…', ''),
      ...dummies.map((d) => new Option(d.username, d.discordId)),
    );
    el.viewAs.hidden = dummies.length === 0;
  } catch { el.viewAs.hidden = true; }
}

// ── players ──────────────────────────────────────────────────────────────
// The league roster down the left: real Discord users merged with the fixed
// dummy coaches. Each row's right-hand tag shows the coach's ready status.

async function loadPlayers() {
  const list = el.playerList;
  if (!list) return;
  // Only show the placeholder on a cold load. On a refresh (a broadcast, a
  // deletion) keep the existing rows visible until the new data swaps in, so
  // repeated refreshes don't flash "Loading…".
  if (!list.querySelector('.player')) list.innerHTML = '<li class="muted">Loading…</li>';

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

    // The right-hand tag reports ready status. Readied-up coaches show a green
    // "ready" from the latest draft state. Before the draft starts an admin can
    // click it to ready / un-ready that account (dummy or real player), so a test
    // roster can be filled without logging in as each. The element is always
    // present so it holds the grid column and keeps the remove button far-right.
    const readied = !!draft?.ready?.includes(p.discordId);
    const adminSetup = !!me?.isAdmin && draft?.state === 'NotStarted';
    const tag = document.createElement(adminSetup ? 'button' : 'span');
    tag.className = 'player-ready' + (readied ? ' is-ready' : '');
    if (adminSetup) {
      tag.classList.add('player-ready-toggle');
      tag.textContent = readied ? 'ready' : 'ready up';
      tag.title = readied ? `Un-ready ${p.username}` : `Ready up ${p.username}`;
      tag.onclick = (e) => { e.stopPropagation(); toggleReady(p.discordId, readied); };
    } else if (readied) {
      tag.textContent = 'ready';
    }

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
      rm.onclick = (e) => { e.stopPropagation(); removePlayer(p, li); };
      li.append(rm);
    }
    return li;
  }));
}

// Admin (pre-start): ready or un-ready an account on its behalf. Lets an admin
// fill a test roster with dummies and players without logging in as each.
async function toggleReady(discordId, currentlyReady) {
  try {
    const res = await Auth.authFetch(
      `/api/admin/drafts/${draftId}/participants/${encodeURIComponent(discordId)}`,
      { method: currentlyReady ? 'DELETE' : 'POST' },
    );
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    await refreshDraft();
    loadPlayers();
  } catch (e) {
    showDraftError(`Could not change ready state — ${e.message}`);
  }
}

// A dummy is a synthetic test profile: its id isn't an all-digit Discord snowflake.
const isDummyPlayer = (p) => !/^\d+$/.test(p.discordId || '');

async function removePlayer(p, li) {
  // Real accounts are a destructive delete (ends their sessions), so confirm those.
  // Dummies are throwaway test profiles, so remove them with no prompt.
  if (!isDummyPlayer(p) &&
      !confirm(`Remove ${p.username}? This deletes their account and ends every session.`)) return;

  // Drop the row now instead of refetching the whole roster. The server's
  // playersChanged broadcast reconciles every client (including this one) once.
  if (li) li.remove();
  try {
    const res = await Auth.authFetch(`/api/players/${encodeURIComponent(p.discordId)}`, { method: 'DELETE' });
    if (!res.ok) {
      const msg = res.status === 403 ? 'Only admins can remove players' : `Failed (${res.status})`;
      throw new Error(msg);
    }
  } catch (e) {
    showDraftError(`Couldn't remove ${p.username}: ${e.message}`);
    loadPlayers(); // restore the row we optimistically removed
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
  // Teams are identified by their coach's Discord name.
  el.teamTitle.textContent = data.username;

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
  // C-tier mons were drafted with a Tera type — show it on the team page as the
  // same round type-symbol icon the pick feed uses.
  if (m.teraType) name.append(' ', teraIcon(m.teraType));

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
    row('Overall Presence', presence),
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

// Teams are identified by their coach, not a separate team name.
const teamName = (id) => {
  const t = draft?.teams.find((t) => t.Id === id || t.id === id);
  return t?.coachName ?? `Team ${id}`;
};

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

// A Tera type shown as its round type-symbol icon (baked into web/type-icons/).
// Used in the pick feed (the pick's own Tera and the passed column) and the team
// preview; the pill teraTag stays on the live option cards.
function teraIcon(teraType) {
  const img = document.createElement('img');
  // Colour is baked into the recoloured PNG (a coloured glyph on transparency),
  // so no type-- CSS tint is needed here.
  img.className = 'tera-icon';
  img.src = `type-icons/${teraType.toLowerCase()}.png`;
  img.width = 18;
  img.height = 18;
  img.alt = `Tera ${teraType}`;
  img.title = `Tera ${teraType}`;
  img.loading = 'lazy';
  return img;
}

const showDraftError = (msg) => { el.draftError.textContent = msg || ''; el.draftError.hidden = !msg; };

// Resolve which draft/league we're in (a single quick fetch), setting draftId +
// leagueId. Kept SEPARATE from the heavier hydrate below so it can finish before we
// land on a tab: the schedule / scoreboard / draft-stats tabs read leagueId, so it
// must be set before they render — but we don't want to wait on the draft state or
// the realtime connection just to show a tab.
async function resolveLeague() {
  teardownDraft();
  draftId = null;
  leagueId = null;
  try {
    const res = await Auth.authFetch('/api/drafts');
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    const drafts = await res.json();
    if (!drafts.length) { showDraftError('No draft configured.'); return; }

    // One draft in the mock. Teams are built at Start, so a viewer needn't own
    // a team to open it — the server tells us our team id once it exists.
    draftId = drafts[0].id;
    leagueId = drafts[0].leagueId ?? null;
  } catch (e) {
    showDraftError(`Can't load the draft — ${e.message}`);
  }
}

// Fill the draft tab (state + roster) and open the realtime connection. Runs AFTER
// we've landed on the remembered tab, so it never delays that; a no-op when there's
// no draft configured.
async function hydrateDraft() {
  if (draftId == null) return;
  try {
    // Warm the schedule so the Teambuilder tab can seed a blank team per matchup on
    // the very first open (best-effort, non-blocking).
    if (leagueId != null) warmMatchups().catch(() => {});

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
  renderTurnCountdown(s);
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
    // The season is capped at one full round-robin, so with few players it runs
    // shorter than the setting. Show what will actually be scheduled.
    if (el.weeksHint) {
      const players = (s.teams?.length) || (s.ready?.length) || 0;
      const eff = s.scheduledWeeks;
      el.weeksHint.textContent = (eff != null && players >= 2 && eff < s.weeks)
        ? `With ${players} players the season runs ${eff} week${eff === 1 ? '' : 's'} (one full round-robin).`
        : '';
    }
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
  // Season simulator: admin-only, pre-start (same context as Start). Shown on
  // the live site too — the server runs in Development so the endpoint exists,
  // and it's admin-gated server-side.
  el.simSeason.hidden = !(isAdmin && s.state === 'NotStarted');
  el.simRandomSeason.hidden = !(isAdmin && s.state === 'NotStarted');
  el.simRandom16.hidden = !(isAdmin && s.state === 'NotStarted');
  // Snapshot is useful once there's a season to capture (drafting or done).
  el.snapshotSeason.hidden = !(isAdmin && s.state !== 'NotStarted');
  el.restoreSeason.hidden = !isAdmin; // restore into any state (it wipes first)
  el.simBattlesLabel.hidden = !(isAdmin && s.state === 'NotStarted'); // the random-sim battles toggle
  el.demoTeamsLabel.hidden = !(isAdmin && s.state === 'NotStarted'); // build demo teams for every player
  // Admin-only. Shown in any state so it's always reachable; the server only
  // readies the new dummy into a not-yet-started draft (otherwise it's just an
  // account). Most useful pre-start, to fill the roster before the draft.
  if (el.addDummy) el.addDummy.hidden = !isAdmin;
  // Bulk-ready every dummy: only useful before the draft starts (readying is a
  // pre-start action), so hide it once running/complete.
  if (el.readyAllDummies) el.readyAllDummies.hidden = !(isAdmin && s.state === 'NotStarted');

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

// "N picks until you're up" for the logged-in coach: a quick read of how close
// their turn is without counting the board by hand. picksUntilMyTurn is computed
// server-side (see the draft-state endpoint); null when the caller has no team,
// the draft isn't running, or their roster is already full.
function renderTurnCountdown(s) {
  const box = el.turnCountdown;
  if (!box) return;
  const n = s.picksUntilMyTurn;
  if (n == null) { box.hidden = true; box.textContent = ''; return; }
  box.hidden = false;
  box.classList.toggle('is-mine', n === 0); // you're on the clock right now
  box.classList.toggle('is-soon', n > 0 && n <= 2); // next up-ish
  box.textContent = n === 0
    ? "You're on the clock!"
    : `${n} pick${n === 1 ? '' : 's'} until you're up`;
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

// The "passed" run: dimmed sprites of the options offered but not taken, with a
// "passed" label. Shared by pick rows (manual/auto) and skip rows so they render
// identically. `json` is the stored [{name,sprite,dexNumber,tier}] JSON, or null.
// Returns a .pick-others div (empty when there's nothing passed).
function passedRun(json) {
  const passed = document.createElement('div');
  passed.className = 'pick-others';
  let others = [];
  try { others = json ? JSON.parse(json) : []; } catch { /* ignore bad json */ }
  if (others.length) {
    for (const o of others) {
      const img = document.createElement('img');
      img.className = 'pick-other-img';
      img.alt = o.name;
      img.title = o.name;
      img.loading = 'lazy';
      applySprite(img, o);
      passed.append(img);
      // Rejected C-tier options carry the Tera type they were offered with: show
      // its type symbol beside the sprite.
      if (o.teraType) passed.append(teraIcon(o.teraType));
    }
  }
  return passed;
}

// The running feed, newest first.
// Build one row of the pick feed. Rows never change once made (a pick is
// immutable; a rollback removes the last one), so the feed can be rendered
// incrementally — see renderPicks.
function pickRow(p) {
  const li = document.createElement('li');
  // The shared tier-fill standard: tier lip + wash, and a --mine highlight for
  // a pick made by the signed-in user's own team.
  const mine = myTeamId != null && p.teamId === myTeamId;
  li.className = `pick tier-fill tier--${p.tier}${mine ? ' tier-fill--mine' : ''}`;

  // Column 1 — the draft pick itself: number, sprite, name, and (C-tier) its Tera
  // chip right after the name so it reads as part of the pick, not the passed run.
  const main = document.createElement('div');
  main.className = 'pick-main';
  const num = document.createElement('span');
  num.className = 'pick-num';
  num.textContent = `#${p.pickNumber}`;
  const name = document.createElement('span');
  name.className = 'pick-name';
  name.textContent = p.name;
  main.append(num, monImg(p), name);
  if (p.teraType) {
    const tera = document.createElement('span');
    tera.className = 'pick-tera';
    tera.append(teraIcon(p.teraType));
    main.append(tera);
  }

  // Column 2 — the options offered but passed on this turn (dimmed sprites). Left.
  const passed = passedRun(p.otherOptions);

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

// A passed turn in the feed: a coach's voluntary skip, or the engine's forced
// pass when nothing was eligible ('auto'). Mirrors pickRow's 3-column grid (with
// an empty middle) so it lines up down the feed.
function skipRow(sk) {
  const li = document.createElement('li');
  // The signed-in coach's own skips get the same "mine" accent as their picks.
  const mine = myTeamId != null && sk.teamId === myTeamId;
  li.className = `pick pick--skip${mine ? ' tier-fill--mine' : ''}`;

  const main = document.createElement('div');
  main.className = 'pick-main';
  const num = document.createElement('span');
  num.className = 'pick-num';
  num.textContent = '–';
  const label = document.createElement('span');
  label.className = 'pick-name pick-skip-label';
  label.textContent = 'Skipped';
  main.append(num, label);

  // The options they passed on by skipping (same "passed" run as a pick row).
  const mid = passedRun(sk.otherOptions);

  const end = document.createElement('div');
  end.className = 'pick-end';
  const who = document.createElement('span');
  who.className = 'pick-team';
  who.textContent = teamName(sk.teamId);
  const badge = document.createElement('span');
  badge.className = 'pick-skip-badge';
  badge.textContent = sk.wasAuto ? 'auto' : 'skip';
  end.append(who, badge);

  li.append(main, mid, end);
  return li;
}

// The running feed, newest first. Rendered incrementally: a new pick just
// prepends its row. The old code rebuilt the whole list every refresh, which
// re-created and re-fetched every sprite in it — so each pick got slower as the
// feed grew. A full rebuild only happens when the list shrinks (rollback/abort).
// Picks and skips as one chronological feed. A skip recorded after N picks slots
// in right after pick #N (before #N+1); ties break by id. Both kinds only ever
// append at the tail in real time, so the incremental render below still holds.
function buildFeed(s) {
  const items = (s.picks || []).map(p => ({ kind: 'pick', a: p.pickNumber, b: 0, c: 0, data: p }));
  for (const sk of s.skips || []) items.push({ kind: 'skip', a: sk.afterPickNumber, b: 1, c: sk.id, data: sk });
  items.sort((x, y) => x.a - y.a || x.b - y.b || x.c - y.c);
  return items;
}

function feedRow(item) {
  return item.kind === 'skip' ? skipRow(item.data) : pickRow(item.data);
}

let renderedPicks = 0;
function renderPicks(s) {
  const feed = buildFeed(s);
  if (!feed.length) {
    if (renderedPicks !== 0 || !el.picks.firstElementChild) {
      el.picks.innerHTML = '<li class="muted">No picks yet.</li>';
      renderedPicks = 0;
      sizePicks();
    }
    return;
  }
  if (feed.length < renderedPicks || renderedPicks === 0) {
    // Fresh feed, or the list shrank — rebuild from scratch.
    el.picks.replaceChildren(...[...feed].reverse().map(feedRow));
    renderedPicks = feed.length;
    sizePicks();
  } else if (feed.length > renderedPicks) {
    // Only the new rows need building; prepend so the newest lands on top and the
    // existing rows (and their already-loaded sprites) are left untouched.
    for (let i = renderedPicks; i < feed.length; i++) el.picks.prepend(feedRow(feed[i]));
    renderedPicks = feed.length;
    sizePicks();
  }
}

// Grow the picks feed down to the bottom of the window, then let it scroll.
// Recomputed on every render, on tab switches, and on resize, because what sits
// above it — the turn panel especially — changes height.
function sizePicks() {
  const view = document.getElementById('view-draft');
  if (!el.picks || !view || view.hidden) return;
  // On the stacked narrow layout the roster sits above the draft, so the pick
  // card starts low (often below the fold). Pinning its bottom to the viewport
  // would leave ~0 height and no way to reach it — so drop the cap and let the
  // list flow at full height and the whole page scroll to it instead.
  if (window.matchMedia('(max-width: 620px)').matches) {
    el.picks.style.maxHeight = '';
    return;
  }
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

  // Picks / skips change the draft analytics — drop the cache and, if that tab is
  // open, refresh it live.
  ['pickMade', 'pickSkipped', 'pickRolledBack', 'draftStateChanged'].forEach((evt) =>
    conn.on(evt, () => { draftStatsData = null; if (!$('view-draftstats')?.hidden) ensureDraftStats(); }));

  // Roster changes (someone signed in / was removed) fan out to all clients.
  conn.on('playersChanged', () => { loadPlayers(); renderImpersonation(Auth.user()); });

  // Someone readied up or left before the draft — update the button + markers.
  conn.on('readyChanged', async () => { await refreshDraft(); loadPlayers(); });

  // A match was scored or the schedule was regenerated — refresh the tab if it's
  // been opened (scheduleData is set on first view) so results land live.
  conn.on('scheduleChanged', () => {
    if (scheduleData) ensureSchedule();
    // A scored match changes standings + stat leaders. Drop the cache; refresh
    // now only if the scoreboard is the view being looked at.
    scoreboardData = null;
    if (!$('view-scoreboard')?.hidden) ensureScoreboard();
  });

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

  // Mobile: the filter column is an accordion that expands out of the button.
  // The Filters button toggles it; Done collapses it. Both are display:none on
  // desktop, so these handlers are inert there.
  if (el.tlFilterOpen) el.tlFilterOpen.onclick = () => {
    const open = el.tlFilters.classList.toggle('open');
    el.tlFilterOpen.classList.toggle('open', open);
    // The button is sticky, so it can be tapped from anywhere down the list; the
    // panel folds out at the top, so bring that into view when opening.
    if (open) window.scrollTo({ top: 0, behavior: 'smooth' });
  };
  if (el.tlDone) el.tlDone.onclick = closeTierFilters;
}

// Collapse the mobile filter panel. No-op when already closed / on desktop, so
// it's safe to call on any view switch.
function closeTierFilters() {
  if (el.tlFilters) el.tlFilters.classList.remove('open');
  if (el.tlFilterOpen) el.tlFilterOpen.classList.remove('open');
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

// Toggle: show every battle, or only the signed-in coach's. Re-renders in place.
let schedMineOnly = false;
if (el.schedMine) el.schedMine.onchange = () => { schedMineOnly = el.schedMine.checked; renderSchedule(); };

function renderSchedule() {
  const data = scheduleData;
  if (!data) return;

  // The "only my battles" toggle only makes sense once the coach has a team.
  const canFilter = data.myTeamId != null;
  if (el.schedFilterbar) el.schedFilterbar.hidden = !canFilter;

  let matches = data.matches ?? [];
  if (schedMineOnly && canFilter) matches = matches.filter((m) => m.mine);
  if (!matches.length) {
    // Empty either because the draft hasn't begun, or the "my battles" filter
    // matched nothing yet.
    el.schedScroll.replaceChildren(muted(
      schedMineOnly && canFilter ? 'None of your battles to show yet.' : 'No schedule yet — it appears once the draft starts.'));
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

  // Land on the present, so already-played weeks sit above and require a scroll up.
  // The whole page scrolls now, so move the window to the present section, leaving
  // room for the sticky header + filter bar above it. Only when the schedule is the
  // visible tab, so a background (SignalR) refresh can't scroll another view.
  const view = document.getElementById('view-schedule');
  const anchor = document.getElementById('sched-present');
  if (anchor && view && !view.hidden) {
    // Reserve room for whatever stays stuck at the top when we land there: always
    // the filter bar, plus the header only where it's sticky (mobile); on desktop
    // the header scrolls away, so counting it would leave an extra gap.
    const header = document.querySelector('header');
    const headerStuck = header && getComputedStyle(header).position === 'sticky';
    const offset = (headerStuck ? header.offsetHeight : 0)
      + (el.schedFilterbar?.offsetHeight || 0) + 12;
    const top = anchor.getBoundingClientRect().top + window.scrollY - offset;
    window.scrollTo({ top: Math.max(0, top) });
  }
}

function schedHeading(text) {
  const h = document.createElement('div');
  h.className = 'sched-heading label';
  h.textContent = text;
  return h;
}

// The match deadline is stored as Sunday 23:59 UTC of its week. Render it in UTC
// so every viewer sees the same "Sun, Jul 20, 11:59 PM" regardless of their
// timezone (a local conversion could shift it off Sunday and off 11:59).
const DUE_FMT = {
  weekday: 'short', month: 'short', day: 'numeric',
  hour: 'numeric', minute: '2-digit', timeZone: 'UTC',
};

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
    when.textContent = 'Due by ' + new Date(m.scheduledFor).toLocaleString(undefined, DUE_FMT);
    head.append(when);
  }

  // Only a coach in this match, or an admin, gets the replay control.
  const canEdit = m.mine || !!draft?.isAdmin;

  // home — score(+replay button) — away, with the winner emphasised once played.
  const homeWon = m.result === 'HomeWin';
  const awayWon = m.result === 'AwayWin';
  const row = document.createElement('div');
  row.className = 'match-row';
  row.append(
    teamSide(m.homeName, m.homeAvatar, m.homeTeamId === m.myTeamId, homeWon, 'home'),
    matchMiddle(m, card, canEdit),
    teamSide(m.awayName, m.awayAvatar, m.awayTeamId === m.myTeamId, awayWon, 'away'),
  );
  card.append(head, row);

  // Played game: a compact per-mon stat strip under the score.
  if (m.played && m.battleStats?.length) card.append(battleStatsBlock(m));

  // Your own unplayed game: nothing to submit. Playing it on our Showdown server
  // reports the finished battle automatically (see /api/showdown/report).
  if (!m.played && m.mine) card.append(autoRecordNote());
  return card;
}

// Under a played match's score: a per-mon stat table for each team (home left, away
// right), scraped from the battle: whether the mon started (led), KOs, damage,
// crits, that-game presence, and non-self (ally) healing. A mon that was brought
// but never hit the field is greyed with all dashes; a plain 0 shows as a dash too.
const DASH = '–';
const dashNum = (v) => (v ? String(v) : DASH);
const dashPct = (v) => (v ? `${v}%` : DASH);

// [label, tooltip]. Full words, so the columns are self-explanatory.
const MSTAT_COLS = [
  ['Started', 'Led the battle (one of the openers, before turn 1)'],
  ['KOs', 'Opposing mons knocked out'],
  ['Faints', 'Times this mon was knocked out'],
  ['Damage', 'Damage dealt to opponents (% of an HP bar)'],
  ['Presence', 'Share of this game spent on the field'],
  ['Healing', 'HP restored to allies (self-healing not counted)'],
];

function battleStatsBlock(m) {
  const wrap = document.createElement('div');
  wrap.className = 'match-stats';
  wrap.append(
    battleStatsSide(m.battleStats.filter((s) => s.teamId === m.homeTeamId)),
    battleStatsSide(m.battleStats.filter((s) => s.teamId === m.awayTeamId)),
  );
  return wrap;
}

// No team-name caption here: the match-row above already names home (left) and away
// (right) in the same left/right positions these two stats tables sit, so a caption
// would just repeat the coach name.
function battleStatsSide(mons) {
  const side = document.createElement('div');
  side.className = 'mstats-side';

  const head = document.createElement('div');
  head.className = 'mstat mstat-head';
  head.append(Object.assign(document.createElement('span'), { className: 'mstat-mon' }));
  for (const [label, title] of MSTAT_COLS) {
    const h = document.createElement('span');
    h.className = 'mstat-h';
    h.textContent = label;
    h.title = title;
    head.append(h);
  }
  side.append(head, ...mons.map(monStatRow));
  return side;
}

function monStatRow(s) {
  const row = document.createElement('div');
  // The shared tier-fill standard (tier lip + wash). No --mine highlight here: the
  // schedule already flags your own matches (match--is-mine), so highlighting your
  // mons on top of that is noise. Benched mons are greyed via --out.
  row.className = `mstat tier-fill tier--${s.tier}${s.played ? '' : ' mstat--out'}`;

  const mon = document.createElement('div');
  mon.className = 'mstat-mon';
  const img = document.createElement('img');
  img.className = 'mstat-img';
  // No name column any more, so the sprite's tooltip is the mon's identity.
  img.alt = s.name; img.title = s.name; img.loading = 'lazy';
  applySprite(img, s);
  mon.append(img);
  // C-tier mons carry a rolled Tera type: show its symbol, greyed if the Tera
  // was never actually used in this battle.
  if (s.tera) {
    const t = teraIcon(s.tera);
    if (!s.teraUsed) { t.classList.add('tera-unused'); t.title = `Tera ${s.tera} (not used)`; }
    mon.append(t);
  }
  row.append(mon);

  // Started: a filled dot for the openers, a dash otherwise.
  const started = document.createElement('span');
  started.className = 'mstat-v';
  if (s.played && s.started) {
    started.textContent = '●';
    started.classList.add('mstat-started');
    started.title = 'Started';
  } else {
    started.textContent = DASH;
  }
  row.append(started);

  const cells = s.played
    ? [dashNum(s.kos), dashNum(s.faints), dashNum(s.dmg), dashPct(s.presence), dashNum(s.heal)]
    : [DASH, DASH, DASH, DASH, DASH];
  for (const c of cells) {
    const v = document.createElement('span');
    v.className = 'mstat-v';
    v.textContent = c;
    row.append(v);
  }
  return row;
}

// The note under a coach's own unplayed match. There's no link to paste: a game
// played against your opponent on our Showdown server is reported and scored on
// its own the moment it ends.
function autoRecordNote() {
  const p = document.createElement('p');
  p.className = 'match-autorecord';
  p.textContent = 'Just play this match against your opponent on our Showdown server and the result records automatically. No replay link to paste.';
  return p;
}

function teamSide(name, avatarUrl, isMe, won, which) {
  const side = document.createElement('div');
  side.className = `match-team match-team--${which}` + (won ? ' won' : '') + (isMe ? ' me' : '');

  const n = document.createElement('span');
  n.className = 'match-team-name';
  n.textContent = name;

  const inner = [n];
  if (isMe) {
    const you = document.createElement('span');
    you.className = 'match-you';
    you.textContent = 'you';
    inner.push(you);
  }

  // The coach's Discord avatar on the OUTER edge of their side (far left for home,
  // far right for away), so the username is pushed inward toward the centre score.
  // Coaches with no linked account (dummies) or a dead avatar URL fall back to a
  // silhouette icon, so every side has the same bubble.
  const av = avatarBubble(avatarUrl, 'match-team-avatar');
  if (which === 'home') side.append(av, ...inner);
  else side.append(...inner, av);
  return side;
}

// A round Discord avatar bubble. Falls back to a "<class>--blank" silhouette for a
// missing url (a dummy account) or an image that fails to load (an errored one).
function avatarBubble(url, className) {
  const span = document.createElement('span');
  span.className = className;
  if (url) {
    const img = document.createElement('img');
    img.src = url; img.alt = ''; img.loading = 'lazy';
    img.onerror = () => { img.remove(); span.classList.add(`${className}--blank`); };
    span.appendChild(img);
  } else {
    span.classList.add(`${className}--blank`);
  }
  return span;
}

// The centre of the score/players row: the score, plus — for a played match and
// only for the two coaches in it or an admin — a dynamic replay button that
// toggles an inline watch/edit panel beneath the card.
function matchMiddle(m, card, canEdit) {
  const wrap = document.createElement('div');
  wrap.className = 'match-mid-wrap';

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
  wrap.append(mid);

  // Team builds as a pokepaste, shown to everyone for a played game that has them.
  if (m.played && m.hasPaste) {
    const paste = document.createElement('a');
    paste.className = 'match-paste-link';
    paste.href = `/api/matches/${m.id}/paste`;
    paste.target = '_blank';
    paste.rel = 'noopener';
    paste.textContent = 'Teams (paste)';
    paste.title = 'Both teams as a Pokémon Showdown / pokepaste export';
    wrap.append(paste);
  }

  if (m.played && canEdit) {
    const actions = document.createElement('div');
    actions.className = 'match-mid-actions';

    // Watch: a plain link to the replay (only when one exists).
    if (m.replayUrl) {
      const watch = document.createElement('a');
      watch.className = 'match-replay-link';
      watch.href = m.replayUrl;
      watch.target = '_blank';
      watch.rel = 'noopener';
      watch.textContent = 'Watch replay';
      actions.append(watch);
    }

    // Edit/Add: toggles the inline link editor beneath the card.
    const edit = document.createElement('button');
    edit.type = 'button';
    edit.className = 'match-replay-btn';
    edit.textContent = m.replayUrl ? 'Edit replay' : 'Add replay';
    let panel = null;
    edit.onclick = () => {
      if (!panel) {
        panel = replayForm(m, { prefill: m.replayUrl, submitLabel: 'Save link' });
        panel.hidden = true; // start hidden so this first click reveals it
        card.append(panel);
      }
      panel.hidden = !panel.hidden;
      edit.classList.toggle('open', !panel.hidden);
    };
    actions.append(edit);

    // Remove: drop the replay and return the match to Pending — the server backs
    // its result + stats out under the hood.
    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'match-replay-btn match-remove';
    remove.textContent = 'Remove';
    remove.onclick = async () => {
      if (!confirm('Remove this replay? The match goes back to unplayed and its result + stats are backed out.')) return;
      remove.disabled = true;
      const res = await Auth.authFetch(`/api/matches/${m.id}/replay`, { method: 'DELETE' });
      if (res.ok) { statsData = null; await ensureSchedule(); }
      else { remove.disabled = false; showDraftError('Failed to remove the replay.'); }
    };
    actions.append(remove);

    wrap.append(actions);
  }
  return wrap;
}

function replayForm(m, opts = {}) {
  const form = document.createElement('form');
  form.className = 'replay-form';

  const input = document.createElement('input');
  input.type = 'url';
  input.className = 'replay-input';
  input.placeholder = 'Paste your Showdown replay link…';
  input.required = true;
  if (opts.prefill) input.value = opts.prefill;

  const btn = document.createElement('button');
  btn.type = 'submit';
  btn.className = 'replay-btn';
  btn.textContent = opts.submitLabel || 'Submit result';

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

  // When editing an existing link, offer a Watch link right in the panel so the
  // coach/admin can check the replay before changing it.
  if (opts.watchUrl) {
    const watch = document.createElement('a');
    watch.className = 'match-replay-link replay-watch';
    watch.href = opts.watchUrl;
    watch.target = '_blank';
    watch.rel = 'noopener';
    watch.textContent = 'Watch replay';
    form.append(watch);
  }
  form.append(input, btn, msg);
  return form;
}

function showView(name) {
  closeTeam(); // a view switch drops the team overlay
  closeTierFilters(); // and the mobile filter panel, so it doesn't linger open
  syncHeaderHeight(); // keep the sticky Filters button's offset correct
  for (const v of document.querySelectorAll('.view')) v.hidden = v.id !== `view-${name}`;
  for (const t of document.querySelectorAll('.tab')) t.classList.toggle('active', t.dataset.view === name);
  // The schedule tab pins the header to the top (desktop too, see style.css).
  document.body.classList.toggle('sched-view', name === 'schedule');
  try { localStorage.setItem('activeTab', name); } catch { /* private mode */ } // remember for next login
  if (name === 'tierlist') ensureTierList();
  if (name === 'schedule') ensureSchedule();
  if (name === 'stats') ensureStats();
  if (name === 'scoreboard') ensureScoreboard();
  if (name === 'draftstats') ensureDraftStats();
  if (name === 'draft') sizePicks();
}

// ── stats page ───────────────────────────────────────────────────────────
// A sortable league-wide table of accrued battle stats (scraped from replays),
// one row per drafted mon, with a couple of derived columns.

let statsData = null;
let statsSort = { key: 'plusminus', dir: -1 };
let statsFilter = { tier: 'all', trainer: 'all' };
let statsFiltersWired = false;

const STAT_COLS = [
  { key: 'pokemon', label: 'Pokémon', tip: 'The drafted Pokémon (row tinted by draft tier).' },
  { key: 'trainer', label: 'Trainer', tip: 'The coach who drafted it.' },
  { key: 'gp', label: 'GP', num: true, tip: 'Games this mon was brought to.' },
  { key: 'presence', label: 'Season Pres.', num: true, fmt: (v) => `${Math.round(v * 100)}%`,
    tip: "Field turns ÷ team's total turns across the season. Games it skipped count as absence, rewarding usage. Teammates sum to 100% (singles) or 200% (doubles)." },
  { key: 'gamePresence', label: 'Game Pres.', num: true, fmt: (v) => `${Math.round(v * 100)}%`,
    tip: 'Of only the games it played, the share of turns it stayed on the field. Independent of usage.' },
  { key: 'k', label: 'KOs', num: true, tip: 'Opposing Pokémon knocked out.' },
  { key: 'd', label: 'Faints', num: true, tip: 'Times this mon was knocked out.' },
  { key: 'plusminus', label: '+/−', num: true, fmt: (v) => (v > 0 ? `+${v}` : `${v}`), tip: 'KOs minus Faints.' },
  { key: 'record', label: 'W–L', num: true, text: (r) => `${r.w}–${r.l}`, tip: 'Record of games it played.' },
  { key: 'winrate', label: 'Win%', num: true, fmt: (v) => `${Math.round(v * 100)}%`,
    tip: 'Win rate over games played; ties break on games played.' },
  { key: 'ratio', label: 'Dmg ratio', num: true, fmt: (v) => (v === Infinity ? '∞' : v.toFixed(2)),
    tip: 'Damage dealt ÷ damage taken (∞ if it never took damage).' },
  { key: 'dealt', label: 'Dealt%', num: true, fmt: (v) => Math.round(v),
    tip: 'Damage dealt to opponents (direct + indirect), cumulative % of an HP bar.' },
  { key: 'dealtDirect', label: 'Dealt·dir%', num: true, fmt: (v) => Math.round(v),
    tip: 'Direct damage to opponents (your moves landing), cumulative %.' },
  { key: 'dealtIndirect', label: 'Dealt·ind%', num: true, fmt: (v) => Math.round(v),
    tip: 'Indirect damage to opponents (hazards, poison/burn, Rocky Helmet, Leech Seed), cumulative %.' },
  { key: 'allyDmg', label: 'Ally dmg%', num: true, fmt: (v) => Math.round(v),
    tip: 'Friendly-fire damage dealt to your own allies (spread moves catching an ally), cumulative % of an HP bar.' },
  { key: 'taken', label: 'Taken%', num: true, fmt: (v) => Math.round(v),
    tip: 'Damage taken from others (direct + indirect), excluding self, cumulative %.' },
  { key: 'takenDirect', label: 'Taken·dir%', num: true, fmt: (v) => Math.round(v),
    tip: 'Direct damage taken (opposing moves landing), cumulative %.' },
  { key: 'takenIndirect', label: 'Taken·ind%', num: true, fmt: (v) => Math.round(v),
    tip: 'Indirect damage taken (opposing hazards, status, weather), cumulative %.' },
  { key: 'takenSelf', label: 'Self-dmg%', num: true, fmt: (v) => Math.round(v),
    tip: 'Self-inflicted damage (recoil, Life Orb, own status, confusion, crash, HP-cost moves), cumulative %. Excluded from Taken.' },
  { key: 'heal', label: 'Heal%', num: true, fmt: (v) => Math.round(v),
    tip: 'HP restored to itself plus allies, cumulative % of an HP bar.' },
  { key: 'recovered', label: 'Self-heal%', num: true, fmt: (v) => Math.round(v),
    tip: 'HP restored to itself (Recover, Roost, Leftovers, drain), cumulative %.' },
  { key: 'healed', label: 'Ally-heal%', num: true, fmt: (v) => Math.round(v),
    tip: 'HP restored to allies (Life Dew, Pollen Puff, Grassy Terrain), cumulative %.' },
  { key: 'enemyHeal', label: 'Foe heal%', num: true, fmt: (v) => Math.round(v),
    tip: 'HP restored to opposing Pokémon (Heal Pulse on a foe, your Grassy Terrain topping them up), cumulative % of an HP bar.' },
  { key: 'crits', label: 'Crits', num: true, tip: 'Critical hits landed.' },
  { key: 'starts', label: 'Starts', num: true, tip: 'Games this mon led (thrown out first).' },
  { key: 'finishes', label: 'Finished', num: true, tip: 'Games this mon was still standing (not fainted) at the end.' },
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
      // Damage to opponents (dealt*) and ally-healing (healed) come straight from
      // the API. Friendly-fire damage and enemy-healing are their own columns now,
      // never folded into the opponent columns.
      allyDmg: (r.dealtAllyDirect || 0) + (r.dealtAllyIndirect || 0),
      enemyHeal: r.healedEnemy || 0,
      heal: r.recovered + r.healed, // total HP restored: to itself + to allies
      ratio: r.taken > 0 ? r.dealt / r.taken : (r.dealt > 0 ? Infinity : 0),
      // Derived: +/− is KOs minus faints; win rate over games with a result.
      plusminus: r.k - r.d,
      winrate: (r.w + r.l) ? r.w / (r.w + r.l) : 0,
      // Season presence: turns on the field ÷ the team's total battle turns (all
      // games). Game presence: ÷ the turns of only the games it actually played.
      presence: r.teamTurns > 0 ? r.activeTurns / r.teamTurns : 0,
      gamePresence: r.playedTurns > 0 ? r.activeTurns / r.playedTurns : 0,
    }));
    wireStatsFilters();
    renderStats();
  } catch (e) {
    body.replaceChildren(Object.assign(document.createElement('p'), {
      className: 'muted', textContent: `Couldn't load stats — ${e.message}`,
    }));
  }
}

const TIER_RANK = { S: 0, A: 1, B: 2, C: 3 };

function statVal(r, key) {
  if (key === 'record') return r.w + r.l; // sort the W–L column by games decided
  if (key === 'tier') return TIER_RANK[r.tier] ?? 9; // group S→A→B→C, not alphabetical
  return r[key];
}

// Fills the Trainer dropdown from the loaded rows and, once, wires both filters
// to re-render. Called after every load; the listeners attach only the first time.
function wireStatsFilters() {
  const trainerSel = $('stats-filter-trainer');
  const trainers = [...new Set(statsData.map((r) => r.trainer))].sort((a, b) => a.localeCompare(b));
  trainerSel.replaceChildren(new Option('All', 'all'), ...trainers.map((t) => new Option(t, t)));
  // Keep the current pick if it still exists, else fall back to All.
  if (!trainers.includes(statsFilter.trainer)) statsFilter.trainer = 'all';
  trainerSel.value = statsFilter.trainer;
  $('stats-filter-tier').value = statsFilter.tier;

  if (statsFiltersWired) return;
  statsFiltersWired = true;
  $('stats-filter-tier').addEventListener('change', (e) => { statsFilter.tier = e.target.value; renderStats(); });
  trainerSel.addEventListener('change', (e) => { statsFilter.trainer = e.target.value; renderStats(); });
}

function renderStats() {
  const body = $('stats-body');
  const { key, dir } = statsSort;
  const filtered = statsData.filter((r) =>
    (statsFilter.tier === 'all' || r.tier === statsFilter.tier) &&
    (statsFilter.trainer === 'all' || r.trainer === statsFilter.trainer));
  const rows = filtered.sort((a, b) => {
    const av = statVal(a, key), bv = statVal(b, key);
    let cmp = dir * (typeof av === 'string' ? av.localeCompare(bv) : av - bv);
    // Win% is noisy at low sample size, so break ties by most games played.
    if (cmp === 0 && key === 'winrate') cmp = b.gp - a.gp;
    return cmp;
  });

  const filteredOut = statsData.length - filtered.length;
  $('stats-summary').textContent = filteredOut
    ? `${filtered.length} of ${statsData.length} mons · sort by any column`
    : `${statsData.length} mons · sort by any column`;

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
    // Rows use the default zebra; the draft tier colours only the frozen Pokémon +
    // Trainer columns (via --tc from tier--X). A mon on the signed-in coach's own
    // team bolds its Pokémon + Trainer text instead of getting a background wash.
    tr.className = `stats-row tier--${r.tier}` + (r.mine ? ' stats-row--mine' : '');

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

// The mobile sticky "Filters" button sits below the sticky header, so it needs
// the header's live height. Publish it as a CSS var; the header can wrap to two
// rows on a narrow phone, so it isn't a constant.
function syncHeaderHeight() {
  const h = document.querySelector('header');
  if (h) document.documentElement.style.setProperty('--header-h', `${h.offsetHeight}px`);
}
syncHeaderHeight();

window.addEventListener('resize', () => { sizePicks(); syncHeaderHeight(); });

// The teambuilder lives on our own Showdown server now, not an embed. We open
// the official Showdown client pointed at our server (the ~~host:port syntax) —
// that's how you reach a custom server's rooms without hosting the client too.
// The teambuilder URL for the signed-in coach (self-hosted Showdown client with
// our league tiers baked in). buildTeambuilderUrl / openTeambuilderWith live in
// teambuilder.js so the "opened URL carries ?name" invariant is unit-tested.
function teambuilderUrl() {
  return buildTeambuilderUrl(Auth.user()?.username, null);
}

// The signed-in coach's season matchups as [{w: week, o: opponentName}], sorted
// by week, read from the already-loaded schedule (no fetch). Passed to the
// teambuilder so it can seed a folder of blank teams, one per matchup (see
// serve-client.js). Sync so openTeambuilder stays a single window.open that
// reliably carries ?name for auto-login.
function myMatchupsFromCache() {
  const d = scheduleData;
  if (!d || !Array.isArray(d.matches) || d.myTeamId == null) return [];
  return d.matches
    .filter((m) => m.mine)
    .sort((a, b) => a.week - b.week)
    .map((m) => ({ w: m.week, o: m.homeTeamId === d.myTeamId ? m.awayName : m.homeName }));
}

// Ensure the schedule is cached, then return the matchups. Fired in the
// background (on boot and after a cold teambuilder open) so the next open seeds.
async function warmMatchups() {
  if (!scheduleData && leagueId != null) {
    try {
      const res = await Auth.authFetch(`/api/leagues/${leagueId}/schedule`);
      if (res.ok) scheduleData = await res.json();
    } catch { /* best-effort — seeding is optional */ }
  }
  return myMatchupsFromCache();
}

// Pre-built random teams (packed strings) from the coach's own roster — one per
// matchup — cached so a cold teambuilder open can seed a filled team per week.
// A single window.open on the click gesture can't await a fetch, so these are
// warmed in the background ahead of time (see boot + a cold open).
// Admin "build demo teams": [{player, team}] for every player, seeded as a folder
// per player onto the admin's own device on the next teambuilder open.
let demoTeamsCache = null;

// Reuse one named tab so repeated opens focus the same window instead of piling
// up duplicates. A SINGLE synchronous window.open in the click gesture keeps the
// popup unblocked and reliably carries ?name for auto-login.
function openTeambuilder() {
  const matchups = myMatchupsFromCache();
  // The "Week X vs <opponent>" folder is seeded with BLANK teams for the coach to
  // fill — we never pre-fill them. Only the admin's demo teams (when built) carry
  // content; those ride along in demoTeamsCache.
  openTeambuilderWith((...a) => window.open(...a), Auth.user()?.username, matchups, null, demoTeamsCache);
  if (!matchups.length) warmMatchups().catch(() => {});
}

// ── mobile nav drawer ────────────────────────────────────────────────────
// Open/close the slide-in tab drawer. The `.open`/`.nav-open` classes only do
// anything inside the mobile media query, so this is inert on desktop.
let navAnimTimer;
function setNav(open) {
  // The slide transition is enabled ONLY around a deliberate open/close (via the
  // temporary `nav-animate` class). So resizing the window across the mobile
  // breakpoint — which flips the drawer's transform from none to off-screen — can
  // never animate it, which is what made the menu flash open then retract.
  el.tabs.classList.add('nav-animate');
  clearTimeout(navAnimTimer);
  navAnimTimer = setTimeout(() => el.tabs.classList.remove('nav-animate'), 300);

  el.tabs.classList.toggle('open', open);
  el.navBackdrop.classList.toggle('open', open);
  document.body.classList.toggle('nav-open', open);
  el.navToggle.setAttribute('aria-expanded', open ? 'true' : 'false');
}
el.navToggle.onclick = () => setNav(!el.tabs.classList.contains('open'));
el.navBackdrop.onclick = () => setNav(false);

document.querySelectorAll('.tab').forEach((t) => {
  t.onclick = () => {
    setNav(false); // picking a tab closes the mobile drawer
    // Teambuilder isn't an in-app view — it launches the external Showdown
    // builder in a new browser tab and leaves the user on their current view.
    if (t.dataset.view === 'teambuilder') { openTeambuilder(); return; }
    // My team isn't a view either — it opens the same team-page overlay you get
    // by clicking your own icon in the roster, for the signed-in user.
    if (t.dataset.view === 'myteam') { const u = Auth.user(); if (u) openTeam(u.discordId, u.username); return; }
    showView(t.dataset.view);
  };
});

// ── scoreboard ─────────────────────────────────────────────────────────────
// Team standings + a few per-mon stat leaderboards, both ranked server-side from
// the stored battle stats (/api/leagues/{id}/scoreboard). Each list entry is
// tinted with the OPGB scheme (orange, purple, green, blue) from the top down,
// cycling every four rows.

let scoreboardData = null;

// A single four-colour gradient (Orange, Purple, Green, Blue) spread across the
// WHOLE standings list: the row at position i of n gets the colour interpolated
// at i/(n-1) along the four stops. Returned as an rgb() string for --opgb, which
// style.css turns into the row's left lip + wash.
const OPGB_STOPS = [[230, 126, 34], [139, 92, 246], [34, 197, 94], [59, 130, 246]];
function standingsColor(i, n) {
  const t = n <= 1 ? 0 : i / (n - 1);
  const seg = t * (OPGB_STOPS.length - 1);
  const idx = Math.min(OPGB_STOPS.length - 2, Math.floor(seg));
  const f = seg - idx;
  const a = OPGB_STOPS[idx], b = OPGB_STOPS[idx + 1];
  const c = a.map((v, k) => Math.round(v + (b[k] - v) * f));
  return `rgb(${c[0]}, ${c[1]}, ${c[2]})`;
}

async function ensureScoreboard() {
  const body = $('scoreboard-body');
  if (!body) return;
  if (scoreboardData) { renderScoreboard(); return; }
  if (leagueId == null) {
    body.replaceChildren(muted('No season yet — standings appear once a draft has started.'));
    return;
  }
  body.innerHTML = '<p class="muted">Loading…</p>';
  try {
    const res = await Auth.authFetch(`/api/leagues/${leagueId}/scoreboard`);
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    scoreboardData = await res.json();
    renderScoreboard();
  } catch (e) {
    body.replaceChildren(muted(`Couldn't load the scoreboard — ${e.message}`));
  }
}

// The four leaderboards and how each mon's value is shown.
const LEADER_CATS = [
  { key: 'presence', label: 'Season presence', fmt: (v) => `${Math.round(v * 100)}%`,
    tip: "Field turns ÷ the team's total battle turns across the season — how much this mon was actually on the field." },
  { key: 'plusMinus', label: '+/−', fmt: (v) => (v > 0 ? `+${v}` : `${v}`),
    tip: 'KOs scored minus times fainted.' },
  { key: 'healing', label: 'Team Healing', fmt: (v) => `${Math.round(v)}%`,
    tip: 'HP restored to allies (Pollen Puff, Life Dew, ally Grassy Terrain). Excludes self-recovery like Rest/Recover.' },
  { key: 'damageRatio', label: 'Damage ratio', fmt: (v) => (v == null ? '∞' : v.toFixed(2)),
    tip: 'Damage dealt ÷ damage taken (∞ if it never took damage).' },
];

function renderScoreboard() {
  const body = $('scoreboard-body');
  const data = scoreboardData;
  if (!body || !data) return;

  // The signed-in (or viewed-as) user's own team, so we can highlight their row +
  // their mons. The server resolves it from the caller's identity (null for an
  // admin with no team, or during impersonation the viewed-as coach's team).
  const myTeamId = data.myTeamId ?? null;

  const frag = document.createDocumentFragment();
  frag.appendChild(standingsCard(data.standings || [], myTeamId));

  const leadersWrap = document.createElement('div');
  leadersWrap.className = 'leaders-grid';
  for (const cat of LEADER_CATS) {
    leadersWrap.appendChild(leaderCard(cat, (data.leaders?.[cat.key]) || [], myTeamId));
  }
  frag.appendChild(leadersWrap);

  body.replaceChildren(frag);
}

function standingsCard(rows, myTeamId = null) {
  const card = document.createElement('section');
  card.className = 'card scoreboard-standings';
  card.appendChild(Object.assign(document.createElement('div'), { className: 'label', textContent: 'Standings' }));

  if (!rows.length) {
    card.appendChild(muted('No teams yet.'));
    return card;
  }

  const list = document.createElement('ol');
  list.className = 'standings-list';
  rows.forEach((r, i) => {
    const li = document.createElement('li');
    li.className = 'standings-row';
    li.style.setProperty('--opgb', standingsColor(i, rows.length));
    if (myTeamId != null && r.teamId === myTeamId) li.classList.add('standings-row--mine');

    const rank = document.createElement('span');
    rank.className = 'standings-rank';
    rank.textContent = `${i + 1}`;

    // The trainer's Discord avatar, just after their position. Synthetic coaches
    // (dummies / sim) and anyone without an avatar get a blank slot so rows align.
    const avatar = document.createElement('span');
    avatar.className = 'standings-avatar';
    if (r.avatarUrl) {
      const img = document.createElement('img');
      img.src = r.avatarUrl;
      img.alt = '';
      img.loading = 'lazy';
      avatar.appendChild(img);
    } else {
      avatar.classList.add('standings-avatar--blank');
    }

    const name = document.createElement('span');
    name.className = 'standings-name';
    name.textContent = r.trainer;

    // The W–L record (which already conveys the win/loss differential the sort
    // leads on) plus the KO tiebreakers.
    const record = statChip('Record', recordText(r), 'Match record: wins-losses' + (r.draws ? '-draws' : ''));
    const koDiff = statChip('KO±', signed(r.koDiff), 'KO differential — total KOs scored minus total faints');
    const kos = statChip('KOs', `${r.totalKos}`, "Total KOs scored across the team's roster");
    const faints = statChip('Faints', `${r.totalFaints}`, "Total times the team's mons fainted");

    const stats = document.createElement('span');
    stats.className = 'standings-stats';
    stats.append(record, koDiff, kos, faints);

    li.append(rank, avatar, name, standingsMvp(r.mvp), stats);
    list.appendChild(li);
  });
  card.appendChild(list);
  return card;
}

// The team's MVP mon (best KO differential) as a compact labelled cell in the
// standings row. A dash for a team that hasn't played yet. Hidden on narrow
// windows (CSS) so it can't crowd the record chips.
function standingsMvp(mvp) {
  const cell = document.createElement('span');
  cell.className = 'standings-mvp';
  cell.append(Object.assign(document.createElement('span'), { className: 'standings-mvp-label', textContent: 'MVP' }));
  if (mvp) {
    // badge + sprite + tera share one line; wrapped so the stacked (mobile) layout
    // can put the label above and the name below them.
    const mon = document.createElement('span');
    mon.className = 'standings-mvp-mon';
    // Tier badge (S/A/B/C) before the sprite, so the name can stay plain white.
    mon.append(Object.assign(document.createElement('span'),
      { className: `tier-badge tier-badge--${mvp.tier}`, textContent: mvp.tier }));
    const img = document.createElement('img');
    img.className = 'standings-mvp-img';
    img.alt = ''; img.title = mvp.pokemon; img.loading = 'lazy';
    applySprite(img, mvp);
    mon.append(img);
    // Tera slot: always present (fixed width) so names line up whether or not the
    // MVP has a Tera type; the icon only fills it when there is one.
    const tera = document.createElement('span');
    tera.className = 'standings-mvp-tera';
    if (mvp.tera) tera.append(teraIcon(mvp.tera));
    mon.append(tera);
    cell.append(mon);
    cell.append(Object.assign(document.createElement('span'), { className: 'standings-mvp-name', textContent: mvp.pokemon }));
    cell.title = `Team MVP: ${mvp.pokemon} (${signed(mvp.kos - mvp.faints)} KO diff)`;
  } else {
    cell.append(Object.assign(document.createElement('span'), { className: 'standings-mvp-name muted', textContent: '—' }));
  }
  return cell;
}

function recordText(r) {
  return r.draws ? `${r.wins}–${r.losses}–${r.draws}` : `${r.wins}–${r.losses}`;
}
const signed = (n) => (n > 0 ? `+${n}` : `${n}`);

function statChip(label, value, tip) {
  const chip = document.createElement('span');
  chip.className = 'stat-chip';
  if (tip) chip.title = tip; // hover explains what the column means
  const l = document.createElement('span'); l.className = 'stat-chip-label'; l.textContent = label;
  const v = document.createElement('span'); v.className = 'stat-chip-value'; v.textContent = value;
  chip.append(l, v);
  return chip;
}

// Every leaderboard shows this many ranks; ranks past the mons that actually
// contributed (the server only returns those) are filled with greyed-out slots.
const LEADER_SLOTS = 5;

function leaderCard(cat, entries, myTeamId = null) {
  const card = document.createElement('section');
  card.className = 'card leader-card';
  const title = Object.assign(document.createElement('div'), { className: 'label leader-title', textContent: cat.label });
  if (cat.tip) { title.title = cat.tip; title.tabIndex = 0; } // hover/focus explains the stat
  card.appendChild(title);

  const list = document.createElement('ol');
  list.className = 'leader-list';
  for (let i = 0; i < LEADER_SLOTS; i++) {
    const e = entries[i];
    // A mon that didn't contribute to this stat (value exactly 0) is shown as a
    // greyed empty slot, not a real row of "0". A null value is NOT zero (it's the
    // damage-ratio infinity: dealt damage, took none), which stays a real top entry.
    const contributed = e && e.value !== 0;
    list.appendChild(contributed ? leaderRow(cat, e, myTeamId) : emptyLeaderSlot());
  }
  card.appendChild(list);
  return card;
}

function leaderRow(cat, e, myTeamId = null) {
  const li = document.createElement('li');
  // The shared tier-entry standard: tier colour + a --mine boolean highlight for
  // a mon on the signed-in user's own team.
  const mine = myTeamId != null && e.teamId === myTeamId;
  li.className = `leader-row tier-entry${e.tier ? ` tier--${e.tier}` : ''}${mine ? ' tier-entry--mine' : ''}`;

  const img = document.createElement('img');
  img.className = 'leader-sprite'; img.alt = ''; img.loading = 'lazy';
  applySprite(img, e);

  const who = document.createElement('span');
  who.className = 'leader-who';
  const monLine = document.createElement('span');
  monLine.className = 'leader-mon-line';
  const mon = document.createElement('span'); mon.className = 'leader-mon'; mon.textContent = e.pokemon;
  monLine.append(mon);
  // C-tier mons drafted with a Tera type — show its type symbol beside the name.
  if (e.tera) monLine.append(teraIcon(e.tera));
  const trainer = document.createElement('span'); trainer.className = 'leader-trainer'; trainer.textContent = e.trainer;
  who.append(monLine, trainer);

  const val = document.createElement('span');
  val.className = 'leader-value';
  val.textContent = cat.fmt(e.value);

  li.append(img, who, val);
  return li;
}

// A placeholder rank for a category no (further) mon contributed to: a greyed,
// empty slot in place of showing a mon whose stat is zero.
function emptyLeaderSlot() {
  const li = document.createElement('li');
  li.className = 'leader-row leader-row--empty';
  const box = document.createElement('span'); box.className = 'leader-sprite leader-sprite--empty';
  const who = document.createElement('span'); who.className = 'leader-who';
  const mon = document.createElement('span'); mon.className = 'leader-mon'; mon.textContent = '—';
  who.append(mon);
  const val = document.createElement('span'); val.className = 'leader-value'; val.textContent = '—';
  li.append(box, who, val);
  return li;
}

// ── draft stats page ─────────────────────────────────────────────────────
// Analytics over the draft itself: which mons went instantly, which were passed
// over most, and how Tera types fared among picks vs rejections. Read-only view
// of /api/leagues/{id}/draft-stats.

let draftStatsData = null;

async function ensureDraftStats() {
  const body = $('draftstats-body');
  if (!body) return;
  if (draftStatsData) { renderDraftStats(); return; }
  if (leagueId == null) {
    body.replaceChildren(muted('No draft yet — analytics appear once a draft has started.'));
    return;
  }
  if (!body.querySelector('.dstat-card')) body.innerHTML = '<p class="muted">Loading…</p>';
  try {
    const res = await Auth.authFetch(`/api/leagues/${leagueId}/draft-stats`);
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    draftStatsData = await res.json();
    renderDraftStats();
  } catch (e) {
    body.replaceChildren(muted(`Couldn't load draft stats: ${e.message}`));
  }
}

function renderDraftStats() {
  const body = $('draftstats-body');
  const d = draftStatsData;
  if (!body || !d) return;
  if (!d.totalPicks) {
    body.replaceChildren(muted('No picks yet — analytics appear once the draft is under way.'));
    return;
  }

  // All six tables sit in one horizontal row; the row scrolls sideways when the
  // cards run past the viewport (see .dstat-grid). Tera titles are prefixed so the
  // two "Most rejected" cards don't read the same when they're side by side.
  const undraftables = d.pokemon.undraftables || [];
  const undraftablesCard = monListCard('Undraftables', undraftables,
    `These ${undraftables.length} Pokémon decided they weren't gonna.`,
    { count: false, drafter: false });

  const grid = document.createElement('div');
  grid.className = 'dstat-grid';
  grid.append(
    monListCard('Instant picks', d.pokemon.instantPicks,
      'Drafted the moment they were offered — never once passed over.', { count: false }),
    monListCard('Most rejected', d.pokemon.mostRejected,
      'How many times each mon was offered but passed over.'),
    monListCard('Put Me In Coach!!!', d.pokemon.putMeInCoach,
      'The most-rejected mons that still made it onto a team.'),
    undraftablesCard,
    teraCountCard('Tera Most Picked', d.tera.mostPicked, 'Most common Tera type among chosen picks.'),
    teraCountCard('Tera Most Rejected', d.tera.mostRejected, 'Most common Tera type among passed-over options.'),
    teraRateCard('Tera Pick Rate', d.tera.pickRate),
  );

  body.replaceChildren(grid);
}

function dstatCard(title, hint) {
  const card = document.createElement('section');
  card.className = 'card dstat-card';
  card.append(Object.assign(document.createElement('div'), { className: 'label', textContent: title }));
  if (hint) card.append(Object.assign(document.createElement('p'), { className: 'dstat-hint muted', textContent: hint }));
  return card;
}

function monListCard(title, rows, hint, opts = {}) {
  const card = dstatCard(title, hint);
  if (!rows || !rows.length) { card.append(muted('None yet.')); return card; }
  const ol = document.createElement('ol');
  ol.className = 'dstat-list';
  for (const r of rows) {
    const li = document.createElement('li');
    // Highlight mons on the logged-in coach's own team.
    li.className = `dstat-row tier-entry${r.tier ? ` tier--${r.tier}` : ''}${r.mine ? ' tier-entry--mine' : ''}`;
    li.append(monImg(r));

    // Name up top, with a small line under it naming who drafted it (or Undrafted).
    // The drafter line is suppressed where every row is undrafted (e.g. Undraftables).
    const who = document.createElement('span');
    who.className = 'dstat-who';
    const name = document.createElement('span');
    name.className = 'dstat-name';
    name.textContent = r.name;
    who.append(name);
    if (opts.drafter !== false) {
      const by = document.createElement('span');
      by.className = 'dstat-drafter' + (r.trainer ? '' : ' dstat-drafter--none');
      by.textContent = r.trainer || 'Undrafted';
      who.append(by);
    }
    li.append(who);

    if (opts.count !== false && r.rejections != null) {
      const val = document.createElement('span');
      val.className = 'dstat-value';
      val.textContent = `×${r.rejections}`;
      val.title = `Passed over ${r.rejections} time${r.rejections === 1 ? '' : 's'}`;
      li.append(val);
    }
    ol.append(li);
  }
  card.append(ol);
  return card;
}

function teraCountCard(title, rows, hint) {
  const card = dstatCard(title, hint);
  if (!rows || !rows.length) { card.append(muted('No Tera data yet.')); return card; }
  const max = Math.max(...rows.map((r) => r.count), 1);
  const ol = document.createElement('ol');
  ol.className = 'dstat-list';
  for (const r of rows) ol.append(teraBarRow(r.type, `${r.count}`, r.count / max));
  card.append(ol);
  return card;
}

function teraRateCard(title, rows) {
  const card = dstatCard(title, 'Of every time a Tera type was offered, the share that was picked.');
  card.classList.add('dstat-rate-card'); // keeps the original gradient direction
  if (!rows || !rows.length) { card.append(muted('No Tera data yet.')); return card; }
  // Bars are relative to the top rate in the list, not out of a flat 100%.
  const max = Math.max(...rows.map((r) => r.rate), 0.0001);
  const ol = document.createElement('ol');
  ol.className = 'dstat-list';
  for (const r of rows) {
    const row = teraBarRow(r.type, `${(r.rate * 100).toFixed(1)}%`, r.rate / max);
    row.title = `${r.picked} picked of ${r.picked + r.rejected} offered`;
    ol.append(row);
  }
  card.append(ol);
  return card;
}

function teraBarRow(type, valueText, frac) {
  const li = document.createElement('li');
  li.className = `dstat-row dstat-tera-row type--${type.toLowerCase()}`;
  // The Tera type symbol labels the row.
  li.append(teraIcon(type));
  const name = document.createElement('span');
  name.className = 'dstat-name dstat-tera-name';
  name.textContent = type;
  const bar = document.createElement('span');
  bar.className = 'dstat-bar';
  const fill = document.createElement('span');
  fill.className = 'dstat-bar-fill';
  fill.style.width = `${Math.max(4, Math.round((frac || 0) * 100))}%`;
  bar.append(fill);
  const val = document.createElement('span');
  val.className = 'dstat-value';
  val.textContent = valueText;
  li.append(name, bar, val);
  return li;
}

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

// Dev-only: replace the league with the canned, fully-played ZST 3 test season.
on('sim-season', async () => {
  if (!confirm('Load ZST 3 — replace this league with the canned test season (real teams, drafted rosters, played matches)? This wipes the current draft.')) return;
  el.simSeason.disabled = true;
  const label = el.simSeason.textContent;
  el.simSeason.textContent = 'Loading… (fetching replays)';
  try {
    const res = await Auth.authFetch('/dev/simulate-season', { method: 'POST' });
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    const r = await res.json();
    statsData = null; // fresh season → drop cached stats
    draftStatsData = null; // and the draft-analytics cache
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

// Purely-random season: synthetic teams, a random valid draft, random stats. No
// replays, so it returns instantly. Same wipe warning as the real one.
// Replace the league with a random test season. teams=null uses the endpoint's
// default; pass a number (e.g. 16) to force that many synthetic players — the
// server clamps it to what the pool can fill. btn is the button that triggered it,
// so its own label reflects progress.
async function runRandomSim(teams, btn) {
  const battles = el.simBattles.checked;
  const how = battles
    ? 'real headless Showdown battles (results + stats recorded from the logs)'
    : 'no battles (the schedule stays pending)';
  const who = teams ? `${teams} synthetic players` : 'synthetic teams';
  if (!confirm(`Replace this league with a RANDOM test season — ${who}, random draft, and ${how}? This wipes the current draft.`)) return;
  btn.disabled = true;
  const label = btn.textContent;
  btn.textContent = battles ? 'Simulating battles…' : 'Simulating…';
  try {
    const q = new URLSearchParams({ real: battles });
    if (teams) q.set('teams', teams);
    const res = await Auth.authFetch(`/dev/simulate-random-season?${q}`, { method: 'POST' });
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    const r = await res.json();
    statsData = null; // fresh season → drop cached stats
    draftStatsData = null; // and the draft-analytics cache
    await refreshDraft();
    loadPlayers();
    let msg = `Random season: ${r.teams} teams, ${r.picks} picks, ${r.skips ? `${r.skips} skips, ` : ''}${r.matches} matches (${r.realBattles ? 'real battles, results + stats recorded from the logs' : 'no battles — schedule left pending'}).`;

    // Build demo teams: an example team for every player, cached to seed onto THIS
    // (the admin's) device as a folder per player the next time the Teambuilder opens.
    if (el.demoTeams.checked) {
      btn.textContent = 'Building demo teams…';
      const n = await prepareDemoTeams();
      if (n != null) msg += ` Demo teams for ${n} players ready — open Teambuilder to see them.`;
    }
    showDraftError(msg);
  } catch (e) {
    showDraftError(`Sim failed — ${e.message}`);
  } finally {
    btn.disabled = false;
    btn.textContent = label;
  }
}

on('sim-random-season', () => runRandomSim(null, el.simRandomSeason));
on('sim-random-16', () => runRandomSim(16, el.simRandom16));

// Download a JSON snapshot of the season so far: every draft pick, every stored
// replay, and the captured team builds. Admin-only (the endpoint enforces it).
on('snapshot-season', async () => {
  if (leagueId == null) return;
  el.snapshotSeason.disabled = true;
  try {
    const res = await Auth.authFetch(`/api/leagues/${leagueId}/snapshot`);
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    const data = await res.json();
    const blob = new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = `draft-snapshot-league${leagueId}-${new Date().toISOString().slice(0, 10)}.json`;
    document.body.append(a); a.click(); a.remove();
    URL.revokeObjectURL(url);
    const withPaste = data.matches.filter((mt) => mt.homeTeamExport || mt.awayTeamExport).length;
    showDraftError(`Snapshot saved: ${data.picks.length} picks, ${data.matches.length} matches (${withPaste} with team builds).`);
  } catch (e) {
    showDraftError(`Snapshot failed: ${e.message}`);
  } finally {
    el.snapshotSeason.disabled = false;
  }
});

// Restore a whole season from a snapshot JSON file: pick a file, POST it, and the
// server wipes + rebuilds the league (teams, picks, schedule, results). Admin-only.
async function restoreFromFile(file) {
  if (!file || leagueId == null) return;
  if (!confirm(`Restore this league from "${file.name}"? This WIPES the current draft, schedule and results, then rebuilds them from the snapshot.`)) return;
  el.restoreSeason.disabled = true;
  const label = el.restoreSeason.textContent;
  el.restoreSeason.textContent = 'Restoring…';
  try {
    const body = await file.text();
    const res = await Auth.authFetch(`/api/leagues/${leagueId}/snapshot`, {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body,
    });
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    const r = await res.json();
    statsData = null; draftStatsData = null; scoreboardData = null; scheduleData = null;
    await refreshDraft();
    loadPlayers();
    showDraftError(`Restored: ${r.teams} teams, ${r.picks} picks${r.missedPicks ? ` (${r.missedPicks} unmatched)` : ''}, ${r.matches} matches (${r.recorded} results recorded).`);
  } catch (e) {
    showDraftError(`Restore failed: ${e.message}`);
  } finally {
    el.restoreSeason.disabled = false;
    el.restoreSeason.textContent = label;
  }
}

on('restore-season', async () => {
  // Prefer the File System Access picker: it can start at the Desktop (where the
  // daily-snapshot folder lives) and remembers the last folder used per id, so it
  // reopens straight into the snapshots folder next time. Falls back to a classic
  // file input where the API isn't available.
  if (window.showOpenFilePicker) {
    try {
      const [handle] = await window.showOpenFilePicker({
        id: 'draft-snapshots',
        startIn: 'desktop',
        multiple: false,
        types: [{ description: 'Season snapshot', accept: { 'application/json': ['.json'] } }],
      });
      await restoreFromFile(await handle.getFile());
    } catch (e) {
      if (e?.name !== 'AbortError') showDraftError(`Restore failed: ${e.message}`);
    }
    return;
  }
  el.restoreFile.click();
});
el.restoreFile?.addEventListener('change', async () => {
  const file = el.restoreFile.files?.[0];
  el.restoreFile.value = ''; // let the same file be picked again later
  await restoreFromFile(file);
});

// Fetch one demo team per readied-up participant (roster if drafted, else a random
// team from the pool) and cache it to seed onto the admin's device on the next
// Teambuilder open. Returns the player count, or null on failure. Best-effort.
async function prepareDemoTeams() {
  try {
    const dres = await Auth.authFetch('/api/teams/demo');
    if (!dres.ok) return null;
    demoTeamsCache = ((await dres.json()).teams || []).filter((t) => t.team);
    return demoTeamsCache.length;
  } catch { return null; }
}

// Checking "Build demo teams" prepares them immediately — no sim required — so a
// real league with readied-up players can seed example teams. Unchecking clears.
el.demoTeams?.addEventListener('change', async () => {
  if (!el.demoTeams.checked) { demoTeamsCache = null; return; }
  const label = el.demoTeamsLabel;
  if (label) label.style.opacity = '0.6';
  const n = await prepareDemoTeams();
  if (label) label.style.opacity = '';
  if (n != null) showDraftError(`Demo teams for ${n} players ready — open Teambuilder to see them.`);
  else { el.demoTeams.checked = false; showDraftError('Could not build demo teams (no readied players yet?).'); }
});

// Ready up / leave before the draft starts. The broadcast updates other clients;
// we refresh locally so the button and roster flip immediately.
on('ready', async () => {
  const action = draft?.myReady ? 'leave' : 'ready';
  showDraftError(await postDraft(`/api/drafts/${draftId}/${action}`) ?? '');
  await refreshDraft();
  loadPlayers();
});

// Admin: add a synthetic coach to fill the roster (readied into a not-yet-started
// draft, so it joins like any coach who signed up).
on('add-dummy', async () => {
  if (el.addDummy) el.addDummy.disabled = true;
  try {
    const res = await Auth.authFetch('/api/admin/dummies', { method: 'POST' });
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    await refreshDraft();
    loadPlayers();
    renderImpersonation(Auth.user()); // the new dummy joins the "View as" list
  } catch (e) {
    showDraftError(`Could not add dummy — ${e.message}`);
  } finally {
    if (el.addDummy) el.addDummy.disabled = false;
  }
});

// Admin: ready every existing dummy into the not-yet-started draft at once, so a
// test roster fills without clicking each one.
on('ready-all-dummies', async () => {
  if (el.readyAllDummies) el.readyAllDummies.disabled = true;
  try {
    const res = await Auth.authFetch('/api/admin/dummies/ready-all', { method: 'POST' });
    if (!res.ok) throw new Error(`Failed (${res.status})`);
    await refreshDraft();
    loadPlayers();
  } catch (e) {
    showDraftError(`Could not ready dummies: ${e.message}`);
  } finally {
    if (el.readyAllDummies) el.readyAllDummies.disabled = false;
  }
});

on('team-close', closeTeam);
// Escape closes the team page (or just the edit form if it's open).
window.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  if (el.tabs.classList.contains('open')) { setNav(false); return; }
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

// Dev-only "Sign in as" picker (localhost). Lists existing accounts (admin + every
// coach) so you can drive the app without Discord. Hidden and inert in production
// (the /dev/* routes 404 there, so populate just fails quietly).
const isLocalhost = location.hostname === 'localhost' || location.hostname === '127.0.0.1';
async function loadDevSignin() {
  if (!isLocalhost || !el.devSignin || !el.devSigninSelect) return;
  let accounts;
  try { accounts = await Auth.devAccounts(); }
  catch { return; } // not in Development — leave it hidden
  el.devSigninSelect.replaceChildren(
    new Option('Choose an account…', ''),
    ...accounts.map((a) => {
      const o = new Option(a.username + (a.isAdmin ? ' (admin)' : ''), a.discordId);
      o.dataset.admin = a.isAdmin ? '1' : '';
      return o;
    }),
  );
  el.devSignin.hidden = false;
}
if (el.devSigninSelect) el.devSigninSelect.onchange = async (e) => {
  const opt = e.target.selectedOptions[0];
  const id = e.target.value;
  if (!id) return;
  try { await Auth.devSignInAs(id, opt?.dataset.admin === '1'); await signedIn(); }
  catch (err) { el.signinError.textContent = err.message; el.signinError.hidden = false; e.target.value = ''; }
};

on('logout', async () => {
  await Auth.logout();
  signedOut();
});

// Admin: pick a dummy coach to view as, or pop back to your own account. Guarded
// like the on() handlers — a missing #view-as (cached HTML/JS mismatch) must not
// throw here at module load, which would stop boot() and kill the whole app.
if (el.viewAs) el.viewAs.onchange = async (e) => {
  const id = e.target.value;
  if (!id) return;
  try { await Auth.impersonate(id); location.reload(); }
  catch (err) { alert(err.message); e.target.value = ''; }
};
on('stop-view-as', () => { Auth.stopImpersonating(); location.reload(); });

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
