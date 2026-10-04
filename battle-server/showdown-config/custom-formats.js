'use strict';

// Custom formats for the draft-league Showdown server.
//
// This repo file is the source of truth. scripts/showdown.js copies it into the
// engine's dist/config/custom-formats.js on every start, so it survives a rebuild
// or an `npm install` that resets node_modules.
//
// Both battle formats run on the `champions` mod (Pokemon Champions, Regulation
// M-B): megas are native and first-class there, so the league no longer needs the
// generated showdown-config/custom-megas.js merge. Champions also changes the
// mechanics the league plays under, see the ZST Season 4 notes below.
//
// THE FORMAT NAMES ARE LORE-BEARING. The teambuilder client decides which dex,
// which stat system and which tier table to use purely by string-matching the
// format id (battle-dex-search.ts, oldclient/client-teambuilder.js), so both names
// must produce an id of the shape gen9 + champions + natdex + <rest>:
//   "champions" -> the Champions dex (megas legal) and Stat Points, not EVs
//   "natdex"    -> straight after "champions", which selects the natdexchampions
//                  tier table, the one scripts/patch-client-tiers.js writes our
//                  X/S/A/B/C/Z tiers into
// Reorder or drop either word and the teambuilder silently builds 510-EV gen-9
// teams the server then rejects. The ids are also hard-coded in
// scripts/serve-client.js, lib/random-team.js and lib/seed-teams.js.

// Showdown's id form: lowercase, alphanumerics only. Matches toID in the engine and
// ShowdownId on the .NET side, so a coach's Showdown name maps to the same key here.
function toId(s) {
  return ('' + (s == null ? '' : s)).toLowerCase().replace(/[^a-z0-9]/g, '');
}

// A forme a mon flips into IN BATTLE by carrying a signature move: Keldeo-Resolute
// (Secret Sword) is the only one in the current dex. It has the same stats, types and
// abilities as its base and the base turns into it mid-battle anyway, so it is the SAME
// draft pick, not a separate one: drafting Keldeo lets a coach bring either. The signal
// is requiredMove + changesFrom with no battleOnly; mega / primal / Pirouette formes
// carry battleOnly and are handled above, and distinct-forme picks (Rotom-Wash,
// Landorus-Therian) have no requiredMove, so they stay their own draft pick.
function moveFormeBase(sp) {
  return sp && sp.exists && sp.requiredMove && typeof sp.changesFrom === 'string' &&
    typeof sp.battleOnly !== 'string' ? sp.changesFrom : null;
}

// A forme you get by giving the BASE species its signature ability, the teambuilder's
// "Special Event Ability" slot: Greninja + Battle Bond is really Greninja-Bond, and
// Rockruff + Own Tempo is Rockruff-Dusk. A coach builds the base and picks the ability;
// Showdown's own validator then rewrites set.species to the forme (team-validator.js),
// which is exactly what we want, since Battle Bond only pays out (+1 Atk/SpA/Spe the
// first time the holder KOes something) for greninjabond, never for plain Greninja. The
// pair is therefore ONE draft pick: identical stats and types, same sprite, and the
// battle log even reports the forme under its base name. The signal is a forme whose
// ONLY ability is its base's S slot, with no battleOnly (which would make it a
// mega/primal/Complete forme handled above) and no requiredMove. In the current dex
// that is precisely Greninja-Bond and Rockruff-Dusk.
function abilityFormeBase(dex, sp) {
  if (!sp || !sp.exists || sp.name === sp.baseSpecies) return null;
  if (sp.battleOnly || sp.requiredMove) return null;
  const base = dex.species.get(sp.baseSpecies);
  const signature = base && base.exists && base.abilities && base.abilities.S;
  const only = sp.abilities && sp.abilities['0'];
  return signature && only && toId(signature) === toId(only) ? base.name : null;
}

