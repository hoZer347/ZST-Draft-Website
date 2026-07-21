using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The pre-start ready-up flow: signing in no longer enrols you, you must ready
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
    public async Task The_reserved_admin_can_ready_up_to_opt_in_as_a_coach()
    {
        // The admin oversees the league without appearing as a player UNLESS they
        // opt in by readying up (see PlayersApi), then they're a coach like anyone.
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);

        var res = await admin.PostAsync($"/api/drafts/{draftId}/ready", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var s = await StateAsync(admin, draftId);
        Assert.True(s.GetProperty("canReady").GetBoolean());
        var ready = s.GetProperty("ready").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("admin", ready);
    }

    [Fact]
    public async Task Readying_all_dummies_readies_synthetic_accounts_but_not_real_users()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);

        // Two dummies (non-numeric ids) and one real Discord user (all-digit id)
        // sign in, but none ready up.
        await Factory.SignedInAsAsync("coach-1");
        await Factory.SignedInAsAsync("coach-2");
        await Factory.SignedInAsAsync("123456789012345678");

        (await admin.PostAsync("/api/admin/dummies/ready-all", null)).EnsureSuccessStatusCode();

        var ready = ReadyIds(await StateAsync(admin, draftId));
        Assert.Contains("coach-1", ready);
        Assert.Contains("coach-2", ready);
        Assert.DoesNotContain("123456789012345678", ready); // real Discord user untouched
        Assert.DoesNotContain("admin", ready);              // oversee-admin untouched
    }

    [Fact]
    public async Task Readying_all_dummies_is_admin_only()
    {
        var coach = await Factory.SignedInAsAsync("coach-1");
        var draftId = await DraftIdAsync(coach);

        var res = await coach.PostAsync("/api/admin/dummies/ready-all", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
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
    public async Task Admin_can_add_a_dummy_that_readies_into_the_draft()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);

        var res = await admin.PostAsync("/api/admin/dummies", null);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var id = Str(body, "discordId")!;
        Assert.StartsWith("dummy-", id);
        Assert.True(body.GetProperty("readied").GetBoolean());

        // It's a readied participant and appears in the roster.
        Assert.Contains(id, ReadyIds(await StateAsync(admin, draftId)));
        var players = await admin.GetFromJsonAsync<JsonElement>("/api/players");
        Assert.Contains(players.EnumerateArray(), p => Str(p, "discordId") == id);
    }

    [Fact]
    public async Task Adding_two_dummies_makes_two_distinct_accounts()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        await DraftIdAsync(admin);

        var a = Str(await (await admin.PostAsync("/api/admin/dummies", null)).Content.ReadFromJsonAsync<JsonElement>(), "discordId");
        var b = Str(await (await admin.PostAsync("/api/admin/dummies", null)).Content.ReadFromJsonAsync<JsonElement>(), "discordId");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task A_non_admin_cannot_add_a_dummy()
    {
        var user = await Factory.SignedInAsAsync("coach-1");
        await DraftIdAsync(user);
        var res = await user.PostAsync("/api/admin/dummies", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_can_ready_and_unready_any_account()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);
        await Factory.SignedInAsAsync("coach-9"); // the account exists

        // Ready them on their behalf, then un-ready.
        (await admin.PostAsync($"/api/admin/drafts/{draftId}/participants/coach-9", null)).EnsureSuccessStatusCode();
        Assert.Contains("coach-9", ReadyIds(await StateAsync(admin, draftId)));

        (await admin.DeleteAsync($"/api/admin/drafts/{draftId}/participants/coach-9")).EnsureSuccessStatusCode();
        Assert.DoesNotContain("coach-9", ReadyIds(await StateAsync(admin, draftId)));
    }

    [Fact]
    public async Task A_non_admin_cannot_ready_another_account()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);
        var coach = await Factory.SignedInAsAsync("coach-1");
        await Factory.SignedInAsAsync("coach-9");

        var res = await coach.PostAsync($"/api/admin/drafts/{draftId}/participants/coach-9", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
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
