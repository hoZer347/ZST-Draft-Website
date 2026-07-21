namespace DraftLeague.Web.Tests;

/// <summary>
/// The Started (led the battle — thrown out first) and Finished (still on the field,
/// not fainted, when the game ended) flags the scraper sets per game, which feed the
/// "Starts" and "Finished" season stats.
/// </summary>
public class ScraperStartFinishTests
{
    // Singles: Pikachu leads and KOs Gengar; Snorlax rides the bench the whole game.
    private const string SinglesLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Pikachu|item
        |poke|p1|Snorlax|item
        |poke|p2|Gengar|item
        |start
        |switch|p1a: Pikachu|Pikachu|100/100
        |switch|p2a: Gengar|Gengar|100/100
        |turn|1
        |move|p1a: Pikachu|Thunderbolt|p2a: Gengar
        |-damage|p2a: Gengar|0 fnt
        |faint|p2a: Gengar
        |win|Alice
        """;

    [Fact]
    public void The_two_leads_start_and_a_benched_mon_does_not()
    {
        var run = ReplayLogRunner.Run(SinglesLog);
        Assert.True(run.Of("Pikachu").Started);   // p1 lead
        Assert.True(run.Of("Gengar").Started);    // p2 lead
        Assert.False(run.Of("Snorlax").Started);  // only in team preview, never sent out
    }

    [Fact]
    public void The_standing_winner_finishes_but_the_fainted_loser_does_not()
    {
        var run = ReplayLogRunner.Run(SinglesLog);
        Assert.True(run.Of("Pikachu").Finished);   // alive on the field at |win|
        Assert.False(run.Of("Gengar").Finished);   // fainted → not standing
        Assert.False(run.Of("Snorlax").Finished);  // never on the field
    }

    // A lead faints and is replaced; the replacement then wins standing.
    private const string BenchReplacementLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Pikachu|item
        |poke|p1|Snorlax|item
        |poke|p2|Gengar|item
        |start
        |switch|p1a: Pikachu|Pikachu|100/100
        |switch|p2a: Gengar|Gengar|100/100
        |turn|1
        |move|p2a: Gengar|Shadow Ball|p1a: Pikachu
        |-damage|p1a: Pikachu|0 fnt
        |faint|p1a: Pikachu
        |switch|p1a: Snorlax|Snorlax|100/100
        |turn|2
        |move|p1a: Snorlax|Body Slam|p2a: Gengar
        |-damage|p2a: Gengar|0 fnt
        |faint|p2a: Gengar
        |win|Alice
        """;

    [Fact]
    public void A_mid_game_switch_in_does_not_start_but_can_finish()
    {
        var run = ReplayLogRunner.Run(BenchReplacementLog);

        // Snorlax came in AFTER turn 1, so it never led — but it was standing at the end.
        Assert.False(run.Of("Snorlax").Started);
        Assert.True(run.Of("Snorlax").Finished);

        // Pikachu led but fainted; Gengar led and fainted. Neither finished.
        Assert.True(run.Of("Pikachu").Started);
        Assert.False(run.Of("Pikachu").Finished);
        Assert.True(run.Of("Gengar").Started);
        Assert.False(run.Of("Gengar").Finished);
    }

    // Doubles: all four lead; Alice's pair KO both of Bob's and stand at the end.
    private const string DoublesLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Rillaboom|item
        |poke|p1|Incineroar|item
        |poke|p2|Garchomp|item
        |poke|p2|Landorus|item
        |start
        |switch|p1a: Rillaboom|Rillaboom|100/100
        |switch|p1b: Incineroar|Incineroar|100/100
        |switch|p2a: Garchomp|Garchomp|100/100
        |switch|p2b: Landorus|Landorus|100/100
        |turn|1
        |move|p1a: Rillaboom|Wood Hammer|p2a: Garchomp
        |-damage|p2a: Garchomp|0 fnt
        |faint|p2a: Garchomp
        |move|p1b: Incineroar|Flare Blitz|p2b: Landorus
        |-damage|p2b: Landorus|0 fnt
        |faint|p2b: Landorus
        |win|Alice
        """;

    [Fact]
    public void Doubles_all_four_leads_start()
    {
        var run = ReplayLogRunner.Run(DoublesLog);
        foreach (var mon in new[] { "Rillaboom", "Incineroar", "Garchomp", "Landorus" })
            Assert.True(run.Of(mon).Started, $"{mon} should have started");
    }

    [Fact]
    public void Doubles_only_the_standing_pair_finishes()
    {
        var run = ReplayLogRunner.Run(DoublesLog);
        Assert.True(run.Of("Rillaboom").Finished);
        Assert.True(run.Of("Incineroar").Finished);
        Assert.False(run.Of("Garchomp").Finished);  // fainted
        Assert.False(run.Of("Landorus").Finished);  // fainted
    }

    // A tie with both mons still standing: both finish.
    private const string TieLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Pikachu|item
        |poke|p2|Gengar|item
        |start
        |switch|p1a: Pikachu|Pikachu|100/100
        |switch|p2a: Gengar|Gengar|100/100
        |turn|1
        |move|p1a: Pikachu|Protect|p1a: Pikachu
        |turn|2
        |tie
        """;

    [Fact]
    public void A_tie_finishes_everyone_left_standing()
    {
        var run = ReplayLogRunner.Run(TieLog);
        Assert.True(run.Of("Pikachu").Finished);
        Assert.True(run.Of("Gengar").Finished);
    }
}
