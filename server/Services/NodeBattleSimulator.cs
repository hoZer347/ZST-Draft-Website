using System.Diagnostics;
using System.Text.Json;

namespace DraftLeague.Web.Services;

/// <summary>
/// Development-only bridge to the headless Showdown battle runner in
/// <c>battle-server/scripts/simulate-season.js</c>. Given a set of matchups (each
/// side a list of pool species slugs), it spawns Node once, streams the spec in,
/// and reads back one real battle log per match — which the season simulator then
/// feeds through the normal replay-stats pipeline.
///
/// Best-effort: any failure (Node missing, battle-server not installed, a crash)
/// returns null so the caller can fall back to fabricated results rather than
/// leaving the dev tool broken.
/// </summary>
public class NodeBattleSimulator(IHostEnvironment env, ILogger<NodeBattleSimulator> log)
{
    /// <summary>One rostered mon: its pool slug and, for C-tier mons, the Tera type
    /// the draft assigned it (null otherwise). A non-null Tera type tells the runner
    /// this mon should terastallize to that type as soon as it can.</summary>
    public record TeamMon(string Slug, string? TeraType);
    public record MatchSpec(string HomeName, string AwayName, IReadOnlyList<TeamMon> HomeTeam, IReadOnlyList<TeamMon> AwayTeam);
    public record BattleResult(string? Winner, int Turns, string Log, string? P1Export = null, string? P2Export = null);

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <returns>One result per match (same order), or null if the run failed.</returns>
    public async Task<IReadOnlyList<BattleResult>?> RunAsync(IReadOnlyList<MatchSpec> matches, CancellationToken ct = default)
    {
        if (matches.Count == 0) return [];

        var battleServer = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "battle-server"));
        var script = Path.Combine(battleServer, "scripts", "simulate-season.js");
        if (!File.Exists(script))
        {
            log.LogWarning("Battle simulator not found at {Script}; falling back to fabricated stats", script);
            return null;
        }

        var spec = JsonSerializer.Serialize(new
        {
            matches = matches.Select(m => new
            {
                homeName = m.HomeName, awayName = m.AwayName,
                // { s: slug, t: teraType|null } per mon — see TeamMon.
                homeTeam = m.HomeTeam.Select(x => new { s = x.Slug, t = x.TeraType }),
                awayTeam = m.AwayTeam.Select(x => new { s = x.Slug, t = x.TeraType }),
            }),
        });

        var psi = new ProcessStartInfo
        {
            FileName = "node",
            WorkingDirectory = battleServer,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("scripts/simulate-season.js");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) { log.LogWarning("Failed to start Node for the battle simulator"); return null; }

            // Feed the spec in and close stdin so the script starts battling; read
            // both pipes concurrently so a full stdout (logs can be ~1 MB/season)
            // can't deadlock against unread stderr.
            await proc.StandardInput.WriteAsync(spec.AsMemory(), ct);
            proc.StandardInput.Close();

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (proc.ExitCode != 0)
            {
                log.LogWarning("Battle simulator exited {Code}: {Err}", proc.ExitCode, Trim(stderr));
                return null;
            }

            var results = JsonSerializer.Deserialize<List<BattleResult>>(stdout, JsonOpts);
            if (results is null || results.Count != matches.Count)
            {
                log.LogWarning("Battle simulator returned {Got} results for {Want} matches", results?.Count, matches.Count);
                return null;
            }
            return results;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Battle simulator invocation failed; falling back to fabricated stats");
            return null;
        }
    }

    private static string Trim(string s) => s.Length > 500 ? s[..500] + "…" : s;
}
