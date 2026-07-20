using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The recorder persists the full split the scraper produces — friendly-fire
/// damage, enemy-healing, and self-inflicted damage each in their own PokemonStat
/// column — and a −1 pass backs every one of them out. Fresh DB per test.
/// </summary>
public class MatchStatsRecorderSplitTests : DraftScenarioBase
{
    // Doubles. Garchomp's Earthquake hits both opponents (Rillaboom, Incineroar)
    // AND its own ally Landorus (friendly fire), then Garchomp takes Life Orb chip
    // (self). Rillaboom's Grassy Terrain heals its ally Incineroar (ally-heal) and
    // the foe Garchomp (enemy-heal). Alice (p1) wins.
    private const string Log = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Rillaboom|item
        |poke|p1|Incineroar|item
        |poke|p2|Garchomp|item
        |poke|p2|Landorus|item
        |start
        |switch|p1a: Rillaboom|Rillaboom|100/100
        |-fieldstart|move: Grassy Terrain|[from] ability: Grassy Surge|[of] p1a: Rillaboom
        |switch|p1b: Incineroar|Incineroar|100/100
        |switch|p2a: Garchomp|Garchomp|100/100
        |switch|p2b: Landorus|Landorus|100/100
        |turn|1
        |move|p2a: Garchomp|Earthquake|p1a: Rillaboom|[spread] p1a,p1b,p2b
        |-damage|p1a: Rillaboom|70/100
        |-damage|p1b: Incineroar|60/100
        |-damage|p2b: Landorus|80/100
        |-damage|p2a: Garchomp|90/100|[from] item: Life Orb
        |-heal|p1b: Incineroar|70/100|[from] Grassy Terrain
        |-heal|p2a: Garchomp|100/100|[from] Grassy Terrain
        |turn|2
        |win|Alice
        """;

    [Fact]
    public async Task Records_ally_damage_enemy_heal_and_self_damage_then_reverses_them()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<MatchStatsRecorder>();

        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Rillaboom", "Incineroar"],
            ("bob", "Bob"), ["Garchomp", "Landorus"]);

        async Task<PokemonStat> StatOf(string mon) =>
            await db.PokemonStats.SingleAsync(x => x.PickId == s.Picks[mon].Id);

        await recorder.ApplyAsync(s.Match, "p1", Log, MatchResult.HomeWin, +1);
        await db.SaveChangesAsync();

        var chomp = await StatOf("garchomp");
        Assert.Equal(70, chomp.DamageDealtDirect, 2);       // to the two opponents
        Assert.Equal(20, chomp.DamageDealtAllyDirect, 2);   // friendly fire on Landorus
        Assert.Equal(10, chomp.DamageTakenSelf, 2);         // Life Orb
        Assert.Equal(0, chomp.DamageTakenDirect, 2);

        var rilla = await StatOf("rillaboom");
        Assert.Equal(10, rilla.HpHealed, 2);        // healed ally Incineroar via terrain
        Assert.Equal(10, rilla.HpHealedEnemy, 2);   // topped up the foe Garchomp
        Assert.Equal(1, rilla.Wins);                // home won

        Assert.Equal(40, (await StatOf("incineroar")).DamageTakenDirect, 2);
        Assert.Equal(20, (await StatOf("landorus")).DamageTakenDirect, 2);

        // Presence denominator is the game's turn count (2) — one field slot per
        // turn. In doubles two mons are active each turn, so the team's mons sum to
        // 200% of it; the denominator itself stays at the 2 turns played.
        Assert.Equal(2, s.Home.BattleTurns);
        Assert.Equal(2, s.Away.BattleTurns);

        // Reverse: a corrected re-report subtracts everything back to zero.
        await recorder.ApplyAsync(s.Match, "p1", Log, MatchResult.HomeWin, -1);
        await db.SaveChangesAsync();

        var chompBack = await StatOf("garchomp");
        Assert.Equal(0, chompBack.DamageDealtDirect, 2);
        Assert.Equal(0, chompBack.DamageDealtAllyDirect, 2);
        Assert.Equal(0, chompBack.DamageTakenSelf, 2);
        var rillaBack = await StatOf("rillaboom");
        Assert.Equal(0, rillaBack.HpHealed, 2);
        Assert.Equal(0, rillaBack.HpHealedEnemy, 2);
        Assert.Equal(0, rillaBack.Wins);
        Assert.Equal(0, s.Home.BattleTurns); // presence denominator backed out too
        Assert.Equal(0, s.Away.BattleTurns);
    }
}
