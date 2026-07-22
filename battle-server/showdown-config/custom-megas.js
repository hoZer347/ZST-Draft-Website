'use strict';

// AUTO-GENERATED (do not hand-edit). The league's custom "megas" (ChampionsRegMA
// content) as PLAIN gen-9 mega data: species + string-format stones + a formats-data
// row, so the stock engine's own canMegaEvo evolves them with NO sim/ruleset/format
// changes. scripts/showdown.js merges these additively into the bundled engine on
// start (surviving npm install). A few megas whose custom ability effect is not ported
// fall back to their base form's vanilla abilities. Compound sub-forme megas
// (Meowstic-M/F, Tatsugiri-*, Magearna-Original) are omitted: their formes are not
// "Mega", so the stock engine cannot treat them as megas.

exports.Pokedex = {
  "raichumegax": {
    "num": 26,
    "name": "Raichu-Mega-X",
    "baseSpecies": "Raichu",
    "forme": "Mega-X",
    "types": [
      "Electric"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 60,
      "atk": 135,
      "def": 95,
      "spa": 90,
      "spd": 95,
      "spe": 110
    },
    "abilities": {
      "0": "Electric Surge"
    },
    "heightm": 1.2,
    "weightkg": 38,
    "color": "Yellow",
    "eggGroups": [
      "Field",
      "Fairy"
    ],
    "requiredItem": "Raichunite X",
    "isNonstandard": null,
    "gen": 9
  },
  "raichumegay": {
    "num": 26,
    "name": "Raichu-Mega-Y",
    "baseSpecies": "Raichu",
    "forme": "Mega-Y",
    "types": [
      "Electric"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 60,
      "atk": 100,
      "def": 55,
      "spa": 160,
      "spd": 80,
      "spe": 130
    },
    "abilities": {
      "0": "No Guard"
    },
    "heightm": 1,
    "weightkg": 26,
    "color": "Yellow",
    "eggGroups": [
      "Field",
      "Fairy"
    ],
    "requiredItem": "Raichunite Y",
    "isNonstandard": null,
    "gen": 9
  },
  "clefablemega": {
    "num": 36,
    "name": "Clefable-Mega",
    "baseSpecies": "Clefable",
    "forme": "Mega",
    "types": [
      "Fairy",
      "Flying"
    ],
    "genderRatio": {
      "M": 0.25,
      "F": 0.75
    },
    "baseStats": {
      "hp": 95,
      "atk": 80,
      "def": 93,
      "spa": 135,
      "spd": 110,
      "spe": 70
    },
    "abilities": {
      "0": "Magic Bounce"
    },
    "heightm": 1.7,
    "weightkg": 42.3,
    "color": "Pink",
    "eggGroups": [
      "Field"
    ],
    "requiredItem": "Clefablite",
    "isNonstandard": null,
    "gen": 9
  },
  "victreebelmega": {
    "num": 71,
    "name": "Victreebel-Mega",
    "baseSpecies": "Victreebel",
    "forme": "Mega",
    "types": [
      "Grass",
      "Poison"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 80,
      "atk": 125,
      "def": 85,
      "spa": 135,
      "spd": 95,
      "spe": 70
    },
    "abilities": {
      "0": "Innards Out"
    },
    "heightm": 4.5,
    "weightkg": 125.5,
    "color": "Green",
    "eggGroups": [
      "Grass"
    ],
    "requiredItem": "Victreebelite",
    "isNonstandard": null,
    "gen": 9
  },
  "starmiemega": {
    "num": 121,
    "name": "Starmie-Mega",
    "baseSpecies": "Starmie",
    "forme": "Mega",
    "types": [
      "Water",
      "Psychic"
    ],
    "genderRatio": {
      "M": 0,
      "F": 0
    },
    "baseStats": {
      "hp": 60,
      "atk": 100,
      "def": 105,
      "spa": 130,
      "spd": 105,
      "spe": 120
    },
    "abilities": {
      "0": "Huge Power"
    },
    "heightm": 2.3,
    "weightkg": 80,
    "color": "Purple",
    "eggGroups": [
      "Water 3"
    ],
    "requiredItem": "Starminite",
    "isNonstandard": null,
    "gen": 9
  },
  "dragonitemega": {
    "num": 149,
    "name": "Dragonite-Mega",
    "baseSpecies": "Dragonite",
    "forme": "Mega",
    "types": [
      "Dragon",
      "Flying"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 91,
      "atk": 124,
      "def": 115,
      "spa": 145,
      "spd": 125,
      "spe": 100
    },
    "abilities": {
      "0": "Multiscale"
    },
    "heightm": 2.2,
    "weightkg": 290,
    "color": "Brown",
    "eggGroups": [
      "Water 1",
      "Dragon"
    ],
    "requiredItem": "Dragoninite",
    "isNonstandard": null,
    "gen": 9
  },
  "meganiummega": {
    "num": 154,
    "name": "Meganium-Mega",
    "baseSpecies": "Meganium",
    "forme": "Mega",
    "types": [
      "Grass",
      "Fairy"
    ],
    "genderRatio": {
      "M": 0.875,
      "F": 0.125
    },
    "baseStats": {
      "hp": 80,
      "atk": 92,
      "def": 115,
      "spa": 143,
      "spd": 115,
      "spe": 80
    },
    "abilities": {
      "0": "Overgrow",
      "H": "Leaf Guard"
    },
    "heightm": 2.4,
    "weightkg": 201,
    "color": "Green",
    "eggGroups": [
      "Monster",
      "Grass"
    ],
    "requiredItem": "Meganiumite",
    "isNonstandard": null,
    "gen": 9
  },
  "feraligatrmega": {
    "num": 160,
    "name": "Feraligatr-Mega",
    "baseSpecies": "Feraligatr",
    "forme": "Mega",
    "types": [
      "Water",
      "Dragon"
    ],
    "genderRatio": {
      "M": 0.875,
      "F": 0.125
    },
    "baseStats": {
      "hp": 85,
      "atk": 160,
      "def": 125,
      "spa": 89,
      "spd": 93,
      "spe": 78
    },
    "abilities": {
      "0": "Torrent",
      "H": "Sheer Force"
    },
    "heightm": 2.3,
    "weightkg": 108.8,
    "color": "Blue",
    "eggGroups": [
      "Monster",
      "Water 1"
    ],
    "requiredItem": "Feraligite",
    "isNonstandard": null,
    "gen": 9
  },
  "skarmorymega": {
    "num": 227,
    "name": "Skarmory-Mega",
    "baseSpecies": "Skarmory",
    "forme": "Mega",
    "types": [
      "Steel",
      "Flying"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 65,
      "atk": 140,
      "def": 110,
      "spa": 40,
      "spd": 100,
      "spe": 110
    },
    "abilities": {
      "0": "Stalwart"
    },
    "heightm": 1.7,
    "weightkg": 40.4,
    "color": "Gray",
    "eggGroups": [
      "Flying"
    ],
    "requiredItem": "Skarmorite",
    "isNonstandard": null,
    "gen": 9
  },
  "chimechomega": {
    "num": 358,
    "name": "Chimecho-Mega",
    "baseSpecies": "Chimecho",
    "forme": "Mega",
    "types": [
      "Psychic",
      "Steel"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 75,
      "atk": 50,
      "def": 110,
      "spa": 135,
      "spd": 120,
      "spe": 65
    },
    "abilities": {
      "0": "Levitate"
    },
    "heightm": 1.2,
    "weightkg": 8,
    "color": "Blue",
    "eggGroups": [
      "Amorphous"
    ],
    "requiredItem": "Chimechite",
    "isNonstandard": null,
    "gen": 9
  },
  "absolmegaz": {
    "num": 359,
    "name": "Absol-Mega-Z",
    "baseSpecies": "Absol",
    "forme": "Mega-Z",
    "types": [
      "Dark",
      "Ghost"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 65,
      "atk": 154,
      "def": 60,
      "spa": 75,
      "spd": 60,
      "spe": 151
    },
    "abilities": {
      "0": "Magic Bounce"
    },
    "heightm": 1.2,
    "weightkg": 49,
    "color": "Black",
    "eggGroups": [
      "Field"
    ],
    "requiredItem": "Absolite Z",
    "isNonstandard": null,
    "gen": 9
  },
  "staraptormega": {
    "num": 398,
    "name": "Staraptor-Mega",
    "baseSpecies": "Staraptor",
    "forme": "Mega",
    "types": [
      "Fighting",
      "Flying"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 85,
      "atk": 140,
      "def": 100,
      "spa": 60,
      "spd": 90,
      "spe": 110
    },
    "abilities": {
      "0": "Contrary"
    },
    "heightm": 1.9,
    "weightkg": 50,
    "color": "Gray",
    "eggGroups": [
      "Flying"
    ],
    "requiredItem": "Staraptite",
    "isNonstandard": null,
    "gen": 9
  },
  "garchompmegaz": {
    "num": 445,
    "name": "Garchomp-Mega-Z",
    "baseSpecies": "Garchomp",
    "forme": "Mega-Z",
    "types": [
      "Dragon"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 108,
      "atk": 130,
      "def": 85,
      "spa": 141,
      "spd": 85,
      "spe": 151
    },
    "abilities": {
      "0": "Sand Force"
    },
    "heightm": 1.9,
    "weightkg": 99,
    "color": "Blue",
    "eggGroups": [
      "Monster",
      "Dragon"
    ],
    "requiredItem": "Garchompite Z",
    "isNonstandard": null,
    "gen": 9
  },
  "lucariomegaz": {
    "num": 448,
    "name": "Lucario-Mega-Z",
    "baseSpecies": "Lucario",
    "forme": "Mega-Z",
    "types": [
      "Fighting",
      "Steel"
    ],
    "genderRatio": {
      "M": 0.875,
      "F": 0.125
    },
    "baseStats": {
      "hp": 70,
      "atk": 100,
      "def": 70,
      "spa": 164,
      "spd": 70,
      "spe": 151
    },
    "abilities": {
      "0": "Adaptability"
    },
    "heightm": 1.3,
    "weightkg": 49.4,
    "color": "Gray",
    "eggGroups": [
      "Field",
      "Human-Like"
    ],
    "requiredItem": "Lucarionite Z",
    "isNonstandard": null,
    "gen": 9
  },
  "froslassmega": {
    "num": 478,
    "name": "Froslass-Mega",
    "baseSpecies": "Froslass",
    "forme": "Mega",
    "types": [
      "Ice",
      "Ghost"
    ],
    "genderRatio": {
      "M": 0,
      "F": 1
    },
    "baseStats": {
      "hp": 70,
      "atk": 80,
      "def": 70,
      "spa": 140,
      "spd": 100,
      "spe": 120
    },
    "abilities": {
      "0": "Snow Warning"
    },
    "heightm": 2.6,
    "weightkg": 29.6,
    "color": "White",
    "eggGroups": [
      "Fairy",
      "Mineral"
    ],
    "requiredItem": "Froslassite",
    "isNonstandard": null,
    "gen": 9
  },
  "heatranmega": {
    "num": 485,
    "name": "Heatran-Mega",
    "baseSpecies": "Heatran",
    "forme": "Mega",
    "types": [
      "Fire",
      "Steel"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 91,
      "atk": 120,
      "def": 106,
      "spa": 175,
      "spd": 141,
      "spe": 67
    },
    "abilities": {
      "0": "Flash Fire",
      "H": "Flame Body"
    },
    "heightm": 2.8,
    "weightkg": 570,
    "color": "Brown",
    "eggGroups": [
      "Undiscovered"
    ],
    "requiredItem": "Heatranite",
    "isNonstandard": null,
    "gen": 9
  },
  "darkraimega": {
    "num": 491,
    "name": "Darkrai-Mega",
    "baseSpecies": "Darkrai",
    "forme": "Mega",
    "types": [
      "Dark"
    ],
    "genderRatio": {
      "M": 0,
      "F": 0
    },
    "baseStats": {
      "hp": 70,
      "atk": 120,
      "def": 130,
      "spa": 165,
      "spd": 130,
      "spe": 85
    },
    "abilities": {
      "0": "Bad Dreams"
    },
    "heightm": 3,
    "weightkg": 240,
    "color": "Black",
    "eggGroups": [
      "Undiscovered"
    ],
    "requiredItem": "Darkranite",
    "isNonstandard": null,
    "gen": 9
  },
  "emboarmega": {
    "num": 500,
    "name": "Emboar-Mega",
    "baseSpecies": "Emboar",
    "forme": "Mega",
    "types": [
      "Fire",
      "Fighting"
    ],
    "genderRatio": {
      "M": 0.875,
      "F": 0.125
    },
    "baseStats": {
      "hp": 110,
      "atk": 148,
      "def": 75,
      "spa": 110,
      "spd": 110,
      "spe": 75
    },
    "abilities": {
      "0": "Mold Breaker"
    },
    "heightm": 1.8,
    "weightkg": 180.3,
    "color": "Red",
    "eggGroups": [
      "Field"
    ],
    "requiredItem": "Emboarite",
    "isNonstandard": null,
    "gen": 9
  },
  "excadrillmega": {
    "num": 530,
    "name": "Excadrill-Mega",
    "baseSpecies": "Excadrill",
    "forme": "Mega",
    "types": [
      "Ground",
      "Steel"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 110,
      "atk": 165,
      "def": 100,
      "spa": 65,
      "spd": 65,
      "spe": 103
    },
    "abilities": {
      "0": "Sand Rush",
      "1": "Sand Force",
      "H": "Mold Breaker"
    },
    "heightm": 0.9,
    "weightkg": 60,
    "color": "Gray",
    "eggGroups": [
      "Field"
    ],
    "requiredItem": "Excadrite",
    "isNonstandard": null,
    "gen": 9
  },
  "scolipedemega": {
    "num": 545,
    "name": "Scolipede-Mega",
    "baseSpecies": "Scolipede",
    "forme": "Mega",
    "types": [
      "Bug",
      "Poison"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 60,
      "atk": 140,
      "def": 149,
      "spa": 75,
      "spd": 99,
      "spe": 62
    },
    "abilities": {
      "0": "Shell Armor"
    },
    "heightm": 3.2,
    "weightkg": 230.5,
    "color": "Red",
    "eggGroups": [
      "Bug"
    ],
    "requiredItem": "Scolipite",
    "isNonstandard": null,
    "gen": 9
  },
  "scraftymega": {
    "num": 560,
    "name": "Scrafty-Mega",
    "baseSpecies": "Scrafty",
    "forme": "Mega",
    "types": [
      "Dark",
      "Fighting"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 65,
      "atk": 130,
      "def": 135,
      "spa": 55,
      "spd": 135,
      "spe": 68
    },
    "abilities": {
      "0": "Intimidate"
    },
    "heightm": 1.1,
    "weightkg": 31,
    "color": "Red",
    "eggGroups": [
      "Field",
      "Dragon"
    ],
    "requiredItem": "Scraftinite",
    "isNonstandard": null,
    "gen": 9
  },
  "eelektrossmega": {
    "num": 604,
    "name": "Eelektross-Mega",
    "baseSpecies": "Eelektross",
    "forme": "Mega",
    "types": [
      "Electric"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 85,
      "atk": 145,
      "def": 80,
      "spa": 135,
      "spd": 90,
      "spe": 80
    },
    "abilities": {
      "0": "Levitate"
    },
    "heightm": 3,
    "weightkg": 180,
    "color": "Blue",
    "eggGroups": [
      "Amorphous"
    ],
    "requiredItem": "Eelektrossite",
    "isNonstandard": null,
    "gen": 9
  },
  "chandeluremega": {
    "num": 609,
    "name": "Chandelure-Mega",
    "baseSpecies": "Chandelure",
    "forme": "Mega",
    "types": [
      "Ghost",
      "Fire"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 60,
      "atk": 75,
      "def": 110,
      "spa": 175,
      "spd": 110,
      "spe": 90
    },
    "abilities": {
      "0": "Infiltrator"
    },
    "heightm": 2.5,
    "weightkg": 69.6,
    "color": "Black",
    "eggGroups": [
      "Amorphous"
    ],
    "requiredItem": "Chandelurite",
    "isNonstandard": null,
    "gen": 9
  },
  "golurkmega": {
    "num": 623,
    "name": "Golurk-Mega",
    "baseSpecies": "Golurk",
    "forme": "Mega",
    "types": [
      "Ground",
      "Ghost"
    ],
    "genderRatio": {
      "M": 0,
      "F": 0
    },
    "baseStats": {
      "hp": 89,
      "atk": 159,
      "def": 105,
      "spa": 70,
      "spd": 105,
      "spe": 55
    },
    "abilities": {
      "0": "Unseen Fist"
    },
    "heightm": 4,
    "weightkg": 330,
    "color": "Green",
    "eggGroups": [
      "Mineral"
    ],
    "requiredItem": "Golurkite",
    "isNonstandard": null,
    "gen": 9
  },
  "chesnaughtmega": {
    "num": 652,
    "name": "Chesnaught-Mega",
    "baseSpecies": "Chesnaught",
    "forme": "Mega",
    "types": [
      "Grass",
      "Fighting"
    ],
    "genderRatio": {
      "M": 0.875,
      "F": 0.125
    },
    "baseStats": {
      "hp": 88,
      "atk": 137,
      "def": 172,
      "spa": 74,
      "spd": 115,
      "spe": 44
    },
    "abilities": {
      "0": "Bulletproof"
    },
    "heightm": 1.6,
    "weightkg": 90,
    "color": "Green",
    "eggGroups": [
      "Field"
    ],
    "requiredItem": "Chesnaughtite",
    "isNonstandard": null,
    "gen": 9
  },
  "delphoxmega": {
    "num": 655,
    "name": "Delphox-Mega",
    "baseSpecies": "Delphox",
    "forme": "Mega",
    "types": [
      "Fire",
      "Psychic"
    ],
    "genderRatio": {
      "M": 0.875,
      "F": 0.125
    },
    "baseStats": {
      "hp": 75,
      "atk": 69,
      "def": 72,
      "spa": 159,
      "spd": 125,
      "spe": 134
    },
    "abilities": {
      "0": "Levitate"
    },
    "heightm": 1.5,
    "weightkg": 39,
    "color": "Red",
    "eggGroups": [
      "Field"
    ],
    "requiredItem": "Delphoxite",
    "isNonstandard": null,
    "gen": 9
  },
  "greninjamega": {
    "num": 658,
    "name": "Greninja-Mega",
    "baseSpecies": "Greninja",
    "forme": "Mega",
    "types": [
      "Water",
      "Dark"
    ],
    "genderRatio": {
      "M": 0.875,
      "F": 0.125
    },
    "baseStats": {
      "hp": 72,
      "atk": 125,
      "def": 77,
      "spa": 133,
      "spd": 81,
      "spe": 142
    },
    "abilities": {
      "0": "Protean"
    },
    "heightm": 1.5,
    "weightkg": 40,
    "color": "Blue",
    "eggGroups": [
      "Water 1"
    ],
    "requiredItem": "Greninjite",
    "isNonstandard": null,
    "gen": 9
  },
  "pyroarmega": {
    "num": 668,
    "name": "Pyroar-Mega",
    "baseSpecies": "Pyroar",
    "forme": "Mega",
    "types": [
      "Fire",
      "Normal"
    ],
    "genderRatio": {
      "M": 0.125,
      "F": 0.875
    },
    "baseStats": {
      "hp": 86,
      "atk": 88,
      "def": 92,
      "spa": 129,
      "spd": 86,
      "spe": 126
    },
    "abilities": {
      "0": "Rivalry",
      "1": "Unnerve",
      "H": "Moxie"
    },
    "heightm": 1.5,
    "weightkg": 93.3,
    "color": "Brown",
    "eggGroups": [
      "Field"
    ],
    "requiredItem": "Pyroarite",
    "isNonstandard": null,
    "gen": 9
  },
  "floettemega": {
    "num": 670,
    "name": "Floette-Mega",
    "baseSpecies": "Floette",
    "battleOnly": "Floette",
    "forme": "Mega",
    "types": [
      "Fairy"
    ],
    "genderRatio": {
      "M": 0,
      "F": 1
    },
    "baseStats": {
      "hp": 74,
      "atk": 85,
      "def": 87,
      "spa": 155,
      "spd": 148,
      "spe": 102
    },
    "abilities": {
      "0": "Fairy Aura"
    },
    "heightm": 0.2,
    "weightkg": 100.8,
    "color": "White",
    "eggGroups": [
      "Undiscovered"
    ],
    "requiredItem": "Floettite",
    "isNonstandard": null,
    "gen": 9
  },
  "malamarmega": {
    "num": 687,
    "name": "Malamar-Mega",
    "baseSpecies": "Malamar",
    "forme": "Mega",
    "types": [
      "Dark",
      "Psychic"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 86,
      "atk": 102,
      "def": 88,
      "spa": 98,
      "spd": 120,
      "spe": 88
    },
    "abilities": {
      "0": "Contrary"
    },
    "heightm": 2.9,
    "weightkg": 69.8,
    "color": "Blue",
    "eggGroups": [
      "Water 1",
      "Water 2"
    ],
    "requiredItem": "Malamarite",
    "isNonstandard": null,
    "gen": 9
  },
  "barbaraclemega": {
    "num": 689,
    "name": "Barbaracle-Mega",
    "baseSpecies": "Barbaracle",
    "forme": "Mega",
    "types": [
      "Rock",
      "Fighting"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 72,
      "atk": 140,
      "def": 130,
      "spa": 64,
      "spd": 106,
      "spe": 88
    },
    "abilities": {
      "0": "Tough Claws"
    },
    "heightm": 2.2,
    "weightkg": 100,
    "color": "Brown",
    "eggGroups": [
      "Water 3"
    ],
    "requiredItem": "Barbaracite",
    "isNonstandard": null,
    "gen": 9
  },
  "dragalgemega": {
    "num": 691,
    "name": "Dragalge-Mega",
    "baseSpecies": "Dragalge",
    "forme": "Mega",
    "types": [
      "Poison",
      "Dragon"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 65,
      "atk": 85,
      "def": 105,
      "spa": 132,
      "spd": 163,
      "spe": 44
    },
    "abilities": {
      "0": "Regenerator"
    },
    "heightm": 2.1,
    "weightkg": 100.3,
    "color": "Brown",
    "eggGroups": [
      "Water 1",
      "Dragon"
    ],
    "requiredItem": "Dragalgite",
    "isNonstandard": null,
    "gen": 9
  },
  "hawluchamega": {
    "num": 701,
    "name": "Hawlucha-Mega",
    "baseSpecies": "Hawlucha",
    "forme": "Mega",
    "types": [
      "Fighting",
      "Flying"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 78,
      "atk": 137,
      "def": 100,
      "spa": 74,
      "spd": 93,
      "spe": 118
    },
    "abilities": {
      "0": "No Guard"
    },
    "heightm": 1,
    "weightkg": 25,
    "color": "Green",
    "eggGroups": [
      "Flying",
      "Human-Like"
    ],
    "requiredItem": "Hawluchanite",
    "isNonstandard": null,
    "gen": 9
  },
  "zygardemega": {
    "num": 718,
    "name": "Zygarde-Mega",
    "baseSpecies": "Zygarde",
    "forme": "Mega",
    "types": [
      "Dragon",
      "Ground"
    ],
    "genderRatio": {
      "M": 0,
      "F": 0
    },
    "baseStats": {
      "hp": 216,
      "atk": 70,
      "def": 91,
      "spa": 216,
      "spd": 85,
      "spe": 100
    },
    "abilities": {
      "0": "Aura Break"
    },
    "heightm": 7.7,
    "weightkg": 610,
    "color": "Green",
    "eggGroups": [
      "Undiscovered"
    ],
    "requiredItem": "Zygardite",
    "isNonstandard": null,
    "gen": 9
  },
  "crabominablemega": {
    "num": 740,
    "name": "Crabominable-Mega",
    "baseSpecies": "Crabominable",
    "forme": "Mega",
    "types": [
      "Fighting",
      "Ice"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 97,
      "atk": 157,
      "def": 122,
      "spa": 62,
      "spd": 107,
      "spe": 33
    },
    "abilities": {
      "0": "Iron Fist"
    },
    "heightm": 2.6,
    "weightkg": 252.8,
    "color": "White",
    "eggGroups": [
      "Water 3"
    ],
    "requiredItem": "Crabominite",
    "isNonstandard": null,
    "gen": 9
  },
  "golisopodmega": {
    "num": 768,
    "name": "Golisopod-Mega",
    "baseSpecies": "Golisopod",
    "forme": "Mega",
    "types": [
      "Bug",
      "Steel"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 75,
      "atk": 150,
      "def": 175,
      "spa": 70,
      "spd": 120,
      "spe": 40
    },
    "abilities": {
      "0": "Emergency Exit"
    },
    "heightm": 2.3,
    "weightkg": 148,
    "color": "Gray",
    "eggGroups": [
      "Bug",
      "Water 3"
    ],
    "requiredItem": "Golisopite",
    "isNonstandard": null,
    "gen": 9
  },
  "drampamega": {
    "num": 780,
    "name": "Drampa-Mega",
    "baseSpecies": "Drampa",
    "forme": "Mega",
    "types": [
      "Normal",
      "Dragon"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 78,
      "atk": 85,
      "def": 110,
      "spa": 160,
      "spd": 116,
      "spe": 36
    },
    "abilities": {
      "0": "Berserk"
    },
    "heightm": 3,
    "weightkg": 240.5,
    "color": "White",
    "eggGroups": [
      "Monster",
      "Dragon"
    ],
    "requiredItem": "Drampanite",
    "isNonstandard": null,
    "gen": 9
  },
  "magearnamega": {
    "num": 801,
    "name": "Magearna-Mega",
    "baseSpecies": "Magearna",
    "forme": "Mega",
    "types": [
      "Steel",
      "Fairy"
    ],
    "genderRatio": {
      "M": 0,
      "F": 0
    },
    "baseStats": {
      "hp": 80,
      "atk": 125,
      "def": 115,
      "spa": 170,
      "spd": 115,
      "spe": 95
    },
    "abilities": {
      "0": "Soul-Heart"
    },
    "heightm": 1.3,
    "weightkg": 248.1,
    "color": "Gray",
    "eggGroups": [
      "Undiscovered"
    ],
    "requiredItem": "Magearnite",
    "isNonstandard": null,
    "gen": 9
  },
  "zeraoramega": {
    "num": 807,
    "name": "Zeraora-Mega",
    "baseSpecies": "Zeraora",
    "forme": "Mega",
    "types": [
      "Electric"
    ],
    "genderRatio": {
      "M": 0,
      "F": 0
    },
    "baseStats": {
      "hp": 88,
      "atk": 157,
      "def": 75,
      "spa": 147,
      "spd": 80,
      "spe": 153
    },
    "abilities": {
      "0": "Volt Absorb"
    },
    "heightm": 1.5,
    "weightkg": 44.5,
    "color": "Yellow",
    "eggGroups": [
      "Undiscovered"
    ],
    "requiredItem": "Zeraorite",
    "isNonstandard": null,
    "gen": 9
  },
  "falinksmega": {
    "num": 870,
    "name": "Falinks-Mega",
    "baseSpecies": "Falinks",
    "forme": "Mega",
    "types": [
      "Fighting"
    ],
    "genderRatio": {
      "M": 0,
      "F": 0
    },
    "baseStats": {
      "hp": 65,
      "atk": 135,
      "def": 135,
      "spa": 70,
      "spd": 65,
      "spe": 100
    },
    "abilities": {
      "0": "Defiant"
    },
    "heightm": 1.6,
    "weightkg": 99,
    "color": "Yellow",
    "eggGroups": [
      "Fairy",
      "Mineral"
    ],
    "requiredItem": "Falinksite",
    "isNonstandard": null,
    "gen": 9
  },
  "scovillainmega": {
    "num": 952,
    "name": "Scovillain-Mega",
    "baseSpecies": "Scovillain",
    "forme": "Mega",
    "types": [
      "Grass",
      "Fire"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 65,
      "atk": 138,
      "def": 85,
      "spa": 138,
      "spd": 85,
      "spe": 75
    },
    "abilities": {
      "0": "Chlorophyll",
      "1": "Insomnia",
      "H": "Moody"
    },
    "heightm": 1.2,
    "weightkg": 22,
    "color": "Green",
    "eggGroups": [
      "Grass"
    ],
    "requiredItem": "Scovillainite",
    "isNonstandard": null,
    "gen": 9
  },
  "glimmoramega": {
    "num": 970,
    "name": "Glimmora-Mega",
    "baseSpecies": "Glimmora",
    "forme": "Mega",
    "types": [
      "Rock",
      "Poison"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 83,
      "atk": 90,
      "def": 105,
      "spa": 150,
      "spd": 96,
      "spe": 101
    },
    "abilities": {
      "0": "Adaptability"
    },
    "heightm": 2.8,
    "weightkg": 77,
    "color": "Blue",
    "eggGroups": [
      "Mineral"
    ],
    "requiredItem": "Glimmoranite",
    "isNonstandard": null,
    "gen": 9
  },
  "baxcaliburmega": {
    "num": 998,
    "name": "Baxcalibur-Mega",
    "baseSpecies": "Baxcalibur",
    "forme": "Mega",
    "types": [
      "Dragon",
      "Ice"
    ],
    "genderRatio": {
      "M": 0.5,
      "F": 0.5
    },
    "baseStats": {
      "hp": 115,
      "atk": 175,
      "def": 117,
      "spa": 105,
      "spd": 101,
      "spe": 87
    },
    "abilities": {
      "0": "Thermal Exchange",
      "H": "Ice Body"
    },
    "heightm": 2.1,
    "weightkg": 315,
    "color": "Blue",
    "eggGroups": [
      "Dragon",
      "Mineral"
    ],
    "requiredItem": "Baxcalibrite",
    "isNonstandard": null,
    "gen": 9
  }
};

