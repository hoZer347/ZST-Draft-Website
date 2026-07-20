using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The pre-start Draft settings (season weeks + pick timeout) applied on Start,
/// and the pick clock they drive. Start never 400s on settings: weeks are clamped
/// to a sane range, but the pick timeout is taken exactly as given — including 0,
/// which fast-forwards the whole draft. Covers the "0 → resets to 24" report and
/// that the clock drains a zero-timeout draft to completion.
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
    [InlineData(0, 3600, 1, 3600)]            // weeks clamped up to 1
    [InlineData(53, 3600, 52, 3600)]          // weeks clamped down to 52
    [InlineData(8, 0, 8, 0)]                   // timeout 0 is taken as-is (quick-draft)
    [InlineData(8, 604801, 8, 604801)]         // no upper cap on the timeout either
    public async Task Weeks_are_clamped_but_the_pick_timeout_is_taken_as_given(
        int weeks, int secs, int expectedWeeks, int expectedSecs)
    {
        var (admin, draftId) = await ReadyOneAsync();

        // Crucially NOT a 400 — accepted and started.
        var res = await admin.PostAsJsonAsync($"/api/admin/drafts/{draftId}/start",
            new { weeks, pickTimerSeconds = secs });
        res.EnsureSuccessStatusCode();

        var s = await StateAsync(admin, draftId);
        Assert.Equal("Running", s.GetProperty("state").GetString());
        Assert.Equal(expectedWeeks, s.GetProperty("weeks").GetInt32());
        Assert.Equal(expectedSecs, s.GetProperty("pickTimerSeconds").GetInt32());
    }

    [Fact]
    public async Task A_tiny_timeout_is_kept_not_reset_to_the_default()
    {
        // The reported bug: 0.00001h → round(0.036) = 0 seconds. It used to 400 the
        // Start (so nothing saved and the field snapped back to 24h). Now 0 is a
        // valid setting and the draft starts with it.
        var (admin, draftId) = await ReadyOneAsync();

        var res = await admin.PostAsJsonAsync($"/api/admin/drafts/{draftId}/start",
            new { weeks = 8, pickTimerSeconds = 0 });
        res.EnsureSuccessStatusCode();

        var s = await StateAsync(admin, draftId);
        Assert.Equal("Running", s.GetProperty("state").GetString());
        Assert.Equal(0, s.GetProperty("pickTimerSeconds").GetInt32()); // kept as 0
        Assert.NotEqual(86400, s.GetProperty("pickTimerSeconds").GetInt32()); // did NOT reset to 24h
    }

    [Fact]
    public async Task Valid_settings_are_applied_and_drive_the_live_clock()
    {
        var (admin, draftId) = await ReadyOneAsync();

        var res = await admin.PostAsJsonAsync($"/api/admin/drafts/{draftId}/start",
            new { weeks = 12, pickTimerSeconds = 120 });
        res.EnsureSuccessStatusCode();

        var s = await StateAsync(admin, draftId);
        Assert.Equal("Running", s.GetProperty("state").GetString());
        Assert.Equal(12, s.GetProperty("weeks").GetInt32());
        Assert.Equal(120, s.GetProperty("pickTimerSeconds").GetInt32());
        // The pick deadline reflects the configured timeout.
        var remaining = s.GetProperty("secondsRemaining");
        Assert.True(remaining.ValueKind != JsonValueKind.Null && remaining.GetInt32() is > 0 and <= 120);
    }

    [Fact]
    public async Task Starting_with_no_settings_body_still_works()
    {
        var (admin, draftId) = await ReadyOneAsync();

        (await admin.PostAsync($"/api/admin/drafts/{draftId}/start", null)).EnsureSuccessStatusCode();

        var s = await StateAsync(admin, draftId);
        Assert.Equal("Running", s.GetProperty("state").GetString());
        Assert.True(s.GetProperty("weeks").GetInt32() >= 1);
    }

    [Fact]
    public async Task A_zero_timeout_fast_forwards_the_whole_draft_to_completion()
    {
        var (admin, draftId) = await ReadyOneAsync();

        // Timeout 0 → every pick's deadline is already past, so the clock's drain
        // loop auto-picks the entire draft in one sweep instead of one per second.
        (await admin.PostAsJsonAsync($"/api/admin/drafts/{draftId}/start",
            new { weeks = 2, pickTimerSeconds = 0 })).EnsureSuccessStatusCode();

        JsonElement s = default;
        for (var i = 0; i < 60; i++)
        {
            s = await StateAsync(admin, draftId);
            if (s.GetProperty("state").GetString() == "Complete") break;
            await Task.Delay(100);
        }

        Assert.Equal("Complete", s.GetProperty("state").GetString());
        var picks = s.GetProperty("picks").EnumerateArray().ToList();
        Assert.NotEmpty(picks);
        Assert.All(picks, p => Assert.True(p.GetProperty("wasAutoPick").GetBoolean()));
    }

    [Fact]
    public async Task Aborting_restores_the_default_weeks_and_timeout()
    {
        // Start a quick-draft-configured run (2 weeks, 0s timeout)…
        var (admin, draftId) = await ReadyOneAsync();
        (await admin.PostAsJsonAsync($"/api/admin/drafts/{draftId}/start",
            new { weeks = 2, pickTimerSeconds = 0 })).EnsureSuccessStatusCode();

        // …then abort. The settings panel should offer 8 weeks / 24h again,
        // not the aborted run's values.
        (await admin.PostAsync($"/api/admin/drafts/{draftId}/abort", null)).EnsureSuccessStatusCode();

        var s = await StateAsync(admin, draftId);
        Assert.Equal("NotStarted", s.GetProperty("state").GetString());
        Assert.Equal(8, s.GetProperty("weeks").GetInt32());
        Assert.Equal(86400, s.GetProperty("pickTimerSeconds").GetInt32());
    }

    [Fact]
    public async Task The_clock_auto_picks_when_the_pick_deadline_passes()
    {
        var (admin, draftId, _) = await StartWithAsync("p1", "p2"); // default (long) timeout

        // Drain the on-clock team's skips so a lapsed window falls through to an
        // auto PICK. With skips in hand a missed window auto-SKIPS first instead
        // (covered in SkipTests).
        var onClock = Int(await StateAsync(admin, draftId), "onClockTeamId");
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var team = await db.Teams.FirstAsync(t => t.Id == onClock);
            team.SkipsRemaining = 0;
            await db.SaveChangesAsync();
        }

        // Force the current pick's clock to have already expired…
        (await admin.PostAsync($"/dev/drafts/{draftId}/expire", null)).EnsureSuccessStatusCode();

        // …then let the background DraftClock catch it and auto-pick.
        JsonElement s = default;
        for (var i = 0; i < 50; i++)
        {
            s = await StateAsync(admin, draftId);
            if (s.GetProperty("picks").GetArrayLength() > 0) break;
            await Task.Delay(100);
        }

        var picks = s.GetProperty("picks").EnumerateArray().ToList();
        Assert.NotEmpty(picks);
        Assert.True(picks[0].GetProperty("wasAutoPick").GetBoolean());
    }
}
