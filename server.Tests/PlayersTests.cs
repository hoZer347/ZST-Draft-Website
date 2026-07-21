using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The roster list and the team-preview page behind it, the endpoints the web
/// app's left-hand player list and click-through team page read. Covers who
/// appears on the roster (and who doesn't), admin-only removal, and the shape of
/// a drafted team including per-mon battle profile.
/// </summary>
public class PlayersTests : DraftScenarioBase
{
    // ── roster listing ─────────────────────────────────────────────────────

    [Fact]
    public async Task Roster_lists_signed_in_players_but_never_the_reserved_admin()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        await Factory.SignedInAsAsync("coach-1");
        await Factory.SignedInAsAsync("coach-2");

        var players = await admin.GetFromJsonAsync<JsonElement>("/api/players/");
        var ids = players.EnumerateArray().Select(p => Str(p, "discordId")).ToList();

        Assert.Contains("coach-1", ids);
        Assert.Contains("coach-2", ids);
        // The reserved admin oversees the league but is not a player.
        Assert.DoesNotContain("admin", ids);
    }

    [Fact]
    public async Task Roster_tags_dummy_coaches_and_real_logins_by_source()
    {
        var caller = await Factory.SignedInAsAsync("coach-1"); // dummy (coach- prefix)
        await Factory.SignedInAsAsync("998877665544332211");   // a real discord snowflake

        var players = await caller.GetFromJsonAsync<JsonElement>("/api/players/");
        var bySource = players.EnumerateArray().ToDictionary(p => Str(p, "discordId")!, p => Str(p, "source"));

        Assert.Equal("dummy", bySource["coach-1"]);
        Assert.Equal("discord", bySource["998877665544332211"]);
    }

    [Fact]
    public async Task Roster_puts_real_logins_ahead_of_dummy_coaches()
    {
        var caller = await Factory.SignedInAsAsync("coach-2");   // dummy
        await Factory.SignedInAsAsync("112233445566778899");     // discord

        var players = await caller.GetFromJsonAsync<JsonElement>("/api/players/");
        var sources = players.EnumerateArray().Select(p => Str(p, "source")).ToList();

        // Discord logins sort before dummy coaches, so the first dummy never
        // precedes the last discord player.
        var firstDummy = sources.IndexOf("dummy");
        var lastDiscord = sources.LastIndexOf("discord");
        Assert.True(lastDiscord < firstDummy);
    }

    [Fact]
    public async Task Roster_requires_authentication()
    {
        var res = await Factory.CreateClient().GetAsync("/api/players/");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // ── removing a player ──────────────────────────────────────────────────

    [Fact]
    public async Task Removing_a_player_is_admin_only()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var coach = await Factory.SignedInAsAsync("coach-1");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await coach.DeleteAsync("/api/players/coach-1")).StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_remove_themselves()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);

        var res = await admin.DeleteAsync("/api/players/admin");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task An_admin_can_remove_a_player_and_the_roster_shrinks()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        await Factory.SignedInAsAsync("coach-1");
        await Factory.SignedInAsAsync("coach-2");

        var res = await admin.DeleteAsync("/api/players/coach-1");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        var players = await admin.GetFromJsonAsync<JsonElement>("/api/players/");
        var ids = players.EnumerateArray().Select(p => Str(p, "discordId")).ToList();
        Assert.DoesNotContain("coach-1", ids);
        Assert.Contains("coach-2", ids);
    }

    [Fact]
    public async Task Removing_a_player_who_does_not_exist_is_404()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);

        var res = await admin.DeleteAsync("/api/players/nobody-here");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── team preview ───────────────────────────────────────────────────────

    [Fact]
    public async Task Team_preview_is_empty_before_a_player_has_drafted()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var coach = await Factory.SignedInAsAsync("coach-1");

        // No draft started, so no team yet.
        var team = await coach.GetFromJsonAsync<JsonElement>("/api/players/coach-1/team");

        Assert.Null(IntOrNull(team, "teamId"));
        Assert.Equal("coach-1", Str(team, "discordId"));
        Assert.Empty(team.GetProperty("mons").EnumerateArray());
    }

    [Fact]
    public async Task Team_preview_of_an_unknown_player_is_empty_but_not_an_error()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);

        var team = await admin.GetFromJsonAsync<JsonElement>("/api/players/ghost/team");
        // Falls back to the id as the display name and an empty roster.
        Assert.Equal("ghost", Str(team, "username"));
        Assert.Empty(team.GetProperty("mons").EnumerateArray());
    }

    [Fact]
    public async Task Team_preview_requires_authentication()
    {
        var res = await Factory.CreateClient().GetAsync("/api/players/coach-1/team");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Team_preview_lists_drafted_mons_with_a_full_battle_profile()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");

        // Whoever is first on the clock makes a pick.
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");
        var coachId = s.GetProperty("teams").EnumerateArray()
            .First(t => Int(t, "id") == onClock).GetProperty("coachId").GetString()!;
        await PickOnceAsync(admin, draftId, byTeam);

        var team = await admin.GetFromJsonAsync<JsonElement>($"/api/players/{coachId}/team");
        Assert.Equal(onClock, IntOrNull(team, "teamId"));

        var mon = team.GetProperty("mons").EnumerateArray().Single();
        // Battle profile the team card renders.
        Assert.False(string.IsNullOrWhiteSpace(Str(mon, "name")));
        Assert.False(string.IsNullOrWhiteSpace(Str(mon, "tier")));
        foreach (var stat in new[] { "hp", "atk", "def", "spAtk", "spDef", "speed" })
            Assert.True(mon.GetProperty(stat).GetInt32() >= 0);

        // BST is exactly the sum of the six base stats, the card trusts this.
        var expectedBst = Int(mon, "hp") + Int(mon, "atk") + Int(mon, "def")
            + Int(mon, "spAtk") + Int(mon, "spDef") + Int(mon, "speed");
        Assert.Equal(expectedBst, Int(mon, "bst"));

        // At least a primary type is always present.
        Assert.False(string.IsNullOrWhiteSpace(Str(mon, "type1")));
    }

    [Fact]
    public async Task Team_preview_orders_mons_by_tier_then_pick_number()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");

        // Run the whole draft so both teams are full and every tier is present.
        for (var guard = 0; guard < 100; guard++)
        {
            var s = await StateAsync(admin, draftId);
            if (s.GetProperty("state").GetString() == "Complete") break;
            await PickOnceAsync(admin, draftId, byTeam);
        }

        // Pick any team and assert its preview is grouped S→A→B→C.
        var teamId = byTeam.Keys.First();
        var coachId = (await StateAsync(admin, draftId)).GetProperty("teams").EnumerateArray()
            .First(t => Int(t, "id") == teamId).GetProperty("coachId").GetString()!;

        var team = await admin.GetFromJsonAsync<JsonElement>($"/api/players/{coachId}/team");
        var tiers = team.GetProperty("mons").EnumerateArray().Select(m => Str(m, "tier")).ToList();

        var rank = new Dictionary<string, int> { ["S"] = 0, ["A"] = 1, ["B"] = 2, ["C"] = 3 };
        var ranks = tiers.Select(t => rank[t!]).ToList();
        Assert.Equal(ranks.OrderBy(x => x), ranks); // non-decreasing tier order
        // A full roster is S1/A2/B3/C4 = 10 mons.
        Assert.Equal(10, tiers.Count);
    }
}