// A forme you get by handing the BASE species a type-changing item: Silvally + Water
// Memory is Silvally-Water, Genesect + Douse Drive is Genesect-Douse, Arceus + a plate.
// Only the type moves; stats and abilities are the base's, and the sheet lists the whole
// family as ONE row, so a drafted Silvally may be brought as any of its 17 memory formes.
// The signal is changesFrom + a required item, no battleOnly, and base stats AND
// abilities identical to the base. That last clause is what keeps the item formes that
// really are separate mons their own draft pick: Giratina-Origin, Necrozma-Ultra and
// Zygarde-Mega restat, and the Ogerpon masks and Venomicon-Epilogue change ability.
function itemFormeBase(dex, sp) {
  if (!sp || !sp.exists || typeof sp.battleOnly === 'string' || !sp.changesFrom) return null;
  if (!sp.requiredItem && !(sp.requiredItems && sp.requiredItems.length)) return null;
  const base = dex.species.get(sp.changesFrom);
  if (!base || !base.exists) return null;
  return sameMap(sp.baseStats, base.baseStats) && sameMap(sp.abilities, base.abilities)
    ? base.name : null;
}

// Shallow equality for the flat string/number maps above (baseStats, abilities), which
// key order can differ on.
function sameMap(a, b) {
  const ka = Object.keys(a || {});
  if (ka.length !== Object.keys(b || {}).length) return false;
  return ka.every((k) => a[k] === b[k]);
}

// A drafted mon's "base" identity for roster matching. Battle-only formes (mega,
// primal, crowned) are built as the base species holding an item and transform in
// battle, so they collapse to that base (you draft "M-Charizard Y" and bring
// Charizard); so do a move-triggered forme (see moveFormeBase), a signature-ability
// forme (see abilityFormeBase) and a type-item forme (see itemFormeBase). Origin,
// regional and other formes are built directly and stay distinct draft picks
// (Giratina-Origin != Giratina, Rotom-Wash != Rotom).
// Collapsing can CHAIN, so keep going until it settles: Greninja-Ash is a battle-only
// forme of Greninja-BOND, which is itself the Battle Bond forme of Greninja, and one
// step would leave the useless id "greninjabond". Bounded so bad data can't spin.
function baseFormeId(dex, name) {
  let sp = dex.species.get(name);
  if (!sp || !sp.exists) return toId(name);
  for (let hops = 0; hops < 4; hops++) {
    const next = typeof sp.battleOnly === 'string' ? sp.battleOnly
      : moveFormeBase(sp) || abilityFormeBase(dex, sp) || itemFormeBase(dex, sp);
    if (!next) break;
    const resolved = dex.species.get(next);
    if (!resolved || !resolved.exists) return toId(next);
    sp = resolved;
  }
  return sp.id;
}

// The battle-only forme a held item transforms its holder into, or null. Mega stones
// name it directly (megaStone); the orbs / rusted items name it via itemUser, but
// only when that species is a battle-only forme (so Light Ball -> Pikachu, a plain
// held item, is ignored). Origin orbs (Griseous Core) are NOT caught here: their
// forme is built directly, and base validation rewrites "base + orb" to the Origin
// species before the roster check, so species membership already covers them.
//
// `holder` is the set's species name, used only to pick the right entry out of a
// megaStone MAP. The Champions engine writes megaStone as {baseSpecies: megaForme}
// where older engines wrote the mega's name as a plain string; both are handled. On
// a wrong holder (Blastoise @ Charizardite Y) the map lookup misses and we fall back
// to the stone's only mega, so the caller still reports whose stone it is.
function formeGrantedByItem(dex, item, holder) {
  if (!item || !item.exists) return null;
  if (item.megaStone) {
    const stone = item.megaStone;
    const name = typeof stone === 'string' ? stone
      : (holder && stone[dex.species.get(holder).baseSpecies]) || Object.values(stone)[0];
    const sp = dex.species.get(name);
    return sp && sp.exists ? sp : null;
  }
  if (item.itemUser && item.itemUser.length === 1) {
    const sp = dex.species.get(item.itemUser[0]);
    if (sp.exists && typeof sp.battleOnly === 'string') return sp;
  }
  return null;
}

