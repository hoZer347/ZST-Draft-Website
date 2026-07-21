using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The real-replay pathway: <see cref="MatchStatsRecorder"/> folds a scored
/// replay's per-mon stats — including self-heal and ally-heal — into the
/// PokemonStat rows the stats page reads, and backs them out again on a −1 pass
/// so a corrected re-report nets to zero. Uses the same hand-authored log as the
/// scraper tests, seeded against a throwaway DB.
/// </summary>
public class MatchStatsRecorderTests(DraftLeagueFactory factory) : IClassFixture<DraftLeagueFactory>
{
    // Alice (Pikachu/Snorlax, p1=home) beats Bob (Gengar/Blissey). Pikachu KOs
    // Gengar, is Toxic'd, Recovers 60→100 (self-heal), then chips Blissey.
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
        |win|Alice
        """;

    private static PokemonEntry Mon(int leagueId, string name) =>
        new() { LeagueId = leagueId, Name = name, Tier = Tier.C, Sprite = name.ToLowerInvariant() };

    [Fact]
    public async Task Recording_a_replay_populates_pokemon_stats_then_reversal_nets_to_zero()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<MatchStatsRecorder>();

        // Minimal league: two coaches, two picks each, one match between them.
        var league = new League { Name = "Recorder Test", OwnerId = "owner" };
        db.Leagues.Add(league);
        await db.SaveChangesAsync();

        var home = new Team { LeagueId = league.Id, Name = "Alice", CoachName = "Alice", CoachId = "alice" };
        var away = new Team { LeagueId = league.Id, Name = "Bob", CoachName = "Bob", CoachId = "bob" };
        db.Teams.AddRange(home, away);
        await db.SaveChangesAsync();

        var draft = new Draft { LeagueId = league.Id };
        db.Drafts.Add(draft);
        await db.SaveChangesAsync();

        var pickNo = 0;
        Pick MakePick(Team team, string name)
        {
            var entry = Mon(league.Id, name);
            db.Pokemon.Add(entry);
            var pick = new Pick { DraftId = draft.Id, PickNumber = ++pickNo, TeamId = team.Id, PokemonEntry = entry, Tier = Tier.C };
            db.Picks.Add(pick);
            return pick;
        }

        var pikachu = MakePick(home, "Pikachu");
        MakePick(home, "Snorlax");
        MakePick(away, "Gengar");
        MakePick(away, "Blissey");
        await db.SaveChangesAsync();

        var match = new Match { LeagueId = league.Id, HomeTeamId = home.Id, AwayTeamId = away.Id, HomeTeam = home, AwayTeam = away };
        db.Matches.Add(match);
        await db.SaveChangesAsync();

        // Record the game (p1 = home).
        await recorder.ApplyAsync(match, "p1", Log, MatchResult.HomeWin, +1);
        await db.SaveChangesAsync();

        var pika = await db.PokemonStats.SingleAsync(s => s.PickId == pikachu.Id);
        Assert.Equal(1, pika.GamesPlayed);
        Assert.Equal(1, pika.Starts);         // led the battle
        Assert.Equal(1, pika.Finishes);       // still standing at |win|
        Assert.Equal(1, pika.Kills);          // KO'd Gengar
        Assert.Equal(1, pika.Wins);           // home won
        Assert.Equal(40, pika.HpRecovered, 2); // Recover 60→100
        Assert.Equal(0, pika.HpHealed, 2);     // healed no ally
        Assert.Equal(4, home.BattleTurns);     // Presence denominator
        Assert.Equal(4, away.BattleTurns);

        // Back it out — a corrected re-report subtracts the old game.
        await recorder.ApplyAsync(match, "p1", Log, MatchResult.HomeWin, -1);
        await db.SaveChangesAsync();

        await db.Entry(pika).ReloadAsync();
        Assert.Equal(0, pika.GamesPlayed);
        Assert.Equal(0, pika.Starts);
        Assert.Equal(0, pika.Finishes);
        Assert.Equal(0, pika.Kills);
        Assert.Equal(0, pika.Wins);
        Assert.Equal(0, pika.HpRecovered, 2);
        Assert.Equal(0, home.BattleTurns);
        Assert.Equal(0, away.BattleTurns);
    }
}
