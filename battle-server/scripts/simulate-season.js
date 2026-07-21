'use strict';

// Headless season runner: reads a JSON spec of matchups from stdin, plays each
// as a real Showdown battle between two random teams built from the two rosters,
// and writes the full battle logs back out as JSON. The .NET season simulator
// feeds these logs into the same replay-stats pipeline a real reported game uses,
// so a "random season" produces genuine, recordable stats instead of made-up ones.
//
//   stdin : { "matches": [ { "homeTeam": [slug,...], "awayTeam": [slug,...] }, ... ] }
//   stdout: [ { "winner": "p1"|"p2"|null, "turns": N, "log": "<full battle log>",
//              "p1Export": "<pokepaste>", "p2Export": "<pokepaste>" }, ... ]
// The two exports are each side's ACTUAL random build, so the season simulator stores
// them on the match exactly like a real reported game does (the Team build links).
//
// Both sides are driven by a RandomPlayerAI that picks a random legal MOVE each
// turn and never voluntarily switches. The one gimmick it takes is Terastallization:
// a C-tier drafted mon teras to its drafted type the first turn it can (see
// TeraPlayerAI + buildTeamWithTera), matching how the league plays C-tier tera.
// Forced switches after a faint still happen.

const { BattleStream, getPlayerStreams, Teams } = require('pokemon-showdown');
const { RandomPlayerAI } = require('pokemon-showdown/dist/sim/tools/random-player-ai');
const { buildTeamWithTera } = require('../lib/random-team');

// A RandomPlayerAI that takes the league's two gimmicks the first turn it can:
// it MEGA-EVOLVES any mon holding a mega stone (a strict upgrade, so always take
// it), and TERASTALLIZES a marked C-tier mon to its drafted type. It leaves the
// base AI's random move choice untouched and just appends `mega` / `terastallize`
// to that move. Each is once per SIDE per battle (not once per mon). A mon can't
// do both, so megas take mega and C-tier non-mega mons take tera.
class TeraPlayerAI extends RandomPlayerAI {
  constructor(playerStream, teraNames) {
    super(playerStream);
    this.teraNames = teraNames instanceof Set ? teraNames : new Set(teraNames || []);
    this.teraUsed = false;
    this.megaUsed = false;
  }

  receiveRequest(request) {
    this._request = request; // captured so choose() can see which slots may tera/mega
    super.receiveRequest(request);
  }

  choose(choice) {
    const req = this._request;
    if (req && req.active) {
      const pokemon = req.side && req.side.pokemon || [];
      const parts = choice.split(', ');
      // Mega-evolve the first eligible mon (once per side). `canMegaEvo` is set on
      // the request when the mon holds its stone and hasn't evolved yet; the AI
      // otherwise never takes the free upgrade.
      for (let i = 0; i < parts.length && !this.megaUsed; i++) {
        const act = req.active[i];
        const part = parts[i];
        if (!act || !act.canMegaEvo) continue;
        if (!/^move /.test(part)) continue;
        if (/\b(terastallize|mega|zmove|dynamax|ultra|max)\b/.test(part)) continue;
        parts[i] = `${part} mega`;
        this.megaUsed = true;
      }
      // Terastallize a marked C-tier mon (once per side).
      if (this.teraNames.size && !this.teraUsed) {
        for (let i = 0; i < parts.length && !this.teraUsed; i++) {
          const act = req.active[i];
          const part = parts[i];
          if (!act || !act.canTerastallize) continue;      // not this mon, or already tera'd
          if (!/^move /.test(part)) continue;              // only when it's actually attacking
          if (/\b(terastallize|mega|zmove|dynamax|ultra|max)\b/.test(part)) continue; // has a gimmick
          const mon = pokemon[i] || {};
          const nick = (mon.ident || '').split(': ')[1] || '';
          const species = (mon.details || '').split(',')[0].trim();
          if (this.teraNames.has(nick) || this.teraNames.has(species)) {
            parts[i] = `${part} terastallize`;
            this.teraUsed = true; // once per side per battle
          }
        }
      }
      choice = parts.join(', ');
    }
    return super.choose(choice);
  }
}

