using System.Diagnostics;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Runs server/test-watchdog.ps1, so a watchdog change that would kill a healthy
/// server again fails the suite.
/// </summary>
public class WatchdogScriptTests
{
    private const int TimeoutMs = 60_000;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PokemonDraftLeague.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }

    [Fact]
    public async Task Watchdog_script_checks_pass()
    {
        if (!OperatingSystem.IsWindows()) return;

        var script = Path.Combine(RepoRoot(), "server", "test-watchdog.ps1");
        using var run = Process.Start(new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        using var timeout = new CancellationTokenSource(TimeoutMs);
        var output = run.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = run.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await run.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            run.Kill(entireProcessTree: true);
            Assert.Fail($"test-watchdog.ps1 did not finish within {TimeoutMs} ms");
        }

        var printed = await output;
        Assert.True(run.ExitCode == 0, $"{printed}\n{await errors}");
        Assert.Contains("checks passed", printed);
    }
}
