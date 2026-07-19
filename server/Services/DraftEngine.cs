using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

public record DraftActionResult(bool Ok, string? Error = null, Pick? Pick = null)
{
    public static DraftActionResult Fail(string error) => new(false, error);
    public static DraftActionResult Success(Pick? pick = null) => new(true, Pick: pick);
}

/// <summary>
/// Owns all mutations to a draft. Ported from the original Python
/// DraftSession, with two deliberate changes:
///
///   1. The database is the source of truth, not an in-memory dict. A server
///      restart mid-draft used to lose every tier count and offered option.
///   2. Mutations serialize on a per-draft lock. The Python version could
///      interleave a coach's pick with the timer's auto-pick and burn two
///      slots for one turn.
/// </summary>
public class DraftEngine(AppDbContext db, IDraftNotifier notifier, ILogger<DraftEngine> log)
{
    private static readonly SemaphoreSlim GlobalLock = new(1, 1);

    /// <summary>How many times a coach may skip (defer) a pick over the draft.</summary>
    public const int MaxSkipsPerTeam = 5;

    /// <summary>The 18 types plus Stellar — the pool a C-tier option's Tera type
    /// is rolled from.</summary>
    private static readonly string[] TeraTypes =
    [
        "Normal", "Fire", "Water", "Electric", "Grass", "Ice", "Fighting", "Poison", "Ground",
        "Flying", "Psychic", "Bug", "Rock", "Ghost", "Dragon", "Dark", "Steel", "Fairy", "Stellar",
    ];

    private static string RandomTera() => TeraTypes[Random.Shared.Next(TeraTypes.Length)];

    /// <summary>
    /// Offers a coach a random sample from a tier's remaining pool.
    ///
    /// Options are persisted, so calling this twice in one turn returns the
    /// same set. That is load-bearing: without it a coach can refresh until
    /// the sample contains something they like.
    /// </summary>
    public async Task<DraftActionResult> OfferOptionsAsync(int draftId, int teamId, Tier tier, CancellationToken ct = default)
    {
        await GlobalLock.WaitAsync(ct);
        try
        {
            var draft = await LoadAsync(draftId, ct);
            if (draft is null) return DraftActionResult.Fail("Draft not found");
            if (draft.State != DraftState.Running) return DraftActionResult.Fail("Draft is not running");

            var onClock = CurrentTeamId(draft);
            if (onClock != teamId) return DraftActionResult.Fail("Not your turn");

            if (RemainingSlots(draft, teamId, tier) <= 0)
                return DraftActionResult.Fail($"No {tier} tier slots remaining");

            // Already offered this turn — return the same set rather than reroll.
            if (draft.Offered.Count > 0)
            {
                if (draft.Offered[0].Tier == tier) return DraftActionResult.Success();
                return DraftActionResult.Fail($"You already opened the {draft.Offered[0].Tier} tier this turn");
            }

            // A team can't be offered a mon that shares a national dex number with
            // one it already holds — that would stack alternate forms of the same
            // species (megas, regional/gender forms). Dex numbers come straight
            // from the sheet; 0 means "unset" and is ignored so it can't block.
            var teamDex = await TeamDexNumbersAsync(teamId, ct);

            var pool = await db.Pokemon
                .Where(p => p.LeagueId == draft.LeagueId && p.Tier == tier && p.DraftedByTeamId == null
                            && !teamDex.Contains(p.DexNumber))
                .ToListAsync(ct);

            if (pool.Count == 0) return DraftActionResult.Fail($"No {tier} tier pokemon left");

            var count = draft.League.TierRules.FirstOrDefault(r => r.Tier == tier)?.OptionsOffered ?? 3;
            var sample = pool.OrderBy(_ => Random.Shared.Next()).Take(Math.Min(count, pool.Count));

            foreach (var p in sample)
                draft.Offered.Add(new OfferedOption
                {
                    DraftId = draft.Id,
                    PokemonEntryId = p.Id,
                    Tier = tier,
                    // C tier draws a Tera type; other tiers don't have one.
                    TeraType = tier == Tier.C ? RandomTera() : null,
                });

            await db.SaveChangesAsync(ct);
            await notifier.OptionsOfferedAsync(draft.Id, teamId, ct);
            return DraftActionResult.Success();
        }
        finally { GlobalLock.Release(); }
    }

