using System.Security.Claims;
using DraftLeague.Web.Data;
using DraftLeague.Web.Hubs;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Api;

public record SubmitReplayRequest(string ReplayUrl);

/// <summary>
/// The season schedule: a round-robin between the league's teams, and the loop
/// that turns a submitted Showdown replay into a scored, standings-affecting
/// result. Teams are created when the draft starts, so a schedule can only be
/// generated once a draft has produced them.
/// </summary>
public static class ScheduleApi
{
    public static void MapScheduleApi(this WebApplication app, string? corsPolicy = null)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        if (corsPolicy is not null) api.RequireCors(corsPolicy);

        // Everyone in the league sees the same schedule; the caller's own team is
        // resolved server-side so the client can size and place their matches.
        api.MapGet("/leagues/{leagueId:int}/schedule", async (
            int leagueId, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var league = await db.Leagues.FirstOrDefaultAsync(l => l.Id == leagueId, ct);
            if (league is null) return Results.NotFound();

            var teams = await db.Teams
                .Where(t => t.LeagueId == leagueId)
                .Select(t => new { t.Id, t.Name, t.CoachId, t.CoachName, t.Wins, t.Losses, t.Draws })
                .ToListAsync(ct);
            var teamName = teams.ToDictionary(t => t.Id, t => t.Name);

            var myDiscordId = me.DiscordId();
            var myTeamId = teams.FirstOrDefault(t => t.CoachId == myDiscordId)?.Id;

            var matches = await db.Matches
                .Where(m => m.LeagueId == leagueId)
                .OrderBy(m => m.Week).ThenBy(m => m.Id)
                .Select(m => new
                {
                    m.Id, m.Week, m.ScheduledFor,
                    m.HomeTeamId, m.AwayTeamId,
                    result = m.Result.ToString(),
                    m.HomeScore, m.AwayScore, m.ReplayUrl,
                })
                .ToListAsync(ct);

            // "This week" is the earliest week that still has an unplayed match —
            // the front of the season. Everything before it is history.
            var currentWeek = matches
                .Where(m => m.result == nameof(MatchResult.Pending))
                .Select(m => (int?)m.Week)
                .Min();

            return Results.Ok(new
            {
                leagueId,
                myTeamId,
                currentWeek,
                teams,
                matches = matches.Select(m => new
                {
                    m.Id, m.Week, m.ScheduledFor,
                    m.HomeTeamId, homeName = teamName.GetValueOrDefault(m.HomeTeamId, $"Team {m.HomeTeamId}"),
                    m.AwayTeamId, awayName = teamName.GetValueOrDefault(m.AwayTeamId, $"Team {m.AwayTeamId}"),
                    m.result, m.HomeScore, m.AwayScore, m.ReplayUrl,
                    played = m.result != nameof(MatchResult.Pending),
                    mine = myTeamId != null && (m.HomeTeamId == myTeamId || m.AwayTeamId == myTeamId),
                }),
            });
        });

        // Submit a replay for one of your matches. The result — winner and score —
        // is read off the replay itself, not taken on trust from the submitter.
        api.MapPost("/matches/{matchId:int}/replay", async (
            int matchId, SubmitReplayRequest req, ClaimsPrincipal me,
            AppDbContext db, ReplayScorer scorer, IHubContext<DraftHub> hub, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ReplayUrl))
                return Results.BadRequest(new { error = "Paste a replay link first." });

            var match = await db.Matches
                .Include(m => m.HomeTeam)
                .Include(m => m.AwayTeam)
                .FirstOrDefaultAsync(m => m.Id == matchId, ct);
            if (match is null) return Results.NotFound();

            // Only a coach in the match, or an admin, may report it.
            var myId = me.DiscordId();
            var isPlayer = myId is not null && (match.HomeTeam.CoachId == myId || match.AwayTeam.CoachId == myId);
            if (!isPlayer && !await me.IsAdminAsync(db, ct)) return Results.Forbid();

            var score = await scorer.ScoreAsync(match, req.ReplayUrl, ct);
            if (!score.Ok) return Results.BadRequest(new { error = score.Error });

            // Re-reporting is allowed (a corrected replay); back out the old result
            // from the standings before applying the new one.
            if (match.Result != MatchResult.Pending)
                ApplyToStandings(match.HomeTeam, match.AwayTeam, match.Result, -1);

