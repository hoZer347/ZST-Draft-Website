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

    [Fact]
    public async Task Starting_the_draft_lays_down_a_round_robin_schedule()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var s = await StateAsync(admin, draftId);
        var leagueId = LeagueId(s);
        var weeks = Int(s, "weeks");

        var sched = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/schedule");
        var matches = sched.GetProperty("matches").EnumerateArray().ToList();

        // Two teams → exactly one match per week, spanning every configured week.
        Assert.Equal(weeks, matches.Count);
        var byWeek = matches.Select(m => Int(m, "week")).OrderBy(w => w).ToList();
        Assert.Equal(Enumerable.Range(1, weeks), byWeek);

        // Every match is the two drafted teams, initially unplayed.
        var teamIds = byTeam.Keys.OrderBy(x => x).ToArray();
        foreach (var m in matches)
        {
            Assert.False(m.GetProperty("played").GetBoolean());
            Assert.Equal("Pending", Str(m, "result"));
            var pair = new[] { Int(m, "homeTeamId"), Int(m, "awayTeamId") }.OrderBy(x => x).ToArray();
            Assert.Equal(teamIds, pair);
        }

        // Nothing played yet, so the current week is week 1.
        Assert.Equal(1, Int(sched, "currentWeek"));
    }

    [Fact]
    public async Task Home_and_away_swap_across_cycles_for_two_teams()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var leagueId = LeagueId(await StateAsync(admin, draftId));

        var sched = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/schedule");
        var matches = sched.GetProperty("matches").EnumerateArray()
            .OrderBy(m => Int(m, "week")).ToList();

        // With n=2 the round-robin cycle is 1 week, so home/away flips every week:
        // consecutive weeks must have opposite home teams.
        for (var i = 1; i < matches.Count; i++)
            Assert.NotEqual(Int(matches[i - 1], "homeTeamId"), Int(matches[i], "homeTeamId"));
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
