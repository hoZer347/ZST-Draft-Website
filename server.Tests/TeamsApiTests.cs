using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The admin "Build demo teams" endpoint (<c>GET /api/teams/demo</c>): one example
/// team per player who has READIED UP for the draft, for the admin to seed onto
/// their own device. Keyed off readied participants (not drafted Teams) so it works
/// before the draft, when nobody has picks yet.
/// </summary>
public class TeamsApiTests : DraftScenarioBase
{
    private static async Task ReadyAsync(HttpClient client, int draftId) =>
        (await client.PostAsync($"/api/drafts/{draftId}/ready", null)).EnsureSuccessStatusCode();

    [Fact]
    public async Task Demo_teams_are_admin_only()
    {
        var coach = await Factory.SignedInAsAsync("coach-1");
        var draftId = await DraftIdAsync(coach);
        await ReadyAsync(coach, draftId);

        // A non-admin coach may not build everyone's demo teams.
        var res = await coach.GetAsync("/api/teams/demo");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Demo_teams_returns_one_entry_per_readied_participant()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);

        var c1 = await Factory.SignedInAsAsync("coach-1");
        var c2 = await Factory.SignedInAsAsync("coach-2");
        await Factory.SignedInAsAsync("coach-3"); // signs in but never readies, excluded
        await ReadyAsync(c1, draftId);
        await ReadyAsync(c2, draftId);

        var body = await admin.GetFromJsonAsync<JsonElement>("/api/teams/demo");
        var teams = body.GetProperty("teams").EnumerateArray().ToList();

        // One per readied player, the un-readied coach-3 does not appear.
        Assert.Equal(2, teams.Count);
        Assert.All(teams, t =>
        {
            Assert.Equal(JsonValueKind.String, t.GetProperty("player").ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("player").GetString()));
            Assert.True(t.TryGetProperty("team", out var team) && team.ValueKind == JsonValueKind.String);
        });
    }

    [Fact]
    public async Task Demo_teams_is_empty_when_nobody_has_readied()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        await DraftIdAsync(admin);

        var body = await admin.GetFromJsonAsync<JsonElement>("/api/teams/demo");
        Assert.Empty(body.GetProperty("teams").EnumerateArray());
    }
}
