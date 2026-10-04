using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The signed-out entry point. A visitor pass is an authenticated token that
/// names nobody: it exists so the read endpoints (all behind
/// RequireAuthorization) will answer someone who has not signed in.
///
/// The property worth guarding is the one that fails silently: an identity-less
/// token must never be able to write. Every write path resolves its caller
/// through DiscordId(), so a visitor is refused everywhere without any rule of
/// its own, and that is exactly the kind of thing a later refactor undoes.
/// </summary>
public class VisitorTests(DraftLeagueFactory factory) : IClassFixture<DraftLeagueFactory>
{
    private async Task<HttpClient> VisitingAsync()
    {
        var client = factory.CreateClient();
        var res = await client.PostAsync("/api/auth/visitor", null);
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body.GetProperty("accessToken").GetString());
        return client;
    }

    [Fact]
    public async Task Visitor_pass_needs_no_account_and_names_nobody()
    {
        var res = await factory.CreateClient().PostAsync("/api/auth/visitor", null);
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("visitor").GetBoolean());
        var user = body.GetProperty("user");
        Assert.Equal(JsonValueKind.Null, user.GetProperty("discordId").ValueKind);
        Assert.False(user.GetProperty("isAdmin").GetBoolean());
        // No refresh token: there is no session to keep alive, the page mints
        // another pass instead.
        Assert.False(body.TryGetProperty("refreshToken", out _));
    }

    [Theory]
    [InlineData("/api/drafts")]
    [InlineData("/api/drafts/1")]
    [InlineData("/api/pool")]
    [InlineData("/api/players")]
    [InlineData("/api/stats")]
    [InlineData("/api/leagues/1/schedule")]
    [InlineData("/api/leagues/1/scoreboard")]
    [InlineData("/api/leagues/1/draft-stats")]
    public async Task Visitor_can_read_the_league(string path)
    {
        var client = await VisitingAsync();
        var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/drafts/1/ready")]
    [InlineData("POST", "/api/drafts/1/leave")]
    [InlineData("POST", "/api/players/me/profile")]
    [InlineData("POST", "/api/preferences")]
    [InlineData("POST", "/api/admin/drafts/1/start")]
    [InlineData("POST", "/api/admin/drafts/1/abort")]
    [InlineData("POST", "/api/admin/invite")]
    [InlineData("POST", "/api/admin/dummies")]
    public async Task Visitor_cannot_write_anything(string method, string path)
    {
        var client = await VisitingAsync();
        var req = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = JsonContent.Create(new { }),
        };
        var res = await client.SendAsync(req);
        Assert.True(
            res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"{method} {path} answered {(int)res.StatusCode}, a visitor must be refused");
    }

    [Fact]
    public async Task Visitor_cannot_draft_for_a_team()
    {
        var client = await VisitingAsync();
        var res = await client.PostAsJsonAsync("/api/drafts/1/pick", new { teamId = 1, pokemonEntryId = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Visitor_is_not_a_user_and_has_no_identity_to_report()
    {
        var client = await VisitingAsync();
        var res = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    // Invites

    [Fact]
    public async Task Only_an_admin_can_mint_a_player_invite()
    {
        var coach = await factory.SignedInAsAsync("coach-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await coach.PostAsync("/api/admin/invite", null)).StatusCode);

        var admin = await factory.SignedInAsAsync("admin", admin: true);
        var res = await admin.PostAsync("/api/admin/invite", null);
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("token").GetString()));
        Assert.Contains("?invite=", body.GetProperty("url").GetString());
        Assert.True(body.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task An_invite_is_recognised_but_an_access_token_is_not()
    {
        var admin = await factory.SignedInAsAsync("admin", admin: true);
        var minted = await (await admin.PostAsync("/api/admin/invite", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        var invite = minted.GetProperty("token").GetString();

        using var scope = factory.Services.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();

        Assert.True(tokens.IsValidInvite(invite));
        Assert.False(tokens.IsValidInvite(null));
        Assert.False(tokens.IsValidInvite("not.a.real.token"));
        // Both are signed with the same key, so only the purpose claim separates
        // them. Without that check any live session would double as an invite.
        Assert.False(tokens.IsValidInvite(tokens.IssueVisitor().AccessToken));
        Assert.False(tokens.IsValidInvite(admin.DefaultRequestHeaders.Authorization!.Parameter));
    }
}

/// <summary>
/// The invite gate on sign-in, exercised with a stand-in for Discord. The rule:
/// a NEW account needs an admin's invite, an existing one never does (so signing
/// out cannot lock a coach out of the league).
/// </summary>
public class InviteSignInTests(InviteSignInTests.Factory factory) : IClassFixture<InviteSignInTests.Factory>
{
    /// <summary>Resolves the "code" straight to a Discord id, so a test can name
    /// whichever account it is signing in as.</summary>
    public sealed class FakeDiscord : IDiscordAuth
    {
        public Task<DiscordUser?> ExchangeCodeAsync(
            string code, string codeVerifier, string redirectUri, CancellationToken ct) =>
            Task.FromResult<DiscordUser?>(new DiscordUser(code, $"user-{code}", null));
    }

    public sealed class Factory : DraftLeagueFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services =>
            {
                var existing = services.SingleOrDefault(d => d.ServiceType == typeof(IDiscordAuth));
                if (existing is not null) services.Remove(existing);
                services.AddSingleton<IDiscordAuth, FakeDiscord>();
            });
        }
    }

    private static object Exchange(string discordId, string? invite) => new
    {
        code = discordId,
        codeVerifier = "verifier",
        redirectUri = "http://localhost:8000/",
        deviceLabel = "Test",
        invite,
    };

    [Fact]
    public async Task A_new_account_without_an_invite_is_turned_away()
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/discord", Exchange("newcomer-1", null));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task A_new_account_with_an_invite_joins_as_a_player()
    {
        var admin = await factory.SignedInAsAsync("admin", admin: true);
        var minted = await (await admin.PostAsync("/api/admin/invite", null))
            .Content.ReadFromJsonAsync<JsonElement>();

        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync(
            "/api/auth/discord", Exchange("newcomer-2", minted.GetProperty("token").GetString()));
        res.EnsureSuccessStatusCode();

        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("newcomer-2", body.GetProperty("user").GetProperty("discordId").GetString());
    }

    [Fact]
    public async Task An_existing_account_never_needs_an_invite()
    {
        // Join once with a link, then come back without one. Signing out must not
        // be able to lock a coach out of a league they are already in.
        var admin = await factory.SignedInAsAsync("admin", admin: true);
        var minted = await (await admin.PostAsync("/api/admin/invite", null))
            .Content.ReadFromJsonAsync<JsonElement>();

        var client = factory.CreateClient();
        (await client.PostAsJsonAsync("/api/auth/discord",
            Exchange("returning-1", minted.GetProperty("token").GetString()))).EnsureSuccessStatusCode();

        var res = await client.PostAsJsonAsync("/api/auth/discord", Exchange("returning-1", null));
        res.EnsureSuccessStatusCode();
        Assert.Equal("returning-1", (await res.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("user").GetProperty("discordId").GetString());
    }

    [Fact]
    public async Task A_forged_invite_does_not_open_the_door()
    {
        var client = factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/auth/discord", Exchange("newcomer-3", "not.a.real.invite"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }
}