// Blocking GET of a coach's drafted roster from the .NET league server, at the point
// of team validation. Team validation in Showdown is SYNCHRONOUS (the worker does
// `problems = validateTeam(...)` with no await), so we can't use async fetch here;
// spawnSync a throwaway node that does the fetch and prints a JSON envelope.
function fetchRosterSync(userid) {
  const { spawnSync } = require('child_process');
  const base = (process.env.DRAFT_ROSTER_URL || 'http://localhost:5211/api/showdown/roster').replace(/\/+$/, '');
  const url = base + '/' + encodeURIComponent(userid);
  const script =
    "fetch(process.env.U).then(async r=>{const t=await r.text();process.stdout.write(JSON.stringify({s:r.status,b:t}));})" +
    ".catch(e=>{process.stdout.write(JSON.stringify({s:0,b:String(e&&e.message||e)}));});";
  const res = spawnSync(process.execPath, ['-e', script],
    { env: Object.assign({}, process.env, { U: url }), timeout: 6000, encoding: 'utf8', maxBuffer: 4 * 1024 * 1024 });
  if (res.error) throw res.error;
  const out = String(res.stdout || '');
  let env;
  try { env = JSON.parse(out); }
  catch { throw new Error('roster fetch produced no response'); }
  if (env.s !== 200) throw new Error(`roster fetch HTTP ${env.s}`);
  try { return JSON.parse(env.b); }
  catch { throw new Error('roster response was not JSON'); }
}

// The roster rules, factored out of validateTeam so they're unit-testable with a
// stub roster + the real Dex (see test/roster-validate.test.js). Given the coach's
// roster { mons: [{ slug, tier, tera, name }] }, returns an array of problem strings:
//   - every set's species must be a drafted mon,
//   - a form-changing item (mega stone, Rusted Sword/Shield, Red/Blue Orb) must sit on
//     the species it transforms AND unlock a battle-only forme the coach drafted,
//   - a C-tier pick must Terastallize to its drafted Tera type.
// Origin formes (Giratina/Dialga/Palkia-Origin) need no special item handling here:
// base validation rewrites "base + orb" to the Origin species before this runs, so
// the species-membership check enforces them like any other distinct forme.
function checkRoster(team, roster, dex) {
  const problems = [];
  const allowed = new Set();       // base ids the coach may bring
  const granted = new Set();       // battle-only forme ids a drafted pick unlocks
  const draftedByBase = new Map(); // base id -> the drafted pick (for tier / tera)
  for (const m of (roster.mons || [])) {
    const slug = m.slug || m.name;
    const sp = dex.species.get(slug);
    let baseId;
    if (sp && sp.exists && typeof sp.battleOnly === 'string') {
      baseId = toId(sp.battleOnly); // mega / primal / crowned: drafted as base + item
      granted.add(sp.id);
    } else {
      baseId = baseFormeId(dex, slug); // own id, or the base of a move-triggered forme
    }
    allowed.add(baseId);
    draftedByBase.set(baseId, m);
  }

  for (const set of team) {
    if (!set) continue;
    const label = set.name && set.name !== set.species ? `${set.species} (${set.name})` : set.species;
    const baseId = baseFormeId(dex, set.species);

    if (!allowed.has(baseId)) {
      problems.push(`${label} is not on your drafted team.`);
      continue; // the item / tera checks below assume a drafted mon
    }

    // A form-changing item must sit on the species it transforms AND unlock a
    // battle-only forme the coach actually drafted (a mega, primal or crowned forme).
    const item = dex.items.get(set.item);
    const grantedForme = formeGrantedByItem(dex, item, set.species);
    if (grantedForme) {
      const owner = grantedForme.battleOnly || grantedForme.baseSpecies;
      if (toId(owner) !== baseId) {
        problems.push(`${label} can't hold ${item.name}: it belongs to ${owner}.`);
      } else if (!granted.has(grantedForme.id)) {
        problems.push(`${label} can't hold ${item.name}: you didn't draft its ${grantedForme.forme} forme (${grantedForme.name}).`);
      }
    }

    // C-tier picks are locked to the Tera type they were drafted with.
    const drafted = draftedByBase.get(baseId);
    if (drafted && String(drafted.tier).toUpperCase() === 'C' && drafted.tera) {
      if (toId(set.teraType) !== toId(drafted.tera)) {
        problems.push(`${label} is a C-tier pick and must use its drafted Tera type ${drafted.tera} (got ${set.teraType || 'none'}).`);
      }
    }
  }
  return problems;
}

