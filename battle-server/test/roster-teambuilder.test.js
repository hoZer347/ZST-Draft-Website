'use strict';

// Unit tests for rosterSpeciesIds (lib/roster-teambuilder.js), which maps a coach's
// drafted roster to the set of species ids the ZST teambuilder should offer. Uses
// the real gen9 Dex so the forme rules (battleOnly / requiredItem) are exercised
// against real data, matching how the browser resolves species.

const { test } = require('node:test');
const assert = require('node:assert');
const { Dex } = require('pokemon-showdown');
const { rosterSpeciesIds, filterPokemonResults, tbID } = require('../lib/roster-teambuilder');
const CUSTOM = require('../showdown-config/custom-megas.js');

const dex = Dex.mod('gen9');
const getSpecies = (slug) => dex.species.get(slug);
const idsFor = (mons) => rosterSpeciesIds(mons, getSpecies);

test('megas are offered as their OWN forme (the league lists megas as formes)', () => {
  const ids = idsFor([{ slug: 'charizard-megay' }, { slug: 'charizard-megax' }]);
  assert.ok(ids.charizardmegay && ids.charizardmegax, 'mega formes offered directly');
  assert.ok(!ids.charizard, 'base Charizard (a different drafted mon) is not unlocked');
});

test('origin / crowned / primal formes are offered as themselves', () => {
  const ids = idsFor([{ slug: 'giratina-origin' }, { slug: 'zamazenta-crowned' }, { slug: 'groudon-primal' }]);
  assert.ok(ids.giratinaorigin && ids.zamazentacrowned && ids.groudonprimal);
  assert.ok(!ids.giratina && !ids.zamazenta && !ids.groudon, 'base forms not unlocked');
});

test('regionals and alolans are offered directly, NOT their base', () => {
  const ids = idsFor([{ slug: 'rotom-wash' }, { slug: 'muk-alola' }]);
  assert.ok(ids.rotomwash && ids.mukalola, 'formes offered directly');
  assert.ok(!ids.rotom && !ids.muk, 'base form is a different mon, not unlocked');
});

test('plain species are offered as themselves', () => {
  const ids = idsFor([{ slug: 'garchomp' }, { slug: 'blissey' }]);
  assert.deepStrictEqual(Object.keys(ids).sort(), ['blissey', 'garchomp']);
});

// ── Comprehensive: EVERY mega/primal + regional forme in the dex ──────────────
// The dex here is custom-merged (the server dex), so it knows the league's custom
// megas too; these sweep every one.

const isMegaForme = (s) => s.exists && s.baseSpecies && s.baseSpecies !== s.name && /^(Mega|Primal)/.test(s.forme || '');
// Directly-buildable regionals only: a Zen/battle forme of a regional (Darmanitan-
// Galar-Zen) has battleOnly and legitimately collapses to base, so exclude those.
const isRegional = (s) => s.exists && s.baseSpecies && s.baseSpecies !== s.name &&
  /-(Alola|Galar|Hisui|Paldea)\b/.test(s.name) && !s.battleOnly && !s.requiredItem;
// The sprite slug the .NET roster endpoint returns (baseSpecies-forme for a forme).
const spriteSlug = (s) => tbID(s.baseSpecies) + (s.forme ? '-' + tbID(s.forme) : '');

test('every mega/primal forme (standard AND custom) is offered as its own forme id', () => {
  const megas = dex.species.all().filter(isMegaForme);
  assert.ok(megas.length > 40, `sanity: found ${megas.length} mega/primal formes`);
  for (const m of megas) {
    const ids = rosterSpeciesIds([{ slug: spriteSlug(m) }], getSpecies);
    assert.ok(ids[tbID(m.id)], `${m.name} (${spriteSlug(m)}) should be offered as ${m.id}; got ${Object.keys(ids)}`);
    assert.ok(!ids[tbID(m.baseSpecies)] || tbID(m.baseSpecies) === tbID(m.id), `${m.name} must not unlock base ${m.baseSpecies}`);
  }
});