exports.Items = {
  "raichunitex": {
    "name": "Raichunite X",
    "spritenum": 496,
    "megaStone": "Raichu-Mega-X",
    "megaEvolves": "Raichu",
    "itemUser": [
      "Raichu"
    ],
    "gen": 9,
    "num": 2635
  },
  "raichunitey": {
    "name": "Raichunite Y",
    "spritenum": 497,
    "megaStone": "Raichu-Mega-Y",
    "megaEvolves": "Raichu",
    "itemUser": [
      "Raichu"
    ],
    "gen": 9,
    "num": 2636
  },
  "clefablite": {
    "name": "Clefablite",
    "spritenum": 544,
    "megaStone": "Clefable-Mega",
    "megaEvolves": "Clefable",
    "itemUser": [
      "Clefable"
    ],
    "gen": 9,
    "num": 2559
  },
  "victreebelite": {
    "name": "Victreebelite",
    "spritenum": 545,
    "megaStone": "Victreebel-Mega",
    "megaEvolves": "Victreebel",
    "itemUser": [
      "Victreebel"
    ],
    "gen": 9,
    "num": 2560
  },
  "starminite": {
    "name": "Starminite",
    "spritenum": 546,
    "megaStone": "Starmie-Mega",
    "megaEvolves": "Starmie",
    "itemUser": [
      "Starmie"
    ],
    "gen": 9,
    "num": 2561
  },
  "dragoninite": {
    "name": "Dragoninite",
    "spritenum": 547,
    "megaStone": "Dragonite-Mega",
    "megaEvolves": "Dragonite",
    "itemUser": [
      "Dragonite"
    ],
    "gen": 9,
    "num": 2562
  },
  "meganiumite": {
    "name": "Meganiumite",
    "spritenum": 548,
    "megaStone": "Meganium-Mega",
    "megaEvolves": "Meganium",
    "itemUser": [
      "Meganium"
    ],
    "gen": 9,
    "num": 2563
  },
  "feraligite": {
    "name": "Feraligite",
    "spritenum": 549,
    "megaStone": "Feraligatr-Mega",
    "megaEvolves": "Feraligatr",
    "itemUser": [
      "Feraligatr"
    ],
    "gen": 9,
    "num": 2564
  },
  "skarmorite": {
    "name": "Skarmorite",
    "spritenum": 550,
    "megaStone": "Skarmory-Mega",
    "megaEvolves": "Skarmory",
    "itemUser": [
      "Skarmory"
    ],
    "gen": 9,
    "num": 2565
  },
  "chimechite": {
    "name": "Chimechite",
    "spritenum": 498,
    "megaStone": "Chimecho-Mega",
    "megaEvolves": "Chimecho",
    "itemUser": [
      "Chimecho"
    ],
    "gen": 9,
    "num": 2637
  },
  "absolitez": {
    "name": "Absolite Z",
    "spritenum": 499,
    "megaStone": "Absol-Mega-Z",
    "megaEvolves": "Absol",
    "itemUser": [
      "Absol"
    ],
    "gen": 9,
    "num": 2638
  },
  "staraptite": {
    "name": "Staraptite",
    "spritenum": 500,
    "megaStone": "Staraptor-Mega",
    "megaEvolves": "Staraptor",
    "itemUser": [
      "Staraptor"
    ],
    "gen": 9,
    "num": 2639
  },
  "garchompitez": {
    "name": "Garchompite Z",
    "spritenum": 501,
    "megaStone": "Garchomp-Mega-Z",
    "megaEvolves": "Garchomp",
    "itemUser": [
      "Garchomp"
    ],
    "gen": 9,
    "num": 2640
  },
  "lucarionitez": {
    "name": "Lucarionite Z",
    "spritenum": 502,
    "megaStone": "Lucario-Mega-Z",
    "megaEvolves": "Lucario",
    "itemUser": [
      "Lucario"
    ],
    "gen": 9,
    "num": 2641
  },
  "froslassite": {
    "name": "Froslassite",
    "spritenum": 551,
    "megaStone": "Froslass-Mega",
    "megaEvolves": "Froslass",
    "itemUser": [
      "Froslass"
    ],
    "gen": 9,
    "num": 2566
  },
  "heatranite": {
    "name": "Heatranite",
    "spritenum": 503,
    "megaStone": "Heatran-Mega",
    "megaEvolves": "Heatran",
    "itemUser": [
      "Heatran"
    ],
    "gen": 9,
    "num": 2567
  },
  "darkranite": {
    "name": "Darkranite",
    "spritenum": 504,
    "megaStone": "Darkrai-Mega",
    "megaEvolves": "Darkrai",
    "itemUser": [
      "Darkrai"
    ],
    "gen": 9,
    "num": 2568
  },
  "emboarite": {
    "name": "Emboarite",
    "spritenum": 552,
    "megaStone": "Emboar-Mega",
    "megaEvolves": "Emboar",
    "itemUser": [
      "Emboar"
    ],
    "gen": 9,
    "num": 2569
  },
  "excadrite": {
    "name": "Excadrite",
    "spritenum": 553,
    "megaStone": "Excadrill-Mega",
    "megaEvolves": "Excadrill",
    "itemUser": [
      "Excadrill"
    ],
    "gen": 9,
    "num": 2570
  },
  "scolipite": {
    "name": "Scolipite",
    "spritenum": 554,
    "megaStone": "Scolipede-Mega",
    "megaEvolves": "Scolipede",
    "itemUser": [
      "Scolipede"
    ],
    "gen": 9,
    "num": 2571
  },
  "scraftinite": {
    "name": "Scraftinite",
    "spritenum": 555,
    "megaStone": "Scrafty-Mega",
    "megaEvolves": "Scrafty",
    "itemUser": [
      "Scrafty"
    ],
    "gen": 9,
    "num": 2572
  },
  "eelektrossite": {
    "name": "Eelektrossite",
    "spritenum": 556,
    "megaStone": "Eelektross-Mega",
    "megaEvolves": "Eelektross",
    "itemUser": [
      "Eelektross"
    ],
    "gen": 9,
    "num": 2573
  },
  "chandelurite": {
    "name": "Chandelurite",
    "spritenum": 557,
    "megaStone": "Chandelure-Mega",
    "megaEvolves": "Chandelure",
    "itemUser": [
      "Chandelure"
    ],
    "gen": 9,
    "num": 2574
  },
  "golurkite": {
    "name": "Golurkite",
    "spritenum": 505,
    "megaStone": "Golurk-Mega",
    "megaEvolves": "Golurk",
    "itemUser": [
      "Golurk"
    ],
    "gen": 9,
    "num": 2642
  },
  "chesnaughtite": {
    "name": "Chesnaughtite",
    "spritenum": 558,
    "megaStone": "Chesnaught-Mega",
    "megaEvolves": "Chesnaught",
    "itemUser": [
      "Chesnaught"
    ],
    "gen": 9,
    "num": 2575
  },
  "delphoxite": {
    "name": "Delphoxite",
    "spritenum": 559,
    "megaStone": "Delphox-Mega",
    "megaEvolves": "Delphox",
    "itemUser": [
      "Delphox"
    ],
    "gen": 9,
    "num": 2576
  },
  "greninjite": {
    "name": "Greninjite",
    "spritenum": 560,
    "megaStone": "Greninja-Mega",
    "megaEvolves": "Greninja",
    "itemUser": [
      "Greninja"
    ],
    "gen": 9,
    "num": 2577
  },
  "pyroarite": {
    "name": "Pyroarite",
    "spritenum": 561,
    "megaStone": "Pyroar-Mega",
    "megaEvolves": "Pyroar",
    "itemUser": [
      "Pyroar"
    ],
    "gen": 9,
    "num": 2578
  },
  "floettite": {
    "name": "Floettite",
    "spritenum": 562,
    "megaStone": "Floette-Mega",
    "megaEvolves": "Floette",
    "itemUser": [
      "Floette"
    ],
    "gen": 9,
    "num": 2579
  },
  "malamarite": {
    "name": "Malamarite",
    "spritenum": 563,
    "megaStone": "Malamar-Mega",
    "megaEvolves": "Malamar",
    "itemUser": [
      "Malamar"
    ],
    "gen": 9,
    "num": 2580
  },
  "barbaracite": {
    "name": "Barbaracite",
    "spritenum": 564,
    "megaStone": "Barbaracle-Mega",
    "megaEvolves": "Barbaracle",
    "itemUser": [
      "Barbaracle"
    ],
    "gen": 9,
    "num": 2581
  },
  "dragalgite": {
    "name": "Dragalgite",
    "spritenum": 565,
    "megaStone": "Dragalge-Mega",
    "megaEvolves": "Dragalge",
    "itemUser": [
      "Dragalge"
    ],
    "gen": 9,
    "num": 2582
  },
  "hawluchanite": {
    "name": "Hawluchanite",
    "spritenum": 566,
    "megaStone": "Hawlucha-Mega",
    "megaEvolves": "Hawlucha",
    "itemUser": [
      "Hawlucha"
    ],
    "gen": 9,
    "num": 2583
  },
  "zygardite": {
    "name": "Zygardite",
    "spritenum": 568,
    "megaStone": "Zygarde-Mega",
    "megaEvolves": "Zygarde",
    "itemUser": [
      "Zygarde"
    ],
    "gen": 9,
    "num": 2584
  },
  "crabominite": {
    "name": "Crabominite",
    "spritenum": 507,
    "megaStone": "Crabominable-Mega",
    "megaEvolves": "Crabominable",
    "itemUser": [
      "Crabominable"
    ],
    "gen": 9,
    "num": 2644
  },
  "golisopite": {
    "name": "Golisopite",
    "spritenum": 508,
    "megaStone": "Golisopod-Mega",
    "megaEvolves": "Golisopod",
    "itemUser": [
      "Golisopod"
    ],
    "gen": 9,
    "num": 2645
  },
  "drampanite": {
    "name": "Drampanite",
    "spritenum": 569,
    "megaStone": "Drampa-Mega",
    "megaEvolves": "Drampa",
    "itemUser": [
      "Drampa"
    ],
    "gen": 9,
    "num": 2585
  },
  "magearnite": {
    "name": "Magearnite",
    "spritenum": 509,
    "megaStone": "Magearna-Mega",
    "megaEvolves": "Magearna",
    "itemUser": [
      "Magearna"
    ],
    "gen": 9,
    "num": 2646
  },
  "zeraorite": {
    "name": "Zeraorite",
    "spritenum": 510,
    "megaStone": "Zeraora-Mega",
    "megaEvolves": "Zeraora",
    "itemUser": [
      "Zeraora"
    ],
    "gen": 9,
    "num": 2586
  },
  "falinksite": {
    "name": "Falinksite",
    "spritenum": 570,
    "megaStone": "Falinks-Mega",
    "megaEvolves": "Falinks",
    "itemUser": [
      "Falinks"
    ],
    "gen": 9,
    "num": 2587
  },
  "scovillainite": {
    "name": "Scovillainite",
    "spritenum": 511,
    "megaStone": "Scovillain-Mega",
    "megaEvolves": "Scovillain",
    "itemUser": [
      "Scovillain"
    ],
    "gen": 9,
    "num": 2647
  },
  "glimmoranite": {
    "name": "Glimmoranite",
    "spritenum": 512,
    "megaStone": "Glimmora-Mega",
    "megaEvolves": "Glimmora",
    "itemUser": [
      "Glimmora"
    ],
    "gen": 9,
    "num": 2650
  },
  "baxcalibrite": {
    "name": "Baxcalibrite",
    "spritenum": 514,
    "megaStone": "Baxcalibur-Mega",
    "megaEvolves": "Baxcalibur",
    "itemUser": [
      "Baxcalibur"
    ],
    "gen": 9,
    "num": 2648
  }
};

exports.FormatsData = {
  "raichumegax": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "raichumegay": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "clefablemega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "victreebelmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "starmiemega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "dragonitemega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "meganiummega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "feraligatrmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "skarmorymega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "chimechomega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "absolmegaz": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "staraptormega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "garchompmegaz": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "lucariomegaz": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "froslassmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "heatranmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "darkraimega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "emboarmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "excadrillmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "scolipedemega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "scraftymega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "eelektrossmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "chandeluremega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "golurkmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "chesnaughtmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "delphoxmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "greninjamega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "pyroarmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "floettemega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "malamarmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "barbaraclemega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "dragalgemega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "hawluchamega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "zygardemega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "crabominablemega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "golisopodmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "drampamega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "magearnamega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "zeraoramega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "falinksmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "scovillainmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "glimmoramega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  },
  "baxcaliburmega": {
    "tier": "Illegal",
    "natDexTier": "Illegal",
    "isNonstandard": null
  }
};
