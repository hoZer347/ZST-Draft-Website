using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The season schedule laid down automatically when the draft starts, and the
/// replay-report endpoint's guards. (Replay scoring itself needs a live replay
/// and is covered elsewhere; here we cover the auth/validation around it.)
/// </summary>
public class ScheduleTests : DraftScenarioBase
{
    private static int LeagueId(JsonElement state) => Int(state, "leagueId");

    /// <summary>The schedule's matches as (week, home, away), for the tests below.</summary>
    private static async Task<List<(int Week, int Home, int Away)>> MatchesAsync(HttpClient admin, int leagueId)
    {
        var sched = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/schedule");
        return sched.GetProperty("matches").EnumerateArray()
            .Select(m => (Int(m, "week"), Int(m, "homeTeamId"), Int(m, "awayTeamId")))
            .ToList();
    }

    private static (int A, int B) Pair(int x, int y) => x < y ? (x, y) : (y, x);

    [Fact]
    public async Task Two_teams_play_a_single_week_capped_below_the_default()
    {
        // Default SeasonWeeks is 8, but the season is capped at one full round-robin:
        // two players is (2 - 1) = 1 week, one match, regardless of the setting.
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var s = await StateAsync(admin, draftId);
        var leagueId = LeagueId(s);

        Assert.Equal(8, Int(s, "weeks")); // the SETTING stays at the default 8
        var matches = await MatchesAsync(admin, leagueId);

        Assert.Single(matches);            // but only one week is scheduled
        Assert.Equal(1, matches[0].Week);
        var teamIds = byTeam.Keys.OrderBy(x => x).ToArray();
        Assert.Equal(teamIds, new[] { matches[0].Home, matches[0].Away }.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Even_roster_is_one_full_round_robin_no_repeats_no_byes()
    {
        // 4 players → 3 weeks (4 - 1), every pair exactly once, everyone plays every week.
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2", "coach-3", "coach-4");
        var leagueId = LeagueId(await StateAsync(admin, draftId));
        var teams = byTeam.Keys.ToList();
        var matches = await MatchesAsync(admin, leagueId);

        var weeks = matches.Select(m => m.Week).Distinct().Count();
        Assert.Equal(3, weeks);
        Assert.Equal(6, matches.Count); // C(4,2)

        // No pair repeats.
        var pairs = matches.Select(m => Pair(m.Home, m.Away)).ToList();
        Assert.Equal(pairs.Count, pairs.Distinct().Count());

        // No byes: all 4 teams appear every week.
        foreach (var g in matches.GroupBy(m => m.Week))
        {
            var playing = g.SelectMany(m => new[] { m.Home, m.Away }).Distinct().ToHashSet();
            Assert.Equal(4, playing.Count);
        }
    }

    [Fact]
    public async Task Odd_roster_gives_every_player_exactly_one_bye()
    {
        // 5 players → 5 weeks. Every pair once, and each week a different player byes
        // so that over the season every player byes exactly once.
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2", "coach-3", "coach-4", "coach-5");
        var leagueId = LeagueId(await StateAsync(admin, draftId));
        var teams = byTeam.Keys.ToList();
        var matches = await MatchesAsync(admin, leagueId);

        Assert.Equal(5, matches.Select(m => m.Week).Distinct().Count());
        Assert.Equal(10, matches.Count); // C(5,2)

        var pairs = matches.Select(m => Pair(m.Home, m.Away)).ToList();
        Assert.Equal(pairs.Count, pairs.Distinct().Count()); // no repeats

        // Each week exactly one team byes; each team byes exactly once overall.
        var byeCount = teams.ToDictionary(t => t, _ => 0);
        foreach (var g in matches.GroupBy(m => m.Week))
        {
            var playing = g.SelectMany(m => new[] { m.Home, m.Away }).Distinct().ToHashSet();
            Assert.Equal(4, playing.Count); // one of the five sits out
            foreach (var t in teams.Where(t => !playing.Contains(t))) byeCount[t]++;
        }
        Assert.All(teams, t => Assert.Equal(1, byeCount[t]));
    }

    [Fact]
    public async Task Configured_weeks_below_the_cap_shortens_the_season()
    {
        // 4 players (cap 3), but the admin sets 2 weeks: the shorter setting wins.
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);
        foreach (var id in new[] { "coach-1", "coach-2", "coach-3", "coach-4" })
            (await (await Factory.SignedInAsAsync(id)).PostAsync($"/api/drafts/{draftId}/ready", null)).EnsureSuccessStatusCode();

        (await admin.PostAsJsonAsync($"/api/admin/drafts/{draftId}/start",
            new { weeks = 2, pickTimerSeconds = 3600 })).EnsureSuccessStatusCode();

        var leagueId = LeagueId(await StateAsync(admin, draftId));
        var matches = await MatchesAsync(admin, leagueId);
        Assert.Equal(2, matches.Select(m => m.Week).Distinct().Count());
    }

    [Fact]
    public async Task Aborting_the_draft_clears_the_schedule_but_keeps_the_teams()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var leagueId = LeagueId(await StateAsync(admin, draftId));

        var before = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/schedule");
        Assert.NotEmpty(before.GetProperty("matches").EnumerateArray());

        (await admin.PostAsync($"/api/admin/drafts/{draftId}/abort", null)).EnsureSuccessStatusCode();

        var after = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/schedule");
        Assert.Empty(after.GetProperty("matches").EnumerateArray()); // matches gone
        Assert.NotEmpty(after.GetProperty("teams").EnumerateArray()); // teams remain (standings reset)
    }

    [Fact]
    public async Task Schedule_of_an_unknown_league_is_404()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var res = await admin.GetAsync("/api/leagues/999999/schedule");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Schedule_requires_authentication()
    {
        var res = await Factory.CreateClient().GetAsync("/api/leagues/1/schedule");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Reporting_a_replay_with_a_blank_url_is_rejected()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var leagueId = LeagueId(await StateAsync(admin, draftId));
        var matchId = Int((await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/schedule"))
            .GetProperty("matches").EnumerateArray().First(), "id");

        var res = await admin.PostAsJsonAsync($"/api/matches/{matchId}/replay", new { replayUrl = "   " });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Reporting_a_replay_for_an_unknown_match_is_404()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var res = await admin.PostAsJsonAsync("/api/matches/999999/replay",
            new { replayUrl = "https://replay.pokemonshowdown.com/gen9customgame-1" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task A_non_participant_cannot_report_a_match()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var leagueId = LeagueId(await StateAsync(admin, draftId));
        var matchId = Int((await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/schedule"))
            .GetProperty("matches").EnumerateArray().First(), "id");

        // A signed-in user with no team in this match.
        var outsider = await Factory.SignedInAsAsync("coach-9");
        var res = await outsider.PostAsJsonAsync($"/api/matches/{matchId}/replay",
            new { replayUrl = "https://replay.pokemonshowdown.com/gen9customgame-1" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
