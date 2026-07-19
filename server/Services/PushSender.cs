using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// Delivers a queued notification to one device. Implemented against Firebase
/// Cloud Messaging, which covers Android, iOS and desktop Flutter from one
/// provider — see PUSH_SETUP.md.
/// </summary>
public interface IPushSender
{
    /// <summary>
    /// Returns true on delivery. Returns false with <paramref name="tokenIsDead"/>
    /// set when the provider says the token will never work again, so the caller
    /// can deactivate it instead of retrying forever.
    /// </summary>
    Task<(bool Sent, bool TokenIsDead, string? Error)> SendAsync(
        DeviceRegistration device, NotificationRecord notification, CancellationToken ct = default);
}

/// <summary>
/// Stand-in until FCM credentials are configured. Logs what would have been
/// sent so the whole pipeline is exercisable without a Firebase project.
/// </summary>
public class LoggingPushSender(ILogger<LoggingPushSender> log) : IPushSender
{
    public Task<(bool, bool, string?)> SendAsync(
        DeviceRegistration device, NotificationRecord n, CancellationToken ct = default)
    {
        log.LogInformation("[push:{Platform}] -> {User}: {Title} — {Body} ({Link})",
            device.Platform, n.UserId, n.Title, n.Body, n.DeepLink);
        return Task.FromResult<(bool, bool, string?)>((true, false, null));
    }
}

/// <summary>
/// Drains the notification queue and pushes to every active device.
///
/// Runs separately from the draft engine on purpose: a slow or down push
/// provider must never block or fail a pick.
/// </summary>
public class PushDispatcher(
    IServiceScopeFactory scopes,
    ILogger<PushDispatcher> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await DrainAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Push dispatch failed");
            }
        }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var sender = scope.ServiceProvider.GetRequiredService<IPushSender>();

        var pending = await db.Notifications
            .Where(n => n.SentAt == null && n.FailureReason == null)
            .OrderBy(n => n.CreatedAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0) return;

        var userIds = pending.Select(n => n.UserId).Distinct().ToList();
        var devices = await db.DeviceRegistrations
            .Where(d => d.IsActive && userIds.Contains(d.UserId))
            .ToListAsync(ct);

        var byUser = devices.GroupBy(d => d.UserId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var n in pending)
        {
            if (!byUser.TryGetValue(n.UserId, out var targets) || targets.Count == 0)
            {
                // No device registered. Mark sent so it stops being retried —
                // it still shows in the in-app history when they next log in.
                n.SentAt = DateTimeOffset.UtcNow;
                continue;
            }

            var anyDelivered = false;
            foreach (var device in targets)
            {
                var (sent, dead, error) = await sender.SendAsync(device, n, ct);
                if (sent)
                {
                    anyDelivered = true;
                }
                else if (dead)
                {
                    log.LogInformation("Deactivating dead token for {User} ({Platform})", device.UserId, device.Platform);
                    device.IsActive = false;
                }
                else
                {
                    log.LogWarning("Push to {User} failed: {Error}", device.UserId, error);
                }
            }

            // Delivered to at least one device is good enough to call it sent.
            if (anyDelivered) n.SentAt = DateTimeOffset.UtcNow;
            else n.FailureReason = "No device accepted the push";
        }

        await db.SaveChangesAsync(ct);
    }
}
