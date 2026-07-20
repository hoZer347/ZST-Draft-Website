using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The server-to-server auto-report endpoint (/api/showdown/report): our Showdown
/// server POSTs a finished battle's log, guarded by a shared secret. A league game
/// is auto-matched to its pending match and fully recorded (score, standings,
/// per-mon stats); anything else is quietly acknowledged, not errored.
/// </summary>
public class ShowdownReportTests : DraftScenarioBase
{
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

    private HttpClient ReportClient(bool withSecret = true)
    {
        var client = Factory.CreateClient();
        if (withSecret) client.DefaultRequestHeaders.Add("X-Report-Secret", DraftLeagueFactory.ReportSecret);
        return client;
    }

    private async Task<SeasonSeed.Seeded> SeedAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), ["Pikachu", "Snorlax"],
            ("bob", "Bob"), ["Gengar", "Blissey"]);
    }

    [Fact]
    public async Task Rejects_a_report_with_no_or_wrong_secret()
    {
        var noSecret = await Factory.CreateClient().PostAsJsonAsync("/api/showdown/report", new { log = Log });
        Assert.Equal(HttpStatusCode.Unauthorized, noSecret.StatusCode);

        var wrong = Factory.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Report-Secret", "nope");
        var res = await wrong.PostAsJsonAsync("/api/showdown/report", new { log = Log });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Records_a_league_game_score_standings_and_stats()
    {
        var seed = await SeedAsync();

        var res = await ReportClient().PostAsJsonAsync("/api/showdown/report", new { log = Log });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("recorded").GetBoolean());
        Assert.Equal(seed.Match.Id, Int(body, "id"));

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var match = await db.Matches.FirstAsync(m => m.Id == seed.Match.Id);
        Assert.Equal(MatchResult.HomeWin, match.Result);
        Assert.Equal(2, match.HomeScore);
        Assert.Equal(1, match.AwayScore);

        var home = await db.Teams.FirstAsync(t => t.Id == seed.Home.Id);
        var away = await db.Teams.FirstAsync(t => t.Id == seed.Away.Id);
        Assert.Equal(1, home.Wins);
        Assert.Equal(1, away.Losses);

        // Per-mon stats were recorded (Pikachu KO'd Gengar).
        var pika = await db.PokemonStats.SingleAsync(s => s.PickId == seed.Picks["pikachu"].Id);
        Assert.Equal(1, pika.GamesPlayed);
        Assert.Equal(1, pika.Kills);
    }

    [Fact]
    public async Task Auto_reported_battle_gets_a_local_replay_link()
    {
        var seed = await SeedAsync();
        (await ReportClient().PostAsJsonAsync("/api/showdown/report", new { log = Log })).EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var match = await db.Matches.FirstAsync(m => m.Id == seed.Match.Id);

        // The score alone isn't enough — an auto-reported battle carries no external
        // Showdown URL, but we captured its log, so "Watch replay" must point at our
        // own renderer. (Regression: it recorded the score but left ReplayUrl null.)
        Assert.Equal($"/api/matches/{match.Id}/replay", match.ReplayUrl);
        Assert.False(string.IsNullOrEmpty(match.ReplayLog));
        Assert.Equal("p1", match.ReplayHomeSide);
    }

    [Fact]
    public async Task Acknowledges_but_does_not_record_a_non_league_game()
    {
        await SeedAsync(); // teams exist, but this log's mons belong to nobody

        var strangers = Log
            .Replace("Pikachu", "Zapdos").Replace("Snorlax", "Moltres")
            .Replace("Gengar", "Articuno").Replace("Blissey", "Lugia");

        var res = await ReportClient().PostAsJsonAsync("/api/showdown/report", new { log = strangers });
        res.EnsureSuccessStatusCode(); // 200, not an error
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("recorded").GetBoolean());
    }
}
