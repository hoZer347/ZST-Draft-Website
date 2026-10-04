using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Guards the claims AUTH_SETUP.md makes about the auth surface. These are the
/// properties that fail silently and dangerously, an endpoint that stops
/// checking the caller looks fine until someone drafts for another team.
/// </summary>
public class AuthTests(DraftLeagueFactory factory) : IClassFixture<DraftLeagueFactory>
{
    // Every route the clients actually call. A new one added outside the
    // authorized group would slip through unnoticed without this.
    [Theory]
    [InlineData("GET", "/api/notifications")]
    [InlineData("GET", "/api/drafts/1")]
    [InlineData("GET", "/api/auth/me")]
    [InlineData("POST", "/api/devices")]
    [InlineData("POST", "/api/preferences")]
    [InlineData("POST", "/api/drafts/1/pick")]
    [InlineData("POST", "/api/admin/drafts/1/start")]
    public async Task Unauthenticated_calls_are_refused(string method, string path)
    {
        var client = factory.CreateClient();
        var res = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Forged_bearer_is_refused()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not.a.real.token");

        var res = await client.GetAsync("/api/notifications");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Dev_token_authenticates_and_me_identifies_the_caller()
    {
        var client = await factory.SignedInAsAsync("coach-1");

        var res = await client.GetAsync("/api/auth/me");
        res.EnsureSuccessStatusCode();

        var me = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("coach-1", me.GetProperty("discordId").GetString());
    }

    [Fact]
    public async Task Signed_in_user_can_read_their_notifications()
    {
        var client = await factory.SignedInAsAsync("coach-1");
        var res = await client.GetAsync("/api/notifications");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Admin_routes_are_closed_to_normal_users()
    {
        var client = await factory.SignedInAsAsync("coach-1");
        var res = await client.PostAsync("/api/admin/drafts/1/start", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Admin_routes_open_for_an_admin()
    {
        var client = await factory.SignedInAsAsync("boss-1", admin: true);
        var res = await client.PostAsync("/api/admin/drafts/1/start", null);

        // The draft may legitimately refuse to start; what matters is that
        // authorization is not what stopped it.
        Assert.NotEqual(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_and_the_old_token_dies_once_the_grace_window_passes()
    {
        // Jwt:RefreshGraceSeconds = 0 is strict single use, which is what the
        // stolen-token case looks like: the replay is nowhere near the rotation.
        using var strict = factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:RefreshGraceSeconds"] = "0" })));
        var client = strict.CreateClient();

        var issued = await (await client.PostAsync("/dev/token/coach-2", null))
            .Content.ReadFromJsonAsync<DraftLeagueFactory.DevToken>();

        var first = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = issued!.RefreshToken });
        first.EnsureSuccessStatusCode();

        var replay = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = issued.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    // Two tabs share one stored refresh token and can present it at the same
    // instant. Failing the second one used to clear the session and bounce the
    // user through Discord again, so inside the grace window it still works.
    [Fact]
    public async Task Racing_refreshes_with_the_same_token_both_succeed()
    {
        var client = factory.CreateClient();

        var issued = await (await client.PostAsync("/dev/token/coach-3", null))
            .Content.ReadFromJsonAsync<DraftLeagueFactory.DevToken>();

        var first = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = issued!.RefreshToken });
        first.EnsureSuccessStatusCode();

        var second = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = issued.RefreshToken });
        second.EnsureSuccessStatusCode();

        // Same user on both, and the pairs are distinct sessions.
        var a = await first.Content.ReadFromJsonAsync<JsonElement>();
        var b = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            a.GetProperty("user").GetProperty("discordId").GetString(),
            b.GetProperty("user").GetProperty("discordId").GetString());
        Assert.NotEqual(
            a.GetProperty("refreshToken").GetString(),
            b.GetProperty("refreshToken").GetString());
    }

    [Fact]
    public async Task Unregistered_redirect_uri_is_refused_before_discord_is_contacted()
    {
        var client = factory.CreateClient();

        var res = await client.PostAsJsonAsync("/api/auth/discord", new
        {
            code = "irrelevant",
            codeVerifier = "irrelevant",
            redirectUri = "https://attacker.example/steal",
            deviceLabel = "test",
        });

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Auth_config_never_leaks_the_secret()
    {
        var client = factory.CreateClient();
        var body = await client.GetStringAsync("/api/auth/config");

        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
    }
}
