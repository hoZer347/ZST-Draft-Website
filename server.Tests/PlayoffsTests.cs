using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The single-elimination playoffs: bracket layout with byes for 12 qualifiers,
/// seeding once the regular season is complete, best-of-three series and advancement,
/// and playoff results staying out of the regular-season record.
/// </summary>
public class PlayoffsTests : IAsyncLifetime
{
    private const int TeamCount = 14;
    private const int LastRegularWeek = 3;

    private readonly DraftLeagueFactory _factory = new();
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await ((IAsyncLifetime)_factory).DisposeAsync();

    private static readonly DateTimeOffset LastDue = new(2026, 11, 1, 23, 59, 0, TimeSpan.Zero);

    /// <summary>A league of <see cref="TeamCount"/> teams, T1 best to T14 worst by
    /// record, with one regular-season game each week (the last left as given).</summary>
    private static async Task<(League League, List<Team> Teams, Match LastGame)> SeedLeagueAsync(
        AppDbContext db, MatchResult lastResult)
    {
        var league = new League { Name = "Playoff league", OwnerId = "owner" };
        db.Leagues.Add(league);
        await db.SaveChangesAsync();

        var teams = Enumerable.Range(1, TeamCount)
            .Select(i => new Team
            {
                LeagueId = league.Id, Name = $"T{i}", CoachName = $"T{i}", CoachId = $"coach-{i}",
                Wins = TeamCount - i, Losses = i,
            })
            .ToList();
        db.Teams.AddRange(teams);

        Match last = null!;
        for (var week = 1; week <= LastRegularWeek; week++)
        {
            last = new Match
            {
                LeagueId = league.Id, Week = week, HomeTeam = teams[0], AwayTeam = teams[1],
                ScheduledFor = LastDue.AddDays(7 * (week - LastRegularWeek)),
                Result = week == LastRegularWeek ? lastResult : MatchResult.HomeWin,
            };
            db.Matches.Add(last);
        }
        await db.SaveChangesAsync();
        return (league, teams, last);
    }

    private static Task<List<Match>> PlayoffGamesAsync(AppDbContext db, int leagueId) => db.Matches
        .Where(m => m.LeagueId == leagueId && m.PlayoffRound != null)
        .OrderBy(m => m.PlayoffRound).ThenBy(m => m.PlayoffSlot).ThenBy(m => m.Id)
        .ToListAsync();

    private static async Task<List<Match>> SeriesAsync(AppDbContext db, int leagueId, int round, int slot) =>
        (await PlayoffGamesAsync(db, leagueId)).Where(g => g.PlayoffRound == round && g.PlayoffSlot == slot).ToList();

    private static async Task<Playoffs.Slot> SlotAsync(AppDbContext db, List<Team> teams, int round, int slot) =>
        Playoffs.Build(teams.Take(Playoffs.Qualifiers).Select(t => t.Id).ToList(),
            await PlayoffGamesAsync(db, teams[0].LeagueId))[round - 1][slot];

    private static async Task PlayAsync(AppDbContext db, Match game, MatchResult result)
    {
        game.Result = result;
        await db.SaveChangesAsync();
        await Playoffs.SyncAsync(db, game.LeagueId);
    }

    private static async Task<(League League, List<Team> Teams)> OpenPlayoffsAsync(AppDbContext db)
    {
        var (league, teams, _) = await SeedLeagueAsync(db, MatchResult.HomeWin);
        await Playoffs.SyncAsync(db, league.Id);
        return (league, teams);
    }

    [Fact]
    public void Game_numbers_count_each_series_in_id_order()
    {
        static Match Game(int id, int? round, int home, int away) =>
            new() { Id = id, PlayoffRound = round, PlayoffSlot = round is null ? null : 0, HomeTeamId = home, AwayTeamId = away };

        var numbers = Playoffs.GameNumbers(
            [Game(5, 1, 8, 9), Game(2, 1, 8, 9), Game(9, 1, 8, 9), Game(3, 2, 1, 9), Game(7, 2, 1, 8), Game(1, null, 8, 9)]);

        Assert.Equal([(2, 1), (3, 1), (5, 2), (7, 1), (9, 3)],
            numbers.OrderBy(kv => kv.Key).Select(kv => (kv.Key, kv.Value)));
    }