// Base ids of the mons a coach may TERASTALLIZE: only C-tier picks that were drafted
// with a Tera type. Megas / primals / Shedinja are Tera-barred at draft time, so their
// roster `tera` is null and they fall out here automatically, no special-casing. Keyed
// by base id (mega base) so it matches a battle mon built as base + stone.
function teraAllowedBaseIds(roster, dex) {
  const ids = new Set();
  for (const m of (roster.mons || [])) {
    if (String(m.tier).toUpperCase() !== 'C' || !m.tera) continue;
    ids.add(baseFormeId(dex, m.slug || m.name));
  }
  return ids;
}

// Null out canTerastallize on every battle mon that isn't a tera-allowed C-tier pick,
// so it can't Terastallize; allowed mons keep it and choose freely. Factored out of the
// format's onBegin so it's unit-testable with stub sides + a stub roster fetcher. Fails
// open per side if its roster fetch throws (the team already validated to reach here).
function applyTeraRestriction(sides, dex, fetchRoster) {
  for (const side of sides || []) {
    const userid = toId(side && side.name);
    if (!userid) continue;
    let roster;
    try { roster = fetchRoster(userid); } catch (e) { continue; }
    if (!roster || !roster.found) continue;
    const canTera = teraAllowedBaseIds(roster, dex);
    for (const pokemon of side.pokemon || []) {
      if (!canTera.has(baseFormeId(dex, pokemon.species.name))) pokemon.canTerastallize = null;
    }
  }
}

// Put each set's Tera type back after the base validation stripped it, and validate
// it the way vanilla gen 9 does. The engine deletes set.teraType on ANY champions mod
// (team-validator.js: `this.gen === 9 && !dex.currentMod.startsWith('champions')`),
// because Champions has no Terastallization, and the team it packs for the battle
// would lose the type with it. The league keeps tera as a C-tier mechanic, so we
// snapshot what the coach declared, let the base validation run, then restore.
// `declared` is the pre-validation teraType per set; missing entries fall back to the
// species' required type or its primary type, exactly as vanilla does.
function restoreTeraTypes(team, declared, dex) {
  const problems = [];
  for (let i = 0; i < (team || []).length; i++) {
    const set = team[i];
    if (!set) continue;
    const species = dex.species.get(set.species);
    const type = dex.types.get(declared[i] || species.requiredTeraType || species.types[0]);
    if (!type.exists || type.isNonstandard) {
      problems.push(`${set.name || set.species}'s Terastal type (${declared[i]}) is invalid.`);
      continue;
    }
    if (species.requiredTeraType && species.requiredTeraType !== type.name) {
      problems.push(`${species.name}'s Terastal type needs to be ${species.requiredTeraType}.`);
      continue;
    }
    set.teraType = type.name;
  }
  return problems;
}

