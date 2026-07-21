'use strict';

// Generates random-but-legal packed teams from a drafted roster, for the draft
// app's "pre-build my teams" option, it seeds a coach's teambuilder with a ready
// starter team per week that they can then edit. Same builder the headless sim
// uses (lib/random-team.js): even EVs, neutral nature, random ability, random 4
// moves from the movepool, random item unless mega.
//
//   stdin : { "roster": [slug,...], "count": N }        → N teams from one roster
//        or { "rosters": [[slug,...], ...] }             → one team per roster (batch)
//   stdout: { "teams": ["<packed team>", ... ] }         // packed teams (Teams.pack)

const { buildRandomTeam } = require('../lib/random-team');

function readStdin() {
  return new Promise((resolve, reject) => {
    let data = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (c) => { data += c; });
    process.stdin.on('end', () => resolve(data));
    process.stdin.on('error', reject);
  });
}

function oneTeam(roster) {
  try { return buildRandomTeam(roster, 6); } // doubles brings 6
  catch { return ''; }                        // roster with no Showdown-known species → blank
}

async function main() {
  const spec = JSON.parse(await readStdin());
  let teams;
  if (Array.isArray(spec.rosters)) {
    // Batch: one team per roster (used for the admin "demo teams for every player").
    teams = spec.rosters.map((r) => oneTeam(Array.isArray(r) ? r : []));
  } else {
    // N teams from a single roster.
    const roster = Array.isArray(spec.roster) ? spec.roster : [];
    const count = Math.max(1, Math.min(30, Number(spec.count) || 1));
    teams = Array.from({ length: count }, () => oneTeam(roster));
  }
  process.stdout.write(JSON.stringify({ teams }));
}

main().catch((e) => { console.error(e); process.exit(1); });
