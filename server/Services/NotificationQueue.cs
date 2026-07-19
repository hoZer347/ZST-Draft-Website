using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

public interface INotificationQueue
{
    /// <summary>
    /// Persists a notification for delivery. Respects the user's per-kind
    /// opt-out, so callers can enqueue freely without checking preferences.
    /// </summary>
    Task EnqueueAsync(NotificationRecord record, CancellationToken ct = default);

    /// <summary>
    /// Persists many notifications in a single round trip — one preference query
    /// and one save for the whole batch. Used by the league fan-out, which would
    /// otherwise issue a query+insert+commit per coach and stall the request that
    /// triggered it (e.g. a draft pick waiting on a dozen sequential writes).
    /// </summary>
    Task EnqueueManyAsync(IReadOnlyCollection<NotificationRecord> records, CancellationToken ct = default);
}

public class NotificationQueue(AppDbContext db, ILogger<NotificationQueue> log) : INotificationQueue
{
    public async Task EnqueueAsync(NotificationRecord record, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(record.UserId))
        {
            log.LogWarning("Refusing to enqueue {Kind} with no UserId", record.Kind);
            return;
        }

        var muted = await db.NotificationPreferences
            .AnyAsync(p => p.UserId == record.UserId && p.Kind == record.Kind && !p.Enabled, ct);

        if (muted) return;

        db.Notifications.Add(record);
        await db.SaveChangesAsync(ct);
    }

    public async Task EnqueueManyAsync(IReadOnlyCollection<NotificationRecord> records, CancellationToken ct = default)
    {
        var valid = records.Where(r => !string.IsNullOrWhiteSpace(r.UserId)).ToList();
        if (valid.Count == 0) return;

        // One query for every opt-out among the recipients, rather than one per
        // record; then a single insert batch and a single commit.
        var userIds = valid.Select(r => r.UserId).Distinct().ToList();
        var muted = (await db.NotificationPreferences
                .Where(p => userIds.Contains(p.UserId) && !p.Enabled)
                .Select(p => new { p.UserId, p.Kind })
                .ToListAsync(ct))
            .Select(p => (p.UserId, p.Kind))
            .ToHashSet();

        var toAdd = valid.Where(r => !muted.Contains((r.UserId, r.Kind))).ToList();
        if (toAdd.Count == 0) return;

        db.Notifications.AddRange(toAdd);
        await db.SaveChangesAsync(ct);
    }
}