// A C-tier pick's Tera type is fixed at DRAFT time: the coach picks nothing, so there is
// nothing to reject either. Stamp the drafted type onto every C-tier set before the
// roster check reads it.
//
// Without this a set can reach validation with NO Tera type (the teambuilder deletes it
// on every species change, and an imported / pasted / clipboarded set never carried one),
// restoreTeraTypes then falls back to the species' primary type exactly as vanilla does,
// and a mon whose drafted type is its SECOND type is silently retyped: Aerodactyl
// (Rock/Flying) drafted as Tera Flying arrives as Tera Rock, and checkRoster refuses a
// team the coach built correctly. Non-C mons are left alone: they are Tera-barred at
// battle start anyway (applyTeraRestriction), so their type never matters.
//
// Returns the list of "Species: Type" strings it changed, for tests and logging.
function applyDraftedTera(team, roster, dex) {
  const teraByBase = new Map();
  for (const m of (roster && roster.mons) || []) {
    if (String(m.tier).toUpperCase() !== 'C' || !m.tera) continue;
    const type = dex.types.get(m.tera);
    // A Tera type the dex doesn't know is a data problem, not something to stamp on:
    // leave the set as-is and let checkRoster report the mismatch.
    if (!type.exists || type.isNonstandard) continue;
    teraByBase.set(baseFormeId(dex, m.slug || m.name), type.name);
  }
  const applied = [];
  for (const set of team || []) {
    if (!set || !set.species) continue;
    const tera = teraByBase.get(baseFormeId(dex, set.species));
    if (!tera) continue;
    // Ogerpon / Terapagos lock their own Tera type; restoreTeraTypes has already
    // reported the clash, so don't overwrite it with a conflicting drafted type.
    const species = dex.species.get(set.species);
    if (species.exists && species.requiredTeraType && species.requiredTeraType !== tera) continue;
    if (set.teraType !== tera) {
      set.teraType = tera;
      applied.push(`${set.species}: ${tera}`);
    }
  }
  return applied;
}

// Rewrite a set built AS a battle-only forme (Floette-Mega @ Floettite, or any mega /
// primal / crowned forme picked directly) back to its base species holding the required
// item. The teambuilder lists megas as their FORME (nicer to pick), so this is the
// "matchmaking reverts it to base form at battle start" step: base validation then
// accepts a legal base + stone set, and the mon mega-evolves in battle as usual.
// Standard megas already build as base + stone, so those sets are left untouched.
function revertBattleFormes(team, dex) {
  for (const set of (team || [])) {
    if (!set || !set.species) continue;
    const sp = dex.species.get(set.species);
    if (sp && sp.exists && typeof sp.battleOnly === 'string' && sp.requiredItem) {
      if (!set.item) set.item = sp.requiredItem;
      set.species = sp.battleOnly;
    }
  }
}

// ── The league's rules, shared by the live format and its simulator twin ─────
//
// ZST Season 4 and ZST Sim MUST resolve to the same mechanics, or a simulated
// season stops predicting anything about real games, so the parts they share live
// here rather than being copy-pasted into both. Each format takes a .slice() of the
// arrays, since Showdown appends to a format's ruleset while building it.

const ZST_RULESET = [
  // The Champions mod's own draft standard: Standard AG (Obtainable, Team Preview,
  // Cancel Mod, Endless Battle Clause, Adjust Level = 50, Species Clause, Min Team
  // Size = 6) plus Nickname Clause, Sleep Clause Mod, OHKO Clause and Evasion Clause,
  // and it reports damage as percentages. Note Adjust Level = 50: Champions battles
  // are level 50, and its stat formula ignores level anyway.
  // Evasion Clause covers evasion abilities (Sand Veil, Snow Cloak), items (Bright
  // Powder, Lax Incense) AND moves (Double Team / Minimize), so do not re-list those.
  'Standard Draft',
  'NatDex Mod',           // +Unobtainable, +Past, Sketch Post-Gen 7 Moves: opens the pool to the full National Dex
  'Item Clause = 1',      // no two mons with the same item (Standard Draft drops the clause; this puts it back)
  'Dynamax Clause',       // no Dynamax
  'Z-Move Clause',        // no Z-moves
];

