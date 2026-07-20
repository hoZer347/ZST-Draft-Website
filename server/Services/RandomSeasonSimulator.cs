using System.Text.Json;
using DraftLeague.Web.Api;
using DraftLeague.Web.Data;
using DraftLeague.Web.Hubs;
using DraftLeague.Web.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// Development-only END-TO-END season generator. It sets up the scaffolding a real
/// league would have — synthetic teams and a random-but-valid snake draft off the
/// REAL pool — and then, for the results, drives the SAME components a live season
/// uses and NOTHING ELSE: every match is a real headless Showdown battle, and each
/// battle's log is fed through <see cref="ReplayScorer"/> and
/// <see cref="MatchReporting"/> exactly as our Showdown server's auto-report does.
/// So no result, score, standing or stat is invented here — all of them are derived
/// from the real logs by the real recording code. Without battles (or if the runner
/// is unavailable) the schedule simply stays Pending: still nothing made up.
///
/// The counterpart to <see cref="SeasonSimulator"/> (which imports real Showdown
/// replays). Use it to exercise the schedule / standings / stats / team pages end to
/// end.
/// </summary>
public class RandomSeasonSimulator(
    AppDbContext db, NodeBattleSimulator battles, MatchStatsRecorder recorder,
    ReplayScorer scorer, IHubContext<DraftHub> hub, ILogger<RandomSeasonSimulator> log)
{
    public record SimResult(int Teams, int Picks, int Matches, bool RealBattles);

    /// <param name="teamCount">How many synthetic teams to draft (clamped to what the pool can fill).</param>
    /// <param name="seed">Fixed seed for a reproducible season; null for a fresh random one.</param>
    /// <param name="realBattles">When true, play each match as a real headless Showdown battle
    /// (random teams, random moves) and record its result + scraped stats through the real
    /// report pipeline. When false (or the runner is unavailable), the schedule stays Pending —
    /// no result, no score, no stats. Nothing is ever fabricated either way.</param>
    public async Task<SimResult> SimulateAsync(
        int draftId, int teamCount = 8, int? seed = null, bool realBattles = true, CancellationToken ct = default)
    {
        var rng = seed is null ? new Random() : new Random(seed.Value);

        var draft = await db.Drafts.Include(d => d.League).ThenInclude(l => l.TierRules)
                        .FirstOrDefaultAsync(d => d.Id == draftId, ct)
                    ?? throw new InvalidOperationException("Draft not found");
        var league = draft.League;
        var leagueId = league.Id;
        var tierRules = league.TierRules.OrderBy(r => r.Tier).ToList();
        if (tierRules.Count == 0) throw new InvalidOperationException("League has no tier rules");

        // ── wipe the league back to a blank slate (same order as SeasonSimulator) ──
        await db.Matches.Where(m => m.LeagueId == leagueId).ExecuteDeleteAsync(ct);
        await db.PokemonStats.Where(s => s.Pick.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.Picks.Where(p => p.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.DraftSlots.Where(s => s.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.DraftParticipants.Where(p => p.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.Pokemon.Where(p => p.LeagueId == leagueId && p.DraftedByTeamId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DraftedByTeamId, (int?)null), ct);
        await db.Teams.Where(t => t.LeagueId == leagueId).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);

        // ── pool grouped by tier, shuffled into draw stacks ────────────────
        var pool = await db.Pokemon.Where(p => p.LeagueId == leagueId).ToListAsync(ct);
        var byTier = pool.GroupBy(p => p.Tier)
            .ToDictionary(g => g.Key, g => new Stack<PokemonEntry>(g.OrderBy(_ => rng.Next())));

        // Clamp the team count to what the scarcest tier can fill a full roster of.
        var maxTeams = tierRules.Where(r => r.SlotsPerTeam > 0)
            .Select(r => (byTier.TryGetValue(r.Tier, out var s) ? s.Count : 0) / r.SlotsPerTeam)
            .DefaultIfEmpty(0).Min();
        teamCount = Math.Clamp(teamCount, 2, Math.Max(2, maxTeams));

        // ── teams ──────────────────────────────────────────────────────────
        // Real signed-in Discord accounts (numeric snowflake ids) get the first
        // teams, so a logged-in member sees a sim team as their own; any remaining
        // teams are filled with synthetic sim coaches. The reserved admin oversees
        // and never plays.
        var realUsers = (await db.Users.ToListAsync(ct))
            .Where(u => u.DiscordId.Length > 0 && u.DiscordId.All(char.IsDigit)
                        && u.DiscordId != AuthApi.AdminDiscordId)
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var teams = new List<Team>();
        for (var i = 0; i < teamCount; i++)
        {
            string coachId, coachName;
            if (i < realUsers.Count)
            {
                coachId = realUsers[i].DiscordId;   // a real member owns this team
                coachName = realUsers[i].Username;
            }
            else
            {
                var n = i - realUsers.Count + 1;
                coachId = $"sim-{n}";
                coachName = $"Sim Coach {n}";
                var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == coachId, ct);
                if (user is null) db.Users.Add(new User { DiscordId = coachId, Username = coachName });
                else user.Username = coachName;
            }
            // No separate team names — a team is shown by its coach's username / dummy name.
            var team = new Team { LeagueId = leagueId, Name = coachName, CoachId = coachId, CoachName = coachName };
            db.Teams.Add(team);
            teams.Add(team);
        }
        await db.SaveChangesAsync(ct); // assign team + user ids

        // ── a valid snake draft, but with each pick's TIER randomised ──────
        // Instead of everyone taking S, then A, then B, then C in lockstep, each
        // team draws a random tier it still owes on its turn — so the pick order
        // interleaves tiers like a real draft, while every team still ends with
        // its exact per-tier quota (S1/A2/B3/C4).
        var offeredByTier = tierRules.ToDictionary(r => r.Tier, r => r.OptionsOffered);
        var quota = tierRules.ToDictionary(r => r.Tier, r => r.SlotsPerTeam);
        var owed = teams.ToDictionary(t => t.Id, _ => new Dictionary<Tier, int>(quota));
        var rosterSize = quota.Values.Sum();
        int pickNo = 1, pos = 0;
        for (var round = 0; round < rosterSize; round++)
        {
            var seq = round % 2 == 0 ? teams : Enumerable.Reverse(teams).ToList();
            foreach (var team in seq)
            {
                var owedTiers = owed[team.Id].Where(kv => kv.Value > 0).Select(kv => kv.Key).ToList();
                var tier = owedTiers[rng.Next(owedTiers.Count)]; // a random tier this team still owes
                owed[team.Id][tier]--;

                var mon = byTier[tier].Pop();
                mon.DraftedByTeam = team;
                var teraType = tier == Tier.C ? RandomTera(rng) : null;
                db.Picks.Add(new Pick
                {
                    DraftId = draftId, PickNumber = pickNo++, TeamId = team.Id,
                    PokemonEntry = mon, Tier = tier, TeraType = teraType,
                    // The "passed" run: a random sample of the same tier's still-
                    // available mons, snapshot in the exact shape the draft engine
                    // stores (so the pick feed renders it identically).
                    OtherOptions = PassedOptions(byTier[tier], tier, offeredByTier[tier], rng),
                });
                db.DraftSlots.Add(new DraftSlot { DraftId = draftId, Position = pos++, TeamId = team.Id });
            }
        }
        draft.CurrentIndex = pos;
        draft.State = DraftState.Complete;
        draft.PickDeadline = null;
        await db.SaveChangesAsync(ct);

        // ── schedule: reuse the tested round-robin (Pending matches) ───────
        await ScheduleApi.GenerateAsync(db, leagueId, league.SeasonWeeks, ct);

        var rosterByTeam = (await db.Picks.Include(p => p.PokemonEntry).Where(p => p.DraftId == draftId).ToListAsync(ct))
            .GroupBy(p => p.TeamId).ToDictionary(g => g.Key, g => g.ToList());
        var matches = await db.Matches
            .Include(m => m.HomeTeam).Include(m => m.AwayTeam)
            .Where(m => m.LeagueId == leagueId).ToListAsync(ct);

        // ── results + stats come ONLY from real battles ────────────────────
        // Every number (score included) is drawn from a real battle log. Without
        // battles — the checkbox unchecked, or the runner unavailable — the matches
        // stay Pending: no score, no stats, ready for replays to be added later.
        var real = realBattles && await RunRealBattlesAsync(matches, rosterByTeam, ct);

        log.LogInformation("Random season for league {League}: {Teams} teams, {Picks} picks, {Matches} matches (real battles: {Real})",
            leagueId, teams.Count, pickNo - 1, matches.Count, real);
        return new SimResult(teams.Count, pickNo - 1, matches.Count, real);
    }

    // The pool slug the battle runner keys on — the Showdown sprite slug, falling
    // back to the name for the odd mon without one (Node skips anything Showdown
    // doesn't know).
    private static string SlugOf(Pick p) =>
        !string.IsNullOrWhiteSpace(p.PokemonEntry.Sprite) && !p.PokemonEntry.Sprite.StartsWith("http")
            ? p.PokemonEntry.Sprite
            : p.PokemonEntry.Name;

    /// <summary>
    /// Plays every match as a real headless Showdown battle, then records each battle
    /// through the EXACT live pipeline — <see cref="ReplayScorer.ReportAsync"/> maps the
    /// log to its scheduled match and scores it, and <see cref="MatchReporting"/> writes
    /// the result, standings, stats and replay link — identical to what our Showdown
    /// server's auto-report does for a real game. Nothing is computed here; the sim only
    /// hands the real logs to the real recorder. Returns false (leaving the matches
    /// Pending) if the battle runner isn't available.
    /// </summary>
    private async Task<bool> RunRealBattlesAsync(List<Match> matches, Dictionary<int, List<Pick>> rosterByTeam, CancellationToken ct)
    {
        var specs = matches.Select(m => new NodeBattleSimulator.MatchSpec(
            m.HomeTeam.CoachName, m.AwayTeam.CoachName,   // the players' Discord / dummy names
            rosterByTeam[m.HomeTeamId].Select(SlugOf).ToList(),
            rosterByTeam[m.AwayTeamId].Select(SlugOf).ToList())).ToList();

        var results = await battles.RunAsync(specs, ct);
        if (results is null) return false;

        // Feed each finished battle's log through the real report pipeline, exactly as
        // POST /api/showdown/report would. ReportAsync identifies which scheduled match
        // the log belongs to (by mapping revealed species → drafted team) and scores it;
        // MatchReporting records result/standings/stats/replay link. Save is inside
        // RecordReportAsync, so each call sees the running totals the last one wrote.
        var recorded = 0;
        foreach (var br in results)
        {
            var report = await scorer.ReportAsync(br.Log, ct);
            if (!report.Ok)
            {
                // A log the scorer couldn't attribute to a pending match leaves that
                // game unrecorded — surfaced, not papered over (the live path drops it too).
                log.LogWarning("Sim battle not recorded: {Reason}", report.Reason);
                continue;
            }
            var match = await db.Matches
                .Include(m => m.HomeTeam).Include(m => m.AwayTeam)
                .FirstAsync(m => m.Id == report.MatchId, ct);
            await MatchReporting.RecordReportAsync(db, recorder, hub, match, report, replayUrl: null, reporterId: null, ct);
            recorded++;
        }

        log.LogInformation("Recorded {Recorded}/{Total} sim battles through the report pipeline", recorded, matches.Count);
        return recorded > 0;
    }

    // The options offered-but-not-taken this turn, in the draft engine's exact
    // JSON shape ([{name,sprite,dexNumber,tier}]), sampled from the same tier's
    // still-available mons. Null when nothing is left to offer.
    private static string? PassedOptions(IEnumerable<PokemonEntry> remaining, Tier tier, int offered, Random rng)
    {
        var passed = remaining.OrderBy(_ => rng.Next()).Take(Math.Max(0, offered - 1))
            .Select(m => new { name = m.Name, sprite = m.Sprite, dexNumber = m.DexNumber, tier = tier.ToString() })
            .ToList();
        return passed.Count > 0 ? JsonSerializer.Serialize(passed) : null;
    }

    private static readonly string[] TeraTypes =
        ["Normal", "Fire", "Water", "Electric", "Grass", "Ice", "Fighting", "Poison", "Ground",
         "Flying", "Psychic", "Bug", "Rock", "Ghost", "Dragon", "Dark", "Steel", "Fairy", "Stellar"];

    private static string RandomTera(Random rng) => TeraTypes[rng.Next(TeraTypes.Length)];
}
