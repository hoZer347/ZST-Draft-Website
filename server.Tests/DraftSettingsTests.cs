using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The pre-start Draft settings (season weeks + pick timeout) the admin applies
/// when starting. Covers the validation bounds and that valid values take effect
/// on the running draft.
/// </summary>
public class DraftSettingsTests : DraftScenarioBase
{
    private async Task<(HttpClient Admin, int DraftId)> ReadyOneAsync()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);
        var coach = await Factory.SignedInAsAsync("coach-1");
        (await coach.PostAsync($"/api/drafts/{draftId}/ready", null)).EnsureSuccessStatusCode();
        return (admin, draftId);
    }

    [Theory]
    [InlineData(0, 3600)]      // weeks too low
    [InlineData(53, 3600)]     // weeks too high
    [InlineData(8, 10)]        // timeout below 30s
    [InlineData(8, 604801)]    // timeout above 7 days
    public async Task Out_of_range_settings_are_rejected(int weeks, int pickTimerSeconds)
    {
        var (admin, draftId) = await ReadyOneAsync();

        var res = await admin.PostAsJsonAsync($"/api/admin/drafts/{draftId}/start",
            new { weeks, pickTimerSeconds });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);

        // The draft did not start.
        var s = await StateAsync(admin, draftId);
        Assert.Equal("NotStarted", s.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Valid_settings_are_applied_and_the_draft_starts()
    {
        var (admin, draftId) = await ReadyOneAsync();

        var res = await admin.PostAsJsonAsync($"/api/admin/drafts/{draftId}/start",
            new { weeks = 12, pickTimerSeconds = 120 });
        res.EnsureSuccessStatusCode();

        var s = await StateAsync(admin, draftId);
        Assert.Equal("Running", s.GetProperty("state").GetString());
        Assert.Equal(12, s.GetProperty("weeks").GetInt32());
        Assert.Equal(120, s.GetProperty("pickTimerSeconds").GetInt32());
        // The live clock reflects the new short timer (≤ 120s remaining).
        var remaining = s.GetProperty("secondsRemaining");
        Assert.True(remaining.ValueKind != JsonValueKind.Null && remaining.GetInt32() <= 120);
    }

    [Fact]
    public async Task Starting_with_no_settings_body_still_works()
    {
        var (admin, draftId) = await ReadyOneAsync();

        // null body → keep the league defaults.
        (await admin.PostAsync($"/api/admin/drafts/{draftId}/start", null)).EnsureSuccessStatusCode();

        var s = await StateAsync(admin, draftId);
        Assert.Equal("Running", s.GetProperty("state").GetString());
        Assert.True(s.GetProperty("weeks").GetInt32() >= 1);
    }
}
