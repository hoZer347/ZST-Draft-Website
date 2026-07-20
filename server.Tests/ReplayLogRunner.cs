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
        var byKey = new Dictionary<string, Pick>();
        var id = 1;
        Pick Get(string side, string species)
        {
            var key = side + ":" + new string(species.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (!byKey.TryGetValue(key, out var p)) byKey[key] = p = new Pick { Id = id++ };
            return p;
        }

        var result = ReplayStatsScraper.Scrape(log, (side, species) => Get(side, species));
        var named = new Dictionary<string, Pick>();
        foreach (var (k, v) in byKey) named[k.Split(':')[1]] = v;
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
