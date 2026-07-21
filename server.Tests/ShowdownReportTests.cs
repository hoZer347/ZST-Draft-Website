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

    // The same matchup re-played with the other coach winning, used to prove a redo
    // replaces the first result rather than stacking on it.
    private const string LogBobWins = """
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
        |move|p2a: Gengar|Shadow Ball|p1a: Pikachu
        |-damage|p1a: Pikachu|0 fnt
        |faint|p1a: Pikachu
        |win|Bob
        """;

    // Opaque build text: the server stores it verbatim and never parses it, so any
    // distinct strings work. v2 stands in for a corrected/re-brought team on a redo.
    private const string AliceBuildV1 = "Pikachu @ Light Ball\nAbility: Static\nTera Type: Electric\n- Volt Tackle";
    private const string AliceBuildV2 = "Pikachu @ Choice Specs\nAbility: Static\nTera Type: Electric\n- Thunderbolt";
    private const string BobBuildV1 = "Gengar @ Life Orb\nAbility: Cursed Body\n- Shadow Ball";
    private const string BobBuildV2 = "Gengar @ Focus Sash\nAbility: Cursed Body\n- Sludge Bomb";

    private HttpClient ReportClient(bool withSecret = true)
    {
        var client = Factory.CreateClient();
        if (withSecret) client.DefaultRequestHeaders.Add("X-Report-Secret", DraftLeagueFactory.ReportSecret);
        return client;
    }

    private Task<HttpResponseMessage> ReportAsync(string log, string? p1 = null, string? p2 = null) =>
        ReportClient().PostAsJsonAsync("/api/showdown/report", new { log, p1Export = p1, p2Export = p2 });

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

        // The score alone isn't enough, an auto-reported battle carries no external
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

    [Fact]
    public async Task Stores_both_team_builds_and_serves_them_per_team()
    {
        var seed = await SeedAsync();
        (await ReportAsync(Log, AliceBuildV1, BobBuildV1)).EnsureSuccessStatusCode();

        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var match = await db.Matches.FirstAsync(m => m.Id == seed.Match.Id);
            // Alice (home) played p1 and Bob (away) p2, so each build lands on its side.
            Assert.Equal(AliceBuildV1, match.HomeTeamExport);
            Assert.Equal(BobBuildV1, match.AwayTeamExport);
        }

        // The per-team paste route (the link under each scoresheet) returns just that
        // side's build; with no team param it returns both (the combined fallback).
        var client = Factory.CreateClient();
        var home = await client.GetStringAsync($"/api/matches/{seed.Match.Id}/paste?team=home");
        Assert.Contains("Light Ball", home);
        Assert.DoesNotContain("Gengar", home);
        var away = await client.GetStringAsync($"/api/matches/{seed.Match.Id}/paste?team=away");
        Assert.Contains("Life Orb", away);
        Assert.DoesNotContain("Pikachu", away);
        var both = await client.GetStringAsync($"/api/matches/{seed.Match.Id}/paste");
        Assert.Contains("Light Ball", both);
        Assert.Contains("Life Orb", both);
    }

    [Fact]
    public async Task Serves_a_rendered_build_page_per_team()
    {
        var seed = await SeedAsync();
        (await ReportAsync(Log, AliceBuildV1, BobBuildV1)).EnsureSuccessStatusCode();

        var client = Factory.CreateClient();
        var res = await client.GetAsync($"/matches/{seed.Match.Id}/teams?team=home");
        res.EnsureSuccessStatusCode();
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);

        var html = await res.Content.ReadAsStringAsync();
        // The page renders the parsed set: species, item, ability and moves, and also
        // carries the verbatim export for import. It's this team only, not the other's.
        Assert.Contains("Pikachu", html);
        Assert.Contains("Light Ball", html);
        Assert.Contains("Volt Tackle", html);
        Assert.Contains("Alice", html);
        Assert.DoesNotContain("Gengar", html);

        // No team param renders both sides on one page.
        var both = await client.GetStringAsync($"/matches/{seed.Match.Id}/teams");
        Assert.Contains("Pikachu", both);
        Assert.Contains("Gengar", both);
    }

    [Fact]
    public async Task Schedule_payload_flags_which_sides_have_a_build()
    {
        var seed = await SeedAsync();
        (await ReportAsync(Log, AliceBuildV1, BobBuildV1)).EnsureSuccessStatusCode();

        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var sched = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{seed.League.Id}/schedule");
        var match = sched.GetProperty("matches").EnumerateArray().First(m => Int(m, "id") == seed.Match.Id);

        // The per-team "Team build" link under each scoresheet is gated on these flags,
        // so they MUST reach the client. Regression: the schedule's final projection
        // forwarded only hasPaste, so the flags were computed but never sent, and no
        // link ever rendered even when both builds were stored.
        Assert.True(match.GetProperty("hasPaste").GetBoolean());
        Assert.True(match.GetProperty("hasHomePaste").GetBoolean());
        Assert.True(match.GetProperty("hasAwayPaste").GetBoolean());
    }

    [Fact]
    public async Task Battle_stats_rows_carry_a_dex_so_icons_always_have_a_fallback()
    {
        // Every mon row that renders an icon MUST ship both a sprite slug and a dex:
        // the client's fallback chain keys its Serebii mega art + PokeAPI base sprite
        // off the dex, so a row without one leaves a custom mega (no gen-5 sprite,
        // e.g. M-Barbaracle) as a broken/missing icon. Regression: the schedule's
        // battle-stats projection sent `sprite` but dropped `dex`.
        var seed = await SeedAsync();
        (await ReportAsync(Log)).EnsureSuccessStatusCode();

        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var sched = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{seed.League.Id}/schedule");
        var match = sched.GetProperty("matches").EnumerateArray().First(m => Int(m, "id") == seed.Match.Id);

        var rows = match.GetProperty("battleStats").EnumerateArray().ToList();
        Assert.NotEmpty(rows); // a played match has scraped per-mon rows
        foreach (var r in rows)
        {
            Assert.True(r.TryGetProperty("sprite", out var sprite) && sprite.ValueKind == JsonValueKind.String,
                "every battle-stats row carries a sprite slug");
            Assert.True(r.TryGetProperty("dex", out var dex) && dex.ValueKind == JsonValueKind.Number,
                "every battle-stats row carries a dex for the icon fallback chain");
            Assert.True(dex.GetInt32() > 0, "the dex is a real national-dex number");
        }
    }

    [Fact]
    public async Task A_server_rebattle_redoes_the_recorded_match_and_refreshes_builds()
    {
        var seed = await SeedAsync();
        (await ReportAsync(Log, AliceBuildV1, BobBuildV1)).EnsureSuccessStatusCode();   // Alice wins 2-1
        var redo = await ReportAsync(LogBobWins, AliceBuildV2, BobBuildV2);             // replayed: Bob wins 2-1
        redo.EnsureSuccessStatusCode();
        Assert.True((await redo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recorded").GetBoolean());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var match = await db.Matches.FirstAsync(m => m.Id == seed.Match.Id);
        Assert.Equal(MatchResult.AwayWin, match.Result);
        Assert.Equal(1, match.HomeScore);
        Assert.Equal(2, match.AwayScore);
        // Builds refreshed to the re-battle's teams.
        Assert.Equal(AliceBuildV2, match.HomeTeamExport);
        Assert.Equal(BobBuildV2, match.AwayTeamExport);

        // Standings reflect only the new result: the first was backed out, not stacked.
        var home = await db.Teams.FirstAsync(t => t.Id == seed.Home.Id);
        var away = await db.Teams.FirstAsync(t => t.Id == seed.Away.Id);
        Assert.Equal(0, home.Wins);
        Assert.Equal(1, home.Losses);
        Assert.Equal(1, away.Wins);
        Assert.Equal(0, away.Losses);

        // Per-mon stats too: Pikachu's game-1 KO was backed out and Gengar now owns
        // the KO, with neither mon double-counted (one game played, not two).
        var pika = await db.PokemonStats.SingleAsync(s => s.PickId == seed.Picks["pikachu"].Id);
        Assert.Equal(1, pika.GamesPlayed);
        Assert.Equal(0, pika.Kills);
        var gengar = await db.PokemonStats.SingleAsync(s => s.PickId == seed.Picks["gengar"].Id);
        Assert.Equal(1, gengar.GamesPlayed);
        Assert.Equal(1, gengar.Kills);
    }

    [Fact]
    public async Task Remove_then_replay_on_the_server_refreshes_the_builds()
    {
        var seed = await SeedAsync();
        (await ReportAsync(Log, AliceBuildV1, BobBuildV1)).EnsureSuccessStatusCode();

        // A coach/admin removes the replay: the match returns to Pending.
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        (await admin.DeleteAsync($"/api/matches/{seed.Match.Id}/replay")).EnsureSuccessStatusCode();

        // Re-playing on the server records it afresh, storing the new builds.
        (await ReportAsync(Log, AliceBuildV2, BobBuildV2)).EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var match = await db.Matches.FirstAsync(m => m.Id == seed.Match.Id);
        Assert.Equal(AliceBuildV2, match.HomeTeamExport);
        Assert.Equal(BobBuildV2, match.AwayTeamExport);

        // One game recorded, not two: Remove backed the first out before the replay.
        var home = await db.Teams.FirstAsync(t => t.Id == seed.Home.Id);
        Assert.Equal(1, home.Wins);
        Assert.Equal(0, home.Losses);
        var pika = await db.PokemonStats.SingleAsync(s => s.PickId == seed.Picks["pikachu"].Id);
        Assert.Equal(1, pika.GamesPlayed);
    }
}
