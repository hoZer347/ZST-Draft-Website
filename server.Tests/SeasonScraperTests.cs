using DraftLeague.Web.Models;
using DraftLeague.Web.Services;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The battle-stats pipeline that powers the team page (KOs/Faints, Damage
/// Ratio, Presence) and the MVP badge, tested at its deterministic core:
/// <see cref="ReplayStatsScraper.Scrape"/> against a hand-authored Showdown log
/// whose every number is known. No network, no DB — just the parser.
/// </summary>
public class SeasonScraperTests
{
    // A complete singles game. Alice (Pikachu/Snorlax) beats Bob (Gengar/Blissey):
    // Pikachu crits and KOs Gengar, is badly poisoned by Blissey's Toxic, Recovers,
    // then chips Blissey while the poison ticks. Snorlax never leaves the bench.
    private const string Log = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Pikachu, M|item
        |poke|p1|Snorlax, M|item
        |poke|p2|Gengar, M|item
        |poke|p2|Blissey, F|item
        |start
        |switch|p1a: Pikachu|Pikachu, M|100/100
        |switch|p2a: Gengar|Gengar, M|100/100
        |turn|1
        |move|p1a: Pikachu|Thunderbolt|p2a: Gengar
        |-crit|p2a: Gengar
        |-damage|p2a: Gengar|50/100
        |move|p2a: Gengar|Shadow Ball|p1a: Pikachu
        |-damage|p1a: Pikachu|60/100
        |turn|2
        |move|p1a: Pikachu|Thunderbolt|p2a: Gengar
        |-damage|p2a: Gengar|0 fnt
        |faint|p2a: Gengar
        |switch|p2a: Blissey|Blissey, F|100/100
        |turn|3
        |move|p2a: Blissey|Toxic|p1a: Pikachu
        |-status|p1a: Pikachu|tox
        |move|p1a: Pikachu|Recover|p1a: Pikachu
        |-heal|p1a: Pikachu|100/100
        |-damage|p1a: Pikachu|94/100|[from] psn
        |turn|4
        |move|p1a: Pikachu|Thunderbolt|p2a: Blissey
        |-damage|p2a: Blissey|70/100
        |-damage|p1a: Pikachu|82/100|[from] psn
        |win|Alice
        """;

    /// <summary>Resolve species→Pick, one Pick per (side, species) named in the log.</summary>
    private static (ReplayStatsScraper.Result Result, Dictionary<string, Pick> Picks) Run()
    {
        var byKey = new Dictionary<string, Pick>();
        var id = 1;
        Pick Get(string side, string species)
        {
            var key = side + ":" + new string(species.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (!byKey.TryGetValue(key, out var p))
                byKey[key] = p = new Pick { Id = id++ };
            return p;
        }

        var result = ReplayStatsScraper.Scrape(Log, (side, species) => Get(side, species));
        // Re-key by species alone for readable assertions (sides don't collide here).
        var named = new Dictionary<string, Pick>();
        foreach (var (k, v) in byKey) named[k.Split(':')[1]] = v;
        return (result, named);
    }

    [Fact]
    public void Turn_count_is_scraped()
    {
        var (r, _) = Run();
        Assert.Equal(4, r.Turns);
    }

    [Fact]
    public void Every_brought_mon_counts_as_played_even_on_the_bench()
    {
        var (r, picks) = Run();
        // All four brought mons appear in the stat table (GamesPlayed is folded in
        // by the sim; here "present in the map" is the played signal).
        foreach (var name in new[] { "pikachu", "snorlax", "gengar", "blissey" })
            Assert.True(r.Stats.ContainsKey(picks[name]), $"{name} should be recorded as played");
    }

    [Fact]
    public void Kills_and_faints_are_credited_to_the_right_mons()
    {
        var (r, picks) = Run();
        Assert.Equal(1, r.Stats[picks["pikachu"]].Kills);  // KO'd Gengar
        Assert.Equal(0, r.Stats[picks["pikachu"]].Deaths);
        Assert.Equal(1, r.Stats[picks["gengar"]].Deaths);  // fainted
        Assert.Equal(0, r.Stats[picks["gengar"]].Kills);
        Assert.Equal(0, r.Stats[picks["blissey"]].Deaths);
    }

    [Fact]
    public void Crits_are_credited_to_the_attacker()
    {
        var (r, picks) = Run();
        Assert.Equal(1, r.Stats[picks["pikachu"]].Crits);
        Assert.Equal(0, r.Stats[picks["gengar"]].Crits);
    }

    [Fact]
    public void Presence_active_turns_track_who_was_on_the_field()
    {
        var (r, picks) = Run();
        Assert.Equal(4, r.Stats[picks["pikachu"]].ActiveTurns); // all four turns
        Assert.Equal(2, r.Stats[picks["gengar"]].ActiveTurns);  // turns 1–2
        Assert.Equal(2, r.Stats[picks["blissey"]].ActiveTurns); // turns 3–4
        Assert.Equal(0, r.Stats[picks["snorlax"]].ActiveTurns); // never switched in
    }

    [Fact]
    public void Damage_dealt_and_taken_balance_including_residual_poison()
    {
        var (r, picks) = Run();
        // Pikachu: 50 + 50 (Gengar) + 30 (Blissey) = 130 dealt.
        Assert.Equal(130, r.Stats[picks["pikachu"]].Dealt, 2);
        // Pikachu took 40 (Shadow Ball) + 6 + 12 (two poison ticks) = 58.
        Assert.Equal(58, r.Stats[picks["pikachu"]].Taken, 2);
        // Gengar dealt 40 (Shadow Ball), took 100 (two Thunderbolts).
        Assert.Equal(40, r.Stats[picks["gengar"]].Dealt, 2);
        Assert.Equal(100, r.Stats[picks["gengar"]].Taken, 2);
        // Blissey's Toxic damage is residual, credited to the Toxic user: 6 + 12 = 18.
        Assert.Equal(18, r.Stats[picks["blissey"]].Dealt, 2);
        Assert.Equal(30, r.Stats[picks["blissey"]].Taken, 2); // the Thunderbolt
    }

    [Fact]
    public void Self_healing_is_recorded_as_recovery_not_ally_heal()
    {
        var (r, picks) = Run();
        Assert.Equal(40, r.Stats[picks["pikachu"]].Recovered, 2); // Recover 60→100
        Assert.Equal(0, r.Stats[picks["pikachu"]].Healed, 2);     // healed nobody else
    }

    [Fact]
    public void Unknown_mons_resolve_to_null_without_crashing()
    {
        // A resolver that knows nobody: the scraper must simply record nothing.
        var r = ReplayStatsScraper.Scrape(Log, (_, _) => null);
        Assert.Empty(r.Stats);
        Assert.Equal(4, r.Turns); // turns are still counted
    }
}
