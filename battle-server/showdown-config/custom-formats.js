'use strict';

// Custom formats for the draft-league Showdown server.
//
// This repo file is the source of truth. scripts/showdown.js copies it into the
// bundled server's dist/config/custom-formats.js on every start (Showdown loads
// custom formats from there and merges them into the ladder/teambuilder list),
// so our format survives an `npm install` that resets node_modules and we never
// hand-edit anything inside node_modules.
//
// For now the league format is a straight copy of [Gen 9] National Dex Doubles —
// a working placeholder so the teambuilder has our format to build for. The
// ruleset/banlist will be customised for the draft league later.

exports.Formats = [
  {
    section: "Draft League",
    column: 2,
  },
  {
    name: "[Gen 9] Draft League",
    desc: "Placeholder league format — currently a copy of National Dex Doubles. Rules TBD.",
    mod: 'gen9',
    gameType: 'doubles',
    ruleset: ['Standard NatDex', 'OHKO Clause', 'Evasion Moves Clause', 'Evasion Abilities Clause', 'Species Clause', 'Gravity Sleep Clause'],
    banlist: [
      'Annihilape', 'Arceus', 'Calyrex-Ice', 'Calyrex-Shadow', 'Dialga', 'Dialga-Origin', 'Eternatus', 'Genesect', 'Gengar-Mega', 'Giratina', 'Giratina-Origin',
      'Groudon', 'Ho-Oh', 'Koraidon', 'Kyogre', 'Kyurem-White', 'Lugia', 'Lunala', 'Magearna', 'Melmetal', 'Metagross-Mega', 'Mewtwo', 'Miraidon', 'Necrozma-Dawn-Wings',
      'Necrozma-Dusk-Mane', 'Necrozma-Ultra', 'Palkia', 'Palkia-Origin', 'Rayquaza', 'Reshiram', 'Shedinja', 'Solgaleo', 'Stakataka', 'Terapagos', 'Urshifu',
      'Urshifu-Rapid-Strike', 'Xerneas', 'Yveltal', 'Zacian', 'Zacian-Crowned', 'Zamazenta', 'Zamazenta-Crowned', 'Zekrom', 'Zygarde-50%', 'Zygarde-Complete',
      'Commander', 'Power Construct', 'Eevium Z', 'Assist', 'Coaching', 'Dark Void', 'Swagger',
    ],
  },
];
