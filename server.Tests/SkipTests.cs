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
    public async Task A_missed_pick_window_auto_skips_while_the_team_still_has_skips()
    {
        var (admin, draftId, _) = await StartWithAsync("coach-1", "coach-2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");
        var before = SkipsOf(s, onClock);

        // The clock auto-picks for a coach who let their window lapse. With skips
        // in hand it should DEFER (spend a skip) rather than force a Pokemon.
        using (var scope = Factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<DraftEngine>();
            Assert.True((await engine.AutoPickAsync(draftId)).Ok);
        }

        var after = await StateAsync(admin, draftId);
        Assert.Empty(after.GetProperty("picks").EnumerateArray());   // no Pokemon forced
        Assert.Equal(before - 1, SkipsOf(after, onClock));           // a skip token was spent
        Assert.NotEqual(onClock, Int(after, "onClockTeamId"));       // and the clock advanced
    }

    [Fact]
    public async Task A_missed_pick_window_auto_picks_a_tier_once_skips_are_gone()
    {
        var (admin, draftId, _) = await StartWithAsync("coach-1", "coach-2");
        var onClock = Int(await StateAsync(admin, draftId), "onClockTeamId");

        // Drain the on-clock team's skips, then let their window lapse.
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var team = await db.Teams.FirstAsync(t => t.Id == onClock);
            team.SkipsRemaining = 0;
            await db.SaveChangesAsync();
        }

        using (var scope = Factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<DraftEngine>();
            Assert.True((await engine.AutoPickAsync(draftId)).Ok);
        }

        // With no skips left it falls back to auto-picking a tier (C-S).
        var after = await StateAsync(admin, draftId);
        var pick = after.GetProperty("picks").EnumerateArray().Single();
        Assert.Equal(onClock, Int(pick, "teamId"));
        Assert.True(pick.GetProperty("wasAutoPick").GetBoolean());
    }

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
    public async Task A_voluntary_skip_records_the_options_it_passed_on()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var onClock = Int(await StateAsync(admin, draftId), "onClockTeamId");

        // Open a tier, note the options, then skip instead of picking.
        (await byTeam[onClock].PostAsJsonAsync($"/api/drafts/{draftId}/offer", new { teamId = onClock, tier = C }))
            .EnsureSuccessStatusCode();
        var offered = await OfferedIdsAsync(admin, draftId);
        Assert.NotEmpty(offered);

        (await byTeam[onClock].PostAsJsonAsync($"/api/drafts/{draftId}/skip", new { teamId = onClock }))
            .EnsureSuccessStatusCode();

        // The skip carries the passed run: every option that was offered, same tier.
        var skip = (await StateAsync(admin, draftId)).GetProperty("skips").EnumerateArray().Single();
        Assert.False(skip.GetProperty("wasAuto").GetBoolean());
        var others = JsonSerializer.Deserialize<List<JsonElement>>(skip.GetProperty("otherOptions").GetString()!)!;
        Assert.Equal(offered.Count, others.Count);
        Assert.All(others, o => Assert.Equal("C", o.GetProperty("tier").GetString()));
    }

    [Fact]
    public async Task An_auto_skip_with_no_tier_opened_shows_the_lowest_tier_options()
    {
        var (admin, draftId, _) = await StartWithAsync("coach-1", "coach-2");

        // The clock lapses without a tier ever opened → auto-skip. Even though the
        // coach never picked a tier, the skip still shows a passed run sampled from
        // the lowest tier they owe (C on a fresh roster), so a timed-out skip is
        // never a blank row.
        using (var scope = Factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<DraftEngine>();
            Assert.True((await engine.AutoPickAsync(draftId)).Ok);
        }

        var skip = (await StateAsync(admin, draftId)).GetProperty("skips").EnumerateArray().Single();
        Assert.True(skip.GetProperty("wasAuto").GetBoolean());
        Assert.Equal(JsonValueKind.String, skip.GetProperty("otherOptions").ValueKind);
        var others = JsonSerializer.Deserialize<List<JsonElement>>(skip.GetProperty("otherOptions").GetString()!)!;
        Assert.NotEmpty(others);
        Assert.All(others, o => Assert.Equal("C", o.GetProperty("tier").GetString()));
    }

    [Fact]
    public async Task An_auto_skip_after_opening_a_tier_shows_those_options()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var onClock = Int(await StateAsync(admin, draftId), "onClockTeamId");

        // Open a HIGHER tier (B) than the auto-skip fallback would pick, then let
        // the window lapse. The passed run must be the B options they actually
        // opened, not the C fallback.
        (await byTeam[onClock].PostAsJsonAsync($"/api/drafts/{draftId}/offer", new { teamId = onClock, tier = B }))
            .EnsureSuccessStatusCode();
        var offered = await OfferedIdsAsync(admin, draftId);
        Assert.NotEmpty(offered);

        using (var scope = Factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<DraftEngine>();
            Assert.True((await engine.AutoPickAsync(draftId)).Ok);
        }

        var skip = (await StateAsync(admin, draftId)).GetProperty("skips").EnumerateArray().Single();
        Assert.True(skip.GetProperty("wasAuto").GetBoolean());
        var others = JsonSerializer.Deserialize<List<JsonElement>>(skip.GetProperty("otherOptions").GetString()!)!;
        Assert.Equal(offered.Count, others.Count);
        Assert.All(others, o => Assert.Equal("B", o.GetProperty("tier").GetString()));
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
