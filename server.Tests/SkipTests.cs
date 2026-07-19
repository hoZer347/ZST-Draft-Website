using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The skip (defer) mechanic: a coach on the clock spends one of their skip
/// tokens to pass their turn to a later cycle, and the slot still comes back to
/// them. Covers ownership/turn enforcement, token accounting and exhaustion, and
/// that skipping never costs a team the ability to fill its roster.
/// </summary>
public class SkipTests : DraftScenarioBase
{
    private static int SkipsOf(JsonElement state, int teamId) =>
        state.GetProperty("teams").EnumerateArray()
            .First(t => Int(t, "id") == teamId).GetProperty("skipsRemaining").GetInt32();

    [Fact]
    public async Task Skipping_spends_one_token_and_moves_the_clock_on()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");

        Assert.Equal(DraftEngine.MaxSkipsPerTeam, SkipsOf(s, onClock));

        (await byTeam[onClock].PostAsJsonAsync($"/api/drafts/{draftId}/skip", new { teamId = onClock }))
            .EnsureSuccessStatusCode();

        var after = await StateAsync(admin, draftId);
        Assert.Equal(DraftEngine.MaxSkipsPerTeam - 1, SkipsOf(after, onClock));
        // The clock advanced to the other team.
        Assert.NotEqual(onClock, Int(after, "onClockTeamId"));
    }

    [Fact]
    public async Task Skipping_is_refused_for_a_team_you_do_not_own()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");
        var offClock = byTeam.Keys.First(t => t != onClock);

        var res = await byTeam[onClock].PostAsJsonAsync(
            $"/api/drafts/{draftId}/skip", new { teamId = offClock });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Skipping_out_of_turn_is_rejected()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");
        var waiting = byTeam.First(kv => kv.Key != onClock);

        // Owns the team (not 403) but it isn't their turn.
        var res = await waiting.Value.PostAsJsonAsync(
            $"/api/drafts/{draftId}/skip", new { teamId = waiting.Key });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Skipping_requires_authentication()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var onClock = Int(await StateAsync(admin, draftId), "onClockTeamId");

        var res = await Factory.CreateClient().PostAsJsonAsync(
            $"/api/drafts/{draftId}/skip", new { teamId = onClock });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task A_team_with_no_skips_left_is_refused()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");

        // Drain the on-clock team's skip tokens directly, then try to skip.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var team = await db.Teams.FirstAsync(t => t.Id == onClock);
            team.SkipsRemaining = 0;
            await db.SaveChangesAsync();
        }

        var res = await byTeam[onClock].PostAsJsonAsync(
            $"/api/drafts/{draftId}/skip", new { teamId = onClock });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Skipping_clears_any_options_opened_this_turn()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var onClock = Int(await StateAsync(admin, draftId), "onClockTeamId");

        // Open a tier (options now live), then skip instead of picking.
        (await byTeam[onClock].PostAsJsonAsync($"/api/drafts/{draftId}/offer", new { teamId = onClock, tier = C }))
            .EnsureSuccessStatusCode();
        Assert.NotEmpty(await OfferedIdsAsync(admin, draftId));

        (await byTeam[onClock].PostAsJsonAsync($"/api/drafts/{draftId}/skip", new { teamId = onClock }))
            .EnsureSuccessStatusCode();

        // The next team on the clock sees no stale options.
        Assert.Empty(await OfferedIdsAsync(admin, draftId));
    }

    [Fact]
    public async Task A_skipped_slot_returns_so_the_team_can_still_fill_its_roster()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");

        // The very first team on the clock skips once, then everyone drafts to
        // completion. The skip must not cost that team a slot.
        var firstOnClock = Int(await StateAsync(admin, draftId), "onClockTeamId");
        (await byTeam[firstOnClock].PostAsJsonAsync($"/api/drafts/{draftId}/skip", new { teamId = firstOnClock }))
            .EnsureSuccessStatusCode();

        for (var guard = 0; guard < 100; guard++)
        {
            var s = await StateAsync(admin, draftId);
            if (s.GetProperty("state").GetString() == "Complete") break;
            await PickOnceAsync(admin, draftId, byTeam);
        }

        var final = await StateAsync(admin, draftId);
        Assert.Equal("Complete", final.GetProperty("state").GetString());

        // The team that skipped still ends with a full, correct roster.
        var picks = final.GetProperty("picks").EnumerateArray()
            .Where(p => Int(p, "teamId") == firstOnClock).ToList();
        Assert.Equal(1, picks.Count(p => p.GetProperty("tier").GetString() == "S"));
        Assert.Equal(2, picks.Count(p => p.GetProperty("tier").GetString() == "A"));
        Assert.Equal(3, picks.Count(p => p.GetProperty("tier").GetString() == "B"));
        Assert.Equal(4, picks.Count(p => p.GetProperty("tier").GetString() == "C"));
    }
}
