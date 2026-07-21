// Pure, DOM-free Pokemon-icon URL resolution.
//
// The single source of truth for what image a drafted mon shows, and the ORDERED
// FALLBACK CHAIN behind it, so no mon ever renders a broken/missing icon. It is
// loaded two ways (same pattern as pool-logic.js):
//   • the browser loads it as a plain <script> BEFORE app.js, so every export
//     lands on globalThis and app.js uses the names unqualified (spriteUrl,
//     serebiiMega, spriteChain);
//   • the Node test suite require()s it (tests/sprite.test.js).
//
// The chain, in order (applySprite walks it on each load error):
//   1. the primary sprite: a full URL in the sheet's `sprite` field used as-is,
//      else Showdown's gen-5 pixel sprite for the slug;
//   2. serebiiMega: the real Legends Z-A mega ARTWORK, for the newer megas
//      (M-Barbaracle, M-Raichu-X, …) that Showdown has no gen-5 sprite for, so a
//      missing mega shows its actual mega form (needs the mon's national dex);
//   3. the PokeAPI sprite by dex number (a base-form safety net that needs a dex);
//   4. the gen-5 base-form slug (mega suffix stripped) — a LAST-RESORT net that
//      needs NO dex, so even a row that forgot to send one still shows something
//      rather than a broken image.
// Steps 2 and 3 fire only when the row carries a dex; step 4 is what guarantees
// the chain is never empty regardless. A proper mega (step 2) always beats a
// base-form fallback because it comes first.
(function (root, factory) {
  const api = factory();
  if (typeof module !== 'undefined' && module.exports) module.exports = api; // Node
  for (const k in api) root[k] = api[k]; // browser: expose as globals for app.js
})(typeof globalThis !== 'undefined' ? globalThis : this, function () {
  'use strict';

  const SHOWDOWN_GEN5 = (slug) => `https://play.pokemonshowdown.com/sprites/gen5/${slug}.png`;
  const POKEAPI = (dex) => `https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/${dex}.png`;

  // Tolerate both row shapes: pool/pick rows use name/dexNumber, stats/schedule
  // rows use pokemon/dex.
  const dexOf = (mon) => mon.dexNumber ?? mon.dex ?? null;
  const nameOf = (mon) => mon.name || mon.pokemon || '';

  // Prefer the sheet's Showdown slug (megas/regional forms share a dex, so a bare
  // dex lookup shows the wrong sprite); fall back to a dex-based sprite. A full URL
  // in the sprite field is used as-is, letting a specific mon override the default
  // gen-5 sprite (e.g. forms with no good gen-5 art).
  const spriteUrl = (mon) =>
    !mon.sprite
      ? POKEAPI(dexOf(mon))
      : /^https?:\/\//.test(mon.sprite)
        ? mon.sprite
        : SHOWDOWN_GEN5(mon.sprite);

  // Serebii's Legends Z-A artwork URL for a mega form, keyed by national dex +
  // mega suffix (-mx / -my / -mz / -m), or null when the mon isn't a mega (or has
  // no dex to key on). This is the source of the actual mega art for the
  // Champions/Z-A megas Showdown has no gen-5 sprite for, so a missing mega shows
  // its real mega form, never the base forme.
  function serebiiMega(mon) {
    const name = nameOf(mon);
    const dex = dexOf(mon);
    const slug = mon.sprite || '';
    const isMega = name.startsWith('M-') || /-mega[xyz]?$/.test(slug);
    if (!isMega || !dex) return null;
    const suffix = /-X$/.test(name) || /-megax$/.test(slug) ? '-mx'
      : /-Y$/.test(name) || /-megay$/.test(slug) ? '-my'
      : /-Z$/.test(name) || /-megaz$/.test(slug) ? '-mz'
      : '-m';
    return `https://www.serebii.net/legendsz-a/pokemon/${String(dex).padStart(3, '0')}${suffix}.png`;
  }

  // The gen-5 sprite for a mega's BASE form (mega suffix stripped off the slug),
  // or null when there's nothing to derive (a full-URL sprite, no slug, or a
  // non-mega whose primary sprite is already the base). Needs NO dex, so it's the
  // last-resort net that keeps the chain non-empty even for a dex-less row.
  function baseGen5Slug(mon) {
    const slug = mon.sprite || '';
    if (!slug || /^https?:\/\//.test(slug)) return null;
    const base = slug.replace(/-mega[xyz]?$/i, '');
    return base === slug ? null : SHOWDOWN_GEN5(base);
  }

  // The ordered, de-duplicated, always-non-empty list of URLs to try for a mon's
  // icon: primary first, then the mega/dex/base-form fallbacks (see file header).
  function spriteChain(mon) {
    const urls = [spriteUrl(mon), serebiiMega(mon)];
    const dex = dexOf(mon);
    if (dex) urls.push(POKEAPI(dex));
    urls.push(baseGen5Slug(mon));
    const seen = new Set();
    return urls.filter((u) => u && !seen.has(u) && seen.add(u));
  }

  return { spriteUrl, serebiiMega, baseGen5Slug, spriteChain };
});
