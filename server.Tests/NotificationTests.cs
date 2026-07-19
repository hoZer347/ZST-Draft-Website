using System.Net;
using System.Net.Http.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Notification delivery rules: the queue honours per-kind opt-outs and refuses
/// empty recipients, the read endpoint is owner-only, and device registration
/// upserts/reassigns a push token to the current owner.
/// </summary>
public class NotificationTests : DraftScenarioBase
{
    private static NotificationRecord Rec(string userId, NotificationKind kind = NotificationKind.PickMade) =>
        new() { UserId = userId, Kind = kind, Title = "t", Body = "b" };

    // ── queue mute rules (service level) ───────────────────────────────────

    [Fact]
    public async Task Enqueue_persists_a_notification_by_default()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<INotificationQueue>();

        await queue.EnqueueAsync(Rec("coach-1"));

        Assert.Equal(1, await db.Notifications.CountAsync(n => n.UserId == "coach-1"));
    }

    [Fact]
    public async Task A_muted_kind_is_suppressed()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<INotificationQueue>();

        db.NotificationPreferences.Add(new NotificationPreference
        {
            UserId = "coach-1", Kind = NotificationKind.PickMade, Enabled = false,
        });
        await db.SaveChangesAsync();

        await queue.EnqueueAsync(Rec("coach-1", NotificationKind.PickMade));
        // A different, un-muted kind still goes through.
        await queue.EnqueueAsync(Rec("coach-1", NotificationKind.YourTurn));

        Assert.Equal(0, await db.Notifications.CountAsync(n => n.Kind == NotificationKind.PickMade));
        Assert.Equal(1, await db.Notifications.CountAsync(n => n.Kind == NotificationKind.YourTurn));
    }

    [Fact]
    public async Task Enqueue_refuses_an_empty_user_id()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<INotificationQueue>();

        await queue.EnqueueAsync(Rec(""));

        Assert.Equal(0, await db.Notifications.CountAsync());
    }

    [Fact]
    public async Task Enqueue_many_inserts_the_unmuted_and_drops_the_muted()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<INotificationQueue>();

        db.NotificationPreferences.Add(new NotificationPreference
        {
            UserId = "coach-2", Kind = NotificationKind.PickMade, Enabled = false,
        });
        await db.SaveChangesAsync();

        await queue.EnqueueManyAsync(new[]
        {
            Rec("coach-1", NotificationKind.PickMade), // kept
            Rec("coach-2", NotificationKind.PickMade), // muted
            Rec("coach-3", NotificationKind.PickMade), // kept
            Rec("", NotificationKind.PickMade),        // no user → dropped
        });

        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == "coach-1"));
        Assert.False(await db.Notifications.AnyAsync(n => n.UserId == "coach-2"));
        Assert.True(await db.Notifications.AnyAsync(n => n.UserId == "coach-3"));
        Assert.Equal(2, await db.Notifications.CountAsync());
    }

    // ── read endpoint ──────────────────────────────────────────────────────

    private async Task<int> SeedNotificationAsync(string userId)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rec = Rec(userId);
        db.Notifications.Add(rec);
        await db.SaveChangesAsync();
        return rec.Id;
    }

    [Fact]
    public async Task Reading_your_own_notification_marks_it_read()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var id = await SeedNotificationAsync("coach-1");

        (await client.PostAsync($"/api/notifications/{id}/read", null)).EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.NotNull((await db.Notifications.FindAsync(id))!.ReadAt);
    }

    [Fact]
    public async Task You_cannot_mark_someone_elses_notification_read()
    {
        await Factory.SignedInAsAsync("coach-1");
        var other = await Factory.SignedInAsAsync("coach-2");
        var id = await SeedNotificationAsync("coach-1");

        var res = await other.PostAsync($"/api/notifications/{id}/read", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Reading_an_unknown_notification_is_404()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var res = await client.PostAsync("/api/notifications/999999/read", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    // ── device registration ────────────────────────────────────────────────

    [Fact]
    public async Task Registering_a_token_already_owned_reassigns_it_to_the_new_owner()
    {
        var c1 = await Factory.SignedInAsAsync("coach-1");
        var c2 = await Factory.SignedInAsAsync("coach-2");

        (await c1.PostAsJsonAsync("/api/devices", new { pushToken = "tok-abc", platform = 0, deviceName = "Pixel" }))
            .EnsureSuccessStatusCode();
        (await c2.PostAsJsonAsync("/api/devices", new { pushToken = "tok-abc", platform = 0, deviceName = "iPhone" }))
            .EnsureSuccessStatusCode();

        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rows = await db.DeviceRegistrations.Where(d => d.PushToken == "tok-abc").ToListAsync();
        // One row, now owned by the second coach.
        Assert.Single(rows);
        Assert.Equal("coach-2", rows[0].UserId);
    }

    [Fact]
    public async Task You_cannot_delete_a_device_token_you_do_not_own()
    {
        var c1 = await Factory.SignedInAsAsync("coach-1");
        var c2 = await Factory.SignedInAsAsync("coach-2");
        (await c1.PostAsJsonAsync("/api/devices", new { pushToken = "tok-xyz", platform = 0, deviceName = "Pixel" }))
            .EnsureSuccessStatusCode();

        var res = await c2.DeleteAsync("/api/devices/tok-xyz");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_unknown_device_token_is_404()
    {
        var client = await Factory.SignedInAsAsync("coach-1");
        var res = await client.DeleteAsync("/api/devices/never-registered");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
