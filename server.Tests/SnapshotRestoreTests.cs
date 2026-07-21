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
/// Season snapshot save + restore across the states a season passes through: mid
/// draft, right after the draft, part way through the season, and complete. Each
/// test seeds a known league straight into the DB, downloads the snapshot over
/// HTTP, restores it back over HTTP, and checks the rebuilt league (picks,
/// standings, per-mon stats and draft-stats) matches the captured one, exactly as
/// it must after a website update rebuilds from a backup.
/// </summary>
public class SnapshotRestoreTests : IAsyncLifetime
{
    private readonly DraftLeagueFactory _factory = new();
    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await ((IAsyncLifetime)_factory).DisposeAsync();

    // Home (p1) sweeps with Pikachu; away's two mons faint. Home wins 2-0. The mons
    // are the two teams' whole rosters, so the scraper resolves every line to a pick.
    private const string BattleLog = """
        |player|p1|Alice|1|
        |player|p2|Bob|2|
        |poke|p1|Pikachu, M|
        |poke|p1|Snorlax, M|
        |poke|p2|Gengar, M|
        |poke|p2|Blissey, F|
        |start
        |switch|p1a: Pikachu|Pikachu, M|100/100
        |switch|p2a: Gengar|Gengar, M|100/100
        |turn|1
        |move|p1a: Pikachu|Thunderbolt|p2a: Gengar
        |-crit|p2a: Gengar
        |-damage|p2a: Gengar|0 fnt
        |faint|p2a: Gengar
        |switch|p2a: Blissey|Blissey, F|100/100
        |turn|2
        |move|p1a: Pikachu|Thunderbolt|p2a: Blissey
        |-damage|p2a: Blissey|0 fnt
        |faint|p2a: Blissey
        |win|Alice
        """;

    // ── seeding ──────────────────────────────────────────────────────────────

    // Builds a league with two rostered teams and `matchCount` matches between them,
    // the first `played` of which are recorded from the shared log (Alice wins each).
    // Returns the league id.
    private async Task<int> SeedAsync(int matchCount, int played, int pickCount = 2)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var recorder = scope.ServiceProvider.GetRequiredService<MatchStatsRecorder>();

        var league = new League { Name = "Snap", OwnerId = "owner" };
        db.Leagues.Add(league);
        await db.SaveChangesAsync();
        var draft = new Draft { LeagueId = league.Id, State = DraftState.Running };
        db.Drafts.Add(draft);
        await db.SaveChangesAsync();

        var home = new Team { LeagueId = league.Id, Name = "Alice", CoachName = "Alice", CoachId = "coach-alice" };
        var away = new Team { LeagueId = league.Id, Name = "Bob", CoachName = "Bob", CoachId = "coach-bob" };
        db.Teams.AddRange(home, away);
        await db.SaveChangesAsync();

        // Rosters: the two battling mons plus a benched one each, up to pickCount.
        var homeMons = new[] { "Pikachu", "Snorlax" };
        var awayMons = new[] { "Gengar", "Blissey" };
        var n = 0;
        void Roster(Team team, string[] mons)
        {
            var first = true;
            foreach (var mon in mons.Take(pickCount))
            {
                var entry = new PokemonEntry { LeagueId = league.Id, Name = mon, Tier = Tier.C, Sprite = mon.ToLowerInvariant(), DraftedByTeamId = team.Id };
                db.Pokemon.Add(entry);
                db.Picks.Add(new Pick
                {
                    DraftId = draft.Id, PickNumber = ++n, TeamId = team.Id, PokemonEntry = entry, Tier = Tier.C,
                    // The passed-options run + auto-pick flag, so the round-trip proves they survive.
                    OtherOptions = OtherOptionsFor(mon),
                    WasAutoPick = first,
                });
                first = false;
            }
        }
        Roster(home, homeMons);
        Roster(away, awayMons);
        await db.SaveChangesAsync();