    /// <summary>Confirms a pick from the options offered this turn.</summary>
    public async Task<DraftActionResult> MakePickAsync(int draftId, int teamId, int pokemonEntryId, CancellationToken ct = default)
    {
        await GlobalLock.WaitAsync(ct);
        try
        {
            var draft = await LoadAsync(draftId, ct);
            if (draft is null) return DraftActionResult.Fail("Draft not found");
            if (draft.State != DraftState.Running) return DraftActionResult.Fail("Draft is not running");
            if (CurrentTeamId(draft) != teamId) return DraftActionResult.Fail("Not your turn");

            var offered = draft.Offered.FirstOrDefault(o => o.PokemonEntryId == pokemonEntryId);
            if (offered is null) return DraftActionResult.Fail("That pokemon was not among your options");

            var pick = await CommitPickAsync(draft, teamId, offered.PokemonEntryId, offered.Tier, offered.TeraType, auto: false, ct);
            await notifier.PickMadeAsync(pick, ct);
            await AnnounceTurnAsync(draft, ct);
            return DraftActionResult.Success(pick);
        }
        finally { GlobalLock.Release(); }
    }

    /// <summary>
    /// The clock expired. Picks for the coach so the draft keeps moving.
    ///
    /// Walks tiers C -> B -> A -> S so an unattended coach loses their least
    /// valuable slot rather than their S pick.
    /// </summary>
    public async Task<DraftActionResult> AutoPickAsync(int draftId, CancellationToken ct = default)
    {
        await GlobalLock.WaitAsync(ct);
        try
        {
            var draft = await LoadAsync(draftId, ct);
            if (draft is null) return DraftActionResult.Fail("Draft not found");
            if (draft.State != DraftState.Running) return DraftActionResult.Fail("Draft is not running");

            var teamId = CurrentTeamId(draft);
            if (teamId is null) return DraftActionResult.Fail("Draft is complete");

            // If they already opened a tier, honour it — they were mid-decision.
            var tiers = draft.Offered.Count > 0
                ? [draft.Offered[0].Tier]
                : new[] { Tier.C, Tier.B, Tier.A, Tier.S };

            foreach (var tier in tiers)
            {
                if (RemainingSlots(draft, teamId.Value, tier) <= 0) continue;

                int? candidateId;
                string? teraType;
                if (draft.Offered.Count > 0)
                {
                    // Prefer something already in front of them over a fresh roll,
                    // carrying the Tera type it was offered with.
                    var opt = draft.Offered[Random.Shared.Next(draft.Offered.Count)];
                    candidateId = opt.PokemonEntryId;
                    teraType = opt.TeraType;
                }
                else
                {
                    // Same dex-number guard as a manual offer: never auto-pick a
                    // form of a species the team already holds.
                    var teamDex = await TeamDexNumbersAsync(teamId.Value, ct);

                    // Randomise client-side. Ordering by Guid.NewGuid() is a SQL
                    // Server idiom that EF cannot translate for SQLite, and it
                    // threw on every tick — silently wedging the whole clock.
                    var ids = await db.Pokemon
                        .Where(p => p.LeagueId == draft.LeagueId && p.Tier == tier && p.DraftedByTeamId == null
                                    && !teamDex.Contains(p.DexNumber))
                        .Select(p => p.Id)
                        .ToListAsync(ct);

                    candidateId = ids.Count == 0 ? null : ids[Random.Shared.Next(ids.Count)];
                    // A fresh C-tier pick still needs a Tera type rolled for it.
                    teraType = candidateId is not null && tier == Tier.C ? RandomTera() : null;
                }

                if (candidateId is null) continue;

                var pick = await CommitPickAsync(draft, teamId.Value, candidateId.Value, tier, teraType, auto: true, ct);
                log.LogInformation("Auto-picked {Pokemon} for team {Team} in draft {Draft}",
                    pick.PokemonEntryId, teamId, draftId);

                await notifier.AutoPickedAsync(pick, ct);
                await AnnounceTurnAsync(draft, ct);
                return DraftActionResult.Success(pick);
            }

            // Nothing eligible anywhere — skip rather than stall the draft.
            log.LogWarning("Skipping pick {Index} in draft {Draft}: no eligible pokemon for team {Team}",
                draft.CurrentIndex, draftId, teamId);

            await AdvanceAsync(draft, ct);
            await notifier.PickSkippedAsync(draft.Id, teamId.Value, ct);
            await AnnounceTurnAsync(draft, ct);
            return DraftActionResult.Success();
        }
        finally { GlobalLock.Release(); }
    }