    [Fact]
    public void Seed_order_keeps_the_top_two_seeds_apart_until_the_final()
    {
        Assert.Equal([1, 4, 2, 3], Playoffs.SeedOrder(4));
        Assert.Equal([1, 16, 8, 9, 4, 13, 5, 12, 2, 15, 7, 10, 3, 14, 6, 11], Playoffs.SeedOrder(16));
    }

    [Fact]
    public void Twelve_qualifiers_give_the_top_four_seeds_byes()
    {
        var rounds = Playoffs.Build(Enumerable.Range(100, 12).ToList(), []);

        Assert.Equal(4, rounds.Count);
        Assert.Equal(["Round 1", "Quarterfinals", "Semifinals", "Final"],
            Enumerable.Range(1, rounds.Count).Select(r => Playoffs.RoundName(r, rounds.Count)));

        var first = rounds[0];
        Assert.Equal(8, first.Count);
        Assert.Equal([1, 4, 2, 3], first.Where(s => s.Bye).Select(s => s.Home!.Seed));
        Assert.All(first.Where(s => s.Bye), s => Assert.Null(s.Away));
        Assert.Equal([(8, 9), (5, 12), (7, 10), (6, 11)],
            first.Where(s => !s.Bye).Select(s => (s.Home!.Seed, s.Away!.Seed)));

        // A bye advances straight away; its opponent waits on the game beside it.
        Assert.Equal(1, rounds[1][0].Home!.Seed);
        Assert.Null(rounds[1][0].Away);
    }

    [Fact]
    public async Task Seeds_wait_for_the_regular_season_then_round_one_is_created()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (league, teams, last) = await SeedLeagueAsync(db, MatchResult.Pending);

        Assert.False(await Playoffs.SyncAsync(db, league.Id));
        Assert.All(teams, t => Assert.Null(t.PlayoffSeed));

        last.Result = MatchResult.HomeWin;
        await db.SaveChangesAsync();
        Assert.True(await Playoffs.SyncAsync(db, league.Id));

        Assert.Equal(Enumerable.Range(1, Playoffs.Qualifiers).Cast<int?>(), teams.Take(Playoffs.Qualifiers).Select(t => t.PlayoffSeed));
        Assert.All(teams.Skip(Playoffs.Qualifiers), t => Assert.Null(t.PlayoffSeed));

        var games = await PlayoffGamesAsync(db, league.Id);
        Assert.Equal([(8, 9), (5, 12), (7, 10), (6, 11)],
            games.Select(g => (teams.IndexOf(g.HomeTeam) + 1, teams.IndexOf(g.AwayTeam) + 1)));
        Assert.All(games, g =>
        {
            Assert.Equal(1, g.PlayoffRound);
            Assert.Equal(LastRegularWeek + 1, g.Week);
            Assert.Equal(LastDue.AddDays(7), g.ScheduledFor);
        });