// The ZST Season 4 format is National Dex DOUBLES. The headless sim uses a
// doubles custom game (gen9's dex → megas usable; no tier banlist, so the draft
// pool decides legality; no learnset validation for the random movesets). Endless
// Battle Clause so a stall can't hang the run, it bans no moves, so it never
// rejects a random team.
const FORMAT = 'gen9doublescustomgame@@@Endless Battle Clause';
const BRING = 6;        // straight doubles: bring 6, two active at a time (NOT VGC's bring-6-pick-4)
const MAX_TURNS = 1000; // backstop in case a battle somehow never resolves

// Export a packed team to pokepaste / Showdown import text, so the season simulator
// can store each side's ACTUAL build on the match (the same builds a real reported
// game captures). Best-effort: any hiccup just yields null and the battle still records.
function exportTeam(packed) {
  try { return Teams.export(Teams.unpack(packed)) || null; }
  catch { return null; }
}

async function runBattle(match) {
  const streams = getPlayerStreams(new BattleStream());
  const spec = { formatid: FORMAT };
  // Name the players after the coaches (their Discord / dummy names) so the log
  // and replay show who actually played, not "Home"/"Away".
  const home = buildTeamWithTera(match.homeTeam || [], BRING);
  const away = buildTeamWithTera(match.awayTeam || [], BRING);
  const p1 = { name: match.homeName || 'Home', team: home.team };
  const p2 = { name: match.awayName || 'Away', team: away.team };

  new TeraPlayerAI(streams.p1, home.teraNames).start();
  new TeraPlayerAI(streams.p2, away.teraNames).start();

  let winnerName = null;
  let turns = 0;
  const lines = [];
  // Read the SPECTATOR stream (the clean public battle log, no |split|/|request|
  // noise), so the same string both scrapes into stats AND renders as a replay.
  const done = (async () => {
    for await (const chunk of streams.spectator) {
      for (const line of chunk.split('\n')) {
        lines.push(line);
        if (line.startsWith('|turn|')) {
          turns = Number(line.slice(6)) || turns;
          if (turns > MAX_TURNS) void streams.omniscient.write('>forcetie');
        } else if (line.startsWith('|win|')) {
          winnerName = line.slice(5).trim();
        }
      }
    }
  })();

  void streams.omniscient.write(
    `>start ${JSON.stringify(spec)}\n` +
    `>player p1 ${JSON.stringify(p1)}\n` +
    `>player p2 ${JSON.stringify(p2)}`
  );
  await done;

  // Map the winning player's name back to its side, normalised (lowercase
  // alnum) so any sanitisation Showdown does to the display name still matches.
  const id = (s) => (s || '').toLowerCase().replace(/[^a-z0-9]/g, '');
  const winner = id(winnerName) === id(p1.name) ? 'p1'
    : id(winnerName) === id(p2.name) ? 'p2' : null;
  return {
    winner, turns, log: lines.join('\n'),
    p1Export: exportTeam(home.team), p2Export: exportTeam(away.team),
  };
}

function readStdin() {
  return new Promise((resolve, reject) => {
    let data = '';
    process.stdin.setEncoding('utf8');
    process.stdin.on('data', (c) => { data += c; });
    process.stdin.on('end', () => resolve(data));
    process.stdin.on('error', reject);
  });
}

async function main() {
  const spec = JSON.parse(await readStdin());
  const matches = Array.isArray(spec.matches) ? spec.matches : [];
  const results = [];
  for (const m of matches) {
    results.push(await runBattle(m));
  }
  process.stdout.write(JSON.stringify(results));
}

main().catch((e) => { console.error(e); process.exit(1); });
