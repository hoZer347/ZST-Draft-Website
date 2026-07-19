using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Self-service profile edits (team name, icon, Showdown handle). The security
/// bit: the icon is only accepted as an http(s) URL, so a smuggled javascript:
/// URL never sticks. Also covers trimming/length caps and auth.
/// </summary>
public class ProfileTests : DraftScenarioBase
{
    private static async Task<JsonElement> UpdateAsync(HttpClient c, object body) =>
        await (await c.PostAsJsonAsync("/api/players/me/profile", body)).Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task A_javascript_url_icon_is_rejected()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var res = await UpdateAsync(client, new { teamName = "Alpha", teamIcon = "javascript:alert(1)", showdownName = "alpha" });
        // Icon nulled out; the rest kept.
        Assert.Equal(JsonValueKind.Null, res.GetProperty("teamIcon").ValueKind);
        Assert.Equal("Alpha", res.GetProperty("teamName").GetString());
    }

    [Fact]
    public async Task An_https_icon_is_kept()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var res = await UpdateAsync(client, new { teamName = "Alpha", teamIcon = "https://example.com/logo.png", showdownName = (string?)null });
        Assert.Equal("https://example.com/logo.png", res.GetProperty("teamIcon").GetString());
    }

    [Fact]
    public async Task A_bare_string_icon_is_rejected()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var res = await UpdateAsync(client, new { teamName = "A", teamIcon = "not-a-url", showdownName = "a" });
        Assert.Equal(JsonValueKind.Null, res.GetProperty("teamIcon").ValueKind);
    }

    [Fact]
    public async Task An_over_long_team_name_is_capped()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var res = await UpdateAsync(client, new { teamName = new string('x', 100), teamIcon = (string?)null, showdownName = (string?)null });
        Assert.Equal(40, res.GetProperty("teamName").GetString()!.Length);
    }

    [Fact]
    public async Task A_blank_name_becomes_null()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var res = await UpdateAsync(client, new { teamName = "   ", teamIcon = (string?)null, showdownName = (string?)null });
        Assert.Equal(JsonValueKind.Null, res.GetProperty("teamName").ValueKind);
    }

    [Fact]
    public async Task Editing_a_profile_requires_authentication()
    {
        var res = await Factory.CreateClient().PostAsJsonAsync("/api/players/me/profile",
            new { teamName = "x", teamIcon = (string?)null, showdownName = (string?)null });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
