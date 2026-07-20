using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// Drives every running draft's pick clock.
///
/// The Python version spawned a thread per draft that slept in one-second
/// increments and held the remaining time in memory — so a restart reset
/// every clock, and N drafts meant N threads. This is a single background
/// service that compares each draft's persisted deadline against the wall
/// clock, which survives restarts and scales to any number of drafts.
/// </summary>
public class DraftClock(IServiceScopeFactory scopes, ILogger<DraftClock> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    /// <summary>Warn the coach on the clock at these remaining-second marks.</summary>
    private static readonly int[] WarnAt = [60, 15];

    /// <summary>Drafts already warned at a given mark, so we warn once per pick.</summary>
    private readonly HashSet<(int DraftId, int PickIndex, int Mark)> _warned = [];

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A bad draft must not kill the clock for every other league.
                log.LogError(ex, "Draft clock sweep failed");
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var engine = scope.ServiceProvider.GetRequiredService<DraftEngine>();
        var notifier = scope.ServiceProvider.GetRequiredService<IDraftNotifier>();

        var now = DateTimeOffset.UtcNow;

        var live = await db.Drafts
            .Where(d => d.State == DraftState.Running && d.PickDeadline != null)
            .Select(d => new { d.Id, d.CurrentIndex, d.PickDeadline })
            .ToListAsync(ct);

        foreach (var d in live)
        {
            var remaining = (int)Math.Ceiling((d.PickDeadline!.Value - now).TotalSeconds);

            if (remaining <= 0)
            {
                _warned.RemoveWhere(w => w.DraftId == d.Id);

                // Drain every already-expired pick in this one sweep, rather than
                // one per 1s tick. With a normal timeout the next pick's deadline
                // lands in the future and the loop stops after a single auto-pick;
                // with a ~0 timeout each new deadline is already past, so the whole
                // draft fast-forwards to completion here (simulating a quick draft).
                // The guard is a backstop against an unexpected non-advancing state.
                for (var guard = 0; guard < 2000; guard++)
                {
                    var r = await engine.AutoPickAsync(d.Id, ct);
                    if (!r.Ok) break; // draft complete, paused, or errored

                    var next = await db.Drafts
                        .Where(x => x.Id == d.Id && x.State == DraftState.Running)
                        .Select(x => x.PickDeadline)
                        .FirstOrDefaultAsync(ct);
                    if (next is null || next.Value > DateTimeOffset.UtcNow) break; // caught up
                }
                continue;
            }

            foreach (var mark in WarnAt)
            {
                if (remaining != mark) continue;
                var key = (d.Id, d.CurrentIndex, mark);
                if (!_warned.Add(key)) continue;

                var teamId = await db.DraftSlots
                    .Where(s => s.DraftId == d.Id && s.Position == d.CurrentIndex)
                    .Select(s => (int?)s.TeamId)
                    .FirstOrDefaultAsync(ct);

                if (teamId is not null)
                    await notifier.TurnWarningAsync(d.Id, teamId.Value, mark, ct);
            }
        }
    }
}
