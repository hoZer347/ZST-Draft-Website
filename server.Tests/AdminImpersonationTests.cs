using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The admin "view as" tools (/api/admin/dummies, /impersonate). An admin can view
/// as ANY user in the league roster (real or synthetic), since they already hold
/// full powers; the endpoints are admin-only and refuse only the reserved
/// oversee-admin identity and unknown accounts.
/// </summary>
public class AdminImpersonationTests : DraftScenarioBase
{
    /// <summary>Seeds a league with a coached team and a matching User row.</summary>
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
    public async Task Admin_lists_all_roster_users_to_view_as()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        await SeedCoachAsync("benbotheclown", "benbotheclown"); // synthetic
        await SeedCoachAsync("209262115196895232", ".hozer");    // real Discord id

        var users = await admin.GetFromJsonAsync<JsonElement>("/api/admin/dummies");
        var ids = users.EnumerateArray().Select(d => Str(d, "discordId")).ToList();

        Assert.Contains("benbotheclown", ids);
        Assert.Contains("209262115196895232", ids);  // real accounts ARE listed now
        Assert.DoesNotContain("admin", ids);           // but not the oversee-admin (self)
    }

    [Fact]
    public async Task Admin_can_view_as_any_user_real_or_synthetic()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        await SeedCoachAsync("209262115196895232", ".hozer"); // a real snowflake id

        var res = await admin.PostAsJsonAsync("/api/admin/impersonate", new { discordId = "209262115196895232" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrEmpty(Str(body, "accessToken")));
        Assert.Equal("209262115196895232", Str(body.GetProperty("user"), "discordId"));
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
    public async Task The_oversee_admin_identity_cannot_be_viewed_as()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var res = await admin.PostAsJsonAsync("/api/admin/impersonate", new { discordId = "admin" });
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
