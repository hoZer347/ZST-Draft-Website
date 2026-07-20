'use strict';

// Custom formats for the draft-league Showdown server.
//
// This repo file is the source of truth. scripts/showdown.js copies it into the
// bundled server's dist/config/custom-formats.js on every start, so it survives
// an `npm install` that resets node_modules.
//
// The name contains "NatDex" on purpose: the teambuilder client keys its tier
// table + mega legality off the format id (it must include natdex/nationaldex),
// which routes it to the gen9natdex data — where megas are legal and where
// scripts/patch-client-tiers.js has written our X/S/A/B/C/Z tiers.

exports.Formats = [
  {
    section: "Draft League",
    column: 2,
  },
  {
    name: "[Gen 9] ZST Season 4",
    desc: "National Dex Doubles draft league. Megas legal (base form + stone). Custom bans and clauses below.",
    mod: 'gen9',
    // Doubles (2v2, bring 4). Built on the National Dex standard so megas /
    // origin formes work; gameType switches it from singles to doubles.
    gameType: 'doubles',
    ruleset: [
      'Standard NatDex',
      'Sleep Clause Mod',     // only one of a side's mons may be asleep from an opponent at a time
      'Dynamax Clause',       // no Dynamax
      'Z-Move Clause',        // no Z-moves
      'Item Clause = 1',      // no two mons with the same item
      'OHKO Clause',          // no Fissure / Sheer Cold / etc.
      'Evasion Clause',       // bans evasion abilities (Sand Veil, Snow Cloak), items (Bright Powder, Lax Incense) AND moves (Double Team / Minimize). Broader than Evasion Moves Clause, which it includes.
      'Swagger Clause',       // bans the move Swagger (replaces the explicit banlist entry below)
    ],
    banlist: [
      'Power Construct',      // ability banned (Zygarde still draftable with other abilities)
      'Revival Blessing',
      'Hidden Power',
      // Zippy Zap (+1 evasion via a 100% self-secondary) is NOT covered by the
      // Evasion Clause, so ban it explicitly. Double Team / Minimize are already
      // banned BY that clause (via its Evasion Moves Clause) — listing them here
      // too is a duplicate that Showdown rejects with "Rule already exists in
      // Evasion Moves Clause", which crashes the server worker on load. Do not
      // re-add them here.
      'Zippy Zap',
      // All type gems banned; the plain Normal Gem stays legal.
      'Fire Gem', 'Water Gem', 'Electric Gem', 'Grass Gem', 'Ice Gem', 'Fighting Gem',
      'Poison Gem', 'Ground Gem', 'Flying Gem', 'Psychic Gem', 'Bug Gem', 'Rock Gem',
      'Ghost Gem', 'Dragon Gem', 'Dark Gem', 'Steel Gem', 'Fairy Gem',
    ],
  },
];
