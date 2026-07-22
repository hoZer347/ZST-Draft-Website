using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// Folds one scored replay's per-mon stats into the two teams' running totals,
/// the same PokemonStat rows the stats page reads. This is what turns a real,
/// coach-submitted replay into stats; the dev season simulator has its own copy
/// of this accumulation for its bulk import.
///
/// <see cref="ApplyAsync"/> is signed: +1 records a newly reported game, −1 backs
/// a previous one out, so a corrected re-report can subtract the old replay before
/// adding the new one. Reversal mirrors the standings back-out in ScheduleApi.
/// </summary>
public class MatchStatsRecorder(AppDbContext db, ILogger<MatchStatsRecorder> log)
{
    /// <param name="match">Loaded with HomeTeam and AwayTeam tracked, their Presence denominators are adjusted.</param>
    /// <param name="homeSide">Which Showdown side ("p1"/"p2") played the home team, from the scorer.</param>
    /// <param name="battleLog">The raw replay log.</param>
    /// <param name="result">The match outcome, for per-mon W/L.</param>
    /// <param name="sign">+1 to record the game, −1 to back it out.</param>
    public async Task ApplyAsync(Match match, string homeSide, string battleLog,
        MatchResult result, int sign, CancellationToken ct = default)
    {
        var homeId = match.HomeTeamId;
        var awayId = match.AwayTeamId;

        // Base-species → pick, per team, so the replay's "Salamence" resolves to a
        // pick drafted as "M-Salamence" (same base) on the right side.
        var picks = await db.Picks
            .Include(p => p.PokemonEntry)
            .Where(p => p.TeamId == homeId || p.TeamId == awayId)
            .ToListAsync(ct);

        var byTeamBase = new Dictionary<int, Dictionary<string, Pick>>();
        foreach (var p in picks)
        {
            if (!byTeamBase.TryGetValue(p.TeamId, out var map))
                byTeamBase[p.TeamId] = map = new Dictionary<string, Pick>();
            map[ReplayStatsScraper.BaseId(p.PokemonEntry.Name)] = p;
            if (!string.IsNullOrEmpty(p.PokemonEntry.Sprite))
                map[ReplayStatsScraper.BaseId(p.PokemonEntry.Sprite)] = p;
        }

        var scraped = ReplayStatsScraper.Scrape(battleLog, (side, species) =>
        {
            var teamId = side == homeSide ? homeId : awayId;
            return byTeamBase.TryGetValue(teamId, out var map)
                ? ReplayStatsScraper.ResolveInMap(map, species) : null;
        });

        if (scraped.Stats.Count == 0)
        {
            log.LogWarning("Stats recorder: no mons resolved for match {Match}; nothing recorded", match.Id);
            return;
        }

        // Season-presence denominator: the game's turn count (one field slot per
        // turn). A team's mons sum to 100% in singles (one up per turn) and 200% in
        // doubles (two up), since each active mon contributes a full turn to the
        // numerator while the denominator counts the turn once.
        match.HomeTeam.BattleTurns += scraped.Turns * sign;
        match.AwayTeam.BattleTurns += scraped.Turns * sign;

        // Existing rows for the picks in this game, so accumulation survives across
        // games and reversal finds what to subtract.
        var pickIds = scraped.Stats.Keys.Select(p => p.Id).ToList();
        var rows = await db.PokemonStats
            .Where(s => pickIds.Contains(s.PickId))
            .ToDictionaryAsync(s => s.PickId, ct);

        foreach (var (pick, gs) in scraped.Stats)
        {
            if (!rows.TryGetValue(pick.Id, out var st))
            {
                if (sign < 0) continue; // nothing to back out
                st = new PokemonStat { PickId = pick.Id };
                db.PokemonStats.Add(st);
                rows[pick.Id] = st;
            }

            st.GamesPlayed += sign;
            st.Starts += (gs.Started ? 1 : 0) * sign;    // led (thrown out first) this game
            st.Finishes += (gs.Finished ? 1 : 0) * sign; // still standing at the end this game
            st.Kills += gs.Kills * sign;
            st.Deaths += gs.Deaths * sign;
            st.AlliesKoed += gs.AlliesKoed * sign;
            st.SelfKos += gs.SelfKos * sign;
            st.Crits += gs.Crits * sign;
            st.ActiveTurns += gs.ActiveTurns * sign;
            st.PlayedTurns += scraped.Turns * sign; // usage-presence denominator
            st.DamageDealtDirect += gs.DealtDirect * sign;
            st.DamageDealtIndirect += gs.DealtIndirect * sign;
            st.DamageDealtAllyDirect += gs.DealtAllyDirect * sign;
            st.DamageDealtAllyIndirect += gs.DealtAllyIndirect * sign;
            st.DamageTakenDirect += gs.TakenDirect * sign;
            st.DamageTakenIndirect += gs.TakenIndirect * sign;
            st.DamageTakenSelf += gs.TakenSelf * sign;
            st.HpRecovered += gs.Recovered * sign;
            st.HpHealed += gs.Healed * sign;
            st.HpHealedEnemy += gs.HealedEnemy * sign;

            if (result != MatchResult.Draw)
            {
                var teamWon = pick.TeamId == homeId
                    ? result == MatchResult.HomeWin
                    : result == MatchResult.AwayWin;
                if (teamWon) st.Wins += sign; else st.Losses += sign;
            }
        }
    }
}
