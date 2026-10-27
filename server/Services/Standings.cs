using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// The standings order, shared by the scoreboard and playoff seeding: record
/// differential (W - L, regular season only), then KO differential, then wins, then
/// total KOs, with team id as the final, stable tiebreak. The KO figures count every
/// game, playoffs included, which can't move seeds frozen before the playoffs began.
/// </summary>
public static class Standings
{
    public record Row(int TeamId, int Wins, int Losses, int Draws, int RecordDiff, int KoDiff, int TotalKos, int TotalFaints);

    public static async Task<List<Row>> ComputeAsync(AppDbContext db, int leagueId, CancellationToken ct)
    {
        var teams = await db.Teams
            .Where(t => t.LeagueId == leagueId)
            .Select(t => new { t.Id, t.Wins, t.Losses, t.Draws })
            .ToListAsync(ct);

        // The KOs column is the team's credited Kills, which the scraper withholds for
        // a self-KO.
        var killsByTeam = await db.PokemonStats
            .Where(s => s.Pick.Team.LeagueId == leagueId && s.GamesPlayed > 0)
            .GroupBy(s => s.Pick.TeamId)
            .Select(g => new { TeamId = g.Key, Kills = g.Sum(s => s.Kills) })
            .ToDictionaryAsync(x => x.TeamId, x => x.Kills, ct);

        // The KO differential is counted per GAME from each side's faints, so an
        // opponent's self-KO (Explosion, Final Gambit) still moves it.
        var played = await db.Matches
            .Where(m => m.LeagueId == leagueId && m.Result != MatchResult.Pending
                        && m.ReplayLog != null && m.ReplayHomeSide != null)
            .Select(m => new { m.HomeTeamId, m.AwayTeamId, m.ReplayHomeSide, m.ReplayLog })
            .ToListAsync(ct);

        var koByTeam = new Dictionary<int, (int OppFaints, int Faints)>();
        void AddGame(int teamId, int oppFaints, int faints)
        {
            var t = koByTeam.GetValueOrDefault(teamId);
            koByTeam[teamId] = (t.OppFaints + oppFaints, t.Faints + faints);
        }
        foreach (var m in played)
        {
            var faints = ReplayScorer.FaintsBySide(m.ReplayLog!);
            var homeFaints = faints.GetValueOrDefault(m.ReplayHomeSide!);
            var awayFaints = faints.GetValueOrDefault(m.ReplayHomeSide == "p1" ? "p2" : "p1");
            AddGame(m.HomeTeamId, awayFaints, homeFaints);
            AddGame(m.AwayTeamId, homeFaints, awayFaints);
        }

        return teams
            .Select(t =>
            {
                var ko = koByTeam.GetValueOrDefault(t.Id);
                return new Row(t.Id, t.Wins, t.Losses, t.Draws, t.Wins - t.Losses,
                    ko.OppFaints - ko.Faints, killsByTeam.GetValueOrDefault(t.Id), ko.Faints);
            })
            .OrderByDescending(r => r.RecordDiff)
            .ThenByDescending(r => r.KoDiff)
            .ThenByDescending(r => r.Wins)
            .ThenByDescending(r => r.TotalKos)
            .ThenBy(r => r.TeamId)
            .ToList();
    }
}
