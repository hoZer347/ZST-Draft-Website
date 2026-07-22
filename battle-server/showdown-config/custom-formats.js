'use strict';

// Custom formats for the draft-league Showdown server.
//
// This repo file is the source of truth. scripts/showdown.js copies it into the
// bundled server's dist/config/custom-formats.js on every start, so it survives
// an `npm install` that resets node_modules.
//
// The name contains "NatDex" on purpose: the teambuilder client keys its tier
// table + mega legality off the format id (it must include natdex/nationaldex),
// which routes it to the gen9natdex data, where megas are legal and where
// scripts/patch-client-tiers.js has written our X/S/A/B/C/Z tiers.

// Showdown's id form: lowercase, alphanumerics only. Matches toID in the engine and
// ShowdownId on the .NET side, so a coach's Showdown name maps to the same key here.
function toId(s) {
  return ('' + (s == null ? '' : s)).toLowerCase().replace(/[^a-z0-9]/g, '');
}

// A drafted mon's "base" identity for roster matching: mega / primal formes collapse
// to their base species (you draft "M-Charizard Y" and may bring Charizard), while
// regional / other formes stay distinct draft picks (Rotom-Wash != Rotom).
function baseFormeId(dex, name) {
  const sp = dex.species.get(name);
  if (sp && sp.exists) {
    const forme = sp.forme || '';
    if (forme.startsWith('Mega') || forme === 'Primal') return toId(sp.baseSpecies);
    return sp.id;
  }
  return toId(name);
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
// stub roster + the real Dex (see test/format.test.js). Given the coach's roster
// { mons: [{ slug, tier, tera, name }] }, returns an array of problem strings:
//   - every set's species must be a drafted mon,
//   - a mega stone must sit on the species it evolves AND be a mega the coach drafted,
//   - a C-tier pick must Terastallize to its drafted Tera type.
function checkRoster(team, roster, dex) {
  const problems = [];
  const allowed = new Set();       // base forme ids the coach may bring
  const megas = new Set();         // mega forme ids the coach actually drafted
  const draftedByBase = new Map(); // base id -> the drafted pick (for tier / tera)
  for (const m of (roster.mons || [])) {
    const slug = m.slug || m.name;
    const sp = dex.species.get(slug);
    let baseId;
    if (sp && sp.exists && ((sp.forme || '').startsWith('Mega') || (sp.forme || '') === 'Primal')) {
      baseId = toId(sp.baseSpecies);
      megas.add(sp.id);
    } else {
      baseId = sp && sp.exists ? sp.id : toId(slug);
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
      continue; // the mega / tera checks below assume a drafted mon
    }

    // Mega stone: must belong to this species, and be a mega the coach drafted.
    const item = dex.items.get(set.item);
    if (item && item.exists && item.megaStone && item.megaEvolves) {
      if (baseFormeId(dex, item.megaEvolves) !== baseId) {
        problems.push(`${label} can't hold ${item.name}: that stone belongs to ${item.megaEvolves}.`);
      } else if (!megas.has(toId(item.megaStone))) {
        problems.push(`${label} can't hold ${item.name}: you drafted the base ${set.species}, not its mega (${item.megaStone}).`);
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
    // A top-level validateTeam REPLACES the base validation (team-validator.js reads
    // this.format.validateTeam first), so we run the standard checks ourselves via
    // this.baseValidateTeam and then layer the draft-roster rules on top. `this` is
    // the TeamValidator (it has .dex and .baseValidateTeam); options.user is the
    // logged-in Showdown id, passed on the challenge / search path (ladders.js).
    validateTeam(team, options) {
      const problems = this.baseValidateTeam(team, options) || [];

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

      for (const p of checkRoster(team, roster, this.dex)) problems.push(p);
      return problems.length ? problems : null;
    },
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
      // banned BY that clause (via its Evasion Moves Clause), listing them here
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

// Exposed for unit tests (test/format.test.js). Showdown only reads exports.Formats,
// so these extra exports are inert on the live server.
exports.checkRoster = checkRoster;
exports.baseFormeId = baseFormeId;
exports.toId = toId;
