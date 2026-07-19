using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The Development-only debug-slot sign-in the web and Android clients share in
/// place of Discord. Covers the slot listing, claim/release lifecycle, the
/// last-claim-wins hand-off, and that claiming drops you into the league as a
/// real roster player while the bare-admin sign-in does not.
/// </summary>
public class DebugSlotsTests : DraftScenarioBase
{
    private static JsonElement Slot(JsonElement slots, int index) =>
        slots.EnumerateArray().First(s => Int(s, "index") == index);

    [Fact]
    public async Task The_four_debug_coaches_start_unclaimed()
    {
        var client = Factory.CreateClient();
        var slots = await client.GetFromJsonAsync<JsonElement>("/dev/slots/");

        Assert.Equal(4, slots.EnumerateArray().Count());
        Assert.All(slots.EnumerateArray(), s => Assert.Null(Str(s, "claimedBy")));
        // Slots map to the seeded coach identities.
        Assert.Equal("coach-1", Str(Slot(slots, 1), "discordId"));
    }

    [Fact]
    public async Task Claiming_a_slot_issues_a_session_and_marks_it_taken()
    {
        var client = Factory.CreateClient();

        var res = await client.PostAsJsonAsync("/dev/slots/1/claim", new { client = "web" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(Str(body, "accessToken")));

        var slots = await client.GetFromJsonAsync<JsonElement>("/dev/slots/");
        Assert.Equal("web", Str(Slot(slots, 1), "claimedBy"));
    }

    [Fact]
    public async Task Claiming_a_held_slot_reports_the_previous_holder()
    {
        var client = Factory.CreateClient();

        (await client.PostAsJsonAsync("/dev/slots/2/claim", new { client = "web" })).EnsureSuccessStatusCode();
        var second = await (await client.PostAsJsonAsync("/dev/slots/2/claim", new { client = "phone" }))
            .Content.ReadFromJsonAsync<JsonElement>();

        // Last claim wins, and the new holder learns who it bumped.
        Assert.Equal("web", Str(second, "previousHolder"));

        var slots = await client.GetFromJsonAsync<JsonElement>("/dev/slots/");
        Assert.Equal("phone", Str(Slot(slots, 2), "claimedBy"));
    }

    [Fact]
    public async Task Releasing_a_slot_frees_it()
    {
        var client = Factory.CreateClient();

        (await client.PostAsJsonAsync("/dev/slots/3/claim", new { client = "web" })).EnsureSuccessStatusCode();
        (await client.PostAsync("/dev/slots/3/release", null)).EnsureSuccessStatusCode();

        var slots = await client.GetFromJsonAsync<JsonElement>("/dev/slots/");
        Assert.Null(Str(Slot(slots, 3), "claimedBy"));
    }

    [Fact]
    public async Task Claiming_an_unknown_slot_is_404()
    {
        var client = Factory.CreateClient();
        var res = await client.PostAsJsonAsync("/dev/slots/99/claim", new { client = "web" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task A_claimed_slot_becomes_a_roster_player()
    {
        var client = Factory.CreateClient();
        (await client.PostAsJsonAsync("/dev/slots/1/claim", new { client = "web" })).EnsureSuccessStatusCode();

        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var players = await admin.GetFromJsonAsync<JsonElement>("/api/players/");
        var ids = players.EnumerateArray().Select(p => Str(p, "discordId")).ToList();

        Assert.Contains("coach-1", ids);
    }

    [Fact]
    public async Task The_bare_admin_sign_in_is_an_admin_but_not_a_roster_player()
    {
        var client = Factory.CreateClient();

        var res = await client.PostAsync("/dev/admin", null);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("user").GetProperty("isAdmin").GetBoolean());

        // Signed in as admin, yet the roster never lists them.
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var players = await admin.GetFromJsonAsync<JsonElement>("/api/players/");
        Assert.DoesNotContain("admin", players.EnumerateArray().Select(p => Str(p, "discordId")));
    }
}