    /// <summary>
    /// The coach on the clock defers their pick to a later cycle, spending one of
    /// their skip tokens. The turn advances; because Start lays down enough snake
    /// rounds to absorb every team's skips, the team comes back around with the
    /// slot still owed.
    /// </summary>
    public async Task<DraftActionResult> SkipPickAsync(int draftId, int teamId, CancellationToken ct = default)
    {
        await GlobalLock.WaitAsync(ct);
        try
        {
            var draft = await LoadAsync(draftId, ct);
            if (draft is null) return DraftActionResult.Fail("Draft not found");
            if (draft.State != DraftState.Running) return DraftActionResult.Fail("Draft is not running");
            if (CurrentTeamId(draft) != teamId) return DraftActionResult.Fail("Not your turn");

            var team = await db.Teams.FirstOrDefaultAsync(t => t.Id == teamId, ct);
            if (team is null) return DraftActionResult.Fail("Team not found");
            if (team.SkipsRemaining <= 0) return DraftActionResult.Fail("No skips remaining");

            team.SkipsRemaining--;
            db.OfferedOptions.RemoveRange(draft.Offered);
            await AdvanceAsync(draft, ct);

            await notifier.PickSkippedAsync(draft.Id, teamId, ct);
            await AnnounceTurnAsync(draft, ct);
            return DraftActionResult.Success();
        }
        finally { GlobalLock.Release(); }
    }

    /// <summary>Undoes the most recent pick and returns the pokemon to the pool.</summary>
    public async Task<DraftActionResult> RollbackAsync(int draftId, CancellationToken ct = default)
    {
        await GlobalLock.WaitAsync(ct);
        try
        {
            var draft = await LoadAsync(draftId, ct);
            if (draft is null) return DraftActionResult.Fail("Draft not found");

            var last = draft.Picks.OrderByDescending(p => p.PickNumber).FirstOrDefault();
            if (last is null) return DraftActionResult.Fail("Nothing to roll back");

            // Returning the pokemon to the pool is the step the Python version
            // left to the sheet write; if that failed the pokemon stayed locked.
            var mon = await db.Pokemon.FirstAsync(p => p.Id == last.PokemonEntryId, ct);
            mon.DraftedByTeamId = null;

            db.Picks.Remove(last);
            db.OfferedOptions.RemoveRange(draft.Offered);

            // Put the clock back on the coach who made that pick, not just one
            // position back — Advance may have auto-skipped finished teams after
            // the pick, so the previous position isn't necessarily theirs.
            var pickerSlot = draft.Order
                .Where(s => s.Position < draft.CurrentIndex && s.TeamId == last.TeamId)
                .OrderByDescending(s => s.Position)
                .FirstOrDefault();
            draft.CurrentIndex = pickerSlot?.Position ?? Math.Max(0, draft.CurrentIndex - 1);
            draft.State = DraftState.Running;
            ResetClock(draft);

            await db.SaveChangesAsync(ct);
            await notifier.PickRolledBackAsync(draft.Id, last.TeamId, mon.Name, ct);
            await AnnounceTurnAsync(draft, ct);
            return DraftActionResult.Success();
        }
        finally { GlobalLock.Release(); }
    }

