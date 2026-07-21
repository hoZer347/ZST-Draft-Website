'use strict';

const { Dex, TeamValidator, Teams } = require('pokemon-showdown');

/**
 * The draft league's battle format.
 *
 * We don't ship a format file into the pokemon-showdown package; instead we
 * build on the sim's built-in `gen9customgame` and layer our own rules on with
 * the `base@@@rule,rule,...` custom-rules syntax the sim already understands.
 * That keeps the format as plain, reviewable data right here, the one place to
 * develop the league's rules.
 *
 * Why customgame as the base (not gen9ou etc.): our pool is a hand-picked draft
 * list, so we do NOT want a tier-based banlist. We start from "anything goes"
 * and add back only the competitive-integrity clauses we actually want.
 *
 * IMPORTANT, the silent-null footgun: `Dex.formats.get('...@@@bad rule')`
 * swallows any error thrown while applying the rules and returns the format with
 * `customRules === null`, i.e. it silently drops EVERY rule and the battle then
 * runs with none. A bad/duplicate/nonexistent rule (e.g. gen9 has no real "Item
 * Clause") triggers this. So we never trust the string blindly, `resolve()`
 * builds the format and asserts the rules actually landed, throwing loudly if
 * not. Develop the rule list through that guard, never around it.
 */

const BASE_FORMAT = 'gen9customgame';

/**
 * The league ruleset. Each entry is a sim rule name or a ban/unban token, and
 * this list IS the format, edit here to change the league's rules.
 *
 * Deliberately NOT included yet:
 *   - Learnset / moveset validation. customgame skips it, and our headless
 *     smoke tests use filler movesets that aren't legal per-species. Real teams
 *     will come from coaches' teambuilder exports; tightening to "Standard"
 *     move legality is the next step once that flow exists. See lib/pack.js.
 *   - Any species banlist, the draft pool already decides who's legal.
 *   - `-Mega`: megas are drafted from our pool, so they stay legal.
 */
const DRAFT_RULES = [
  // Team Preview is intentionally omitted, gen9customgame already includes it,
  // and re-declaring it is a duplicate rule that trips the silent-null (above).
  'Species Clause',        // no two of the same species (dex#) on a team
  'Nickname Clause',       // no misleading nicknames
  'Sleep Clause Mod',      // can't put more than one foe to sleep at once
  'Evasion Clause',        // umbrella: bans evasion moves, items AND abilities
  'OHKO Clause',           // no Fissure / Sheer Cold etc.
  'Endless Battle Clause', // the sim force-ends a stall loop rather than hanging
];

/** Build the `base@@@rule,rule,...` format id from a rule list. */
function buildFormatId(rules = DRAFT_RULES, base = BASE_FORMAT) {
  return rules.length ? `${base}@@@${rules.join(',')}` : base;
}

/**
 * Resolve our format and PROVE the rules applied (see the silent-null note
 * above). Returns the resolved sim Format. Throws with a specific message if the
 * sim dropped the rules or a rule failed to take effect, which is exactly the
 * signal you want while developing the rule list.
 */
function resolve(rules = DRAFT_RULES, base = BASE_FORMAT) {
  const id = buildFormatId(rules, base);
  const format = Dex.formats.get(id);

  if (!format.exists) throw new Error(`Format base '${base}' does not exist`);

  if (rules.length) {
    // customRules going null means the sim swallowed an error and dropped
    // everything, treat it as a hard failure, not a silent no-op.
    if (!format.customRules || format.customRules.length !== rules.length) {
      throw new Error(
        `Format rules were dropped by the sim (a rule is invalid, duplicate, or ` +
        `conflicts). Passed ${rules.length} rule(s); got ` +
        `${format.customRules ? format.customRules.length : 'null'}. Rules: ${rules.join(', ')}`
      );
    }

    // Belt and braces: confirm each clause is actually present in the rule
    // table, so a rule that parses but no-ops can't slip through.
    const table = Dex.formats.getRuleTable(format);
    for (const rule of rules) {
      const token = toRuleToken(rule);
      if (token && !table.has(token)) {
        throw new Error(`Rule '${rule}' resolved but is missing from the rule table`);
      }
    }
  }

  return format;
}

// Map a human rule name to the id the rule table keys on. Ban/unban tokens
// (leading +/-/*) are left to the table's own membership check and skipped here.
function toRuleToken(rule) {
  if (/^[+\-*]/.test(rule)) return null;
  return rule.toLowerCase().replace(/[^a-z0-9]/g, '');
}

/**
 * Validate a packed team (or set array) against the draft format. Returns an
 * array of problem strings, empty means legal. Note: with learnset validation
 * off (see above), this currently enforces the clauses, level and team-size
 * rules, not per-species move legality.
 */
function validateTeam(team) {
  const validator = new TeamValidator(buildFormatId());
  // TeamValidator wants unpacked sets; accept either a packed string (what
  // lib/pack.js produces) or an already-unpacked set array.
  const sets = typeof team === 'string' ? Teams.unpack(team) : team;
  return validator.validateTeam(sets) || [];
}

module.exports = {
  BASE_FORMAT,
  DRAFT_RULES,
  buildFormatId,
  resolve,
  validateTeam,
  /** The ready-to-use format id string for BattleRoom/`>start`. */
  DRAFT_FORMAT_ID: buildFormatId(),
};