        Assert.False(await Playoffs.SyncAsync(db, league.Id));
    }

    [Fact]
    public async Task A_series_goes_to_a_third_game_only_at_one_all()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (league, teams) = await OpenPlayoffsAsync(db);

        await PlayAsync(db, (await SeriesAsync(db, league.Id, 1, 1)).Single(), MatchResult.HomeWin);
        var series = await SeriesAsync(db, league.Id, 1, 1);
        Assert.Equal([MatchResult.HomeWin, MatchResult.Pending], series.Select(g => g.Result));
        Assert.Empty(await SeriesAsync(db, league.Id, 2, 0));

        await PlayAsync(db, series[1], MatchResult.AwayWin);
        series = await SeriesAsync(db, league.Id, 1, 1);
        Assert.Equal([MatchResult.HomeWin, MatchResult.AwayWin, MatchResult.Pending], series.Select(g => g.Result));
        Assert.All(series, g => Assert.Equal(
            (teams[7].Id, teams[8].Id, LastRegularWeek + 1, LastDue.AddDays(7)),
            (g.HomeTeamId, g.AwayTeamId, g.Week, g.ScheduledFor)));
        Assert.Empty(await SeriesAsync(db, league.Id, 2, 0));

        await PlayAsync(db, series[2], MatchResult.AwayWin);
        var slot = await SlotAsync(db, teams, 1, 1);
        Assert.Equal((1, 2, teams[8].Id), (slot.HomeWins, slot.AwayWins, slot.Winner?.TeamId));
        Assert.Equal(3, (await SeriesAsync(db, league.Id, 1, 1)).Count);

        var quarter = Assert.Single(await SeriesAsync(db, league.Id, 2, 0));
        Assert.Equal((teams[0].Id, teams[8].Id, MatchResult.Pending), (quarter.HomeTeamId, quarter.AwayTeamId, quarter.Result));
        Assert.Equal(LastRegularWeek + 2, quarter.Week);
    }

    [Fact]
    public async Task A_two_nil_sweep_needs_no_third_game()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (league, teams) = await OpenPlayoffsAsync(db);

        await PlayAsync(db, (await SeriesAsync(db, league.Id, 1, 1)).Single(), MatchResult.HomeWin);
        await PlayAsync(db, (await SeriesAsync(db, league.Id, 1, 1))[1], MatchResult.HomeWin);

        Assert.Equal([MatchResult.HomeWin, MatchResult.HomeWin],
            (await SeriesAsync(db, league.Id, 1, 1)).Select(g => g.Result));
        var slot = await SlotAsync(db, teams, 1, 1);
        Assert.Equal((2, 0, teams[7].Id), (slot.HomeWins, slot.AwayWins, slot.Winner?.TeamId));
        Assert.Single(await SeriesAsync(db, league.Id, 2, 0));
    }

    [Fact]
    public async Task A_drawn_game_counts_for_neither_side_and_adds_another()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (league, teams) = await OpenPlayoffsAsync(db);

        await PlayAsync(db, (await SeriesAsync(db, league.Id, 1, 1)).Single(), MatchResult.Draw);
        var slot = await SlotAsync(db, teams, 1, 1);
        Assert.Equal([MatchResult.Draw, MatchResult.Pending], slot.Games.Select(g => g.Result));
        Assert.Equal((0, 0), (slot.HomeWins, slot.AwayWins));
        Assert.Null(slot.Winner);

        await PlayAsync(db, slot.Games[1], MatchResult.HomeWin);
        await PlayAsync(db, (await SeriesAsync(db, league.Id, 1, 1))[2], MatchResult.HomeWin);

        slot = await SlotAsync(db, teams, 1, 1);
        Assert.Equal(3, slot.Games.Count);
        Assert.Equal((2, 0, teams[7].Id), (slot.HomeWins, slot.AwayWins, slot.Winner?.TeamId));
    }

    [Fact]
    public async Task A_series_win_advances_and_a_flipped_series_repoints_the_unplayed_next_game()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (league, teams) = await OpenPlayoffsAsync(db);

        var first = (await SeriesAsync(db, league.Id, 1, 1)).Single();
        first.Result = MatchResult.HomeWin;
        MatchReporting.ApplyToStandings(first, first.Result, +1);
        await db.SaveChangesAsync();
        await Playoffs.SyncAsync(db, league.Id);

        // Playoff results stay out of the regular-season record.
        Assert.Equal(TeamCount - 8, teams[7].Wins);
        Assert.Equal(9, teams[8].Losses);

        var series = await SeriesAsync(db, league.Id, 1, 1);
        await PlayAsync(db, series[1], MatchResult.HomeWin);
        var quarter = Assert.Single(await SeriesAsync(db, league.Id, 2, 0));
        Assert.Equal((teams[0].Id, teams[7].Id), (quarter.HomeTeamId, quarter.AwayTeamId));

        series[0].Result = MatchResult.AwayWin;
        series[1].Result = MatchResult.AwayWin;
        await db.SaveChangesAsync();
        await Playoffs.SyncAsync(db, league.Id);
        Assert.Equal(quarter.Id, Assert.Single(await SeriesAsync(db, league.Id, 2, 0)).Id);
        Assert.Equal(teams[8].Id, quarter.AwayTeamId);

        // Once played, a game is left alone even if its feeder changes again, and the
        // series starts over between the new pair.
        quarter.Result = MatchResult.HomeWin;
        series[0].Result = MatchResult.HomeWin;
        series[1].Result = MatchResult.HomeWin;
        await db.SaveChangesAsync();
        await Playoffs.SyncAsync(db, league.Id);
        Assert.Equal(teams[8].Id, quarter.AwayTeamId);
        var next = Assert.Single((await SlotAsync(db, teams, 2, 0)).Games);
        Assert.NotEqual(quarter.Id, next.Id);
        Assert.Equal((teams[7].Id, MatchResult.Pending), (next.AwayTeamId, next.Result));
    }

    [Fact]
    public async Task Clearing_a_deciding_result_reopens_the_series_and_withdraws_the_next_round_game()
    {
        int leagueId;
        List<int> seriesIds;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            leagueId = (await OpenPlayoffsAsync(db)).League.Id;
            await PlayAsync(db, (await SeriesAsync(db, leagueId, 1, 1)).Single(), MatchResult.HomeWin);
            await PlayAsync(db, (await SeriesAsync(db, leagueId, 1, 1))[1], MatchResult.HomeWin);
            Assert.Single(await SeriesAsync(db, leagueId, 2, 0));
            seriesIds = (await SeriesAsync(db, leagueId, 1, 1)).Select(g => g.Id).ToList();
        }

        var admin = await _factory.SignedInAsAsync("admin", admin: true);
        (await admin.DeleteAsync($"/api/matches/{seriesIds[1]}/replay")).EnsureSuccessStatusCode();

        using var check = _factory.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<AppDbContext>();
        var series = await SeriesAsync(after, leagueId, 1, 1);
        Assert.Equal(seriesIds, series.Select(g => g.Id));
        Assert.Equal([MatchResult.HomeWin, MatchResult.Pending], series.Select(g => g.Result));
        Assert.Empty(await SeriesAsync(after, leagueId, 2, 0));
    }

    [Fact]
    public async Task Every_game_of_a_series_is_locked_once_the_next_round_is_played()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (league, _) = await OpenPlayoffsAsync(db);

        await PlayAsync(db, (await SeriesAsync(db, league.Id, 1, 1)).Single(), MatchResult.AwayWin);
        await PlayAsync(db, (await SeriesAsync(db, league.Id, 1, 1))[1], MatchResult.AwayWin);
        var series = await SeriesAsync(db, league.Id, 1, 1);
        foreach (var game in series) Assert.False(await Playoffs.IsLockedAsync(db, game, CancellationToken.None));

        var quarter = Assert.Single(await SeriesAsync(db, league.Id, 2, 0));
        quarter.Result = MatchResult.HomeWin;
        await db.SaveChangesAsync();
        foreach (var game in series) Assert.True(await Playoffs.IsLockedAsync(db, game, CancellationToken.None));
        Assert.False(await Playoffs.IsLockedAsync(db, quarter, CancellationToken.None));

        var admin = await _factory.SignedInAsAsync("admin", admin: true);
        var res = await admin.DeleteAsync($"/api/matches/{series[0].Id}/replay");
        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
    }

    [Fact]
    public async Task Schedule_numbers_each_playoff_game_within_its_series()
    {
        int leagueId;
        List<int> seriesIds;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            leagueId = (await OpenPlayoffsAsync(db)).League.Id;
            await PlayAsync(db, (await SeriesAsync(db, leagueId, 1, 1)).Single(), MatchResult.Draw);
            await PlayAsync(db, (await SeriesAsync(db, leagueId, 1, 1))[1], MatchResult.HomeWin);
            seriesIds = (await SeriesAsync(db, leagueId, 1, 1)).Select(g => g.Id).ToList();
        }

        var admin = await _factory.SignedInAsAsync("admin", admin: true);
        var matches = (await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/schedule"))
            .GetProperty("matches").EnumerateArray().ToDictionary(m => m.GetProperty("id").GetInt32());

        Assert.Equal([1, 2, 3], seriesIds.Select(id => matches[id].GetProperty("game").GetInt32()));
        Assert.All(seriesIds, id => Assert.Equal("Round 1", matches[id].GetProperty("round").GetString()));
        Assert.All(matches.Values.Where(m => m.GetProperty("round").ValueKind == JsonValueKind.Null),
            m => Assert.Equal(JsonValueKind.Null, m.GetProperty("game").ValueKind));
    }

    [Fact]
    public async Task Playoff_games_count_toward_ko_totals_but_not_the_record()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var (league, _, _) = await SeedLeagueAsync(db, MatchResult.HomeWin);
        await Playoffs.SyncAsync(db, league.Id);

        var game = (await PlayoffGamesAsync(db, league.Id)).First();
        game.Result = MatchResult.HomeWin;
        game.ReplayLog = "|faint|p2a: Away\n|faint|p2a: Away";
        game.ReplayHomeSide = "p1";
        MatchReporting.ApplyToStandings(game, game.Result, +1);
        await db.SaveChangesAsync();

        var rows = (await Standings.ComputeAsync(db, league.Id, CancellationToken.None)).ToDictionary(r => r.TeamId);
        var (home, away) = (rows[game.HomeTeamId], rows[game.AwayTeamId]);
        Assert.Equal((2, -2), (home.KoDiff, away.KoDiff));
        Assert.Equal(2, away.TotalFaints);
        Assert.Equal((TeamCount - 8, 8), (home.Wins, home.Losses));
    }

    [Fact]
    public async Task Concurrent_syncs_create_round_one_once()
    {
        int leagueId;
        using (var scope = _factory.Services.CreateScope())
            leagueId = (await SeedLeagueAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), MatchResult.HomeWin)).League.Id;

        using var a = _factory.Services.CreateScope();
        using var b = _factory.Services.CreateScope();
        var dbA = a.ServiceProvider.GetRequiredService<AppDbContext>();
        var dbB = b.ServiceProvider.GetRequiredService<AppDbContext>();
        await Task.WhenAll(
            Task.Run(() => Playoffs.SyncAsync(dbA, leagueId)),
            Task.Run(() => Playoffs.SyncAsync(dbB, leagueId)));

        Assert.Equal(4, (await PlayoffGamesAsync(dbA, leagueId)).Count);
    }

    [Fact]
    public async Task Starting_or_aborting_a_draft_clears_last_seasons_seeds()
    {
        var admin = await _factory.SignedInAsAsync("admin", admin: true);
        var coach = await _factory.SignedInAsAsync("coach-1");
        int draftId, leagueId;
        using (var scope = _factory.Services.CreateScope())
        {
            var draft = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Drafts.FirstAsync();
            (draftId, leagueId) = (draft.Id, draft.LeagueId);
        }

        async Task SeedEveryTeamAsync()
        {
            using var scope = _factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AppDbContext>().Teams
                .Where(t => t.LeagueId == leagueId).ExecuteUpdateAsync(s => s.SetProperty(t => t.PlayoffSeed, 1));
        }

        async Task AssertUnseededAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.True(await db.Teams.AnyAsync(t => t.LeagueId == leagueId));
            Assert.False(await db.Teams.AnyAsync(t => t.LeagueId == leagueId && t.PlayoffSeed != null));
        }

        async Task StartAsync()
        {
            (await coach.PostAsync($"/api/drafts/{draftId}/ready", null)).EnsureSuccessStatusCode();
            (await admin.PostAsJsonAsync($"/api/admin/drafts/{draftId}/start",
                new { weeks = 2, pickTimerSeconds = 3600 })).EnsureSuccessStatusCode();
        }

        await StartAsync();
        await SeedEveryTeamAsync();
        (await admin.PostAsync($"/api/admin/drafts/{draftId}/abort", null)).EnsureSuccessStatusCode();
        await AssertUnseededAsync();

        await SeedEveryTeamAsync();
        await StartAsync();
        await AssertUnseededAsync();
    }

    [Fact]
    public async Task Endpoint_projects_from_standings_until_seeds_are_frozen()
    {
        int leagueId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            leagueId = (await SeedLeagueAsync(db, MatchResult.Pending)).League.Id;
        }
        var client = await _factory.SignedInAsAsync("coach-1");

        async Task<JsonElement> GetAsync()
        {
            var res = await client.GetAsync($"/api/leagues/{leagueId}/playoffs");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            return await res.Content.ReadFromJsonAsync<JsonElement>();
        }

        var projected = await GetAsync();
        Assert.True(projected.GetProperty("projected").GetBoolean());
        var rounds = projected.GetProperty("rounds").EnumerateArray().ToList();
        Assert.Equal(["Round 1", "Quarterfinals", "Semifinals", "Final"], rounds.Select(r => r.GetProperty("name").GetString()));
        var topBye = rounds[0].GetProperty("slots")[0];
        Assert.True(topBye.GetProperty("bye").GetBoolean());
        Assert.Equal("T1", topBye.GetProperty("home").GetProperty("name").GetString());
        Assert.Empty(topBye.GetProperty("games").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, topBye.GetProperty("winnerTeamId").ValueKind);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var pending = await db.Matches.SingleAsync(m => m.LeagueId == leagueId && m.Result == MatchResult.Pending);
            pending.Result = MatchResult.HomeWin;
            await db.SaveChangesAsync();
            await Playoffs.SyncAsync(db, leagueId);
        }

        var seeded = await GetAsync();
        Assert.False(seeded.GetProperty("projected").GetBoolean());
        var opening = Assert.Single(seeded.GetProperty("rounds")[0].GetProperty("slots")[1].GetProperty("games").EnumerateArray());
        Assert.Equal((1, "Pending"), (opening.GetProperty("game").GetInt32(), opening.GetProperty("result").GetString()));

        const string replay = "https://replay.pokemonshowdown.com/gen9-game1";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var game = await db.Matches.SingleAsync(m => m.Id == opening.GetProperty("matchId").GetInt32());
            (game.HomeScore, game.AwayScore, game.ReplayUrl) = (3, 0, replay);
            await PlayAsync(db, game, MatchResult.HomeWin);
        }

        var slot = (await GetAsync()).GetProperty("rounds")[0].GetProperty("slots")[1];
        Assert.Equal((1, 0), (slot.GetProperty("homeWins").GetInt32(), slot.GetProperty("awayWins").GetInt32()));
        Assert.Equal(JsonValueKind.Null, slot.GetProperty("winnerTeamId").ValueKind);
        var games = slot.GetProperty("games").EnumerateArray().ToList();
        Assert.Equal([1, 2], games.Select(g => g.GetProperty("game").GetInt32()));
        Assert.Equal(("HomeWin", 3, 0, replay), (games[0].GetProperty("result").GetString(),
            games[0].GetProperty("homeScore").GetInt32(), games[0].GetProperty("awayScore").GetInt32(),
            games[0].GetProperty("replayUrl").GetString()));
        Assert.Equal(("Pending", JsonValueKind.Null), (games[1].GetProperty("result").GetString(),
            games[1].GetProperty("replayUrl").ValueKind));
    }
}
