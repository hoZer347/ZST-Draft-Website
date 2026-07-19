using System.Net;
using System.Text.Json;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The pre-start ready-up flow: signing in no longer enrols you — you must ready
/// up, and the Start roster is built from who did. Covers the guards, idempotency,
/// leaving, and the state fields the header toggle + roster markers read.
/// </summary>
public class ReadyTests : DraftScenarioBase
{
    private static string[] ReadyIds(JsonElement state) =>
        state.GetProperty("ready").EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public async Task Signing_in_alone_does_not_ready_you_up()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var draftId = await DraftIdAsync(client);

        var s = await StateAsync(client, draftId);
        Assert.Empty(ReadyIds(s));
        Assert.False(s.GetProperty("myReady").GetBoolean());
        Assert.True(s.GetProperty("canReady").GetBoolean());
    }

    [Fact]
    public async Task Readying_up_adds_you_to_the_ready_set()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var draftId = await DraftIdAsync(client);

        (await client.PostAsync($"/api/drafts/{draftId}/ready", null)).EnsureSuccessStatusCode();

        var s = await StateAsync(client, draftId);
        Assert.Contains("coach-1", ReadyIds(s));
        Assert.True(s.GetProperty("myReady").GetBoolean());
    }

    [Fact]
    public async Task Readying_up_twice_is_idempotent()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var draftId = await DraftIdAsync(client);

        (await client.PostAsync($"/api/drafts/{draftId}/ready", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/drafts/{draftId}/ready", null)).EnsureSuccessStatusCode();

        var s = await StateAsync(client, draftId);
        Assert.Single(ReadyIds(s), "coach-1");
    }

    [Fact]
    public async Task Leaving_removes_you_from_the_ready_set()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var draftId = await DraftIdAsync(client);

        (await client.PostAsync($"/api/drafts/{draftId}/ready", null)).EnsureSuccessStatusCode();
        (await client.PostAsync($"/api/drafts/{draftId}/leave", null)).EnsureSuccessStatusCode();

        var s = await StateAsync(client, draftId);
        Assert.DoesNotContain("coach-1", ReadyIds(s));
        Assert.False(s.GetProperty("myReady").GetBoolean());
    }

    [Fact]
    public async Task The_reserved_admin_cannot_ready_up()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);

        var res = await admin.PostAsync($"/api/drafts/{draftId}/ready", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // …and canReady is false for the admin.
        var s = await StateAsync(admin, draftId);
        Assert.False(s.GetProperty("canReady").GetBoolean());
    }

    [Fact]
    public async Task Readying_after_the_draft_has_started_is_refused()
    {
        // StartWithAsync readies + starts p1/p2.
        var (_, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");

        // A latecomer signs in and tries to ready a running draft.
        var late = await Factory.SignedInAsAsync("coach-3");
        var res = await late.PostAsync($"/api/drafts/{draftId}/ready", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Ready_requires_authentication()
    {
        var draftId = await DraftIdAsync(await Factory.SignedInAsAsync("coach-1"));
        var res = await Factory.CreateClient().PostAsync($"/api/drafts/{draftId}/ready", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Starting_with_nobody_readied_up_is_refused()
    {
        // Players sign in but none ready up.
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        await Factory.SignedInAsAsync("coach-1");
        var draftId = await DraftIdAsync(admin);

        var res = await admin.PostAsync($"/api/admin/drafts/{draftId}/start", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Only_readied_players_get_a_team_at_start()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);
        var readied = await Factory.SignedInAsAsync("coach-1");
        var benched = await Factory.SignedInAsAsync("coach-2"); // signs in, never readies

        (await readied.PostAsync($"/api/drafts/{draftId}/ready", null)).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/admin/drafts/{draftId}/start", null)).EnsureSuccessStatusCode();

        var s = await StateAsync(admin, draftId);
        var coachIds = s.GetProperty("teams").EnumerateArray()
            .Select(t => t.GetProperty("coachId").GetString()).ToList();
        Assert.Contains("coach-1", coachIds);
        Assert.DoesNotContain("coach-2", coachIds);
    }
}