            match.Result = score.Outcome;
            match.HomeScore = score.HomeScore;
            match.AwayScore = score.AwayScore;
            match.ReplayUrl = req.ReplayUrl.Trim();
            match.ReportedByCoachId = myId;
            match.ReportedAt = DateTimeOffset.UtcNow;
            ApplyToStandings(match.HomeTeam, match.AwayTeam, match.Result, +1);

            await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync("scheduleChanged", new { leagueId = match.LeagueId }, ct);
            return Results.Ok(new
            {
                match.Id, result = match.Result.ToString(), match.HomeScore, match.AwayScore,
            });
        });

    }

    /// <summary>
    /// Lays down the round-robin for a league's teams over <paramref name="weeks"/>
    /// weeks, replacing any existing schedule. Called automatically when the draft
    /// starts — teams exist by then, and the week count comes from the league's
    /// SeasonWeeks (set on the pre-start Draft settings). No-op with fewer than two
    /// teams, or once results are in, so a re-start can't wipe a live season.
    /// </summary>
    /// <returns>The number of matches created (0 if it was skipped).</returns>
    public static async Task<int> GenerateAsync(AppDbContext db, int leagueId, int weeks, CancellationToken ct = default)
    {
        if (weeks < 1) weeks = 1;

        var teamIds = await db.Teams.Where(t => t.LeagueId == leagueId).Select(t => t.Id).ToListAsync(ct);
        if (teamIds.Count < 2) return 0;

        // Never disturb a season that's already producing results.
        if (await db.Matches.AnyAsync(m => m.LeagueId == leagueId && m.Result != MatchResult.Pending, ct))
            return 0;

        await db.Matches.Where(m => m.LeagueId == leagueId).ExecuteDeleteAsync(ct);

        // Weekly cadence off a clean anchor (midnight UTC today), so each week's
        // matches carry a plausible date the client can show.
        var seasonStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var rounds = RoundRobin(teamIds, weeks);
        for (var w = 0; w < rounds.Count; w++)
            foreach (var (home, away) in rounds[w])
                db.Matches.Add(new Match
                {
                    LeagueId = leagueId,
                    Week = w + 1,
                    HomeTeamId = home,
                    AwayTeamId = away,
                    ScheduledFor = seasonStart.AddDays(7 * w),
                });

        await db.SaveChangesAsync(ct);
        return rounds.Sum(x => x.Count);
    }

    /// <summary>
    /// Round-robin pairings via the circle method: fix one team, rotate the rest.
    /// A single round-robin is (n-1) weeks; asking for more repeats the cycle with
    /// home/away swapped each time round, asking for fewer takes the first weeks.
    /// An odd team count gets a bye (that team simply doesn't play that week).
    /// </summary>
    private static List<List<(int Home, int Away)>> RoundRobin(List<int> teamIds, int weeks)
    {
        // A sentinel bye keeps the pairing arithmetic even; matches against it drop.
        const int Bye = -1;
        var arr = new List<int>(teamIds);
        if (arr.Count % 2 == 1) arr.Add(Bye);

        var n = arr.Count;
        var half = n / 2;
        var result = new List<List<(int, int)>>();

        for (var w = 0; w < weeks; w++)
        {
            var swap = (w / (n - 1)) % 2 == 1; // flip home/away on each full cycle
            var week = new List<(int, int)>();
            for (var i = 0; i < half; i++)
            {
                var a = arr[i];
                var b = arr[n - 1 - i];
                if (a == Bye || b == Bye) continue;
                week.Add(swap ? (b, a) : (a, b));
            }
            result.Add(week);

            // Rotate all but the first element one step clockwise.
            var last = arr[n - 1];
            arr.RemoveAt(n - 1);
            arr.Insert(1, last);
        }
        return result;
    }

    /// <summary>Adds (<paramref name="sign"/> = +1) or backs out (-1) a result's W/L/D.</summary>
    private static void ApplyToStandings(Team home, Team away, MatchResult result, int sign)
    {
        switch (result)
        {
            case MatchResult.HomeWin: home.Wins += sign; away.Losses += sign; break;
            case MatchResult.AwayWin: away.Wins += sign; home.Losses += sign; break;
            case MatchResult.Draw: home.Draws += sign; away.Draws += sign; break;
        }
    }
}