const ZST_BANLIST = [
  'Power Construct',      // ability banned (Zygarde still draftable with other abilities)
  'Revival Blessing',
  'Hidden Power',
  'Swagger',              // banned as a move: the engine dropped Swagger Clause, so there is no rule to use
  'Rusted Shield',        // bans Zamazenta-Crowned (its required item), base Zamazenta stays legal
  // Zippy Zap (+1 evasion via a 100% self-secondary) is NOT covered by the
  // Evasion Clause, so ban it explicitly. Double Team / Minimize are already
  // banned BY that clause (via its Evasion Moves Clause), listing them here
  // too is a duplicate that Showdown rejects with "Rule already exists in
  // Evasion Moves Clause", which crashes the server worker on load. Do not
  // re-add them here.
  'Zippy Zap',
  // All type gems banned; the plain Normal Gem stays legal.
  'Fire Gem', 'Water Gem', 'Electric Gem', 'Grass Gem', 'Ice Gem', 'Fighting Gem',
  'Poison Gem', 'Ground Gem', 'Flying Gem', 'Psychic Gem', 'Bug Gem', 'Rock Gem',
  'Ghost Gem', 'Dragon Gem', 'Dark Gem', 'Steel Gem', 'Fairy Gem',
];

// The Champions mod switches Terastallization off outright (its actions.canTerastallize
// returns null for everyone), because megas are that game's gimmick. The league keeps
// tera as a C-tier-only mechanic, so restore the vanilla gen-9 rule: a format's `actions`
// is merged over the mod's in the BattleActions constructor. Vanilla already bars a mon
// holding a Z-crystal or a usable mega stone; the live format's onBegin then nulls
// canTerastallize on everything that isn't a C-tier pick with a drafted Tera type.
const LEAGUE_ACTIONS = {
  canTerastallize(pokemon) {
    if (pokemon.getItem().zMove || pokemon.canMegaEvo || this.dex.gen !== 9) return null;
    return pokemon.teraType;
  },
};

// The two National Dex hooks, lifted from the engine's own "[Gen 9 Champions] NatDex
// Draft" (config/formats.ts). The Champions mod only carries the mons and moves that
// game ships with (it cut ~194 gen-9 moves and trimmed learnsets: Archaludon lost Body
// Press, Garchomp lost Aqua Tail), so NatDex Mod alone would leave every past-gen move
// unlearnable and every past-gen mon "Illegal". These defer to the gen-9 National Dex AG
// validator for learnsets and to gen 9's natDexTier for species legality, which is what
// puts the cut moves and mons back. checkCanLearn falls back to the champions verdict
// when National Dex says no, so Champions-only species (its new megas) still validate.
// Both run inside baseValidateTeam, so the roster check in validateTeam layers on top.
// Keep them in sync if the engine's copy changes.
const NATDEX_HOOKS = {
  checkCanLearn(move, species, setSources, set) {
    if (!this.ruleTable.has('natdexmod')) return this.checkCanLearn(move, species, setSources, set);
    const { TeamValidator } = require('../sim/team-validator');
    const natDex = TeamValidator.get(`gen${this.gen}nationaldexag`)
      .checkCanLearn(move, species, this.allSources(species), set);
    if (typeof natDex === 'string') return this.checkCanLearn(move, species, setSources, set);
    return natDex;
  },
  onValidateSet(set) {
    if (!this.ruleTable.has('natdexmod')) return;
    const species = this.dex.species.get(set.species);
    let tier = species.tier;
    if (tier === 'Illegal') tier = this.dex.mod('gen9').species.get(species.name).natDexTier;
    if (tier === 'Illegal') {
      if (this.ruleTable.has(`+pokemon:${species.id}`)) return;
      return [`${set.name || set.species} does not exist in the National Dex.`];
    }
    const requireObtainable = this.ruleTable.has('obtainable');
    if (requireObtainable) {
      if (species.natDexTier === 'Unreleased') {
        const basePokemon = this.toID(species.baseSpecies);
        if (this.ruleTable.has(`+pokemon:${species.id}`) || this.ruleTable.has(`+basepokemon:${basePokemon}`) ||
          this.ruleTable.has('+pokemontag:unobtainable')) {
          return;
        }
        return [`${set.name || set.species} does not exist in the National Dex.`];
      }
      for (const moveid of set.moves) {
        const move = this.dex.moves.get(moveid);
        if (move.isNonstandard === 'Unobtainable' && move.gen === this.dex.gen) {
          if (this.ruleTable.has(`+move:${move.id}`)) continue;
          const problem = `${set.name}'s move ${move.name} does not exist in the National Dex.`;
          if (this.ruleTable.has('omunobtainablemoves')) {
            const { outOfBattleSpecies } = this.getValidationSpecies(set);
            if (!this.omCheckCanLearn(move, outOfBattleSpecies, this.allSources(outOfBattleSpecies), set, problem)) continue;
          }
          return [problem];
        }
      }
    }
    // Any item that was legal in Gen 7 (Normal Gem for example) should be usable.
    if (!set.item) return;
    let item = this.dex.items.get(set.item);
    let gen = this.dex.gen;
    while (item.isNonstandard && gen >= 7) {
      item = this.dex.forGen(gen).items.get(item.id);
      gen--;
    }
    if (requireObtainable && item.isNonstandard) {
      if (this.ruleTable.has(`+item:${item.id}`)) return;
      return [`${set.name}'s item ${item.name} does not exist in Gen ${this.dex.gen}.`];
    }
  },
};

