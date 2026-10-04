'use strict';

const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const { rewriteCdnAssets } = require('../lib/client-shell');

const ROOT = path.join(__dirname, '..', 'client', 'play.pokemonshowdown.com');
const SHELL = path.join(ROOT, 'index-old.html');
const onDisk = (p) => fs.existsSync(path.join(ROOT, p));
const shell = fs.existsSync(SHELL) ? fs.readFileSync(SHELL, 'utf8') : null;
const at = (attr) => (html, file) => new RegExp(`${attr}="${file.replace(/[.*+?^$()|[\]\\]/g, '\\$&')}`).test(html);
const src = at('src');
const href = at('href');

test('every .js we have is served from our own host', { skip: shell ? false : 'no client checkout' }, () => {
  const out = rewriteCdnAssets(shell, onDisk);
  // graphics.js is the one that broke every battle: upstream's copy calls an eHTML
  // helper our pinned battledata.js does not define.
  assert.ok(src(out, '/data/graphics.js'), 'graphics.js should be same-origin');
  for (const f of ['/js/battle.js', '/data/pokedex.js', '/js/oldclient/client-battle.js', '/js/oldclient/client-teambuilder.js']) {
    assert.ok(src(out, f), `${f} should be same-origin`);
  }
  assert.ok(!/src="\/\/play\.pokemonshowdown\.com\/[^"]*\.js/.test(out.replace(/\/\/play\.pokemonshowdown\.com\/data\/pokedex-mini\.js[^"]*/g, '')),
    'no .js we have should still point at the CDN');
});

// Upstream moved the `.teamselect` layout onto the new client's `.team.teamselect`
// markup, which left our pinned classic client's float:left team icons spilling out
// of the challenge/battle team button.
test('every .css we have is served from our own host', { skip: shell ? false : 'no client checkout' }, () => {
  const out = rewriteCdnAssets(shell, onDisk);
  for (const f of ['/style/oldclient.css', '/style/battle.css', '/style/sim-types.css', '/style/utilichart.css', '/style/font-awesome.css']) {
    assert.ok(href(out, f), `${f} should be same-origin`);
  }
  assert.ok(!/href="\/\/play\.pokemonshowdown\.com\/[^"]*\.css/.test(out), 'no .css we have should still point at the CDN');
});

// build-minidex deletes data/pokedex-mini.js unless the sprite pack is present, so we
// never have it locally. It is generated data with no calls into any helper.
test('a .js we do not have stays on the CDN', () => {
  const html = '<script src="//play.pokemonshowdown.com/data/pokedex-mini.js?"></script>';
  assert.equal(rewriteCdnAssets(html, () => false), html);
});

// The CDN runs this one as PHP. Ours is the PHP source, so serving it would hand the
// browser "<?php" to parse as a script.
test('clean-cookies.php stays on the CDN even though we have the file', () => {
  const html = '<script src="//play.pokemonshowdown.com/js/oldclient/clean-cookies.php"></script>';
  assert.equal(rewriteCdnAssets(html, () => true), html);
});

test('assets that carry no code are left alone', () => {
  const html = '<img src="//play.pokemonshowdown.com/pokemonshowdownbeta.png" ' +
    'srcset="//play.pokemonshowdown.com/pokemonshowdownbeta@2x.png 2x">';
  assert.equal(rewriteCdnAssets(html, () => true), html);
});

// The sprite fix injects absolute https:// URLs into the same string this runs over.
test('absolute CDN urls inside injected script are untouched', () => {
  const html = `<script>var MS={"staraptormega":{"url":"https://play.pokemonshowdown.com/sprites/gen5/staraptor.png"}};</script>`;
  assert.equal(rewriteCdnAssets(html, () => true), html);
});

test('the query string is preserved so cache-busting survives', () => {
  const html = '<script src="//play.pokemonshowdown.com/js/battle.js?v=9"></script>';
  assert.equal(rewriteCdnAssets(html, () => true), '<script src="/js/battle.js?v=9"></script>');
});

test('single-quoted attributes work too, and a quote is required on both sides', () => {
  assert.equal(rewriteCdnAssets(`<script src='//play.pokemonshowdown.com/js/battle.js'>`, () => true),
    `<script src='/js/battle.js'>`);
  assert.equal(rewriteCdnAssets(`<script src="//play.pokemonshowdown.com/js/battle.js'>`, () => true),
    `<script src="//play.pokemonshowdown.com/js/battle.js'>`);
});