    /// <summary>
    /// Resets a draft to the start line: every pick undone, every pokemon back
    /// in the pool, the clock cleared, back to NotStarted. Used to abandon a
    /// mock and run it again from scratch.
    /// </summary>
    public async Task<DraftActionResult> AbortAsync(int draftId, CancellationToken ct = default)
    {
        await GlobalLock.WaitAsync(ct);
        try
        {
            var draft = await LoadAsync(draftId, ct);
            if (draft is null) return DraftActionResult.Fail("Draft not found");

            // Return every drafted pokemon in this league to the pool in one hit.
            await db.Pokemon
                .Where(p => p.LeagueId == draft.LeagueId && p.DraftedByTeamId != null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DraftedByTeamId, (int?)null), ct);

            db.Picks.RemoveRange(draft.Picks); // scraped stats cascade with the picks
            db.OfferedOptions.RemoveRange(draft.Offered);
            draft.CurrentIndex = 0;
            draft.State = DraftState.NotStarted;
            draft.PickDeadline = null;

            // The schedule is downstream of the draft — clear it too, so a re-run
            // starts from a clean slate rather than fixtures for teams that are
            // about to re-draft.
            await db.Matches.Where(m => m.LeagueId == draft.LeagueId).ExecuteDeleteAsync(ct);

            // Reset everything the season fed into: skip allowance, standings, and
            // the battle-turn totals behind Presence.
            await db.Teams
                .Where(t => t.LeagueId == draft.LeagueId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.SkipsRemaining, MaxSkipsPerTeam)
                    .SetProperty(t => t.Wins, 0)
                    .SetProperty(t => t.Losses, 0)
                    .SetProperty(t => t.Draws, 0)
                    .SetProperty(t => t.BattleTurns, 0), ct);

