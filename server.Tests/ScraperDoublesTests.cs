namespace DraftLeague.Web.Tests;

/// <summary>
/// Doubles-specific scraper behaviour: Ally Switch (|swap|) trading the two active
/// mons so later damage lands on the right one, and presence counting both active
/// mons on a side each turn.
/// </summary>
public class ScraperDoublesTests
{
    // Rillaboom (p1a) and Incineroar (p1b) Ally Switch, so Incineroar ends up in the
    // p1a slot. Garchomp then hits p1a — which is now Incineroar, not Rillaboom.
    private const string AllySwitchLog = """
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
        |move|p1a: Rillaboom|Ally Switch|p1a: Rillaboom
        |swap|p1a: Rillaboom|1
        |move|p2a: Garchomp|Dragon Claw|p1a: Incineroar
        |-damage|p1a: Incineroar|60/100
        |turn|2
        |win|Bob
        """;

    [Fact]
    public void Ally_switch_moves_the_slot_so_damage_hits_the_swapped_mon()
    {
        var run = ReplayLogRunner.Run(AllySwitchLog);

        // Incineroar (swapped into p1a) took the Dragon Claw; Rillaboom took nothing.
        Assert.Equal(40, run.Of("Incineroar").TakenDirect, 2);
        Assert.Equal(0, run.Of("Rillaboom").TakenDirect, 2);
        Assert.Equal(40, run.Of("Garchomp").DealtDirect, 2);
    }

    // A calm two-turn doubles game: all four mons stay in, nobody faints.
    private const string DoublesPresenceLog = """
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
        |move|p1a: Rillaboom|Protect|p1a: Rillaboom
        |turn|2
        |move|p1a: Rillaboom|Protect|p1a: Rillaboom
        |win|Bob
        """;

    [Fact]
    public void Both_active_mons_on_a_side_accrue_presence_each_turn()
    {
        var run = ReplayLogRunner.Run(DoublesPresenceLog);

        Assert.Equal(2, run.Result.Turns);
        // Every mon that stayed on the field is present for both turns — including
        // the two that shared a side, so the team's field-time is 2 mons × 2 turns.
        foreach (var mon in new[] { "Rillaboom", "Incineroar", "Garchomp", "Landorus" })
            Assert.Equal(2, run.Of(mon).ActiveTurns);
    }
}
