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
    private static (ReplayStatsScraper.Result Result, Dictionary<string, Pick> Picks) Run() => RunLog(Log);

    private static (ReplayStatsScraper.Result Result, Dictionary<string, Pick> Picks) RunLog(string log)
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

        var result = ReplayStatsScraper.Scrape(log, (side, species) => Get(side, species));
        // Re-key by species alone for readable assertions (sides don't collide here).
        var named = new Dictionary<string, Pick>();
        foreach (var (k, v) in byKey) named[k.Split(':')[1]] = v;
        return (result, named);
    }

    // A doubles turn: Cresselia (p1a) uses Lunar Blessing, which heals BOTH itself
    // and its ally Amoonguss (p1b). Showdown logs a move's healing as a bare
    // |-heal| with no [from]/[of] tags, so the ally's heal must be attributed to
    // Cresselia (the mon that just moved), not banked as the ally's own recovery.
    private const string DoublesHealLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Cresselia, F|item
        |poke|p1|Amoonguss, M|item
        |poke|p2|Incineroar, M|item
        |poke|p2|Rillaboom, M|item
        |start
        |switch|p1a: Cresselia|Cresselia, F|100/100
        |switch|p1b: Amoonguss|Amoonguss, M|100/100
        |switch|p2a: Incineroar|Incineroar, M|100/100
        |switch|p2b: Rillaboom|Rillaboom, M|100/100
        |turn|1
        |move|p2a: Incineroar|Fake Out|p1a: Cresselia
        |-damage|p1a: Cresselia|85/100
        |move|p2b: Rillaboom|Wood Hammer|p1b: Amoonguss
        |-damage|p1b: Amoonguss|60/100
        |move|p1a: Cresselia|Lunar Blessing|p1a: Cresselia
        |-heal|p1a: Cresselia|100/100
        |-heal|p1b: Amoonguss|85/100
        |turn|2
        |win|Alice
        """;

    [Fact]
    public void Ally_heal_from_a_move_is_credited_to_the_caster_not_the_recipient()
    {
        var (r, picks) = RunLog(DoublesHealLog);

        // Cresselia's own portion (85→100 = 15) is recovery; the ally portion
        // (Amoonguss 60→85 = 25) is healing given, credited to Cresselia.
        Assert.Equal(15, r.Stats[picks["cresselia"]].Recovered, 2);
        Assert.Equal(25, r.Stats[picks["cresselia"]].Healed, 2);

        // Amoonguss neither recovered on its own nor healed anyone.
        Assert.Equal(0, r.Stats[picks["amoonguss"]].Recovered, 2);
        Assert.Equal(0, r.Stats[picks["amoonguss"]].Healed, 2);
    }

    // Ferrothorn sets Stealth Rock; Charizard switches into it and faints. Both
    // the chip damage and the KO belong to Ferrothorn, the setter — which is
    // still on the field on the other side.
    private const string HazardLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Ferrothorn, M|item
        |poke|p1|Skarmory, M|item
        |poke|p2|Talonflame, M|item
        |poke|p2|Charizard, M|item
        |start
        |switch|p1a: Ferrothorn|Ferrothorn, M|100/100
        |switch|p2a: Talonflame|Talonflame, M|100/100
        |turn|1
        |move|p1a: Ferrothorn|Stealth Rock|p2a: Talonflame
        |-sidestart|p2: Bob|move: Stealth Rock
        |turn|2
        |switch|p2a: Charizard|Charizard, M|100/100
        |-damage|p2a: Charizard|0 fnt|[from] Stealth Rock
        |faint|p2a: Charizard
        |win|Alice
        """;

    [Fact]
    public void Entry_hazard_damage_and_kills_are_credited_to_the_setter()
    {
        var (r, picks) = RunLog(HazardLog);

        // All indirect — Ferrothorn never hit Charizard with a move.
        Assert.Equal(100, r.Stats[picks["ferrothorn"]].DealtIndirect, 2);
        Assert.Equal(0, r.Stats[picks["ferrothorn"]].DealtDirect, 2);
        Assert.Equal(1, r.Stats[picks["ferrothorn"]].Kills);
        Assert.Equal(100, r.Stats[picks["charizard"]].TakenIndirect, 2);
        Assert.Equal(0, r.Stats[picks["charizard"]].TakenDirect, 2);
        Assert.Equal(1, r.Stats[picks["charizard"]].Deaths);
    }

    // Gengar poisons Snorlax, then switches out for Clefable. The poison ticks
    // it down and eventually KOs it — both the chip and the kill must still trace
    // to Gengar, off the field, not the Clefable now standing in its slot.
    private const string PoisonSwitchLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Gengar, M|item
        |poke|p1|Clefable, M|item
        |poke|p2|Snorlax, M|item
        |start
        |switch|p1a: Gengar|Gengar, M|100/100
        |switch|p2a: Snorlax|Snorlax, M|100/100
        |turn|1
        |move|p1a: Gengar|Toxic|p2a: Snorlax
        |-status|p2a: Snorlax|tox
        |turn|2
        |switch|p1a: Clefable|Clefable, M|100/100
        |-damage|p2a: Snorlax|50/100|[from] psn
        |turn|3
        |-damage|p2a: Snorlax|0 fnt|[from] psn
        |faint|p2a: Snorlax
        |win|Alice
        """;

    [Fact]
    public void Residual_poison_ko_credits_the_inflictor_even_after_it_switches_out()
    {
        var (r, picks) = RunLog(PoisonSwitchLog);

        Assert.Equal(1, r.Stats[picks["gengar"]].Kills);              // KO still Gengar's
        Assert.Equal(0, r.Stats[picks["clefable"]].Kills);           // not the mon now on-field
        Assert.Equal(100, r.Stats[picks["gengar"]].DealtIndirect, 2); // 50 + 50 poison chip, all indirect
        Assert.Equal(0, r.Stats[picks["gengar"]].DealtDirect, 2);
        Assert.Equal(1, r.Stats[picks["snorlax"]].Deaths);
    }

    // Rillaboom's Grassy Surge sets Grassy Terrain on switch-in. At end of turn it
    // heals every grounded mon: its ally Incineroar (credited to Rillaboom as ally-
    // healing) and the foe Garchomp (the foe's own recovery, no credit to Rillaboom).
    private const string GrassyTerrainLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Rillaboom, M|item
        |poke|p1|Incineroar, M|item
        |poke|p2|Garchomp, M|item
        |poke|p2|Landorus, M|item
        |start
        |switch|p1a: Rillaboom|Rillaboom, M|100/100
        |-fieldstart|move: Grassy Terrain|[from] ability: Grassy Surge|[of] p1a: Rillaboom
        |switch|p1b: Incineroar|Incineroar, M|100/100
        |switch|p2a: Garchomp|Garchomp, M|100/100
        |switch|p2b: Landorus|Landorus, M|100/100
        |turn|1
        |move|p2a: Garchomp|Dragon Claw|p1b: Incineroar
        |-damage|p1b: Incineroar|75/100
        |move|p1a: Rillaboom|Fake Out|p2a: Garchomp
        |-damage|p2a: Garchomp|85/100
        |-heal|p1b: Incineroar|85/100|[from] Grassy Terrain
        |-heal|p2a: Garchomp|95/100|[from] Grassy Terrain
        |turn|2
        |win|Alice
        """;

    [Fact]
    public void Grassy_terrain_ally_healing_is_credited_to_the_setter()
    {
        var (r, picks) = RunLog(GrassyTerrainLog);

        // Incineroar 75→85 (+10) healed by its ally's terrain → Rillaboom's ally-heal.
        Assert.Equal(10, r.Stats[picks["rillaboom"]].Healed, 2);
        Assert.Equal(0, r.Stats[picks["incineroar"]].Recovered, 2);

        // Garchomp 85→95 (+10) is the foe topped up by Rillaboom's terrain: it's the
        // setter's enemy-healing (excluded by default), not the foe's own recovery.
        Assert.Equal(10, r.Stats[picks["rillaboom"]].HealedEnemy, 2);
        Assert.Equal(0, r.Stats[picks["garchomp"]].Recovered, 2);
        Assert.Equal(0, r.Stats[picks["garchomp"]].Healed, 2);
    }

    // Doubles: Garchomp's Earthquake hits its own ally Landorus (friendly fire) as
    // well as the two opponents. The ally portion is banked separately and never
    // scores as damage-to-opponents.
    private const string FriendlyFireLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Rillaboom, M|item
        |poke|p1|Incineroar, M|item
        |poke|p2|Garchomp, M|item
        |poke|p2|Landorus, M|item
        |start
        |switch|p1a: Rillaboom|Rillaboom, M|100/100
        |switch|p1b: Incineroar|Incineroar, M|100/100
        |switch|p2a: Garchomp|Garchomp, M|100/100
        |switch|p2b: Landorus|Landorus, M|100/100
        |turn|1
        |move|p2a: Garchomp|Earthquake|p1a: Rillaboom|[spread] p1a,p1b,p2b
        |-damage|p1a: Rillaboom|70/100
        |-damage|p1b: Incineroar|60/100
        |-damage|p2b: Landorus|75/100
        |turn|2
        |win|Bob
        """;

    // Bad Dreams chips the sleeping foe each turn, carrying [of] the Darkrai
    // holder — so it's credited to Darkrai like Rocky Helmet, not left uncredited.
    private const string BadDreamsLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Darkrai, M|item
        |poke|p2|Snorlax, M|item
        |start
        |switch|p1a: Darkrai|Darkrai, M|100/100
        |switch|p2a: Snorlax|Snorlax, M|100/100
        |turn|1
        |move|p1a: Darkrai|Dark Void|p2a: Snorlax
        |-status|p2a: Snorlax|slp
        |turn|2
        |-damage|p2a: Snorlax|88/100|[from] ability: Bad Dreams|[of] p1a: Darkrai
        |win|Alice
        """;

    [Fact]
    public void Bad_dreams_chip_is_credited_to_the_ability_holder_via_of()
    {
        var (r, picks) = RunLog(BadDreamsLog);
        Assert.Equal(12, r.Stats[picks["darkrai"]].DealtIndirect, 2);
        Assert.Equal(12, r.Stats[picks["snorlax"]].TakenIndirect, 2);
    }

    [Fact]
    public void Friendly_fire_from_a_spread_move_is_kept_apart_from_damage_to_opponents()
    {
        var (r, picks) = RunLog(FriendlyFireLog);
        var chomp = r.Stats[picks["garchomp"]];

        // Opponents: Rillaboom 30 + Incineroar 40 = 70 direct to enemies.
        Assert.Equal(70, chomp.DealtDirect, 2);
        // Ally Landorus took 25 — friendly fire, banked apart, not in DealtDirect.
        Assert.Equal(25, chomp.DealtAllyDirect, 2);
        Assert.Equal(25, r.Stats[picks["landorus"]].TakenDirect, 2);
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
