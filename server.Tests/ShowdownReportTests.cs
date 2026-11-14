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

    private const string StrayLog = """
        |gametype|doubles
        |player|p1|DraftTester|2|
        |player|p2|DraftNetLive|266|
        |gen|9
        |tier|[Gen 9 Champions] NatDex ZST Sim
        |poke|p1|Garchomp, L50, F|
        |poke|p1|Whimsicott, L50, M|
        |poke|p1|Incineroar, L50, F|
        |poke|p1|Rotom-Wash, L50|
        |poke|p1|Amoonguss, L50, F|
        |poke|p1|Dragonite, L50, M|
        |poke|p2|Bastiodon, L50, M|
        |poke|p2|Drakloak, L50, M|
        |poke|p2|Meltan, L50|
        |poke|p2|Simisear, L50, F|
        |poke|p2|Pikachu-Kalos, L50, M|
        |poke|p2|Skrelp, L50, F|
        |teamsize|p1|6
        |teamsize|p2|6
        |start
        |switch|p1a: Rotom|Rotom-Wash, L50|157/157
        |switch|p1b: Incineroar|Incineroar, L50, F|202/202
        |switch|p2a: Simisear|Simisear, L50, F|162/162
        |switch|p2b: Bastiodon|Bastiodon, L50, M|144/144
        |turn|1
        |move|p1a: Rotom|Protect|p1a: Rotom
        |move|p1b: Incineroar|Fake Out|p2a: Simisear
        |-damage|p2a: Simisear|128/162
        |-damage|p1b: Incineroar|177/202|[from] item: Jaboca Berry|[of] p2a: Simisear
        |move|p2b: Bastiodon|Earthquake|p1b: Incineroar|[spread] p2a,p1b
        |-damage|p2a: Simisear|80/162
        |-damage|p1b: Incineroar|141/202
        |turn|2
        |switch|p1b: Amoonguss|Amoonguss, L50, F|221/221
        |move|p2a: Simisear|Superpower|p1b: Amoonguss
        |-damage|p1b: Amoonguss|200/221
        |move|p1a: Rotom|Thunder Wave|p2b: Bastiodon
        |turn|3
        |-heal|p1b: Amoonguss|221/221|[from] ability: Regenerator|[silent]
        |switch|p1b: Dragonite|Dragonite, L50, M|198/198
        |move|p2a: Simisear|Fire Punch|p1b: Dragonite
        |-damage|p1b: Dragonite|192/198
        |move|p1a: Rotom|Thunder Wave|p2b: Bastiodon
        |move|p2b: Bastiodon|Earth Power|p1a: Rotom
        |turn|4
        |switch|p1a: Garchomp|Garchomp, L50, F|215/215
        |move|p1b: Dragonite|Extreme Speed|p2b: Bastiodon
        |-damage|p2b: Bastiodon|136/144 par
        |move|p2a: Simisear|Shadow Claw|p1b: Dragonite
        |-damage|p1b: Dragonite|175/198
        |move|p2b: Bastiodon|Earth Power|p1a: Garchomp
        |-damage|p1a: Garchomp|193/215
        |turn|5
        |switch|p1b: Amoonguss|Amoonguss, L50, F|221/221
        |move|p2a: Simisear|Shadow Claw|p1a: Garchomp
        |-damage|p1a: Garchomp|177/215
        |-damage|p2a: Simisear|60/162|[from] ability: Rough Skin|[of] p1a: Garchomp
        |move|p1a: Garchomp|Earthquake|p2a: Simisear|[spread] p1b,p2a,p2b
        |-damage|p1b: Amoonguss|121/221
        |-damage|p2a: Simisear|0 fnt
        |-damage|p2b: Bastiodon|0 fnt
        |faint|p2a: Simisear
        |faint|p2b: Bastiodon
        |switch|p2a: Drakloak|Drakloak, L50, M|165/165
        |switch|p2b: Skrelp|Skrelp, L50, F|126/126
        |turn|6
        |switch|p1a: Incineroar|Incineroar, L50, F|141/202
        |move|p2a: Drakloak|Steel Wing|p1a: Incineroar
        |-damage|p1a: Incineroar|129/202
        |move|p2b: Skrelp|Aqua Tail|p1a: Incineroar
        |-damage|p1a: Incineroar|49/202
        |move|p1b: Amoonguss|Toxic|p2b: Skrelp
        |turn|7
        |-heal|p1b: Amoonguss|194/221|[from] ability: Regenerator|[silent]
        |switch|p1b: Rotom|Rotom-Wash, L50|157/157
        |move|p1a: Incineroar|Fake Out|p2a: Drakloak
        |move|p2a: Drakloak|Fire Blast|p1b: Rotom
        |-damage|p1b: Rotom|144/157
        |move|p2b: Skrelp|Aqua Tail|p1a: Incineroar
        |-damage|p1a: Incineroar|0 fnt
        |faint|p1a: Incineroar
        |switch|p1a: Whimsicott|Whimsicott, L50, M|167/167
        |turn|8
        |move|p1a: Whimsicott|Encore|p2b: Skrelp
        |move|p2a: Drakloak|Steel Wing|p1a: Whimsicott
        |-damage|p1a: Whimsicott|119/167
        |move|p1b: Rotom|Hydro Pump|p2a: Drakloak
        |-damage|p2a: Drakloak|122/165
        |move|p2b: Skrelp|Aqua Tail|p1b: Rotom|[miss]
        |turn|9
        |switch|p1b: Dragonite|Dragonite, L50, M|175/198
        |move|p1a: Whimsicott|Encore||[still]
        |move|p2a: Drakloak|Steel Wing|p1a: Whimsicott
        |-damage|p1a: Whimsicott|71/167
        |move|p2b: Skrelp|Aqua Tail|p1b: Dragonite
        |-damage|p1b: Dragonite|157/198
        |turn|10
        |move|p1a: Whimsicott|Encore||[still]
        |move|p2a: Drakloak|Steel Wing|p1a: Whimsicott
        |-damage|p1a: Whimsicott|23/167
        |move|p1b: Dragonite|Outrage|p2a: Drakloak
        |-damage|p2a: Drakloak|0 fnt
        |faint|p2a: Drakloak
        |move|p2b: Skrelp|Aqua Tail|p1b: Dragonite
        |-damage|p1b: Dragonite|117/198
        |switch|p2a: Meltan|Meltan, L50|129/129
        |turn|11
        |move|p1a: Whimsicott|Tailwind|p1a: Whimsicott
        |move|p1b: Dragonite|Outrage|p2b: Skrelp|[from] lockedmove
        |-damage|p2b: Skrelp|0 fnt
        |faint|p2b: Skrelp
        |move|p2a: Meltan|Flash Cannon|p1a: Whimsicott
        |-damage|p1a: Whimsicott|0 fnt
        |faint|p1a: Whimsicott
        |switch|p1a: Rotom|Rotom-Wash, L50|144/157
        |switch|p2b: Pikachu|Pikachu-Kalos, L50, M|112/112
        |turn|12
        |move|p1a: Rotom|Thunderbolt|p2a: Meltan
        |-damage|p2a: Meltan|0 fnt
        |faint|p2a: Meltan
        |move|p1b: Dragonite|Outrage|p2b: Pikachu|[from] lockedmove
        |-damage|p2b: Pikachu|0 fnt
        |faint|p2b: Pikachu
        |win|DraftTester
        """;

    private static readonly string[] StrayHomeMons = ["Whimsicott", "Dragonite", "Tyranitar", "Ferrothorn", "Clefable", "Gholdengo"];
    private static readonly string[] StrayAwayMons = ["Bastiodon", "Kingambit", "Corviknight", "Toxapex", "Hatterene", "Lucario"];

    private const string LeagueGameLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |tier|[Gen 9 Champions] NatDex ZST Sim
        |poke|p1|Whimsicott, L50|
        |poke|p1|Dragonite, L50|
        |poke|p1|Tyranitar, L50|
        |poke|p1|Ferrothorn, L50|
        |poke|p1|Clefable, L50|
        |poke|p1|Gholdengo, L50|
        |poke|p2|Bastiodon, L50|
        |poke|p2|Kingambit, L50|
        |poke|p2|Corviknight, L50|
        |poke|p2|Toxapex, L50|
        |poke|p2|Hatterene, L50|
        |poke|p2|Lucario, L50|
        |teamsize|p1|6
        |teamsize|p2|6
        |start
        |switch|p1a: Tyranitar|Tyranitar, L50|100/100
        |switch|p2a: Lucario|Lucario, L50|100/100
        |turn|1
        |move|p2a: Lucario|Close Combat|p1a: Tyranitar
        |-damage|p1a: Tyranitar|0 fnt
        |faint|p1a: Tyranitar
        |win|Bob
        """;

    private HttpClient ReportClient(bool withSecret = true)
    {
        var client = Factory.CreateClient();
        if (withSecret) client.DefaultRequestHeaders.Add("X-Report-Secret", DraftLeagueFactory.ReportSecret);
        return client;
    }

    private Task<HttpResponseMessage> ReportAsync(string log, string? p1 = null, string? p2 = null) =>
        ReportClient().PostAsJsonAsync("/api/showdown/report", new { log, p1Export = p1, p2Export = p2 });

    private async Task<SeasonSeed.Seeded> SeedAsync(string[]? homeMons = null, string[]? awayMons = null)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seed = await SeasonSeed.SeasonAsync(db,
            ("alice", "Alice"), homeMons ?? ["Pikachu", "Snorlax"],
            ("bob", "Bob"), awayMons ?? ["Gengar", "Blissey"]);

        // An unplayed game elsewhere keeps the regular season open, so a re-battle
        // redoes Alice v Bob instead of landing in a playoff final between them.
        var carol = new Team { LeagueId = seed.League.Id, Name = "Carol", CoachName = "Carol", CoachId = "carol" };
        var dave = new Team { LeagueId = seed.League.Id, Name = "Dave", CoachName = "Dave", CoachId = "dave" };
        db.Matches.Add(new Match { LeagueId = seed.League.Id, Week = 1, HomeTeam = carol, AwayTeam = dave });
        await db.SaveChangesAsync();
        return seed;
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
    public async Task A_scrims_rematch_leaves_the_recorded_league_game_alone()
    {
        var seed = await SeedAsync();
        (await ReportAsync(Log, AliceBuildV1, BobBuildV1)).EnsureSuccessStatusCode();   // Alice wins 2-1
        var scrim = await ReportAsync("|tier|[Gen 9 Champions] NatDex Scrims\n" + LogBobWins, AliceBuildV2, BobBuildV2);
        scrim.EnsureSuccessStatusCode();
        Assert.False((await scrim.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recorded").GetBoolean());

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var match = await db.Matches.FirstAsync(m => m.Id == seed.Match.Id);
        Assert.Equal(MatchResult.HomeWin, match.Result);
        Assert.Equal(AliceBuildV1, match.HomeTeamExport);
        var home = await db.Teams.FirstAsync(t => t.Id == seed.Home.Id);
        Assert.Equal(1, home.Wins);
        Assert.Equal(0, home.Losses);
        var gengar = await db.PokemonStats.SingleAsync(s => s.PickId == seed.Picks["gengar"].Id);
        Assert.Equal(0, gengar.Kills);
    }

    [Fact]
    public async Task Recording_the_last_regular_game_opens_the_playoffs()
    {
        var seed = await SeedAsync();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var other = await db.Matches.SingleAsync(m => m.LeagueId == seed.League.Id && m.Id != seed.Match.Id);
            other.Result = MatchResult.HomeWin;
            await db.SaveChangesAsync();
        }

        (await ReportAsync(Log, AliceBuildV1, BobBuildV1)).EnsureSuccessStatusCode();

        using var check = Factory.Services.CreateScope();
        var games = await check.ServiceProvider.GetRequiredService<AppDbContext>().Matches
            .Where(m => m.LeagueId == seed.League.Id && m.PlayoffRound != null).ToListAsync();
        Assert.Equal(2, games.Count);
        Assert.All(games, g => Assert.Equal((1, MatchResult.Pending, 2), (g.PlayoffRound!.Value, g.Result, g.Week)));
    }

    [Fact]
    public async Task Successive_reports_fill_a_best_of_three_series_in_order()
    {
        var seed = await SeedAsync();
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Matches.Where(m => m.LeagueId == seed.League.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Result, MatchResult.HomeWin));
            await db.Teams.Where(t => t.Id == seed.Home.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.PlayoffSeed, 1));
            await db.Teams.Where(t => t.Id == seed.Away.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.PlayoffSeed, 2));
            await Playoffs.SyncAsync(db, seed.League.Id);
        }

        async Task<List<Match>> FinalAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Matches
                .Where(m => m.LeagueId == seed.League.Id && m.PlayoffRound != null).OrderBy(m => m.Id).ToListAsync();
        }

        (await ReportAsync(Log, AliceBuildV1, BobBuildV1)).EnsureSuccessStatusCode();
        (await ReportAsync(LogBobWins, AliceBuildV2, BobBuildV2)).EnsureSuccessStatusCode();
        Assert.Equal([MatchResult.HomeWin, MatchResult.AwayWin, MatchResult.Pending], (await FinalAsync()).Select(g => g.Result));

        (await ReportAsync(Log, AliceBuildV1, BobBuildV2)).EnsureSuccessStatusCode();
        var games = await FinalAsync();
        Assert.Equal([MatchResult.HomeWin, MatchResult.AwayWin, MatchResult.HomeWin], games.Select(g => g.Result));
        Assert.All(games, g => Assert.Equal((seed.Home.Id, seed.Away.Id), (g.HomeTeamId, g.AwayTeamId)));
        Assert.Equal([(AliceBuildV1, BobBuildV1), (AliceBuildV2, BobBuildV2), (AliceBuildV1, BobBuildV2)],
            games.Select(g => (g.HomeTeamExport, g.AwayTeamExport)));

        // A decided series has no next game, so another report redoes its latest one.
        (await ReportAsync(LogBobWins, AliceBuildV2, BobBuildV1)).EnsureSuccessStatusCode();
        games = await FinalAsync();
        Assert.Equal([MatchResult.HomeWin, MatchResult.AwayWin, MatchResult.AwayWin], games.Select(g => g.Result));
        Assert.Equal((AliceBuildV2, BobBuildV1), (games[2].HomeTeamExport, games[2].AwayTeamExport));
    }

    [Fact]
    public async Task A_rebattle_leaves_a_playoff_game_alone_once_the_next_round_is_played()
    {
        var seed = await SeedAsync();
        (await ReportAsync(Log, AliceBuildV1, BobBuildV1)).EnsureSuccessStatusCode();

        int firstRoundId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var carol = await db.Teams.SingleAsync(t => t.LeagueId == seed.League.Id && t.CoachId == "carol");
            var firstRound = new Match
            {
                LeagueId = seed.League.Id, Week = 2, PlayoffRound = 1, PlayoffSlot = 0,
                HomeTeamId = seed.Home.Id, AwayTeamId = seed.Away.Id, Result = MatchResult.HomeWin,
            };
            db.Matches.AddRange(firstRound, new Match
            {
                LeagueId = seed.League.Id, Week = 3, PlayoffRound = 2, PlayoffSlot = 0,
                HomeTeamId = seed.Home.Id, AwayTeamId = carol.Id, Result = MatchResult.HomeWin,
            });
            await db.SaveChangesAsync();
            firstRoundId = firstRound.Id;
        }

        var redo = await ReportAsync(LogBobWins, AliceBuildV2, BobBuildV2);
        Assert.False((await redo.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recorded").GetBoolean());

        using var check = Factory.Services.CreateScope();
        var matches = check.ServiceProvider.GetRequiredService<AppDbContext>().Matches;
        Assert.Equal(MatchResult.HomeWin, (await matches.SingleAsync(m => m.Id == firstRoundId)).Result);
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

    [Fact]
    public async Task A_stray_battle_sharing_a_few_mons_leaves_the_recorded_match_alone()
    {
        var seed = await SeedAsync(StrayHomeMons, StrayAwayMons);
        var league = await ReportAsync(LeagueGameLog);
        Assert.True((await league.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recorded").GetBoolean());

        async Task<string> SnapshotAsync()
        {
            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var match = await db.Matches.AsNoTracking().SingleAsync(m => m.Id == seed.Match.Id);
            var teams = await db.Teams.AsNoTracking()
                .Where(t => t.Id == seed.Home.Id || t.Id == seed.Away.Id).OrderBy(t => t.Id)
                .Select(t => new { t.Id, t.Wins, t.Losses }).ToListAsync();
            var stats = await db.PokemonStats.AsNoTracking().OrderBy(s => s.PickId).ToListAsync();
            return JsonSerializer.Serialize(new
            {
                match.Result, match.HomeScore, match.AwayScore, match.ReplayLog, match.ReplayHomeSide, match.ReplayUrl,
                teams, stats,
            });
        }

        var before = await SnapshotAsync();
        Assert.Contains("|win|Bob", before);

        var stray = await ReportAsync(StrayLog);
        stray.EnsureSuccessStatusCode();
        Assert.False((await stray.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recorded").GetBoolean());
        Assert.Equal(before, await SnapshotAsync());
    }

    [Fact]
    public async Task Scoring_a_stray_battle_against_a_match_sharing_a_few_mons_fails()
    {
        var seed = await SeedAsync(StrayHomeMons, StrayAwayMons);

        using var scope = Factory.Services.CreateScope();
        var match = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Matches.SingleAsync(m => m.Id == seed.Match.Id);
        var result = await scope.ServiceProvider.GetRequiredService<ReplayScorer>().ScoreLogAsync(match, StrayLog);
        Assert.False(result.Ok);
    }
}
