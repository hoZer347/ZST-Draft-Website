using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// The single-elimination playoff bracket. Once every regular-season game is played,
/// the top <see cref="Qualifiers"/> teams by <see cref="Standings"/> are seeded; the
/// bracket is padded to a power of two and the top seeds take the byes. Every pairing
/// is a best-of-three series. Each of its games is an ordinary <see cref="Match"/> tagged
/// with its round and slot, created one at a time while the series is undecided, so it
/// is reported and scored like any other game. A drawn game counts for neither team.
/// </summary>
public static class Playoffs
{
    public const int Qualifiers = 12;
    public const int WinsNeeded = 2;
    public const string LockedError = "The next round has been played, so this result stands.";
    private const int DaysPerRound = 7;

    // Two results reported together would otherwise both see an unseeded league and
    // each create round 1.
    private static readonly SemaphoreSlim SyncLock = new(1, 1);

    public record Entrant(int Seed, int TeamId);

    /// <summary>One bracket position: the series between these two teams. <see cref="Games"/>
    /// are its games in order, the next unplayed one last.</summary>
    public record Slot(int Round, int Index, Entrant? Home, Entrant? Away, bool Bye,
        IReadOnlyList<Match> Games, int HomeWins, int AwayWins, Entrant? Winner);

    public static int BracketSize(int entrants)
    {
        var size = 1;
        while (size < entrants) size *= 2;
        return size;
    }

    public static int RoundCount(int entrants) => entrants < 2 ? 0 : (int)Math.Log2(BracketSize(entrants));

    public static async Task<int> RoundCountAsync(AppDbContext db, int leagueId, CancellationToken ct) =>
        RoundCount(await db.Teams.CountAsync(t => t.LeagueId == leagueId && t.PlayoffSeed != null, ct));

    public static string RoundName(int round, int rounds) => (rounds - round) switch
    {
        0 => "Final",
        1 => "Semifinals",
        2 => "Quarterfinals",
        _ => $"Round {round}",
    };

    /// <summary>Seeds in first-round bracket order (1, 16, 8, 9, 4, 13, ...), so the top
    /// two seeds can only meet in the final.</summary>
    public static int[] SeedOrder(int size)
    {
        var order = new List<int> { 1 };
        while (order.Count < size)
        {
            var mirror = order.Count * 2 + 1;
            order = order.SelectMany(s => new[] { s, mirror - s }).ToList();
        }
        return [.. order];
    }

    /// <summary>
    /// Lays out the bracket for <paramref name="seeded"/> (team ids, top seed first) and
    /// the playoff games recorded so far. A game only counts toward a series when it is
    /// between that slot's two teams, so a re-reported earlier round can't leave a stale
    /// winner standing.
    /// </summary>
    public static List<List<Slot>> Build(IReadOnlyList<int> seeded, IEnumerable<Match> games)
    {
        var rounds = new List<List<Slot>>();
        if (seeded.Count < 2) return rounds;

        var byPos = ByPosition(games);
        var feeders = SeedOrder(BracketSize(seeded.Count))
            .Select(s => s <= seeded.Count ? new Entrant(s, seeded[s - 1]) : null)
            .ToList();

        for (var round = 1; feeders.Count > 1; round++)
        {
            var slots = new List<Slot>();
            var winners = new List<Entrant?>();
            for (var i = 0; i < feeders.Count / 2; i++)
            {
                var (home, away) = (feeders[2 * i], feeders[2 * i + 1]);
                if (home is null || (away is not null && away.Seed < home.Seed)) (home, away) = (away, home);

                // Byes only exist in the first round: there a missing entrant is a seed
                // past the field, later it is a game still to be decided.
                var bye = round == 1 && away is null;
                List<Match> series = bye || home is null || away is null
                    ? []
                    : byPos.GetValueOrDefault((round, i), [])
                        .Where(g => g.HomeTeamId == home.TeamId && g.AwayTeamId == away.TeamId).ToList();
                var homeWins = series.Count(g => g.Result == MatchResult.HomeWin);
                var awayWins = series.Count(g => g.Result == MatchResult.AwayWin);
                var winner = bye ? home
                    : homeWins >= WinsNeeded ? home
                    : awayWins >= WinsNeeded ? away
                    : null;

                slots.Add(new Slot(round, i, home, away, bye, series, homeWins, awayWins, winner));
                winners.Add(winner);
            }
            rounds.Add(slots);
            feeders = winners;
        }
        return rounds;
    }

