using System.Text.Json;
using DraftLeague.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// Writes a season snapshot for every league to a folder on the desktop once a day
/// at 3am US Eastern (an automatic backup). Registered as a hosted service; also
/// resolvable directly so a dev endpoint can trigger a save on demand.
/// </summary>
public class SnapshotScheduler(IServiceProvider services, ILogger<SnapshotScheduler> log) : BackgroundService
{
    private const int HourEastern = 3; // 3am ET

    /// <summary>The desktop folder daily snapshots land in.</summary>
    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "DraftLeagueSnapshots");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try { delay = TimeUntilNext(HourEastern); }
            catch (Exception ex) { log.LogWarning(ex, "Snapshot schedule calc failed; retrying in 1h"); delay = TimeSpan.FromHours(1); }
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }
            try { await SaveAllAsync(ct); }
            catch (Exception ex) { log.LogWarning(ex, "Daily snapshot save failed"); }
        }
    }

    // Time from now until the next HH:00 US Eastern. Uses the tz rules for the target
    // date, so it lands on 3am local across DST changes (EST or EDT).
    private static TimeSpan TimeUntilNext(int hour)
    {
        var tz = EasternTz();
        var nowE = TimeZoneInfo.ConvertTime(DateTime.UtcNow, tz);
        var date = nowE.TimeOfDay < TimeSpan.FromHours(hour) ? nowE.Date : nowE.Date.AddDays(1);
        var targetLocal = DateTime.SpecifyKind(date.AddHours(hour), DateTimeKind.Unspecified);
        var delay = TimeZoneInfo.ConvertTimeToUtc(targetLocal, tz) - DateTime.UtcNow;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1);
    }

    private static TimeZoneInfo EasternTz()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"); } // Windows id
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.FindSystemTimeZoneById("America/New_York"); } // IANA
    }

    /// <summary>
    /// Writes one snapshot file per league to <see cref="Folder"/> (created if needed),
    /// named by league id and the Eastern date. Returns the files written.
    /// </summary>
    public async Task<List<string>> SaveAllAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(Folder);
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var leagueIds = await db.Leagues.Select(l => l.Id).ToListAsync(ct);
        var stamp = TimeZoneInfo.ConvertTime(DateTime.UtcNow, EasternTz()).ToString("yyyy-MM-dd");
        var written = new List<string>();
        foreach (var id in leagueIds)
        {
            var snap = await SeasonSnapshot.BuildAsync(db, id, ct);
            if (snap is null) continue;
            var path = Path.Combine(Folder, $"draft-snapshot-league{id}-{stamp}.json");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snap, Json), ct);
            written.Add(path);
        }
        if (written.Count > 0) log.LogInformation("Saved {N} daily snapshot(s) to {Folder}", written.Count, Folder);
        return written;
    }
}
