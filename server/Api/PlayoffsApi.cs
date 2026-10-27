using System.Security.Claims;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Api;

/// <summary>
/// The playoff bracket (see <see cref="Playoffs"/>). Before the regular season ends
/// no seeds are frozen, so it serves the bracket the current standings would make,
/// flagged as projected. Read-only: games are created by <see cref="Playoffs.SyncAsync"/>.
/// </summary>
public static class PlayoffsApi
{
    public static void MapPlayoffsApi(this WebApplication app, string? corsPolicy = null)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        if (corsPolicy is not null) api.RequireCors(corsPolicy);

        api.MapGet("/leagues/{leagueId:int}/playoffs", async (
            int leagueId, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            if (!await db.Leagues.AnyAsync(l => l.Id == leagueId, ct)) return Results.NotFound();

            var teams = await db.Teams
                .Where(t => t.LeagueId == leagueId)
                .Select(t => new { t.Id, t.CoachId, t.CoachName, t.PlayoffSeed })
                .ToDictionaryAsync(t => t.Id, ct);
            var myDiscordId = me.DiscordId();
            var myTeamId = teams.Values.FirstOrDefault(t => t.CoachId == myDiscordId)?.Id;

            var projected = !teams.Values.Any(t => t.PlayoffSeed is not null);
            var seeded = projected
                ? (await Standings.ComputeAsync(db, leagueId, ct)).Take(Playoffs.Qualifiers).Select(r => r.TeamId).ToList()
                : teams.Values.Where(t => t.PlayoffSeed is not null).OrderBy(t => t.PlayoffSeed).Select(t => t.Id).ToList();
            // Only what the bracket shows: the stored logs and builds stay in the database.
            var games = projected
                ? []
                : await db.Matches.Where(m => m.LeagueId == leagueId && m.PlayoffRound != null)
                    .Select(m => new Match
                    {
                        Id = m.Id, LeagueId = m.LeagueId, PlayoffRound = m.PlayoffRound, PlayoffSlot = m.PlayoffSlot,
                        HomeTeamId = m.HomeTeamId, AwayTeamId = m.AwayTeamId, Result = m.Result,
                        HomeScore = m.HomeScore, AwayScore = m.AwayScore, ReplayUrl = m.ReplayUrl,
                    })
                    .ToListAsync(ct);
            var avatars = await ScheduleApi.TeamAvatarsAsync(db, teams.Values.Select(t => (t.Id, t.CoachId)), ct);

            object? Side(Playoffs.Entrant? e) => e is null ? null : new
            {
                seed = e.Seed,
                teamId = e.TeamId,
                name = teams[e.TeamId].CoachName,
                avatar = avatars[e.TeamId],
            };

            var bracket = Playoffs.Build(seeded, games);
            return Results.Ok(new
            {
                leagueId,
                myTeamId,
                projected,
                rounds = bracket.Select((slots, i) => new
                {
                    round = i + 1,
                    name = Playoffs.RoundName(i + 1, bracket.Count),
                    slots = slots.Select(s => new
                    {
                        index = s.Index,
                        bye = s.Bye,
                        home = Side(s.Home),
                        away = Side(s.Away),
                        homeWins = s.HomeWins,
                        awayWins = s.AwayWins,
                        winnerTeamId = s.Bye ? null : s.Winner?.TeamId,
                        games = s.Games.Select((g, n) => new
                        {
                            matchId = g.Id,
                            game = n + 1,
                            result = g.Result.ToString(),
                            homeScore = g.HomeScore,
                            awayScore = g.AwayScore,
                            replayUrl = g.ReplayUrl,
                        }),
                    }),
                }),
            });
        });
    }
}
