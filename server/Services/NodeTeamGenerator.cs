using System.Diagnostics;
using System.Text.Json;

namespace DraftLeague.Web.Services;

/// <summary>
/// Bridge to <c>battle-server/scripts/random-teams.js</c>: turns a drafted roster
/// into N random-but-legal packed Showdown teams, for the "pre-build my teams"
/// option that seeds a coach's teambuilder. Best-effort — returns null if Node or
/// the battle-server isn't available, so the teambuilder just opens with blank
/// teams instead.
/// </summary>
public class NodeTeamGenerator(IHostEnvironment env, ILogger<NodeTeamGenerator> log)
{
    private sealed record Output(List<string> Teams);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>N random teams from one roster.</summary>
    public Task<IReadOnlyList<string>?> GenerateAsync(IReadOnlyList<string> roster, int count, CancellationToken ct = default) =>
        roster.Count == 0 || count <= 0 ? Task.FromResult<IReadOnlyList<string>?>([]) : InvokeAsync(new { roster, count }, ct);

    /// <summary>One random team per roster (batch — for demo teams across all players).</summary>
    public Task<IReadOnlyList<string>?> GenerateBatchAsync(IReadOnlyList<IReadOnlyList<string>> rosters, CancellationToken ct = default) =>
        rosters.Count == 0 ? Task.FromResult<IReadOnlyList<string>?>([]) : InvokeAsync(new { rosters }, ct);

    private async Task<IReadOnlyList<string>?> InvokeAsync(object spec, CancellationToken ct)
    {
        var battleServer = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "battle-server"));
        var script = Path.Combine(battleServer, "scripts", "random-teams.js");
        if (!File.Exists(script)) { log.LogWarning("Team generator not found at {Script}", script); return null; }

        var specJson = JsonSerializer.Serialize(spec);
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
        psi.ArgumentList.Add("scripts/random-teams.js");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            await proc.StandardInput.WriteAsync(specJson.AsMemory(), ct);
            proc.StandardInput.Close();
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (proc.ExitCode != 0) { log.LogWarning("Team generator exited {Code}: {Err}", proc.ExitCode, stderr); return null; }
            return JsonSerializer.Deserialize<Output>(stdout, JsonOpts)?.Teams;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Team generator invocation failed");
            return null;
        }
    }
}
