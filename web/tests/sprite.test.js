'use strict';

const { test } = require('node:test');
const assert = require('node:assert/strict');
const { spriteUrl, serebiiMega, baseGen5Slug, spriteChain } = require('../sprite.js');

// The load-bearing guarantee these guard: EVERY mon gets a non-empty fallback
// chain, so an icon can never render broken/missing (the M-Barbaracle-in-the-
// Schedule regression, where a row without a dex had no fallback at all).

const GEN5 = (s) => `https://play.pokemonshowdown.com/sprites/gen5/${s}.png`;
const POKEAPI = (d) => `https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/${d}.png`;

test('the chain is never empty, even for a mon with no dex (the missing-icon bug)', () => {
  // A custom mega whose gen-5 sprite 404s and whose row forgot to send a dex: the
  // OLD code produced an empty fallback list → broken image. The chain must still
  // offer a real image (the gen-5 base form, which needs no dex).
  const noDex = { name: 'M-Barbaracle', sprite: 'barbaracle-mega' };
  const chain = spriteChain(noDex);
  assert.ok(chain.length >= 2, 'has a primary plus at least one fallback');
  assert.ok(chain.includes(GEN5('barbaracle')), 'falls back to the gen-5 BASE form without needing a dex');
  // Every mon we could ever render must yield a usable chain.
  for (const mon of [
    { name: 'Pikachu', sprite: 'pikachu' },
    { name: 'M-Barbaracle', sprite: 'barbaracle-mega' },
    { pokemon: 'Garchomp', sprite: 'garchomp', dex: 445 },
    { name: 'Missing', sprite: '' },       // no slug at all
  ]) {
    assert.ok(spriteChain(mon).length >= 1, `${mon.name || mon.pokemon} yields a non-empty chain`);
  }
});

test('a custom mega WITH a dex prefers real mega art, then base-form nets', () => {
  const barb = { name: 'M-Barbaracle', sprite: 'barbaracle-mega', dex: 689 };
  const chain = spriteChain(barb);
  // Order matters: primary gen-5 (will 404), THEN Serebii mega art (the good one),
  // then the dex/base-form safety nets. Mega art must come before any base form.
  assert.equal(chain[0], GEN5('barbaracle-mega'), 'primary is the gen-5 mega slug');
  const serebii = serebiiMega(barb);
  assert.ok(serebii && chain.includes(serebii), 'real Serebii mega art is in the chain');
  assert.ok(chain.indexOf(serebii) < chain.indexOf(POKEAPI(689)), 'mega art beats the base-form PokeAPI net');
  assert.ok(chain.includes(POKEAPI(689)) && chain.includes(GEN5('barbaracle')), 'both base-form nets present');
});

test('serebiiMega maps the mega suffix from the name or the slug', () => {
  const u = (dex, sfx) => `https://www.serebii.net/legendsz-a/pokemon/${dex}${sfx}.png`;
  assert.equal(serebiiMega({ name: 'M-Charizard-X', sprite: 'charizard-megax', dex: 6 }), u('006', '-mx'));
  assert.equal(serebiiMega({ name: 'M-Charizard-Y', sprite: 'charizard-megay', dex: 6 }), u('006', '-my'));
  assert.equal(serebiiMega({ pokemon: 'M-Barbaracle', sprite: 'barbaracle-mega', dex: 689 }), u('689', '-m'));
  // Not a mega, or no dex to key on → no Serebii URL.
  assert.equal(serebiiMega({ name: 'Garchomp', sprite: 'garchomp', dex: 445 }), null);
  assert.equal(serebiiMega({ name: 'M-Barbaracle', sprite: 'barbaracle-mega' }), null);
});

test('spriteUrl: full-URL override, gen-5 slug, or dex fallback', () => {
  assert.equal(spriteUrl({ sprite: 'https://example.com/x.png' }), 'https://example.com/x.png');
  assert.equal(spriteUrl({ sprite: 'garchomp' }), GEN5('garchomp'));
  assert.equal(spriteUrl({ sprite: '', dexNumber: 25 }), POKEAPI(25));
  assert.equal(spriteUrl({ sprite: '', dex: 25 }), POKEAPI(25)); // stats/schedule row shape
});

test('a full-URL sprite is used as-is and derives no base-form net', () => {
  const mon = { name: 'M-Whatever', sprite: 'https://cdn.example/mega.png', dex: 42 };
  const chain = spriteChain(mon);
  assert.equal(chain[0], 'https://cdn.example/mega.png');
  assert.equal(baseGen5Slug(mon), null, 'no slug to strip a mega suffix from');
});

test('the chain de-duplicates (a non-mega base equals its primary)', () => {
  const chain = spriteChain({ name: 'Garchomp', sprite: 'garchomp', dex: 445 });
  assert.equal(new Set(chain).size, chain.length, 'no repeated URLs');
});
