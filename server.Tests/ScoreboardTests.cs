using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The scoreboard endpoint: team standings (sorted W-L, KO diff, wins, KOs) and
/// per-mon stat leaders (presence, +/-, healing, damage ratio), all rolled up
/// from the stored PokemonStat rows and the teams' match records. Seeds known
/// values straight into the DB so every ranking and tiebreak is hand-checkable.
/// </summary>
public class ScoreboardTests : IAsyncLifetime
{
    private readonly DraftLeagueFactory _factory = new();
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await ((IAsyncLifetime)_factory).DisposeAsync();

    private int _pickNo;

    private Team AddTeam(AppDbContext db, League league, string name, int wins, int losses, int battleTurns = 100)
    {
        var team = new Team
        {
            LeagueId = league.Id, Name = name, CoachName = name, CoachId = name,
            Wins = wins, Losses = losses, BattleTurns = battleTurns,
        };
        db.Teams.Add(team);
        return team;
    }

    // dealt/taken/heal go into the "direct"/"recovered" buckets; the endpoint sums
    // direct+indirect (dealt/taken) and recovered+healed (heal), so these become
    // the totals it ranks on.
    private void AddMon(AppDbContext db, Draft draft, League league, Team team, string name,
        int k, int d, int activeTurns, double dealt, double taken, double heal, int gp = 1)
    {
        var entry = new PokemonEntry
        {
            LeagueId = league.Id, Name = name, Tier = Tier.C,
            Sprite = name.ToLowerInvariant(), DraftedByTeamId = team.Id,
        };
        db.Pokemon.Add(entry);
        var pick = new Pick { DraftId = draft.Id, PickNumber = ++_pickNo, TeamId = team.Id, PokemonEntry = entry, Tier = Tier.C };
        db.Picks.Add(pick);
        db.PokemonStats.Add(new PokemonStat
        {
            Pick = pick, GamesPlayed = gp, Kills = k, Deaths = d, ActiveTurns = activeTurns,
            DamageDealtDirect = dealt, DamageTakenDirect = taken, HpRecovered = heal,
        });
    }

    private async Task<(int leagueId, Draft draft, League league)> NewLeagueAsync(AppDbContext db)
    {
        var league = new League { Name = "SB", OwnerId = "owner" };
        db.Leagues.Add(league);
        await db.SaveChangesAsync();
        var draft = new Draft { LeagueId = league.Id };
        db.Drafts.Add(draft);
        await db.SaveChangesAsync();
        return (league.Id, draft, league);
    }

    private async Task<JsonElement> ScoreboardAsync(int leagueId)
    {
        var client = await _factory.SignedInAsAsync("viewer");
        var res = await client.GetAsync($"/api/leagues/{leagueId}/scoreboard");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Standings_sort_by_recordDiff_then_koDiff_then_wins_then_totalKos()
    {
        int leagueId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var (id, draft, league) = await NewLeagueAsync(db);
            leagueId = id;

            // Engineered so the label order == the expected finish order, each step
            // decided by exactly one tiebreak:
            var t1 = AddTeam(db, league, "T1", wins: 5, losses: 1); // recordDiff 4 -> 1st outright
            var t2 = AddTeam(db, league, "T2", wins: 4, losses: 2); // rd 2, koDiff 6, wins 4
            var t3 = AddTeam(db, league, "T3", wins: 3, losses: 1); // rd 2, koDiff 6, wins 3, totalKos 10
            var t4 = AddTeam(db, league, "T4", wins: 3, losses: 1); // rd 2, koDiff 6, wins 3, totalKos 8
            var t5 = AddTeam(db, league, "T5", wins: 3, losses: 1); // rd 2, koDiff 3 (lower)
            var t6 = AddTeam(db, league, "T6", wins: 0, losses: 0); // rd 0, no games at all
            await db.SaveChangesAsync();

            AddMon(db, draft, league, t1, "t1a", k: 3, d: 1, activeTurns: 0, dealt: 0, taken: 0, heal: 0);
            AddMon(db, draft, league, t2, "t2a", k: 6, d: 0, activeTurns: 0, dealt: 0, taken: 0, heal: 0); // koDiff 6, kos 6
            AddMon(db, draft, league, t3, "t3a", k: 10, d: 4, activeTurns: 0, dealt: 0, taken: 0, heal: 0); // koDiff 6, kos 10
            AddMon(db, draft, league, t4, "t4a", k: 8, d: 2, activeTurns: 0, dealt: 0, taken: 0, heal: 0); // koDiff 6, kos 8
            AddMon(db, draft, league, t5, "t5a", k: 5, d: 2, activeTurns: 0, dealt: 0, taken: 0, heal: 0); // koDiff 3
            // t6 has no picks/stats at all.
            await db.SaveChangesAsync();
        }

        var sb = await ScoreboardAsync(leagueId);
        var standings = sb.GetProperty("standings").EnumerateArray().ToList();

        Assert.Equal(
            new[] { "T1", "T2", "T3", "T4", "T5", "T6" },
            standings.Select(s => s.GetProperty("trainer").GetString()).ToArray());

        // Spot-check the numbers behind the ordering.
        Assert.Equal(4, standings[0].GetProperty("recordDiff").GetInt32());
        Assert.Equal(6, standings[1].GetProperty("koDiff").GetInt32());
        Assert.Equal(4, standings[1].GetProperty("wins").GetInt32());
        Assert.Equal(10, standings[2].GetProperty("totalKos").GetInt32()); // T3 beats T4 on totalKos
        Assert.Equal(4, standings[2].GetProperty("totalFaints").GetInt32()); // T3: KOs 10, deaths 4
        Assert.Equal(8, standings[3].GetProperty("totalKos").GetInt32());
        Assert.Equal(2, standings[3].GetProperty("totalFaints").GetInt32()); // T4: KOs 8, deaths 2
        Assert.Equal(3, standings[4].GetProperty("koDiff").GetInt32());

        // A team with no games still appears, at the bottom, all zeros.
        Assert.Equal("T6", standings[5].GetProperty("trainer").GetString());
        Assert.Equal(0, standings[5].GetProperty("totalKos").GetInt32());
        Assert.Equal(0, standings[5].GetProperty("recordDiff").GetInt32());
    }

