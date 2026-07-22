using DraftLeague.Web.Models;
using DraftLeague.Web.Services;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Runs a hand-authored Showdown battle log through <see cref="ReplayStatsScraper"/>,
/// giving each distinct (side, species) a stable Pick, and returns the per-mon
/// stats re-keyed by species id for readable assertions. Test logs must therefore
/// use a distinct species per side (no mirror matches).
/// </summary>
internal static class ReplayLogRunner
{
    public static (ReplayStatsScraper.Result Result, Dictionary<string, Pick> Picks) Run(string log)
    {
        // Resolve exactly as production does: a per-side base-id → pick map read
        // through ResolveInMap, so a mon's base and mega/battle forms (Charizard ↔
        // Charizard-Mega-Y, Palafin ↔ Palafin-Hero) collapse to ONE drafted pick,
        // and pre-form-change damage lands on the same row.
        var bySide = new Dictionary<string, Dictionary<string, Pick>>();
        var named = new Dictionary<string, Pick>();
        var id = 1;
        Pick Get(string side, string species)
        {
            if (!bySide.TryGetValue(side, out var map)) bySide[side] = map = new();
            var p = ReplayStatsScraper.ResolveInMap(map, species);
            if (p is null) map[ReplayStatsScraper.BaseId(species)] = p = new Pick { Id = id++ };
            var sid = new string(species.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            named.TryAdd(sid, p); // look up by the exact species that switched in
            return p;
        }

        var result = ReplayStatsScraper.Scrape(log, Get);
        return (result, named);
    }

    /// <summary>Stats for a species that appeared, or an all-zero GameStat if it never got a row.</summary>
    public static ReplayStatsScraper.GameStat Of(
        this (ReplayStatsScraper.Result Result, Dictionary<string, Pick> Picks) run, string species)
    {
        var id = new string(species.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return run.Picks.TryGetValue(id, out var pick) && run.Result.Stats.TryGetValue(pick, out var gs)
            ? gs : new ReplayStatsScraper.GameStat();
    }
}
