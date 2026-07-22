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

    // ── survivor score never inflated by a mon appearing under two names ──────
    // The bug: the score was brought - faints, where "brought" was a DE-DUPED SET of
    // species ids from |poke| + |switch|. A mon whose team-preview name differs from
    // its battle forme (Urshifu shown as "Urshifu" in |poke|, "Urshifu-Rapid-Strike"
    // on switch; a mega that returns as its Mega forme; etc.) landed in that set
    // twice, inflating the count. A team that loses ALL its mons then scored 1, not 0,
    // producing impossible results like a decisive 1-1. Score off the roster size
    // instead, so the count is right however a mon's name is spelled.

    // Alice (home) brings [transformMon, Snorlax] and loses BOTH; Bob wins with both
    // alive. previewName is shown in team preview, battleName once it switches in.
    private static string WipeLog(string previewName, string battleName, bool teamsize = true) => $"""
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        {(teamsize ? "|teamsize|p1|2\n|teamsize|p2|2" : "|clearpoke")}
        |poke|p1|{previewName}, M
        |poke|p1|Snorlax
        |poke|p2|Gengar
        |poke|p2|Blissey
        |start
        |switch|p1a: Mon|{battleName}|100/100
        |switch|p2a: Gengar|Gengar|100/100
        |turn|1
        |move|p2a: Gengar|Shadow Ball|p1a: Mon
        |-damage|p1a: Mon|0 fnt
        |faint|p1a: Mon
        |switch|p1a: Snorlax|Snorlax|100/100
        |move|p2a: Gengar|Shadow Ball|p1a: Snorlax
        |-damage|p1a: Snorlax|0 fnt
        |faint|p1a: Snorlax
        |win|Bob
        """;

    [Theory]
    [InlineData("Urshifu", "Urshifu-Rapid-Strike")] // forme hidden in team preview
    [InlineData("Landorus", "Landorus-Therian")]
    [InlineData("Zacian", "Zacian-Crowned")]
    [InlineData("Calyrex", "Calyrex-Shadow")]
    [InlineData("Charizard", "Charizard-Mega-Y")]   // returns to the field as its mega forme
    [InlineData("Greninja", "Greninja")]            // control: same name, still 0
    public async Task A_fully_wiped_team_scores_zero_whatever_names_its_mons_show(string preview, string battle)
    {
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), [battle, "Snorlax"],
            ("bob", "Bob"), ["Gengar", "Blissey"]);

        var r = await Scorer(scope).ScoreLogAsync(s.Match, WipeLog(preview, battle));

        Assert.True(r.Ok);
        Assert.Equal(MatchResult.AwayWin, r.Outcome);
        Assert.Equal(0, r.HomeScore); // every one of Alice's mons fainted → ZERO remain, never 1
        Assert.Equal(2, r.AwayScore);
    }

    [Fact]
    public async Task Scores_off_the_poke_count_when_teamsize_is_absent()
    {
        // Older/edge logs may omit |teamsize|; the roster size then comes from the
        // |poke| lines (one per mon), which is still immune to the forme double-count.
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Urshifu-Rapid-Strike", "Snorlax"],
            ("bob", "Bob"), ["Gengar", "Blissey"]);

        var r = await Scorer(scope).ScoreLogAsync(s.Match, WipeLog("Urshifu", "Urshifu-Rapid-Strike", teamsize: false));

        Assert.True(r.Ok);
        Assert.Equal(0, r.HomeScore);
        Assert.Equal(2, r.AwayScore);
    }

    [Fact]
    public async Task The_winners_survivor_count_is_not_inflated_by_a_forme_either()
    {
        // Home wins holding a forme'd mon: it brought 3, lost only 1, so exactly 2
        // survive, even though its Urshifu shows two names in the log.
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Urshifu-Rapid-Strike", "Snorlax", "Pikachu"],
            ("bob", "Bob"), ["Gengar", "Blissey"]);

        var log = """
            |player|p1|Alice|1|
            |player|p2|Bob|2|
            |teamsize|p1|3
            |teamsize|p2|2
            |poke|p1|Urshifu, M
            |poke|p1|Snorlax
            |poke|p1|Pikachu
            |poke|p2|Gengar
            |poke|p2|Blissey
            |start
            |switch|p1a: Urshifu|Urshifu-Rapid-Strike|100/100
            |switch|p2a: Gengar|Gengar|100/100
            |turn|1
            |move|p2a: Gengar|Shadow Ball|p1a: Urshifu
            |-damage|p1a: Urshifu|0 fnt
            |faint|p1a: Urshifu
            |faint|p2a: Gengar
            |faint|p2a: Blissey
            |win|Alice
            """;
        var r = await Scorer(scope).ScoreLogAsync(s.Match, log);

        Assert.True(r.Ok);
        Assert.Equal(MatchResult.HomeWin, r.Outcome);
        Assert.Equal(2, r.HomeScore); // 3 brought - 1 fainted, NOT 3 (would be if Urshifu double-counted)
        Assert.Equal(0, r.AwayScore);
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
    public async Task ReportAsync_redoes_the_recorded_match_when_none_is_pending()
    {
        using var scope = Factory.Services.CreateScope();
        var db = Db(scope);
        var s = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Pikachu", "Snorlax"],
            ("bob", "Bob"), ["Gengar", "Blissey"]);

        // The only scheduled match is already recorded (with the opposite result).
        s.Match.Result = MatchResult.AwayWin;
        await db.SaveChangesAsync();

        // A fresh battle between the same teams is treated as a REDO of that match, so
        // a re-played game refreshes its result, rather than being ignored.
        var r = await Scorer(scope).ReportAsync(Log);

        Assert.True(r.Ok);
        Assert.Equal(s.Match.Id, r.MatchId);
        Assert.Equal(MatchResult.HomeWin, r.Outcome); // the redo's actual result
    }
}