    [Fact]
    public async Task Leaders_rank_top_five_mons_per_category_with_their_trainer()
    {
        int leagueId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var (id, draft, league) = await NewLeagueAsync(db);
            leagueId = id;

            var solo = AddTeam(db, league, "Solo", wins: 0, losses: 0, battleTurns: 100);
            await db.SaveChangesAsync();

            //         name  k  d  active dealt taken heal    presence  +/-  ratio  heal
            AddMon(db, draft, league, solo, "m1", 10, 1, 90, 200, 100, 50); // .90   9    2.0    50
            AddMon(db, draft, league, solo, "m2", 8, 2, 80, 150, 50, 40);   // .80   6    3.0    40
            AddMon(db, draft, league, solo, "m3", 6, 6, 70, 50, 0, 30);     // .70   0    inf    30
            AddMon(db, draft, league, solo, "m4", 4, 1, 60, 100, 200, 20);  // .60   3    0.5    20
            AddMon(db, draft, league, solo, "m5", 2, 5, 50, 10, 100, 10);   // .50  -3    0.1    10
            AddMon(db, draft, league, solo, "m6", 1, 1, 40, 0, 0, 5);       // .40   0    0      5
            await db.SaveChangesAsync();
        }

        var sb = await ScoreboardAsync(leagueId);
        var leaders = sb.GetProperty("leaders");

        string[] Names(string cat) => leaders.GetProperty(cat).EnumerateArray()
            .Select(e => e.GetProperty("pokemon").GetString()!).ToArray();

        // Presence = activeTurns / teamTurns; m6 (.40) is cut by the top-5.
        Assert.Equal(new[] { "m1", "m2", "m3", "m4", "m5" }, Names("presence"));
        // +/- = KOs - deaths; the 0/0 tie (m3 vs m6) breaks on KOs (m3 has more).
        Assert.Equal(new[] { "m1", "m2", "m4", "m3", "m6" }, Names("plusMinus"));
        // Healing = recovered + ally heal.
        Assert.Equal(new[] { "m1", "m2", "m3", "m4", "m5" }, Names("healing"));
        // Damage ratio = dealt / taken; m3 took no damage -> infinite, ranked 1st.
        Assert.Equal(new[] { "m3", "m2", "m1", "m4", "m5" }, Names("damageRatio"));

        var topPres = leaders.GetProperty("presence").EnumerateArray().First();
        Assert.Equal(0.90, topPres.GetProperty("value").GetDouble(), 3);
        Assert.Equal("Solo", topPres.GetProperty("trainer").GetString()); // trainer travels with the mon

        // Infinite ratio serializes as null; the next entry is a finite 3.0.
        var ratios = leaders.GetProperty("damageRatio").EnumerateArray().ToList();
        Assert.Equal(JsonValueKind.Null, ratios[0].GetProperty("value").ValueKind);
        Assert.Equal(3.0, ratios[1].GetProperty("value").GetDouble(), 3);

        Assert.Equal(9, leaders.GetProperty("plusMinus").EnumerateArray().First().GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Unknown_league_is_404()
    {
        var client = await _factory.SignedInAsAsync("viewer");
        var res = await client.GetAsync("/api/leagues/999999/scoreboard");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Scoreboard_requires_authentication()
    {
        var res = await _factory.CreateClient().GetAsync("/api/leagues/1/scoreboard");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