            await db.SaveChangesAsync(ct);
            await notifier.DraftStateChangedAsync(draft.Id, DraftState.NotStarted, ct);
            await notifier.ScheduleChangedAsync(draft.LeagueId, ct);
            return DraftActionResult.Success();
        }
        finally { GlobalLock.Release(); }
    }

    /// <summary>
    /// Starts the draft, building the roster and snake order from whoever has
    /// signed in — not a fixed seeded set. Every real user (the reserved admin
    /// excluded) gets a team if they don't already have one, and the order is
    /// rebuilt over those teams so a restart picks up anyone who joined since.
    /// </summary>
    public async Task<DraftActionResult> StartAsync(int draftId, CancellationToken ct = default)
    {
        await GlobalLock.WaitAsync(ct);
        try
        {
            var draft = await LoadAsync(draftId, ct);
            if (draft is null) return DraftActionResult.Fail("Draft not found");
            if (draft.State == DraftState.Running) return DraftActionResult.Fail("Already running");

            // Roster = coaches who readied up for this draft, in the order they
            // did so. The reserved admin oversees the draft but never plays, and
            // signing in no longer auto-enrols anyone — readiness is opt-in.
            var readyIds = await db.DraftParticipants
                .Where(p => p.DraftId == draft.Id)
                .OrderBy(p => p.ReadyAt)
                .Select(p => p.DiscordId)
                .ToListAsync(ct);
            var readyUsers = await db.Users
                .Where(u => readyIds.Contains(u.DiscordId) && u.DiscordId != Api.DebugSlotsApi.AdminDiscordId)
                .ToListAsync(ct);
            // Preserve ready order (the DB Contains query doesn't guarantee it).
            var players = readyIds
                .Select(id => readyUsers.FirstOrDefault(u => u.DiscordId == id))
                .Where(u => u is not null).Select(u => u!)
                .ToList();
            if (players.Count == 0) return DraftActionResult.Fail("No coaches have readied up yet");

            // Give each player a team in this league if they don't have one.
            var teams = await db.Teams.Where(t => t.LeagueId == draft.LeagueId).ToListAsync(ct);
            var roster = new List<Team>();
            foreach (var u in players)
            {
                var team = teams.FirstOrDefault(t => t.CoachId == u.DiscordId);
                if (team is null)
                {
                    team = new Team { LeagueId = draft.LeagueId, Name = u.Username, CoachId = u.DiscordId, CoachName = u.Username };
                    db.Teams.Add(team);
                }
                else
                {
                    // Keep both fresh — a team first created before the user had a
                    // proper display name (e.g. a /dev/token sign-in names them
                    // after their id) would otherwise stay stuck showing the id.
                    team.Name = u.Username;
                    team.CoachName = u.Username;
                }
                team.SkipsRemaining = MaxSkipsPerTeam; // fresh allowance each draft
                roster.Add(team);
            }
            await db.SaveChangesAsync(ct); // assign team ids before wiring the order

            // Rebuild the snake order. Each team owes Σ SlotsPerTeam picks, plus
            // up to MaxSkipsPerTeam deferrals — so lay down that many rounds and
            // every team is guaranteed enough turns to fill even if it skips the
            // maximum. Finished teams' surplus turns are auto-skipped in Advance.
            db.DraftSlots.RemoveRange(draft.Order);
            draft.Order.Clear();
            var rounds = draft.League.TierRules.Sum(r => r.SlotsPerTeam) + MaxSkipsPerTeam;
            var pos = 0;
            for (var round = 0; round < rounds; round++)
            {
                var seq = round % 2 == 0 ? roster : Enumerable.Reverse(roster);
                foreach (var t in seq)
                    draft.Order.Add(new DraftSlot { DraftId = draft.Id, Position = pos++, TeamId = t.Id });
            }

            draft.CurrentIndex = 0;
            draft.State = DraftState.Running;
            ResetClock(draft);
            await db.SaveChangesAsync(ct);

            await notifier.DraftStateChangedAsync(draft.Id, DraftState.Running, ct);
            await AnnounceTurnAsync(draft, ct);
            return DraftActionResult.Success();
        }
        finally { GlobalLock.Release(); }
    }

    // ── internals ──────────────────────────────────────────────────────

    private async Task<Draft?> LoadAsync(int draftId, CancellationToken ct) =>
        await db.Drafts
            .Include(d => d.League).ThenInclude(l => l.TierRules)
            .Include(d => d.Order)
            .Include(d => d.Offered).ThenInclude(o => o.PokemonEntry) // for the passed-on snapshot
            .Include(d => d.Picks)
            // Split query: these are several independent collections (Order, Picks,
            // Offered, TierRules). Loaded in one SQL statement they'd cartesian-
            // multiply — Order × Picks rows — so each pick's load grew with the
            // draft. Separate queries keep every load proportional to the data.
            .AsSplitQuery()
            .FirstOrDefaultAsync(d => d.Id == draftId, ct);

    private static int? CurrentTeamId(Draft draft) =>
        draft.Order.FirstOrDefault(s => s.Position == draft.CurrentIndex)?.TeamId;

    private int RemainingSlots(Draft draft, int teamId, Tier tier)
    {
        var allowed = draft.League.TierRules.FirstOrDefault(r => r.Tier == tier)?.SlotsPerTeam ?? 0;
        var used = draft.Picks.Count(p => p.TeamId == teamId && p.Tier == tier);
        return allowed - used;
    }

    /// <summary>
    /// National dex numbers already on a team, for the "no two forms of the same
    /// species" rule. Reads the drafted mons' real dex number from the pool; 0
    /// (unset) is dropped so it can never block an unrelated mon.
    /// </summary>
    private Task<List<int>> TeamDexNumbersAsync(int teamId, CancellationToken ct) =>
        db.Pokemon
            .Where(p => p.DraftedByTeamId == teamId && p.DexNumber > 0)
            .Select(p => p.DexNumber)
            .Distinct()
            .ToListAsync(ct);

    /// <summary>Total picks a full roster holds — the sum of every tier's slots.</summary>
    private static int TotalSlots(Draft draft) => draft.League.TierRules.Sum(r => r.SlotsPerTeam);

    /// <summary>
    /// A team is full once it has as many picks as a roster holds. Per-tier caps
    /// are enforced at offer time, so a full count means every tier is satisfied.
    /// </summary>
    private bool IsRosterFull(Draft draft, int teamId) =>
        draft.Picks.Count(p => p.TeamId == teamId) >= TotalSlots(draft);

    private bool AllRostersFull(Draft draft) =>
        draft.Order.Select(s => s.TeamId).Distinct().All(t => IsRosterFull(draft, t));

    private async Task<Pick> CommitPickAsync(Draft draft, int teamId, int pokemonId, Tier tier, string? teraType, bool auto, CancellationToken ct)
    {
        var pick = new Pick
        {
            DraftId = draft.Id,
            // Sequential over actual picks, not the position index — with skips
            // and auto-skips the index is sparse, which would leave gaps in the
            // "#N" pick numbering.
            PickNumber = draft.Picks.Count + 1,
            TeamId = teamId,
            PokemonEntryId = pokemonId,
            Tier = tier,
            WasAutoPick = auto,
            TeraType = teraType,
        };
        // Snapshot the options offered but not taken, before they're cleared —
        // the pick feed shows what the coach passed on.
        var others = draft.Offered
            .Where(o => o.PokemonEntryId != pokemonId && o.PokemonEntry is not null)
            .Select(o => new
            {
                name = o.PokemonEntry.Name,
                sprite = o.PokemonEntry.Sprite,
                dexNumber = o.PokemonEntry.DexNumber,
                tier = o.Tier.ToString(),
            })
            .ToList();
        pick.OtherOptions = others.Count > 0 ? System.Text.Json.JsonSerializer.Serialize(others) : null;

        db.Picks.Add(pick);

        var mon = await db.Pokemon.FirstAsync(p => p.Id == pokemonId, ct);
        mon.DraftedByTeamId = teamId;

        db.OfferedOptions.RemoveRange(draft.Offered);
        await AdvanceAsync(draft, ct);
        return pick;
    }

    private async Task AdvanceAsync(Draft draft, CancellationToken ct)
    {
        draft.CurrentIndex++;

        // Cycle forward to the next team that still owes picks. Teams that have
        // already filled their roster — whether they finished early or deferred
        // with skips and then filled up — are silently passed over. The draft is
        // done the moment every roster is full (or, as a backstop, when the laid-
        // down order runs out).
        while (true)
        {
            if (AllRostersFull(draft) || draft.CurrentIndex >= draft.Order.Count)
            {
                draft.State = DraftState.Complete;
                draft.PickDeadline = null;
                break;
            }

            var teamId = CurrentTeamId(draft);
            if (teamId is not null && IsRosterFull(draft, teamId.Value))
            {
                draft.CurrentIndex++; // finished team — skip their turn
                continue;
            }

            ResetClock(draft);
            break;
        }

        await db.SaveChangesAsync(ct);
    }

    private static void ResetClock(Draft draft) =>
        draft.PickDeadline = DateTimeOffset.UtcNow.AddSeconds(draft.League.PickTimerSeconds);

    private async Task AnnounceTurnAsync(Draft draft, CancellationToken ct)
    {
        if (draft.State == DraftState.Complete)
        {
            await notifier.DraftStateChangedAsync(draft.Id, DraftState.Complete, ct);
            return;
        }
        var teamId = CurrentTeamId(draft);
        if (teamId is not null) await notifier.YourTurnAsync(draft.Id, teamId.Value, ct);
    }
}
