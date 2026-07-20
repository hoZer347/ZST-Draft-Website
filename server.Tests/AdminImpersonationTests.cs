using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The admin "view as a dummy coach" tools (/api/admin/dummies, /impersonate).
/// A dummy is a team's coach whose id isn't a real Discord snowflake (all digits);
/// the endpoints are admin-only and refuse to hand out a real member's session.
/// </summary>
public class AdminImpersonationTests : DraftScenarioBase
{
    /// <summary>Seeds a league with two coached teams and matching User rows.</summary>
    private async Task SeedCoachAsync(string coachId, string username, bool isAdmin = false)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var league = db.Leagues.First(); // the DevSeed league
        db.Users.Add(new User { DiscordId = coachId, Username = username, IsAdmin = isAdmin });
        db.Teams.Add(new Team { LeagueId = league.Id, Name = username, CoachName = username, CoachId = coachId });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Admin_lists_only_synthetic_dummy_coaches()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        await SeedCoachAsync("benbotheclown", "benbotheclown"); // dummy
        await SeedCoachAsync("burndt", "Burndt");               // dummy
        await SeedCoachAsync("209262115196895232", ".hozer");    // real Discord id

        var dummies = await admin.GetFromJsonAsync<JsonElement>("/api/admin/dummies");
        var ids = dummies.EnumerateArray().Select(d => Str(d, "discordId")).ToList();

        Assert.Contains("benbotheclown", ids);
        Assert.Contains("burndt", ids);
        Assert.DoesNotContain("209262115196895232", ids); // real accounts are never listed
    }

    [Fact]
    public async Task Admin_can_impersonate_a_dummy_and_gets_that_users_session()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        await SeedCoachAsync("benbotheclown", "benbotheclown");

        var res = await admin.PostAsJsonAsync("/api/admin/impersonate", new { discordId = "benbotheclown" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrEmpty(Str(body, "accessToken")));
        var user = body.GetProperty("user");
        Assert.Equal("benbotheclown", Str(user, "discordId"));
        Assert.False(user.GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task A_non_admin_cannot_list_or_impersonate()
    {
        await SeedCoachAsync("benbotheclown", "benbotheclown");
        var user = await Factory.SignedInAsAsync("coach-1"); // not admin

        var list = await user.GetAsync("/api/admin/dummies");
        var imp = await user.PostAsJsonAsync("/api/admin/impersonate", new { discordId = "benbotheclown" });

        // RequireRole("admin") rejects a non-admin token before the handler runs.
        Assert.Equal(HttpStatusCode.Forbidden, list.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, imp.StatusCode);
    }

    [Fact]
    public async Task A_real_discord_account_cannot_be_impersonated()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        await SeedCoachAsync("209262115196895232", ".hozer"); // real snowflake id

        var res = await admin.PostAsJsonAsync("/api/admin/impersonate", new { discordId = "209262115196895232" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task An_admin_account_cannot_be_impersonated()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        // A synthetic-id account that is nonetheless an admin — still refused.
        await SeedCoachAsync("superuser", "superuser", isAdmin: true);

        var res = await admin.PostAsJsonAsync("/api/admin/impersonate", new { discordId = "superuser" });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Impersonating_an_unknown_account_is_404()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var res = await admin.PostAsJsonAsync("/api/admin/impersonate", new { discordId = "nobody" });
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
