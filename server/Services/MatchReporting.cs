using DraftLeague.Web.Data;
using DraftLeague.Web.Hubs;
using DraftLeague.Web.Models;
using Microsoft.AspNetCore.SignalR;

namespace DraftLeague.Web.Services;

/// <summary>
/// The single place a scored battle becomes a recorded result: it writes the match
/// fields, folds the log into standings and per-mon stats, and broadcasts the
/// change. Shared by every path that records a game — a coach pasting a replay, our
/// Showdown server's auto-report, and the season simulator — so all of them derive
/// the result the same way (from the log, via <see cref="ReplayScorer"/>) and none
/// of them invents a score, a result, or a stat.
/// </summary>
public static class MatchReporting
{
    /// <summary>
    /// Records a <see cref="ReplayScorer.AutoReport"/> onto its match: result, score,
    /// stored log, replay link, standings and per-mon stats, then broadcasts. Pass an
    /// explicit <paramref name="replayUrl"/> when the game has a real Showdown replay
    /// URL; otherwise (we captured the log ourselves) it points "Watch replay" at our
    /// local renderer so an auto-reported battle still gets a link.
    /// </summary>
    public static async Task RecordReportAsync(
        AppDbContext db, MatchStatsRecorder recorder, IHubContext<DraftHub> hub,
        Match match, ReplayScorer.AutoReport report, string? replayUrl, string? reporterId, CancellationToken ct)
    {
        match.Result = report.Outcome;
        match.HomeScore = report.HomeScore;
        match.AwayScore = report.AwayScore;
        // An explicit URL wins (a coach pasted a real Showdown replay). Otherwise,
        // when we captured the log ourselves (the Showdown server's auto-report or a
        // headless sim battle), point "Watch replay" at our local renderer — so an
        // auto-reported battle still gets a replay link, not just a score.
        if (replayUrl is not null) match.ReplayUrl = replayUrl;
        else if (!string.IsNullOrEmpty(report.Log)) match.ReplayUrl = $"/api/matches/{match.Id}/replay";
        match.ReplayLog = report.Log;              // stored so it can be backed out later
        match.ReplayHomeSide = report.HomeSide;
        match.ReportedByCoachId = reporterId;
        match.ReportedAt = DateTimeOffset.UtcNow;
        ApplyToStandings(match.HomeTeam, match.AwayTeam, match.Result, +1);
        if (report.Log is not null && report.HomeSide is not null)
            await recorder.ApplyAsync(match, report.HomeSide, report.Log, report.Outcome, +1, ct);

        await db.SaveChangesAsync(ct);
        await hub.Clients.All.SendAsync("scheduleChanged", new { leagueId = match.LeagueId }, ct);
    }

    /// <summary>
    /// Backs a match's currently-recorded result out of the standings and the stats
    /// page, using its STORED log (no re-fetch). No-op if the match is Pending. Does
    /// NOT save — the caller persists after clearing the match fields.
    /// </summary>
    public static async Task BackOutAsync(MatchStatsRecorder recorder, Match match, CancellationToken ct)
    {
        if (match.Result == MatchResult.Pending) return;
        ApplyToStandings(match.HomeTeam, match.AwayTeam, match.Result, -1);
        if (!string.IsNullOrEmpty(match.ReplayLog) && match.ReplayHomeSide is not null)
            await recorder.ApplyAsync(match, match.ReplayHomeSide, match.ReplayLog, match.Result, -1, ct);
    }

    /// <summary>Adds (<paramref name="sign"/> = +1) or backs out (-1) a result's W/L/D.</summary>
    public static void ApplyToStandings(Team home, Team away, MatchResult result, int sign)
    {
        switch (result)
        {
            case MatchResult.HomeWin: home.Wins += sign; away.Losses += sign; break;
            case MatchResult.AwayWin: away.Wins += sign; home.Losses += sign; break;
            case MatchResult.Draw: home.Draws += sign; away.Draws += sign; break;
        }
    }
}
