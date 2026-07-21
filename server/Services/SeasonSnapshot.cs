using DraftLeague.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// Builds the JSON season snapshot (every team's draft picks and every match's
/// stored replay + captured team builds), shared by the download endpoint and the
/// daily auto-snapshot background job. The restore endpoint reads exactly this shape.
/// </summary>
public static class SeasonSnapshot
{
    /// <returns>The snapshot object (JSON-serialisable), or null if the league is gone.</returns>
    public static async Task<object?> BuildAsync(AppDbContext db, int leagueId, CancellationToken ct = default)
    {
        var league = await db.Leagues.FirstOrDefaultAsync(l => l.Id == leagueId, ct);
        if (league is null) return null;

        var teams = await db.Teams.Where(t => t.LeagueId == leagueId)
            .Select(t => new { t.Id, t.CoachId, t.CoachName }).ToListAsync(ct);

        var picks = await db.Picks
            .Where(p => p.Team.LeagueId == leagueId)
            .OrderBy(p => p.TeamId).ThenBy(p => p.PickNumber)
            .Select(p => new
            {
                p.TeamId, p.PickNumber, tier = p.Tier.ToString(), p.TeraType,
                pokemon = p.PokemonEntry.Name, p.PokemonEntry.Sprite, p.PokemonEntry.DexNumber,
                p.OtherOptions, p.WasAutoPick, // the passed-options run + auto-pick flag, for the pick feed
            }).ToListAsync(ct);

        var matches = await db.Matches.Where(m => m.LeagueId == leagueId)
            .OrderBy(m => m.Week).ThenBy(m => m.Id)
            .Select(m => new
            {
                m.Id, m.Week, m.ScheduledFor, m.HomeTeamId, m.AwayTeamId,
                result = m.Result.ToString(), m.HomeScore, m.AwayScore,
                m.ReplayUrl, m.ReplayLog, m.ReplayHomeSide, m.HomeTeamExport, m.AwayTeamExport,
            }).ToListAsync(ct);

        return new
        {
            league = league.Name, leagueId, capturedAt = DateTimeOffset.UtcNow,
            teams, picks, matches,
        };
    }
}
