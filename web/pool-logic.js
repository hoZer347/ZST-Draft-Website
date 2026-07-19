// Pure, DOM-free tier-list + MVP logic.
//
// This is the single source of truth for how the pool is filtered and how a
// team's MVP is chosen. It is loaded two ways:
//   • the browser loads it as a plain <script> BEFORE app.js, so every export
//     lands on globalThis and app.js uses the names unqualified (ROLES,
//     rolesOf, pickMvp, poolMatches, ALL_TYPES);
//   • the Node test/benchmark suite require()s it (tests/pool-logic.test.js,
//     tests/tierlist.bench.js).
// Keeping it free of the DOM and of any framework is what lets the same code
// path be unit-tested and benchmarked headlessly.
(function (root, factory) {
  const api = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = api; // Node
  for (const k in api) root[k] = api[k]; // browser: expose as globals for app.js
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  const ALL_TYPES = ['Normal', 'Fire', 'Water', 'Electric', 'Grass', 'Ice', 'Fighting', 'Poison',
    'Ground', 'Flying', 'Psychic', 'Bug', 'Rock', 'Ghost', 'Dragon', 'Dark', 'Steel', 'Fairy'];

  // Roles are derived from base stats (we have no move data), so they're rough
  // signposts, not a full competitive classification. Each is [label, predicate].
  const ROLES = [
    ['Physical', (m) => m.atk >= 100],
    ['Special', (m) => m.spAtk >= 100],
    ['Fast', (m) => m.speed >= 100],
    ['Bulky', (m) => m.hp >= 100],
    ['Wall', (m) => m.def >= 110 || m.spDef >= 110],
  ];
  const rolesOf = (m) => ROLES.filter(([, f]) => f(m)).map(([n]) => n);

  // The team MVP: highest KO differential (KOs − faints), then win rate, then
  // raw KOs, then lower tier wins (a C-tier over-performer beats an S-tier).
  // Only mons with a logged game are eligible; returns null if none have played.
  function pickMvp(mons) {
    const rank = { C: 0, B: 1, A: 2, S: 3 }; // lower tier ranks first on a tie
    const wr = (s) => (s.w + s.l > 0 ? s.w / (s.w + s.l) : 0);
    const played = mons.filter((m) => m.stats && m.stats.gp > 0);
    if (!played.length) return null;
    return played.slice().sort((a, b) => {
      const da = a.stats.k - a.stats.d;
      const db = b.stats.k - b.stats.d;
      if (db !== da) return db - da;
      const wa = wr(a.stats);
      const wb = wr(b.stats);
      if (wb !== wa) return wb - wa;
      if (b.stats.k !== a.stats.k) return b.stats.k - a.stats.k;
      return (rank[a.tier] ?? 9) - (rank[b.tier] ?? 9);
    })[0];
  }

  // Pure tier-list filter predicate. `c` is a plain criteria object read off the
  // filter controls: { search, availableOnly, tiers:[], type1, type2, roles:[] }.
  // `skip` names one facet to ignore (so a facet's own option counts aren't
  // suppressed by its current selection); pass null/undefined to apply them all.
  function poolMatches(m, c, skip) {
    if (skip !== 'search' && c.search) {
      const q = c.search.trim().toLowerCase();
      if (q && !m.name.toLowerCase().includes(q)) return false;
    }
    if (skip !== 'avail' && c.availableOnly && m.drafted) return false;
    if (skip !== 'tier' && c.tiers && c.tiers.length && !c.tiers.includes(m.tier)) return false;
    const types = [m.type1, m.type2].filter(Boolean);
    if (skip !== 'type1' && c.type1 && !types.includes(c.type1)) return false;
    if (skip !== 'type2' && c.type2 && !types.includes(c.type2)) return false;
    if (skip !== 'roles' && c.roles && c.roles.length) {
      const mr = rolesOf(m);
      if (!c.roles.every((r) => mr.includes(r))) return false;
    }
    return true;
  }

  return { ALL_TYPES, ROLES, rolesOf, pickMvp, poolMatches };
});
