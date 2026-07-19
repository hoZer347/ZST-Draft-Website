'use strict';
// Performance guard for the tier-list filter — the hot path that runs on every
// keystroke/checkbox in the browse view. It sets a benchmark and FAILS the
// suite when a change makes filtering meaningfully slower.
//
// Wall-clock ms is machine-dependent, so we normalise: a fixed calibration loop
// measures this CPU, and the recorded score is (filter time ÷ calibration time)
// — a unitless ratio that's stable across machines and CI. The baseline lives
// in perf-baseline.json; the first run (or UPDATE_BASELINE=1) writes it.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { poolMatches, rolesOf } = require('../pool-logic.js');
const { randomPool } = require('./helpers.js');

const BASELINE_PATH = path.join(__dirname, 'perf-baseline.json');
const POOL_SIZE = 5000;        // well above the real pool (~1.2k) to stress it
const RENDER_REPEATS = 40;     // one measured batch ≈ 40 full re-renders
const WARN_FACTOR = 1.30;      // >30% slower than baseline → warn
const FAIL_FACTOR = 2.50;      // >150% slower → fail (generous, absorbs noise)

// A representative filter render touches the whole pool once per facet (the
// facet-count passes) plus the final full pass — mirroring tlRefreshFacets +
// renderTierList in app.js.
const SKIPS = [null, 'type1', 'type2', 'tier', 'roles', 'search', 'avail'];
const CRITERIA = { search: 'mon', availableOnly: true, tiers: ['S', 'B'], type1: 'Water', type2: '', roles: ['Fast'] };

function oneRender(pool) {
  let passed = 0;
  for (const skip of SKIPS) {
    for (let i = 0; i < pool.length; i++) if (poolMatches(pool[i], CRITERIA, skip)) passed++;
  }
  // Role facet also enumerates rolesOf across the pool (checkbox enable/disable).
  for (let i = 0; i < pool.length; i++) passed += rolesOf(pool[i]).length;
  return passed;
}

// Fixed, pool-logic-independent workload → this machine's raw speed.
function calibrationMs() {
  const start = performance.now();
  let x = 0;
  for (let i = 0; i < 20_000_000; i++) x += Math.sqrt(i % 997) * 1.0000001;
  const ms = performance.now() - start;
  if (x < 0) throw new Error('unreachable'); // keep the loop from being optimised away
  return ms;
}

function bestOf(fn, rounds) {
  let best = Infinity;
  for (let r = 0; r < rounds; r++) {
    const start = performance.now();
    fn();
    best = Math.min(best, performance.now() - start);
  }
  return best;
}

test('tier-list filter stays within its performance budget', () => {
  const pool = randomPool(7, POOL_SIZE);

  // Warm up the JIT before measuring.
  for (let r = 0; r < 5; r++) oneRender(pool);

  const calib = bestOf(() => calibrationMs(), 3);
  const filterMs = bestOf(() => { for (let r = 0; r < RENDER_REPEATS; r++) oneRender(pool); }, 5);
  const score = filterMs / calib; // unitless, machine-independent

  const meta = { poolSize: POOL_SIZE, renderRepeats: RENDER_REPEATS, skips: SKIPS.length };
  const updating = process.env.UPDATE_BASELINE === '1';
  let baseline = null;
  if (fs.existsSync(BASELINE_PATH) && !updating) {
    baseline = JSON.parse(fs.readFileSync(BASELINE_PATH, 'utf8'));
  }

  if (!baseline) {
    fs.writeFileSync(BASELINE_PATH, JSON.stringify({ score, ...meta }, null, 2) + '\n');
    console.log(`[tierlist bench] wrote baseline score=${score.toFixed(3)} ` +
      `(filter ${filterMs.toFixed(1)}ms / calib ${calib.toFixed(1)}ms, pool ${POOL_SIZE})`);
    return; // nothing to compare against yet
  }

  const ratio = score / baseline.score;
  const line = `[tierlist bench] score=${score.toFixed(3)} baseline=${baseline.score.toFixed(3)} ` +
    `ratio=${ratio.toFixed(2)}x (filter ${filterMs.toFixed(1)}ms / calib ${calib.toFixed(1)}ms)`;

  if (ratio >= FAIL_FACTOR) {
    assert.fail(`${line}\nTier-list filtering regressed ≥${FAIL_FACTOR}× vs baseline. ` +
      `If this is an intentional cost, re-baseline with UPDATE_BASELINE=1.`);
  } else if (ratio >= WARN_FACTOR) {
    console.warn(`⚠️  ${line}\n   Filtering is >${Math.round((WARN_FACTOR - 1) * 100)}% slower ` +
      `than baseline — a feature may have degraded the tier list.`);
  } else {
    console.log(`${line} ✓`);
    // A big, durable speedup is worth locking in so future regressions are
    // measured from the new floor — but only when explicitly re-baselining.
    if (updating) {
      fs.writeFileSync(BASELINE_PATH, JSON.stringify({ score, ...meta }, null, 2) + '\n');
    }
  }
});