exports.Formats = [
  {
    section: "Draft League",
    column: 2,
  },
  {
    name: "[Gen 9 Champions] NatDex ZST Season 4",
    desc: "Champions National Dex Doubles draft league. Megas legal (base form + stone), 66 Stat Points per mon. Custom bans and clauses below.",
    // Pokemon Champions (Regulation M-B). Megas are native here, so the league no
    // longer needs a custom mega merge, and the mod brings the Champions mechanics
    // with it: no IVs and a flat stat formula (base + EVs + 75 HP / +20 other), PP
    // capped at 20, no Trick Room speed underflow, megas that stay mega after
    // fainting. Terastallization is OFF in the mod; the league restores it for
    // C-tier picks only (see `actions` below).
    mod: 'champions',
    // gameType switches the format from singles to doubles; everything else about
    // the league's rules is the ruleset/banlist below.
    gameType: 'doubles',
    // A top-level validateTeam REPLACES the base validation (team-validator.js reads
    // this.format.validateTeam first), so we run the standard checks ourselves via
    // this.baseValidateTeam and then layer the draft-roster rules on top. `this` is
    // the TeamValidator (it has .dex and .baseValidateTeam); options.user is the
    // logged-in Showdown id, passed on the challenge / search path (ladders.js).
    validateTeam(team, options) {
      // Revert mega/etc. formes (Floette-Mega @ stone) to base + stone FIRST, so the
      // base validation below sees a legal team and the mon mega-evolves in battle.
      revertBattleFormes(team, this.dex);
      // Champions strips teraType during validation; snapshot it before, restore after
      // (see restoreTeraTypes), so the C-tier tera rule below still has one to check.
      const declaredTera = (team || []).map((set) => set && set.teraType);
      const problems = this.baseValidateTeam(team, options) || [];
      for (const p of restoreTeraTypes(team, declaredTera, this.dex)) problems.push(p);

      const userid = toId(options && options.user);
      if (!userid) {
        problems.push(`This format only allows your own drafted team, but the server couldn't tell who you are. Reconnect with your league Showdown name.`);
        return problems;
      }

      let roster;
      try { roster = fetchRosterSync(userid); }
      catch (e) {
        problems.push(`Couldn't reach the league server to verify your drafted team (${e.message}). Try again in a moment.`);
        return problems;
      }
      if (!roster || !roster.found) {
        problems.push(`No drafted team is registered to the Showdown name "${(options && options.user) || userid}". Set your Showdown name in the league app (your profile) to match this account, then reload.`);
        return problems;
      }

      // C-tier picks are Tera-locked to what they were drafted with, so fill that in
      // before the roster check rather than judging whatever survived the client.
      applyDraftedTera(team, roster, this.dex);
      for (const p of checkRoster(team, roster, this.dex)) problems.push(p);
      return problems.length ? problems : null;
    },
    // Live tera rule: only C-tier picks with a drafted Tera type may Terastallize (so
    // never megas or Shedinja). We DISABLE tera on every other mon by nulling its
    // canTerastallize at battle start; allowed mons keep it, so a coach still CHOOSES
    // when to tera (nothing is forced, unlike the simulated-season AI). `this` is the
    // Battle here. Fails open (leaves tera as-is) if a roster fetch hiccups, since the
    // team already validated against that same roster to reach the battle.
    onBegin() {
      applyTeraRestriction(this.sides, this.dex, fetchRosterSync);
    },
    actions: LEAGUE_ACTIONS,
    ruleset: ZST_RULESET.slice(),
    banlist: ZST_BANLIST.slice(),
    ...NATDEX_HOOKS,
  },
  {
    // The season simulator's format: ZST Season 4 minus the two things a headless run
    // can't do. There is no logged-in coach, so no roster to validate a team against
    // or to restrict tera by (validateTeam / onBegin are dropped, which also means the
    // simulator's random teams are never rejected, and every mon may tera). Everything
    // that decides how a battle PLAYS is the same object as the live format's, so a
    // simulated season predicts real games: Champions mechanics, doubles, the same
    // clauses, bans, National Dex learnsets and restored Terastallization.
    //
    // Used by scripts/simulate-season.js as both the battle format and the legality
    // gate it validates generated teams through, and by lib/format.js for the
    // interactive battle-room server. Hidden from every picker: it is not a format a
    // coach should ever find.
    name: "[Gen 9 Champions] NatDex ZST Sim",
    desc: "Headless simulator twin of ZST Season 4: same mechanics and rules, no draft-roster check.",
    mod: 'champions',
    gameType: 'doubles',
    rated: false,
    searchShow: false,
    challengeShow: false,
    tournamentShow: false,
    actions: LEAGUE_ACTIONS,
    // Endless Battle Clause is already in Standard AG; it is what stops a stalled
    // random-AI game from hanging a whole simulated season.
    ruleset: ZST_RULESET.slice(),
    banlist: ZST_BANLIST.slice(),
    ...NATDEX_HOOKS,
  },
  {
    // Open doubles scrims: bring any team, no tier/legality restrictions, just the
    // battle QoL mods. Defined here (not the built-in Doubles Custom Game) because
    // the self-hosted client only surfaces our custom formats + random battles; a
    // built-in custom game never reaches its Battle! picker. No draft-roster check,
    // this is for free practice games, not league matches.
    name: "[Gen 9 Champions] NatDex Scrims",
    desc: "Open Champions doubles scrims. Bring any team; standard battle mods, no clauses or tier bans.",
    // Same mod as ZST Season 4 so a scrim plays by the mechanics league games do
    // (Champions stats, megas, PP cap), including the restored Terastallization,
    // which is unrestricted here since there is no roster to check a tier against.
    mod: 'champions',
    gameType: 'doubles',
    rated: false,
    searchShow: true,
    challengeShow: true,
    actions: LEAGUE_ACTIONS,
    // NatDex Mod is a no-op on legality here (nothing requires Obtainable), but it
    // keeps the format's name honest about the pool it offers.
    ruleset: ['Cancel Mod', 'HP Percentage Mod', 'Sleep Clause Mod', 'Endless Battle Clause', 'Team Preview', 'NatDex Mod'],
  },
];

// Exposed for unit tests (test/format.test.js). Showdown only reads exports.Formats,
// so these extra exports are inert on the live server.
exports.checkRoster = checkRoster;
exports.baseFormeId = baseFormeId;
exports.moveFormeBase = moveFormeBase;
exports.abilityFormeBase = abilityFormeBase;
exports.itemFormeBase = itemFormeBase;
exports.teraAllowedBaseIds = teraAllowedBaseIds;
exports.applyTeraRestriction = applyTeraRestriction;
exports.revertBattleFormes = revertBattleFormes;
exports.restoreTeraTypes = restoreTeraTypes;
exports.applyDraftedTera = applyDraftedTera;
exports.toId = toId;
