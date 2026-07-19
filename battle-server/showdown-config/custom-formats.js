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
    name: "[Gen 9] NatDex Draft League",
    desc: "National Dex draft league. Megas legal (base form + stone). Custom bans and clauses below.",
    mod: 'gen9',
    // Singles. Built on the National Dex standard so megas / origin formes work.
    ruleset: [
      'Standard NatDex',
      'Dynamax Clause',       // no Dynamax
      'Z-Move Clause',        // no Z-moves
      'Item Clause = 1',      // no two mons with the same item
      'OHKO Clause',          // no Fissure / Sheer Cold / etc.
      'Evasion Moves Clause', // no Double Team / Minimize
    ],
    banlist: [
      'Power Construct',      // ability banned (Zygarde still draftable with other abilities)
      'Swagger',
      'Revival Blessing',
      'Hidden Power',
      // All type gems banned; the plain Normal Gem stays legal.
      'Fire Gem', 'Water Gem', 'Electric Gem', 'Grass Gem', 'Ice Gem', 'Fighting Gem',
      'Poison Gem', 'Ground Gem', 'Flying Gem', 'Psychic Gem', 'Bug Gem', 'Rock Gem',
      'Ghost Gem', 'Dragon Gem', 'Dark Gem', 'Steel Gem', 'Fairy Gem',
    ],
  },
];
