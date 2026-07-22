---
name: bug-finding-tests
description: Simulate an 8-player season with real headless battles, then fan out one agent per game to audit the replay-stats scraper's attribution against the raw battle log (damage & healing source/target, presence/started/finished, kill credit, and dealt-vs-taken conservation totals). Surfaces attribution bugs the hand-authored unit tests don't cover. Use when asked to bug-hunt the scraper / stat attribution, or to sanity-check it after scraper changes.
---

# Scraper attribution bug-finder

Runs a real 8-player sim season, then has an agent audit every game's scraped stats
against its battle log to find damage/healing that was mis-attributed, dropped, or
double-counted. The unit tests (`server.Tests/ScraperAttributionTests.cs`) cover known
cases by hand; this finds the unknown ones on real random battles.

The whole run happens on a **throwaway localhost instance**, never the live `:5211`
server (per the project's verify-on-isolated rule). Nothing is written to the real DB.

## 1. Build and start an isolated instance

```bash
cd "c:/Users/3hoze/Desktop/Pokemon Draft League/server"
dotnet build DraftLeague.Web.csproj -clp:ErrorsOnly | tail -3
rm -f verify.db*
ASPNETCORE_ENVIRONMENT=Development ConnectionStrings__Default="Data Source=verify.db" \
  ./bin/Debug/net9.0/DraftLeague.Web.exe --urls http://localhost:5199 >/tmp/bugfind.log 2>&1 &
for i in $(seq 1 25); do [ "$(curl -s -o /dev/null -w '%{http_code}' http://localhost:5199/ 2>/dev/null)" = "200" ] && break; sleep 1; done
TOK=$(curl -s -X POST "http://localhost:5199/dev/token/900001?admin=true" | python -c "import sys,json;print(json.load(sys.stdin)['accessToken'])")
```

## 2. Simulate the 8-player season (real battles)

Real headless Showdown battles, so the logs (and thus the stats) are genuine. A full
round-robin is 28 games.

```bash
curl -s -X POST "http://localhost:5199/dev/simulate-random-season?teams=8&real=true" \
  -H "Authorization: Bearer $TOK" --max-time 300 \
  | python -c "import sys,json;r=json.load(sys.stdin);print(r['matches'],'matches, realBattles',r['realBattles'])"
```

If `realBattles` is `false`, the Node battle runner wasn't found; stop and fix that
(the audit is meaningless on fabricated stats).

## 3. Dump each game's audit bundle

`GET /dev/match-scrape/{id}` returns, for one match: the raw `log`, every mon's FULL
`GameStat` (all damage/heal/presence buckets, not the trimmed schedule strip), `turns`,
the recorded `result`/scores, and both team pastes (`homeExport`/`awayExport`). Save one
file per game into the scratchpad so the agents can read them without huge prompts.

```bash
OUT="<scratchpad>/bugfind"; mkdir -p "$OUT"   # use the session scratchpad dir
IDS=$(python -c "import sqlite3;print(' '.join(str(r[0]) for r in sqlite3.connect('verify.db').execute('SELECT Id FROM Matches WHERE ReplayLog IS NOT NULL ORDER BY Id')))")
for id in $IDS; do
  curl -s "http://localhost:5199/dev/match-scrape/$id" -H "Authorization: Bearer $TOK" > "$OUT/match-$id.json"
done
echo "$IDS"
```

Optional fast pre-filter (cheap, scraper-internal): flag games whose dealt-to-others
!= taken-from-others before spending agents, they are guaranteed to contain a bug.

```bash
for f in "$OUT"/match-*.json; do python -c "
import json,sys
d=json.load(open('$f'))
if not d.get('played'): sys.exit()
M=d['mons']
dealt=sum(x['dealtDirect']+x['dealtIndirect']+x['dealtAllyDirect']+x['dealtAllyIndirect'] for x in M)
taken=sum(x['takenDirect']+x['takenIndirect'] for x in M)
if abs(dealt-taken)>1: print('$f CONSERVATION off by %.2f'%(dealt-taken))
"; done
```

## 4. Fan out one auditor agent per game

Invoking this skill is explicit opt-in to multi-agent orchestration, so use the
**Workflow** tool: pipeline over the match ids, one agent per game, each returning
structured findings. Give every agent the audit spec below and the path to its bundle
file (it Reads the JSON itself). Keep the agent model at the session default.

Each agent must run ALL of these checks and report every discrepancy with the exact log
lines that prove it. HP in the log is a fraction: the mon's OWN side shows real HP
(`150/281`), the OPPONENT shows a percent (`70/100`); the scraper works in "% of that
mon's max bar", so convert each own-side hit to `(prevHP-newHP)/maxHP*100` (track each
slot's current HP and its `/Y` denominator from `|switch|`; the percent side is `/100`).

**A. Damage conservation (scraper-internal, no log parse).** Σ over mons of
`dealtDirect+dealtIndirect+dealtAllyDirect+dealtAllyIndirect` must equal Σ of
`takenDirect+takenIndirect`. Every point dealt to a non-self target is taken by that
target, so any diff > ~1% of a bar means damage was credited to a dealer with no victim
(or a victim with no dealer). This alone catches most attribution bugs.

**B. Damage fully accounted (log vs scraper).** Sum every `|-damage|` hit (as % of the
target's bar). It must equal Σ over mons of `takenDirect+takenIndirect+takenSelf`. Any
shortfall is UNATTRIBUTED damage. Then per mon: Σ of `-damage` targeting its slots must
match that mon's `takenDirect+takenIndirect+takenSelf`.

**C. Healing fully accounted (log vs scraper).** Sum every `|-heal|` gain (as % of bar).
It must equal Σ over mons of `recovered+healed+healedEnemy`. Any shortfall is
unattributed healing.

**D. Source & target correctness.** For every `-damage`/`-heal` line carrying `[from]`
/`[of]`, confirm the scraper credited the RIGHT mon, per the rules in
`memory/showdown-damage-heal-attribution.md` and `server/Services/ReplayStatsScraper.cs`:
- a move's own damage/heal (bare, no `[from]`) → the mover / the healed mon itself;
- `[of]`-tagged hitback (Rocky Helmet, Rough Skin, Iron Barbs, Aftermath, Bad Dreams,
  Liquid Ooze, Leech Seed's damage) → the `[of]` holder, credited even after it faints;
- poison/burn chip → the mon that inflicted the status (or SELF for own Toxic/Flame Orb,
  which is `[from] item:` with NO `[of]`); hazards → the setter; weather → uncredited;
- recoil / Life Orb / crash / HP-cost / confusion / own-orb → the victim's `takenSelf`,
  never an opponent's `taken`.
Flag any mon whose dealt/taken/healed bucket is impossible given the log (e.g. a benched
mon credited damage, or opponent damage landing in `takenSelf`).

**E. Presence / started / finished.** `started` must be true iff the mon was an opener
(a `|switch|` before `|turn|1`); `finished` true iff it was on the field and not fainted
at `|win|`/`|tie|`; a mon that never took the field has `activeTurns==0`, `started==false`
and deals no direct damage. Flag contradictions.

**F. Kills / deaths.** Σ `deaths` must equal the count of `|faint|` lines, and each mon's
`deaths` is 0 or 1. Σ `kills` <= Σ `deaths` (self-KOs / uncredited faints have no killer).
Each faint's killer should be the last mon to damage that slot; flag a kill credited to a
mon that never damaged the victim.

Have each agent return, via structured output: `matchId`, `clean` (bool), and
`findings[]` where each finding is `{check: "A".."F", severity, summary, monsInvolved,
expected, gotFromScraper, logEvidence}`. Tolerance for HP arithmetic: ~1% of a bar
(rounding); anything larger is a real discrepancy.

## 5. Aggregate and report

Collect findings across all games. Dedupe by (check, effect/log-pattern), because a real
scraper bug reproduces across many games. Report, most-systematic first: the check that
failed, how many games hit it, a representative game id + the minimal log excerpt, and
the scraper's wrong value vs the correct one. Call out which are systematic (same effect
every time, a genuine bug) vs one-offs (likely an unresolved mon / Illusion / a log
oddity). If every game is clean within tolerance, say so plainly.

## 6. Before ANY code change: submit the replay + get approval

This is a hard gate. **Never edit `ReplayStatsScraper.cs` (or any scraper code) off the
back of a finding without the user's approval first.** For each confirmed bug, present:

1. **The replay.** Save the offending game's raw log to a file
   (`<scratchpad>/bugfind/<id>.log`) and, while the isolated instance is still up, give
   the viewable render link `http://localhost:5199/api/matches/<liveId>/replay` (use a
   match id that still exists in the CURRENT `verify.db`; note that each new sim reseeds
   and deletes earlier games' matches, so if you want a viewable replay, audit and gate
   ONE season before simulating the next, or re-inject the saved log). The raw `.log`
   can also be dropped into Showdown's replay viewer.
2. **A brief description of what went wrong** (2-4 sentences): which mon, which check
   failed, the scraper's value vs the log's, and the suspected cause with the minimal
   log excerpt.
3. **The proposed fix** (one line: where in `ReplayStatsScraper.cs`, what change).

Then STOP and ask for approval. Only after the user approves: add a matching hand-
authored case to `server.Tests/ScraperAttributionTests.cs` (shared `ReplayLogRunner`)
that reproduces the bug, make the fix, and confirm the new test plus the full suite pass.

## 7. Teardown (only after the findings are reported / approved)

Keep the instance up until the replays have been submitted for review (they render from
it). Then:

```bash
powershell -Command "Get-NetTCPConnection -LocalPort 5199 -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id \$_.OwningProcess -Force }"
rm -f "c:/Users/3hoze/Desktop/Pokemon Draft League/server/verify.db"*
```

Kill by PORT, not process name (the isolated exe shares the live server's process name).