        for (var i = 0; i < matchCount; i++)
        {
            var match = new Match { LeagueId = league.Id, Week = i + 1, HomeTeamId = home.Id, AwayTeamId = away.Id, HomeTeam = home, AwayTeam = away };
            db.Matches.Add(match);
            await db.SaveChangesAsync();
            if (i < played)
            {
                match.Result = MatchResult.HomeWin;
                match.HomeScore = 2; match.AwayScore = 0;
                match.ReplayLog = BattleLog; match.ReplayHomeSide = "p1";
                match.ReplayUrl = $"/api/matches/{match.Id}/replay";
                match.HomeTeamExport = "Pikachu\nAbility: Static\n- Thunderbolt\n";
                match.AwayTeamExport = "Gengar\nAbility: Levitate\n- Shadow Ball\n";
                MatchReporting.ApplyToStandings(home, away, MatchResult.HomeWin, +1);
                await recorder.ApplyAsync(match, "p1", BattleLog, MatchResult.HomeWin, +1, default);
                await db.SaveChangesAsync(); // persist between games so stats accumulate, not duplicate
            }
        }
        await db.SaveChangesAsync();
        return league.Id;
    }

    // A distinct passed-options run per mon, in the JSON shape a real pick uses.
    private static string OtherOptionsFor(string mon) =>
        $"[{{\"name\":\"passed-{mon}\",\"sprite\":\"passed-{mon.ToLowerInvariant()}\",\"tier\":\"C\"}}]";

    private async Task<HttpClient> AdminAsync() => await _factory.SignedInAsAsync("admin", admin: true);

    private static async Task<JsonElement> RestoreAsync(HttpClient admin, int leagueId, JsonElement snapshot)
    {
        var res = await admin.PostAsJsonAsync($"/api/leagues/{leagueId}/snapshot", snapshot);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    // ── the DB truth after a round-trip ──────────────────────────────────────

    private async Task<(int Teams, int Picks, int PlayedMatches, int HomeWins, int Crits, int PikachuKills, int PassedRuns, int AutoPicks)> DbFactsAsync(int leagueId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var teams = await db.Teams.CountAsync(t => t.LeagueId == leagueId);
        var picks = await db.Picks.CountAsync(p => p.Team.LeagueId == leagueId);
        var playedMatches = await db.Matches.CountAsync(m => m.LeagueId == leagueId && m.Result != MatchResult.Pending);
        var homeWins = await db.Teams.Where(t => t.LeagueId == leagueId).SumAsync(t => t.Wins);
        var crits = await db.PokemonStats.Where(s => s.Pick.Team.LeagueId == leagueId).SumAsync(s => s.Crits);
        var pika = await db.PokemonStats
            .Where(s => s.Pick.Team.LeagueId == leagueId && s.Pick.PokemonEntry.Name == "Pikachu")
            .SumAsync(s => s.Kills);
        // The passed-options run and the auto-pick flag must survive save + restore.
        var passedRuns = await db.Picks.CountAsync(p => p.Team.LeagueId == leagueId && p.OtherOptions != null);
        var autoPicks = await db.Picks.CountAsync(p => p.Team.LeagueId == leagueId && p.WasAutoPick);
        return (teams, picks, playedMatches, homeWins, crits, pika, passedRuns, autoPicks);
    }

    // The exact passed-options JSON stored on a mon's pick (for a value-level check).
    private async Task<string?> PassedRunForAsync(int leagueId, string pokemon)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Picks
            .Where(p => p.Team.LeagueId == leagueId && p.PokemonEntry.Name == pokemon)
            .Select(p => p.OtherOptions).FirstOrDefaultAsync();
    }

    // ── tests ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mid_draft_snapshot_restores_the_picks_made_so_far()
    {
        // A draft in progress: only one mon drafted per team, no matches.
        var leagueId = await SeedAsync(matchCount: 0, played: 0, pickCount: 1);
        var admin = await AdminAsync();

        var snap = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/snapshot");
        Assert.Equal(2, snap.GetProperty("picks").GetArrayLength());
        // The snapshot carries the passed-options run for each pick.
        Assert.All(snap.GetProperty("picks").EnumerateArray(),
            p => Assert.False(string.IsNullOrEmpty(p.GetProperty("otherOptions").GetString())));

        var r = await RestoreAsync(admin, leagueId, snap);
        Assert.Equal(2, r.GetProperty("teams").GetInt32());
        Assert.Equal(2, r.GetProperty("picks").GetInt32());
        Assert.Equal(0, r.GetProperty("missedPicks").GetInt32());

        var facts = await DbFactsAsync(leagueId);
        Assert.Equal(2, facts.Teams);
        Assert.Equal(2, facts.Picks);
        Assert.Equal(0, facts.PlayedMatches);
        Assert.Equal(2, facts.PassedRuns);   // both picks kept their passed-options run
        Assert.Equal(2, facts.AutoPicks);    // (each team's first pick was flagged auto)
        Assert.Equal(OtherOptionsFor("Pikachu"), await PassedRunForAsync(leagueId, "Pikachu")); // exact value survived
    }

    [Fact]
    public async Task Post_draft_snapshot_restores_full_rosters_and_pending_schedule()
    {
        // Draft done, season not started: full rosters, matches all pending.
        var leagueId = await SeedAsync(matchCount: 3, played: 0);
        var admin = await AdminAsync();

        var snap = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/snapshot");
        var r = await RestoreAsync(admin, leagueId, snap);
        Assert.Equal(4, r.GetProperty("picks").GetInt32());
        Assert.Equal(3, r.GetProperty("matches").GetInt32());
        Assert.Equal(0, r.GetProperty("recorded").GetInt32()); // nothing played

        var facts = await DbFactsAsync(leagueId);
        Assert.Equal((2, 4, 0), (facts.Teams, facts.Picks, facts.PlayedMatches));
        Assert.Equal(0, facts.HomeWins);
        Assert.Equal(4, facts.PassedRuns); // every pick's passed-options run survived
        Assert.Equal(2, facts.AutoPicks);
    }

    [Theory]
    [InlineData(3, 2)] // part way through: 2 of 3 games played
    [InlineData(3, 3)] // season complete: all games played
    public async Task Restore_rebuilds_standings_and_stats_from_the_stored_logs(int matchCount, int played)
    {
        var leagueId = await SeedAsync(matchCount, played);
        var admin = await AdminAsync();

        var before = await DbFactsAsync(leagueId);
        Assert.Equal(played, before.PlayedMatches);
        Assert.Equal(played, before.HomeWins);            // Alice won every played game
        Assert.Equal(2 * played, before.PikachuKills);    // 2 KOs per game
        Assert.Equal(played, before.Crits);               // one crit per game
        Assert.Equal(4, before.PassedRuns);               // every pick carries a passed-options run

        // Snapshot, then restore back into the same league.
        var snap = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/snapshot");
        var r = await RestoreAsync(admin, leagueId, snap);
        Assert.Equal(played, r.GetProperty("recorded").GetInt32());
        Assert.Equal(0, r.GetProperty("missedPicks").GetInt32());

        // Everything derived is rebuilt identically from the stored logs.
        var after = await DbFactsAsync(leagueId);
        Assert.Equal(before, after);

        // Standings over HTTP: Alice leads with `played` wins, Bob has `played` losses.
        var sb = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/scoreboard");
        var standings = sb.GetProperty("standings").EnumerateArray()
            .ToDictionary(s => s.GetProperty("trainer").GetString()!, s => (w: s.GetProperty("wins").GetInt32(), l: s.GetProperty("losses").GetInt32()));
        Assert.Equal((played, 0), standings["Alice"]);
        Assert.Equal((0, played), standings["Bob"]);
    }

    [Fact]
    public async Task Restore_preserves_the_draft_stats()
    {
        var leagueId = await SeedAsync(matchCount: 2, played: 2);
        var admin = await AdminAsync();

        static string Norm(JsonElement e)
        {
            // Draft-stats are keyed by mon/coach, not by the ids that get reassigned on
            // restore, so a normalised text compare is stable across the round-trip.
            using var doc = JsonDocument.Parse(e.GetRawText());
            return JsonSerializer.Serialize(doc.RootElement);
        }

        var beforeStats = Norm(await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/draft-stats"));

        var snap = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/snapshot");
        await RestoreAsync(admin, leagueId, snap);

        var afterStats = Norm(await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/draft-stats"));
        Assert.Equal(beforeStats, afterStats);
    }

    [Fact]
    public async Task Restore_requires_admin()
    {
        var leagueId = await SeedAsync(matchCount: 1, played: 1);
        var admin = await AdminAsync();
        var snap = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/snapshot");

        var coach = await _factory.SignedInAsAsync("coach-nobody");
        var res = await coach.PostAsJsonAsync($"/api/leagues/{leagueId}/snapshot", snap);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
