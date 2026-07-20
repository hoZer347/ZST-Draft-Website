using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// <see cref="ReplayScorer"/>'s network-free core: ScoreLogAsync reads a battle
/// log against a known matchup (winner, mons-remaining score, which Showdown side
/// was the home team by roster overlap), and ReportAsync auto-identifies which
/// scheduled match a stray log belongs to. Each test gets a fresh throwaway DB
/// (ReportAsync queries drafted mons globally, so tests must not share one).
/// </summary>
public class ReplayScorerTests : DraftScenarioBase
{
    // p1 = Alice (Pikachu, Snorlax); p2 = Bob (Gengar, Blissey). Bob's Gengar
    // faints; Alice wins. Two mons brought each side.
    private const string Log = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Pikachu|item
        |poke|p1|Snorlax|item
        |poke|p2|Gengar|item
        |poke|p2|Blissey|item
        |start
        |switch|p1a: Pikachu|Pikachu|100/100
        |switch|p2a: Gengar|Gengar|100/100
        |turn|1
        |move|p1a: Pikachu|Thunderbolt|p2a: Gengar
        |-damage|p2a: Gengar|0 fnt
        |faint|p2a: Gengar
        |win|Alice
        """;

    private ReplayScorer Scorer(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<ReplayScorer>();
    private static AppDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<AppDbContext>();

    [Fact]
    public async Task Scores_the_home_win_with_mons_remaining_and_home_side()
    {
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Pikachu", "Snorlax"],
            ("bob", "Bob"), ["Gengar", "Blissey"]);

        var r = await Scorer(scope).ScoreLogAsync(s.Match, Log);

        Assert.True(r.Ok);
        Assert.Equal(MatchResult.HomeWin, r.Outcome);
        Assert.Equal(2, r.HomeScore); // both mons survived
        Assert.Equal(1, r.AwayScore); // Gengar fainted
        Assert.Equal("p1", r.HomeSide);
    }

    [Fact]
    public async Task Detects_a_swapped_side_from_roster_overlap()
    {
        // Home team drafted the mons the LOG's p2 brought, so p2 is the home side.
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Gengar", "Blissey"],   // home = log's p2
            ("bob", "Bob"), ["Pikachu", "Snorlax"]);      // away = log's p1

        var r = await Scorer(scope).ScoreLogAsync(s.Match, Log);

        Assert.True(r.Ok);
        Assert.Equal("p2", r.HomeSide);
        Assert.Equal(MatchResult.AwayWin, r.Outcome); // the winner (Alice/p1) is the away team
        Assert.Equal(1, r.HomeScore);                 // home (p2) lost Gengar
        Assert.Equal(2, r.AwayScore);
    }

    [Fact]
    public async Task Scores_a_tie()
    {
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Pikachu", "Snorlax"],
            ("bob", "Bob"), ["Gengar", "Blissey"]);

        var tieLog = Log.Replace("|win|Alice", "|tie");
        var r = await Scorer(scope).ScoreLogAsync(s.Match, tieLog);

        Assert.True(r.Ok);
        Assert.Equal(MatchResult.Draw, r.Outcome);
    }

    [Fact]
    public async Task An_unfinished_log_is_rejected()
    {
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Pikachu", "Snorlax"],
            ("bob", "Bob"), ["Gengar", "Blissey"]);

        var noWinner = Log.Replace("|win|Alice", "");
        var r = await Scorer(scope).ScoreLogAsync(s.Match, noWinner);

        Assert.False(r.Ok);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public async Task A_log_whose_teams_dont_match_the_matchup_is_rejected()
    {
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Garchomp", "Landorus"],
            ("bob", "Bob"), ["Toxapex", "Ferrothorn"]);

        var r = await Scorer(scope).ScoreLogAsync(s.Match, Log); // log has Pikachu/Gengar etc.

        Assert.False(r.Ok);
    }

    [Fact]
    public async Task Scoring_needs_both_teams_to_have_a_roster()
    {
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        // Home has no picks.
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), [],
            ("bob", "Bob"), ["Gengar", "Blissey"]);

        var r = await Scorer(scope).ScoreLogAsync(s.Match, Log);

        Assert.False(r.Ok);
    }

    [Fact]
    public async Task ReportAsync_finds_the_pending_match_between_the_two_teams()
    {
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Pikachu", "Snorlax"],
            ("bob", "Bob"), ["Gengar", "Blissey"]);

        var r = await Scorer(scope).ReportAsync(Log);

        Assert.True(r.Ok);
        Assert.Equal(s.Match.Id, r.MatchId);
        Assert.Equal(MatchResult.HomeWin, r.Outcome);
        Assert.Equal("p1", r.HomeSide);
    }

    [Fact]
    public async Task ReportAsync_ignores_a_log_whose_mons_arent_drafted()
    {
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Garchomp", "Landorus"],
            ("bob", "Bob"), ["Toxapex", "Ferrothorn"]);

        var r = await Scorer(scope).ReportAsync(Log); // Pikachu/Gengar belong to nobody

        Assert.False(r.Ok);
    }

    [Fact]
    public async Task ReportAsync_ignores_a_matchup_with_no_pending_match()
    {
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Pikachu", "Snorlax"],
            ("bob", "Bob"), ["Gengar", "Blissey"]);

        // Mark the only match as already played.
        s.Match.Result = MatchResult.HomeWin;
        await db.SaveChangesAsync();

        var r = await Scorer(scope).ReportAsync(Log);

        Assert.False(r.Ok);
    }
}
