using System.Text.Json;
using DraftLeague.Web.Api;
using DraftLeague.Web.Data;
using DraftLeague.Web.Hubs;
using DraftLeague.Web.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// Development-only END-TO-END season generator that drives the SAME code a live
/// league does and inserts nothing by hand. It readies synthetic coaches up as real
/// <see cref="DraftParticipant"/>s, starts the draft with <see cref="DraftEngine"/>,
/// then plays it out through the engine turn by turn: each turn it "presses" a random
/// tier the team owes (<see cref="DraftEngine.OfferOptionsAsync"/>) and randomly either
/// picks one of the offered options (<see cref="DraftEngine.MakePickAsync"/>) or skips
/// (<see cref="DraftEngine.SkipPickAsync"/>). So every pick, skip and passed-option
/// snapshot is produced by the engine, not fabricated. The only random data it
/// invents is the choices themselves (which tier, pick vs skip, which option) and the
/// Showdown battle teams; every match is a real headless battle whose log is recorded
/// through the built-in report pipeline (<see cref="ReplayScorer"/> +
/// <see cref="MatchReporting"/>) that maps a game to its scheduled match. Without
/// battles the schedule just stays Pending.
///
/// The counterpart to <see cref="SeasonSimulator"/> (which imports real Showdown
/// replays). Use it to exercise the schedule / standings / stats / team pages end to
/// end.
/// </summary>
public class RandomSeasonSimulator(
    AppDbContext db, DraftEngine engine, NodeBattleSimulator battles, MatchStatsRecorder recorder,
    ReplayScorer scorer, IHubContext<DraftHub> hub, ILogger<RandomSeasonSimulator> log)
{
    public record SimResult(int Teams, int Picks, int Matches, bool RealBattles, int Skips = 0);

    // Per-turn random choices (percent). A coach either "lapses" (their clock runs
    // out after opening a tier -> the engine auto-skips if they hold a skip, else
    // auto-picks), voluntarily skips (presses skip), or picks. The rest are picks.
    private const int AutoLapsePercent = 10;
    private const int VoluntarySkipPercent = 15;

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
        var quota = tierRules.ToDictionary(r => r.Tier, r => r.SlotsPerTeam);

        // Clamp the team count to what the scarcest tier can fill a full roster of.
        var poolByTier = (await db.Pokemon.Where(p => p.LeagueId == leagueId)
                .Select(p => p.Tier).ToListAsync(ct))
            .GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        var maxTeams = tierRules.Where(r => r.SlotsPerTeam > 0)
            .Select(r => (poolByTier.GetValueOrDefault(r.Tier)) / r.SlotsPerTeam)
            .DefaultIfEmpty(0).Min();
        teamCount = Math.Clamp(teamCount, 2, Math.Max(2, maxTeams));

        // ── reset to a clean, NOT-started draft (cleanup only, nothing invented) ──
        await db.Matches.Where(m => m.LeagueId == leagueId).ExecuteDeleteAsync(ct);
        await db.PokemonStats.Where(s => s.Pick.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.Picks.Where(p => p.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.DraftSkips.Where(s => s.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.DraftSlots.Where(s => s.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.OfferedOptions.Where(o => o.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.DraftParticipants.Where(p => p.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.Pokemon.Where(p => p.LeagueId == leagueId && p.DraftedByTeamId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DraftedByTeamId, (int?)null), ct);
        await db.Teams.Where(t => t.LeagueId == leagueId).ExecuteDeleteAsync(ct);
        draft.State = DraftState.NotStarted;
        draft.CurrentIndex = 0;
        draft.PickDeadline = null;
        await db.SaveChangesAsync(ct);

        // ── coaches: real signed-in members first, then synthetic sim coaches, all
        // readied up through the real participant mechanism (no bespoke insertion —
        // the same DraftParticipant rows a coach's "ready up" makes). The reserved
        // admin oversees and never plays.
        var realUsers = (await db.Users.ToListAsync(ct))
            .Where(u => u.DiscordId.Length > 0 && u.DiscordId.All(char.IsDigit)
                        && u.DiscordId != AuthApi.AdminDiscordId)
            .OrderBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var coachIds = new List<string>();
        for (var i = 0; i < teamCount; i++)
        {
            if (i < realUsers.Count) { coachIds.Add(realUsers[i].DiscordId); continue; }
            var n = i - realUsers.Count + 1;
            var id = $"sim-{n}";
            var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == id, ct);
            if (user is null) db.Users.Add(new User { DiscordId = id, Username = $"Sim Coach {n}" });
            else user.Username = $"Sim Coach {n}";
            coachIds.Add(id);
        }
        await db.SaveChangesAsync(ct);
        foreach (var id in coachIds)
            db.DraftParticipants.Add(new DraftParticipant { DraftId = draftId, DiscordId = id });
        await db.SaveChangesAsync(ct);

        // ── start the draft + lay down the schedule with the REAL functions, exactly
        // as the admin's Start does (teams are created here, from the participants). ──
        var start = await engine.StartAsync(draftId, ct);
        if (!start.Ok) throw new InvalidOperationException($"Sim could not start the draft: {start.Error}");
        await ScheduleApi.GenerateAsync(db, leagueId, league.SeasonWeeks, ct);

        // ── drive the draft through the REAL engine: on each turn "press" a random
        // tier the team still owes, then randomly lapse / skip / pick. The engine
        // records the picks, the skips and every passed option — nothing is written
        // by hand here. ──
        var maxSteps = teamCount * (quota.Values.Sum() + DraftEngine.MaxSkipsPerTeam) + teamCount + 10;
        for (var step = 0; step < maxSteps; step++)
        {
            var st = await db.Drafts.AsNoTracking()
                .Where(d => d.Id == draftId)
                .Select(d => new { d.State, d.CurrentIndex }).FirstAsync(ct);
            if (st.State != DraftState.Running) break;

            var onClock = await db.DraftSlots.AsNoTracking()
                .Where(s => s.DraftId == draftId && s.Position == st.CurrentIndex)
                .Select(s => (int?)s.TeamId).FirstOrDefaultAsync(ct);
            if (onClock is null) break;

            // Tiers this team still owes: quota minus what it's already drafted.
            var used = (await db.Picks.AsNoTracking()
                    .Where(p => p.DraftId == draftId && p.TeamId == onClock)
                    .Select(p => p.Tier).ToListAsync(ct))
                .GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
            var owed = quota.Where(kv => kv.Value - used.GetValueOrDefault(kv.Key) > 0)
                .Select(kv => kv.Key).ToList();
            if (owed.Count == 0) { await engine.AutoPickAsync(draftId, ct, preferSkip: false); continue; }

            // "Press" a random owed tier.
            var tier = owed[rng.Next(owed.Count)];
            var offer = await engine.OfferOptionsAsync(draftId, onClock.Value, tier, ct);
            if (!offer.Ok) { await engine.AutoPickAsync(draftId, ct, preferSkip: false); continue; }

            var skipsLeft = await db.Teams.AsNoTracking()
                .Where(t => t.Id == onClock).Select(t => t.SkipsRemaining).FirstAsync(ct);
            var roll = rng.Next(100);

            // A modelled clock-lapse after opening the tier: AutoPickAsync (the same
            // call the live clock makes) auto-skips if the team still holds a skip,
            // else auto-picks. Either way the skip/pick carries the offered set.
            if (roll < AutoLapsePercent)
            {
                await engine.AutoPickAsync(draftId, ct, preferSkip: true);
                continue;
            }
            // Voluntary skip: "press" the skip button after the tier's open, so the
            // engine's skip carries the full offered set.
            if (skipsLeft > 0 && roll < AutoLapsePercent + VoluntarySkipPercent)
            {
                await engine.SkipPickAsync(draftId, onClock.Value, ct);
                continue;
            }
            // Otherwise pick a random offered option.
            var offered = await db.OfferedOptions.AsNoTracking()
                .Where(o => o.DraftId == draftId).Select(o => o.PokemonEntryId).ToListAsync(ct);
            if (offered.Count == 0) { await engine.AutoPickAsync(draftId, ct, preferSkip: false); continue; }
            if (!(await engine.MakePickAsync(draftId, onClock.Value, offered[rng.Next(offered.Count)], ct)).Ok)
                await engine.AutoPickAsync(draftId, ct, preferSkip: false);
        }

        // ── battles: real headless Showdown games off random teams (the one allowed
        // fabrication), recorded through the built-in "detect the game, put it in the
        // schedule" pipeline. Without battles the schedule simply stays Pending. ──
        var rosterByTeam = (await db.Picks.Include(p => p.PokemonEntry).Where(p => p.DraftId == draftId).ToListAsync(ct))
            .GroupBy(p => p.TeamId).ToDictionary(g => g.Key, g => g.ToList());
        var matches = await db.Matches
            .Include(m => m.HomeTeam).Include(m => m.AwayTeam)
            .Where(m => m.LeagueId == leagueId).ToListAsync(ct);
        var teamsCount = await db.Teams.CountAsync(t => t.LeagueId == leagueId, ct);
        // Count from the DB, so auto-picks/auto-skips the engine made are included too.
        var pickCount = await db.Picks.CountAsync(p => p.DraftId == draftId, ct);
        var skipCount = await db.DraftSkips.CountAsync(s => s.DraftId == draftId, ct);

        var real = realBattles && await RunRealBattlesAsync(matches, rosterByTeam, ct);

        log.LogInformation("Random season for league {League}: {Teams} teams, {Picks} picks, {Skips} skips, {Matches} matches (real battles: {Real})",
            leagueId, teamsCount, pickCount, skipCount, matches.Count, real);
        return new SimResult(teamsCount, pickCount, matches.Count, real, skipCount);
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
        // C-tier picks carry the Tera type the draft rolled them; the runner teras
        // them ASAP. Non-C picks have no Tera type and never tera.
        static NodeBattleSimulator.TeamMon MonOf(Pick p) => new(SlugOf(p), p.Tier == Tier.C ? p.TeraType : null);
        var specs = matches.Select(m => new NodeBattleSimulator.MatchSpec(
            m.HomeTeam.CoachName, m.AwayTeam.CoachName,   // the players' Discord / dummy names
            rosterByTeam[m.HomeTeamId].Select(MonOf).ToList(),
            rosterByTeam[m.AwayTeamId].Select(MonOf).ToList())).ToList();

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
            await MatchReporting.RecordReportAsync(db, recorder, hub, match, report, replayUrl: null, reporterId: null, ct,
                p1Export: br.P1Export, p2Export: br.P2Export);
            recorded++;
        }

        log.LogInformation("Recorded {Recorded}/{Total} sim battles through the report pipeline", recorded, matches.Count);
        return recorded > 0;
    }

}
