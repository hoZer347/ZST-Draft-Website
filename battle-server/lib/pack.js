'use strict';

const { Teams, Dex } = require('./engine').requireEngine();

// Four broadly-useful coverage moves. In a custom game movesets aren't
// validated, so this lets any drafted species battle without per-species
// learnset wrangling. Real matches will use each coach's own teambuilder
// export instead of these filler sets, this is only for headless proofs and
// smoke tests where we just need a legal, playable team.
const FILLER_MOVES = ['bodyslam', 'earthquake', 'icebeam', 'shadowball'];

/**
 * Build a packed team from a list of species keys, our pool's `sprite` slug
 * (e.g. "charizard-megay"), which toID-normalises to a real Showdown species.
 *
 * Everything but the species is left at a neutral, spread default. The result
 * is a packed team string, the format the sim and the client both accept.
 */
function packDraftTeam(speciesKeys) {
  const sets = speciesKeys.map((key) => {
    const species = Dex.species.get(key);
    if (!species.exists) throw new Error(`Unknown species: ${key}`);
    return {
      name: species.name,
      species: species.name,
      ability: species.abilities['0'] || 'No Ability',
      moves: FILLER_MOVES,
      nature: 'Hardy',
      gender: '',
      item: '',
      // Champions numbers: battles are level 50, and a mon gets 66 Stat Points
      // (max 32 per stat) rather than 510 EVs, so spread 11 across the six. IVs
      // don't exist in Champions; its stat formula never reads them.
      level: 50,
      evs: { hp: 11, atk: 11, def: 11, spa: 11, spd: 11, spe: 11 },
      ivs: { hp: 31, atk: 31, def: 31, spa: 31, spd: 31, spe: 31 },
    };
  });
  return Teams.pack(sets);
}

module.exports = { packDraftTeam, FILLER_MOVES };