test('every custom mega resolves to its own forme id even when the (client) dex lacks it', () => {
  // The browser's CDN dex lacks the league's custom megas, so simulate that: hide any
  // custom-mega species and confirm the slug's own id is still what gets offered.
  const customIds = new Set(Object.keys(CUSTOM.Pokedex || {}));
  assert.ok(customIds.size > 0, 'sanity: custom megas present in config');
  const clientGet = (slug) => { const s = dex.species.get(slug); return (s.exists && customIds.has(s.id)) ? { exists: false } : s; };
  for (const id of customIds) {
    const sp = dex.species.get(id);
    if (!sp.exists) continue;
    const ids = rosterSpeciesIds([{ slug: spriteSlug(sp) }], clientGet); // e.g. "raichu-megax"
    assert.ok(ids[sp.id], `custom ${sp.name} (${spriteSlug(sp)}) should map to ${sp.id}; got ${Object.keys(ids)}`);
  }
});

test('every regional forme is offered as itself, never collapsing to its base', () => {
  const forms = dex.species.all().filter(isRegional);
  assert.ok(forms.length > 15, `sanity: found ${forms.length} regional formes`);
  for (const f of forms) {
    const ids = rosterSpeciesIds([{ slug: spriteSlug(f) }], getSpecies);
    assert.ok(ids[tbID(f.id)], `${f.name} should be offered as itself; got ${Object.keys(ids)}`);
    assert.ok(!ids[tbID(f.baseSpecies)], `${f.name} must NOT unlock base ${f.baseSpecies}`);
  }
});

test('falls back to name when slug is absent, and to raw id for unknown mons', () => {
  const ids = idsFor([{ name: 'Tapu Koko' }, { slug: 'not-a-real-mon' }]);
  assert.ok(ids.tapukoko, 'name used when slug missing');
  assert.ok(ids.notarealmon, 'unknown slug kept as-is so a pick never vanishes');
});

test('empty / missing roster yields an empty set', () => {
  assert.deepStrictEqual(idsFor([]), {});
  assert.deepStrictEqual(idsFor(null), {});
});

// ── filterPokemonResults: what the "+" browse and typed search actually show ──

const ALLOW = { garchomp: true, marshadow: true, tyranitar: true };

test('browse list keeps only drafted mons and drops now-empty tier headers', () => {
  const rows = [
    ['sortpokemon', ''],
    ['header', 'S'], ['pokemon', 'marshadow'], ['pokemon', 'arceus'],
    ['header', 'A'], ['pokemon', 'tyranitar'],
    ['header', 'B'], ['pokemon', 'garchomp'],
    ['header', 'C'], ['pokemon', 'pikachu'],           // no drafted C mon -> header dropped
  ];
  assert.deepStrictEqual(filterPokemonResults(rows, ALLOW), [
    ['sortpokemon', ''],
    ['header', 'S'], ['pokemon', 'marshadow'],
    ['header', 'A'], ['pokemon', 'tyranitar'],
    ['header', 'B'], ['pokemon', 'garchomp'],
  ]);
});

test('typed search drops ability/move/type filter chips and undrafted mons', () => {
  // Typing "gar" in stock Showdown surfaces Garchomp (drafted), Gardevoir (not),
  // plus a "Gunk Shot" move and a "Guts" ability as filter chips.
  const rows = [
    ['pokemon', 'garchomp'], ['pokemon', 'gardevoir'],
    ['move', 'gunkshot'], ['ability', 'guts'], ['type', 'Grass'],
  ];
  assert.deepStrictEqual(filterPokemonResults(rows, ALLOW), [['pokemon', 'garchomp']]);
});

test('a search matching no drafted mon yields nothing to pick', () => {
  const rows = [['pokemon', 'gardevoir'], ['ability', 'guts']];
  assert.deepStrictEqual(filterPokemonResults(rows, ALLOW), []);
});

test('no roster (null allowed) leaves results untouched', () => {
  const rows = [['pokemon', 'gardevoir'], ['ability', 'guts']];
  assert.strictEqual(filterPokemonResults(rows, null), rows);
});
