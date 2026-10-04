'use strict';

// Point the Showdown client shell's script AND stylesheet tags at OUR copy of the
// client instead of hotlinking play.pokemonshowdown.com. Pure, so the tests drive the
// real index-old.html through the same code the server runs (see
// test/client-shell.test.js).
//
// index-old.html ships with most of its assets on `//play.pokemonshowdown.com/...`
// and only a handful (config.js, battledata.js, storage.js, client.js) on our own
// host. That mix is not a pinned version: the CDN is upstream's live deploy, so
// anything they ship runs immediately against our pinned files.
//
// It broke every battle in the league. Upstream rebuilt data/graphics.js around a new
// `eHTML(...)` helper that lives in their battledata.js; ours predates it, so
// BattleScene.reset threw `ReferenceError: eHTML is not defined`, the `new Battle()`
// inside client-battle.js's initialize never returned, and the battle room was never
// added to app.rooms. Both players and every spectator sat on a room that never
// appeared, while the server waited at team preview.
//
// The CSS drifted the same way. Upstream deleted the `.select.teamselect` layout
// (fixed height, nowrap, overflow hidden) and the `.teamselect strong/small/.picon`
// rules, replacing them with `.team.teamselect` ones the NEW client's markup uses.
// Our pinned classic client still renders `<button class="select teamselect">` with
// float:left icons, so those icons had nothing holding them: they wrapped and spilled
// out of the challenge/battle team button.
//
// The checkout under client/ is a complete, self-consistent client (our build made it,
// and patch-client-tiers.js / patch-client-tera.js patch it), so serve all of it and
// leave the CDN what we cannot serve: the images, and the one file it runs as PHP.

const SERVE_LOCALLY = /\.(js|css)$/;

/**
 * @param {string} html            the client shell
 * @param {(p: string) => boolean} hasLocalFile  is this ROOT-relative path on disk?
 * @returns {string} the shell with every locally available .js/.css pointed at our host
 */
function rewriteCdnAssets(html, hasLocalFile) {
  // The quote either side is what keeps this off the absolute
  // `https://play.pokemonshowdown.com/sprites/...` URLs the sprite fix injects into
  // the same string.
  return String(html).replace(/(["'])\/\/play\.pokemonshowdown\.com(\/[^"']*?)\1/g, (whole, quote, file) => {
    const clean = file.split('?')[0];
    // The shell also pulls js/oldclient/clean-cookies.php, which the CDN EXECUTES:
    // our copy is the PHP source, so serving it hands the browser "<?php" to parse
    // as a script. Extension-gating covers that and every image too.
    if (!SERVE_LOCALLY.test(clean) || !hasLocalFile(clean)) return whole;
    return quote + file + quote;
  });
}

module.exports = { rewriteCdnAssets };