    /// <summary>
    /// Brings a league's playoffs up to date: seeds the bracket once the regular season
    /// is complete and keeps exactly one unplayed game in every undecided series whose
    /// two teams are known, re-pointed at those teams. Unplayed games of a decided or
    /// no longer ready series are dropped. A played game is never touched. Seeds stay
    /// frozen once set. Idempotent; saves only when something changed.
    /// </summary>
    /// <returns>Whether anything changed.</returns>
    public static async Task<bool> SyncAsync(AppDbContext db, int leagueId, CancellationToken ct = default)
    {
        await SyncLock.WaitAsync(ct);
        try
        {
            return await SyncLockedAsync(db, leagueId, ct);
        }
        finally
        {
            SyncLock.Release();
        }
    }

    /// <summary>
    /// Whether <paramref name="match"/> is a decided playoff game whose winner has since
    /// played in a later round. Its result then stands: changing it would leave that
    /// later game between the wrong teams.
    /// </summary>
    public static Task<bool> IsLockedAsync(AppDbContext db, Match match, CancellationToken ct) =>
        match.PlayoffRound is not int round || match.Result == MatchResult.Pending
            ? Task.FromResult(false)
            : db.Matches.AnyAsync(m => m.LeagueId == match.LeagueId && m.PlayoffRound > round
                && m.Result != MatchResult.Pending
                && (m.HomeTeamId == match.HomeTeamId || m.AwayTeamId == match.HomeTeamId
                    || m.HomeTeamId == match.AwayTeamId || m.AwayTeamId == match.AwayTeamId), ct);

    private static async Task<bool> SyncLockedAsync(AppDbContext db, int leagueId, CancellationToken ct)
    {
        var teams = await db.Teams.Where(t => t.LeagueId == leagueId).ToListAsync(ct);
        var regular = await db.Matches
            .Where(m => m.LeagueId == leagueId && m.PlayoffRound == null)
            .Select(m => new { m.Week, m.ScheduledFor, m.Result })
            .ToListAsync(ct);

        if (!teams.Any(t => t.PlayoffSeed is not null))
        {
            if (teams.Count < 2 || regular.Count == 0 || regular.Any(m => m.Result == MatchResult.Pending)) return false;
            var order = await Standings.ComputeAsync(db, leagueId, ct);
            var byId = teams.ToDictionary(t => t.Id);
            for (var i = 0; i < Math.Min(Qualifiers, order.Count); i++) byId[order[i].TeamId].PlayoffSeed = i + 1;
        }

        var seeded = teams.Where(t => t.PlayoffSeed is not null).OrderBy(t => t.PlayoffSeed).Select(t => t.Id).ToList();
        var games = await db.Matches.Where(m => m.LeagueId == leagueId && m.PlayoffRound != null).ToListAsync(ct);
        var byPos = ByPosition(games);

        var last = regular.OrderByDescending(m => m.Week).FirstOrDefault();
        foreach (var slot in Build(seeded, games).SelectMany(r => r))
        {
            if (slot.Bye) continue;
            var unplayed = byPos.GetValueOrDefault((slot.Round, slot.Index), [])
                .Where(g => g.Result == MatchResult.Pending).ToList();
            if (slot.Home is null || slot.Away is null || slot.Winner is not null)
            {
                db.Matches.RemoveRange(unplayed);
                continue;
            }
            if (unplayed.Count == 0)
            {
                db.Matches.Add(new Match
                {
                    LeagueId = leagueId,
                    Week = (last?.Week ?? 0) + slot.Round,
                    PlayoffRound = slot.Round,
                    PlayoffSlot = slot.Index,
                    HomeTeamId = slot.Home.TeamId,
                    AwayTeamId = slot.Away.TeamId,
                    ScheduledFor = last?.ScheduledFor?.AddDays(DaysPerRound * slot.Round),
                });
                continue;
            }
            unplayed[0].HomeTeamId = slot.Home.TeamId;
            unplayed[0].AwayTeamId = slot.Away.TeamId;
            db.Matches.RemoveRange(unplayed.Skip(1));
        }

        if (!db.ChangeTracker.HasChanges()) return false;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>A position's games in the order they were created, which is the order
    /// they are played in.</summary>
    private static Dictionary<(int Round, int Slot), List<Match>> ByPosition(IEnumerable<Match> games) => games
        .Where(g => g.PlayoffRound is not null && g.PlayoffSlot is not null)
        .GroupBy(g => (g.PlayoffRound!.Value, g.PlayoffSlot!.Value))
        .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Id).ToList());

    /// <summary>Each playoff game's number within its series (1 = first), by match id.</summary>
    public static Dictionary<int, int> GameNumbers(IEnumerable<Match> games) => games
        .Where(g => g.PlayoffRound is not null)
        .GroupBy(g => (g.PlayoffRound, g.PlayoffSlot, g.HomeTeamId, g.AwayTeamId))
        .SelectMany(g => g.OrderBy(m => m.Id).Select((m, i) => (m.Id, Number: i + 1)))
        .ToDictionary(x => x.Id, x => x.Number);
}
