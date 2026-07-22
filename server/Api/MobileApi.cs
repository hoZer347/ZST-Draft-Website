using System.Security.Claims;
using DraftLeague.Web.Data;
using DraftLeague.Web.Hubs;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Api;

public record RegisterDeviceRequest(string PushToken, DevicePlatform Platform, string? DeviceName);
public record SetPreferenceRequest(NotificationKind Kind, bool Enabled);
public record PickRequest(int TeamId, int PokemonEntryId);
public record OfferRequest(int TeamId, Tier Tier);
public record SkipRequest(int TeamId);
public record DraftSettingsRequest(int Weeks, int PickTimerSeconds);

/// <summary>
/// The surface both the Flutter app and the web frontend use.
///
/// Every route here is authenticated. The caller's identity comes from the JWT,
/// never from the request body, an earlier version took a userId in the body
/// and trusted it, which let anyone act as anyone.
/// </summary>
public static class MobileApi
{
    public static void MapMobileApi(this WebApplication app, string? corsPolicy = null)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        if (corsPolicy is not null) api.RequireCors(corsPolicy);

        // ── devices ────────────────────────────────────────────────────
        api.MapPost("/devices", async (RegisterDeviceRequest req, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var userId = me.DiscordId();
            if (userId is null) return Results.Unauthorized();

            // Tokens rotate and get reassigned across reinstalls, so upsert on
            // the token itself rather than piling up rows per install.
            var existing = await db.DeviceRegistrations.FirstOrDefaultAsync(d => d.PushToken == req.PushToken, ct);
            if (existing is not null)
            {
                // A token can move to a different account when a device is
                // handed over, reassigning is correct, and stops the previous
                // owner's alerts landing on someone else's phone.
                existing.UserId = userId;
                existing.Platform = req.Platform;
                existing.DeviceName = req.DeviceName;
                existing.LastSeenAt = DateTimeOffset.UtcNow;
                existing.IsActive = true;
            }
            else
            {
                db.DeviceRegistrations.Add(new DeviceRegistration
                {
                    UserId = userId,
                    PushToken = req.PushToken,
                    Platform = req.Platform,
                    DeviceName = req.DeviceName,
                });
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        api.MapDelete("/devices/{token}", async (string token, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var userId = me.DiscordId();
            var device = await db.DeviceRegistrations.FirstOrDefaultAsync(d => d.PushToken == token, ct);
            if (device is null) return Results.NotFound();

            // Don't let one user deactivate another's device by guessing tokens.
            if (device.UserId != userId) return Results.Forbid();

            device.IsActive = false;
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // ── notifications ──────────────────────────────────────────────
        // No userId in the route any more: you only ever read your own.
        api.MapGet("/notifications", async (bool? unreadOnly, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var userId = me.DiscordId();
            if (userId is null) return Results.Unauthorized();

            var q = db.Notifications.Where(n => n.UserId == userId);
            if (unreadOnly == true) q = q.Where(n => n.ReadAt == null);

            var items = await q
                .OrderByDescending(n => n.CreatedAt)
                .Take(100)
                .Select(n => new
                {
                    n.Id, kind = n.Kind.ToString(), n.Title, n.Body,
                    n.DeepLink, n.LeagueId, n.CreatedAt, n.ReadAt,
                })
                .ToListAsync(ct);

            return Results.Ok(items);
        });

        api.MapPost("/notifications/{id:int}/read", async (int id, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var userId = me.DiscordId();
            var n = await db.Notifications.FindAsync([id], ct);
            if (n is null) return Results.NotFound();
            if (n.UserId != userId) return Results.Forbid();

            n.ReadAt ??= DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // ── preferences ────────────────────────────────────────────────
        api.MapPost("/preferences", async (SetPreferenceRequest req, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var userId = me.DiscordId();
            if (userId is null) return Results.Unauthorized();

            var pref = await db.NotificationPreferences
                .FirstOrDefaultAsync(p => p.UserId == userId && p.Kind == req.Kind, ct);

            if (pref is null)
                db.NotificationPreferences.Add(new NotificationPreference
                {
                    UserId = userId, Kind = req.Kind, Enabled = req.Enabled,
                });
            else
                pref.Enabled = req.Enabled;

            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // The full draftable pool, for the tier-list browse view. Every mon with
        // its battle profile and whether it's already been drafted.
        api.MapGet("/pool", async (AppDbContext db, CancellationToken ct) =>
        {
            var pool = await db.Pokemon
                .OrderBy(p => p.Tier).ThenBy(p => p.Name)
                .Select(p => new
                {
                    p.Id, p.Name, p.DexNumber, p.Sprite, tier = p.Tier.ToString(),
                    p.Hp, p.Atk, p.Def, p.SpAtk, p.SpDef, p.Speed,
                    bst = p.Hp + p.Atk + p.Def + p.SpAtk + p.SpDef + p.Speed,
                    p.Type1, p.Type2, p.Ability1, p.Ability2, p.HiddenAbility,
                    drafted = p.DraftedByTeamId != null,
                    // Who holds it, for the tier list. Null while still in the pool.
                    ownerTeamId = p.DraftedByTeamId,
                    owner = p.DraftedByTeam == null ? null : p.DraftedByTeam.CoachName,
                })
                .ToListAsync(ct);
            return Results.Ok(pool);
        });

        // Pre-built random teams from the signed-in coach's own drafted roster,
        // the teambuilder's "pre-build my teams" seed (a ready starter team per
        // week the coach can then edit). Best-effort: empty if the roster or the
        // generator is unavailable.
        api.MapGet("/teams/random", async (int? count, ClaimsPrincipal me, AppDbContext db, NodeTeamGenerator gen, CancellationToken ct) =>
        {
            var discordId = me.DiscordId();
            if (discordId is null) return Results.Unauthorized();

            var team = await db.Teams.FirstOrDefaultAsync(t => t.CoachId == discordId, ct);
            if (team is null) return Results.Ok(new { teams = Array.Empty<string>() });

            var picks = await db.Picks.Include(p => p.PokemonEntry)
                .Where(p => p.TeamId == team.Id).ToListAsync(ct);
            var roster = picks.Select(p =>
                !string.IsNullOrWhiteSpace(p.PokemonEntry.Sprite) && !p.PokemonEntry.Sprite.StartsWith("http")
                    ? p.PokemonEntry.Sprite
                    : p.PokemonEntry.Name).ToList();

            var teams = await gen.GenerateAsync(roster, Math.Clamp(count ?? 1, 1, 30), ct) ?? [];
            return Results.Ok(new { teams });
        });

        // Admin-only: one demo random team per player who's READIED UP for the draft,
        // keyed by the player's name, for the admin to seed onto their own device (a
        // folder per player) as examples. A demo team is built from the player's
        // drafted roster if they have one, otherwise from the whole league pool (so it
        // works before the draft, when readied players have no picks yet). Falls back
        // to one team per existing Team when nobody has readied (e.g. a sim league).
        // Best-effort: a player the generator can't build gets an empty team.
        api.MapGet("/teams/demo", async (ClaimsPrincipal me, AppDbContext db, NodeTeamGenerator gen, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();

            var draft = await db.Drafts.Include(d => d.League).ThenInclude(l => l.Teams)
                .OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
            if (draft is null) return Results.Ok(new { teams = Array.Empty<object>() });

            static string Slug(PokemonEntry e) =>
                !string.IsNullOrWhiteSpace(e.Sprite) && !e.Sprite.StartsWith("http") ? e.Sprite : e.Name;

            // The full pool, the source for a player who hasn't drafted yet.
            var pool = (await db.Pokemon.Where(p => p.LeagueId == draft.LeagueId).ToListAsync(ct))
                .Select(Slug).ToList();

            // Each team's drafted roster, so a player who HAS drafted gets their own mons.
            var teamIds = draft.League.Teams.Select(t => t.Id).ToList();
            var rosterByTeam = (await db.Picks.Include(p => p.PokemonEntry)
                    .Where(p => teamIds.Contains(p.TeamId)).ToListAsync(ct))
                .GroupBy(p => p.TeamId)
                .ToDictionary(g => g.Key, g => g.Select(p => Slug(p.PokemonEntry)).ToList());

            var users = (await db.Users.ToListAsync(ct)).ToDictionary(u => u.DiscordId, u => u.Username);

            // The readied-up players, in ready order. If nobody's readied, fall back to
            // whatever teams exist (covers a simulated league with no ready-ups).
            var readied = await db.DraftParticipants.Where(p => p.DraftId == draft.Id)
                .OrderBy(p => p.ReadyAt).Select(p => p.DiscordId).ToListAsync(ct);

            var coaches = readied.Count > 0
                ? readied.Select(id => (
                    name: users.GetValueOrDefault(id, id),
                    teamId: draft.League.Teams.FirstOrDefault(t => t.CoachId == id)?.Id)).ToList()
                : draft.League.Teams.OrderBy(t => t.Id).Select(t => (name: t.CoachName, teamId: (int?)t.Id)).ToList();

            var rosters = coaches
                .Select(c => (IReadOnlyList<string>)(c.teamId is int tid
                    && rosterByTeam.TryGetValue(tid, out var r) && r.Count > 0 ? r : pool))
                .ToList();
            var packed = await gen.GenerateBatchAsync(rosters, ct) ?? [];

            var result = coaches.Select((c, i) => new { player = c.name, team = i < packed.Count ? packed[i] : "" });
            return Results.Ok(new { teams = result });
        });

        // Accrued battle stats for every drafted mon that has played, scraped
        // from the season's replays. One row per pick (a mon on a team).
        api.MapGet("/stats", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            // Which teams this caller coaches, resolved server-side, so a mon on the
            // signed-in coach's own team can be flagged (`mine`) without the client
            // depending on the draft-state global being loaded first.
            var myDiscordId = me.DiscordId();
            var myTeamIds = await db.Teams.Where(t => t.CoachId == myDiscordId).Select(t => t.Id).ToListAsync(ct);
            var rows = await db.PokemonStats
                .Where(s => s.GamesPlayed > 0)
                .Select(s => new
                {
                    pokemon = s.Pick.PokemonEntry.Name,
                    dex = s.Pick.PokemonEntry.DexNumber,
                    s.Pick.PokemonEntry.Sprite,
                    tier = s.Pick.Tier.ToString(),
                    tera = s.Pick.TeraType,
                    teamId = s.Pick.TeamId,
                    mine = myTeamIds.Contains(s.Pick.TeamId),
                    trainer = s.Pick.Team.CoachName,
                    team = s.Pick.Team.Name,
                    activeTurns = s.ActiveTurns,
                    playedTurns = s.PlayedTurns, // denominator for in-game (usage) presence
                    teamTurns = s.Pick.Team.BattleTurns,
                    gp = s.GamesPlayed,
                    starts = s.Starts, finishes = s.Finishes,
                    k = s.Kills, d = s.Deaths, allyKos = s.AlliesKoed, selfKos = s.SelfKos, w = s.Wins, l = s.Losses,
                    dealt = s.DamageDealtDirect + s.DamageDealtIndirect,
                    dealtDirect = s.DamageDealtDirect, dealtIndirect = s.DamageDealtIndirect,
                    dealtAllyDirect = s.DamageDealtAllyDirect, dealtAllyIndirect = s.DamageDealtAllyIndirect,
                    taken = s.DamageTakenDirect + s.DamageTakenIndirect,
                    takenDirect = s.DamageTakenDirect, takenIndirect = s.DamageTakenIndirect,
                    takenSelf = s.DamageTakenSelf,
                    recovered = s.HpRecovered, healed = s.HpHealed, healedEnemy = s.HpHealedEnemy, crits = s.Crits,
                })
                .ToListAsync(ct);
            return Results.Ok(rows);
        });

        // ── draft ──────────────────────────────────────────────────────
        // The drafts this user can see. One per league; used by the web client
        // to find which draft to open after signing in.
        api.MapGet("/drafts", async (AppDbContext db, CancellationToken ct) =>
        {
            var drafts = await db.Drafts
                .Include(d => d.League)
                .Select(d => new { d.Id, d.LeagueId, league = d.League.Name, state = d.State.ToString() })
                .ToListAsync(ct);
            return Results.Ok(drafts);
        });

        // Readable by any signed-in user: a draft is public within the league,
        // and the offered options only exist for whoever is on the clock.
        api.MapGet("/drafts/{draftId:int}", async (int draftId, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var draft = await db.Drafts
                .Include(d => d.League).ThenInclude(l => l.Teams)
                .Include(d => d.League).ThenInclude(l => l.TierRules)
                .Include(d => d.Order)
                .Include(d => d.Offered).ThenInclude(o => o.PokemonEntry)
                .Include(d => d.Picks).ThenInclude(p => p.PokemonEntry)
                .Include(d => d.Skips)
                // Split query: several independent collections (Teams, TierRules,
                // Order, Offered, Picks, Skips) in one statement cartesian-multiply,
                // so this GET got slower with every pick. Separate queries keep it flat.
                .AsSplitQuery()
                .FirstOrDefaultAsync(d => d.Id == draftId, ct);

            if (draft is null) return Results.NotFound();

            var onClock = draft.Order.FirstOrDefault(s => s.Position == draft.CurrentIndex)?.TeamId;
            // The caller's own team, resolved server-side: teams are created at
            // Start (after login), so the client's cached session can't know it.
            var myDiscordId = me.DiscordId();
            var myTeamId = draft.League.Teams.FirstOrDefault(t => t.CoachId == myDiscordId)?.Id;
            // Live admin flag so the client shows admin controls from DB truth,
            // never a stale token.
            var isAdmin = await me.IsAdminAsync(db, ct);

            // Who's readied up for the (not-yet-started) draft, and whether the
            // caller is one of them / may become one.
            var readyIds = await db.DraftParticipants
                .Where(p => p.DraftId == draftId)
                .OrderBy(p => p.ReadyAt)
                .Select(p => p.DiscordId)
                .ToListAsync(ct);
            var myReady = myDiscordId is not null && readyIds.Contains(myDiscordId);
            var canReady = draft.State == DraftState.NotStarted
                           && myDiscordId is not null;

            // Progress is measured in actual picks, not order positions: skips and
            // finished-team auto-skips make the position index sparse, so the
            // order is laid down longer than the real pick count.
            var slotsPerRoster = draft.League.TierRules.Sum(r => r.SlotsPerTeam);
            var participants = draft.Order.Select(s => s.TeamId).Distinct().Count();
            var totalPicks = participants * slotsPerRoster;
            var picksMade = draft.Picks.Count;

            // How many picks until the caller is next on the clock. Walk the laid-down
            // snake order forward from the current position, simulating each team
            // filling its roster: a team with no slots left is silently passed over
            // (mirrors the engine's finished-team skip), every other slot is one pick.
            // Count the picks before we reach the caller's next slot. 0 means "you're
            // on the clock now"; null when the caller has no team, the draft isn't
            // running, or the caller's roster is already full (no turn is coming).
            int? picksUntilMyTurn = null;
            if (myTeamId is not null && draft.State == DraftState.Running)
            {
                var remaining = draft.League.Teams.ToDictionary(
                    t => t.Id, t => slotsPerRoster - draft.Picks.Count(p => p.TeamId == t.Id));
                var orderList = draft.Order.OrderBy(s => s.Position).ToList();
                var count = 0;
                for (var i = draft.CurrentIndex; i < orderList.Count; i++)
                {
                    var tid = orderList[i].TeamId;
                    if (!remaining.TryGetValue(tid, out var left) || left <= 0) continue; // full: skipped
                    if (tid == myTeamId.Value) { picksUntilMyTurn = count; break; }
                    count++;
                    remaining[tid] = left - 1;
                }
            }

            return Results.Ok(new
            {
                draft.Id,
                draft.LeagueId,
                league = draft.League.Name,
                state = draft.State.ToString(),
                pickNumber = Math.Min(picksMade + 1, totalPicks),
                totalPicks,
                onClockTeamId = onClock,
                myTeamId,
                picksUntilMyTurn,
                isAdmin,
                ready = readyIds,
                myReady,
                canReady,
                // Pre-start settings, so the admin panel can show current values.
                weeks = draft.League.SeasonWeeks,
                // The season is capped at one full round-robin, so with few players
                // it runs shorter than the setting. This is what will actually be
                // scheduled given who's in (readied players pre-start, teams after).
                scheduledWeeks = Math.Min(
                    draft.League.SeasonWeeks,
                    ScheduleApi.SingleRoundRobinWeeks(draft.League.Teams.Count > 0 ? draft.League.Teams.Count : readyIds.Count)),
                pickTimerSeconds = draft.League.PickTimerSeconds,
                secondsRemaining = draft.PickDeadline is null
                    ? (int?)null
                    : Math.Max(0, (int)Math.Ceiling((draft.PickDeadline.Value - DateTimeOffset.UtcNow).TotalSeconds)),
                // Every team in the league, and how many of each tier they still
                // owe, the client renders per-team roster columns from this.
                teams = draft.League.Teams.Select(t => new
                {
                    t.Id, t.Name, t.CoachName, t.CoachId, t.SkipsRemaining,
                }),
                tierRules = draft.League.TierRules
                    .OrderBy(r => r.Tier)
                    .Select(r => new { tier = r.Tier.ToString(), r.SlotsPerTeam, r.OptionsOffered }),
                // Snake order as a flat list of team ids by position, so the
                // client can show who's up next.
                order = draft.Order.OrderBy(s => s.Position).Select(s => s.TeamId),
                offered = draft.Offered.Select(o => new
                {
                    o.PokemonEntryId, o.PokemonEntry.Name, o.PokemonEntry.DexNumber,
                    o.PokemonEntry.Sprite, tier = o.Tier.ToString(), o.TeraType,
                }),
                // The full pick history (chronological), with enough to render
                // each mon: the board and the running list both read this.
                picks = draft.Picks
                    .OrderBy(p => p.PickNumber)
                    .Select(p => new
                    {
                        p.PickNumber, p.TeamId, p.PokemonEntryId,
                        p.PokemonEntry.Name, p.PokemonEntry.DexNumber, p.PokemonEntry.Sprite,
                        tier = p.Tier.ToString(), p.WasAutoPick, p.TeraType,
                        // JSON snapshot of the options passed on this turn (client parses it).
                        p.OtherOptions,
                    }),
                // Passed turns, interleaved into the feed client-side by
                // afterPickNumber (the skip sits right after that pick).
                skips = draft.Skips
                    .OrderBy(s => s.Id)
                    .Select(s => new { s.Id, s.TeamId, s.AfterPickNumber, s.WasAuto, s.OtherOptions }),
            });
        });

        // Ready up: opt in to the draft before it starts. The Start roster is
        // built from these, so signing in alone no longer enrols you.
        api.MapPost("/drafts/{draftId:int}/ready", async (
            int draftId, ClaimsPrincipal me, AppDbContext db, IDraftNotifier notifier, CancellationToken ct) =>
        {
            var discordId = me.DiscordId();
            if (discordId is null) return Results.Unauthorized();

            var draft = await db.Drafts.FindAsync([draftId], ct);
            if (draft is null) return Results.NotFound();
            if (draft.State != DraftState.NotStarted)
                return Results.BadRequest(new { error = "The draft has already started." });

            var already = await db.DraftParticipants
                .AnyAsync(p => p.DraftId == draftId && p.DiscordId == discordId, ct);
            if (!already)
            {
                db.DraftParticipants.Add(new DraftParticipant { DraftId = draftId, DiscordId = discordId });
                await db.SaveChangesAsync(ct);
                await notifier.ReadyChangedAsync(draftId, ct);
            }
            return Results.Ok();
        });

        // Leave: withdraw before the draft starts.
        api.MapPost("/drafts/{draftId:int}/leave", async (
            int draftId, ClaimsPrincipal me, AppDbContext db, IDraftNotifier notifier, CancellationToken ct) =>
        {
            var discordId = me.DiscordId();
            if (discordId is null) return Results.Unauthorized();

            var draft = await db.Drafts.FindAsync([draftId], ct);
            if (draft is null) return Results.NotFound();
            if (draft.State != DraftState.NotStarted)
                return Results.BadRequest(new { error = "The draft has already started." });

            var removed = await db.DraftParticipants
                .Where(p => p.DraftId == draftId && p.DiscordId == discordId)
                .ExecuteDeleteAsync(ct);
            if (removed > 0) await notifier.ReadyChangedAsync(draftId, ct);
            return Results.Ok();
        });

        api.MapPost("/drafts/{draftId:int}/offer", async (
            int draftId, OfferRequest req, ClaimsPrincipal me, DraftEngine engine, AppDbContext db, CancellationToken ct) =>
        {
            if (!await me.OwnsTeamAsync(db, req.TeamId, ct)) return Results.Forbid();
            var r = await engine.OfferOptionsAsync(draftId, req.TeamId, req.Tier, ct);
            return r.Ok ? Results.Ok() : Results.BadRequest(new { error = r.Error });
        });

        api.MapPost("/drafts/{draftId:int}/pick", async (
            int draftId, PickRequest req, ClaimsPrincipal me, DraftEngine engine, AppDbContext db, CancellationToken ct) =>
        {
            if (!await me.OwnsTeamAsync(db, req.TeamId, ct)) return Results.Forbid();
            var r = await engine.MakePickAsync(draftId, req.TeamId, req.PokemonEntryId, ct);
            return r.Ok ? Results.Ok() : Results.BadRequest(new { error = r.Error });
        });

        // Defer this pick to a later cycle, spending one skip token.
        api.MapPost("/drafts/{draftId:int}/skip", async (
            int draftId, SkipRequest req, ClaimsPrincipal me, DraftEngine engine, AppDbContext db, CancellationToken ct) =>
        {
            if (!await me.OwnsTeamAsync(db, req.TeamId, ct)) return Results.Forbid();
            var r = await engine.SkipPickAsync(draftId, req.TeamId, ct);
            return r.Ok ? Results.Ok() : Results.BadRequest(new { error = r.Error });
        });

        // Undo the most recent pick. Allowed for an admin or the coach who made
        // that pick, a coach can take back their own mistake, but not someone
        // else's. (The admin group below also exposes this for tooling.)
        api.MapPost("/drafts/{draftId:int}/rollback", async (
            int draftId, ClaimsPrincipal me, DraftEngine engine, AppDbContext db, CancellationToken ct) =>
        {
            var last = await db.Picks
                .Where(p => p.DraftId == draftId)
                .OrderByDescending(p => p.PickNumber)
                .FirstOrDefaultAsync(ct);
            if (last is null) return Results.BadRequest(new { error = "Nothing to roll back" });

            if (!await me.IsAdminAsync(db, ct) && !await me.OwnsTeamAsync(db, last.TeamId, ct))
                return Results.Forbid();

            var r = await engine.RollbackAsync(draftId, ct);
            return r.Ok ? Results.Ok() : Results.BadRequest(new { error = r.Error });
        });

        // ── admin ──────────────────────────────────────────────────────
        // RequireRole is a coarse gate; each handler re-checks admin against the
        // live DB so a demoted account can't act on a still-valid token.
        var admin = app.MapGroup("/api/admin").RequireAuthorization(p => p.RequireRole("admin"));
        if (corsPolicy is not null) admin.RequireCors(corsPolicy);

        admin.MapPost("/drafts/{id:int}/start", async (int id, DraftSettingsRequest? req, ClaimsPrincipal me, DraftEngine e, PokedexSync sync, AppDbContext db, IHubContext<DraftHub> hub, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();

            var draft = await db.Drafts.Include(d => d.League).FirstOrDefaultAsync(d => d.Id == id, ct);
            if (draft is null) return Results.NotFound();

            // Pre-start settings are applied at kickoff, not saved separately, the
            // numbers the admin sees on the panel are the ones the draft runs with.
            // StartAsync reads PickTimerSeconds off this same tracked league, so the
            // new clock takes effect immediately.
            //
            // Never 400 the Start over settings. Weeks stays sane (the round-robin
            // needs at least one). The pick timeout is taken exactly as given, even
            // 0, which makes every pick auto-fire immediately so a draft can be
            // fast-forwarded/simulated. Negatives (a past deadline) behave like 0.
            if (req is not null)
            {
                draft.League.SeasonWeeks = Math.Clamp(req.Weeks, 1, 52);
                draft.League.PickTimerSeconds = Math.Max(0, req.PickTimerSeconds);
                await db.SaveChangesAsync(ct);
            }

            // Refresh the pool from the source sheet before the draft begins, so
            // sheet edits show up here. Best-effort, RefreshAsync swallows and
            // logs failures rather than blocking the draft.
            await sync.RefreshAsync(draft.LeagueId, ct);

            var r = await e.StartAsync(id, ct);
            if (!r.Ok) return Results.BadRequest(new { error = r.Error });

            // Teams exist now that the draft has started, so lay down the season's
            // round-robin automatically over the configured number of weeks.
            await ScheduleApi.GenerateAsync(db, draft.LeagueId, draft.League.SeasonWeeks, ct);
            await hub.Clients.All.SendAsync("scheduleChanged", new { leagueId = draft.LeagueId }, ct);
            return Results.Ok();
        });

        admin.MapPost("/drafts/{id:int}/rollback", async (int id, ClaimsPrincipal me, DraftEngine e, AppDbContext db, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();
            var r = await e.RollbackAsync(id, ct);
            return r.Ok ? Results.Ok() : Results.BadRequest(new { error = r.Error });
        });

        admin.MapPost("/drafts/{id:int}/abort", async (int id, ClaimsPrincipal me, DraftEngine e, AppDbContext db, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();
            var r = await e.AbortAsync(id, ct);
            return r.Ok ? Results.Ok() : Results.BadRequest(new { error = r.Error });
        });

        // Admin: rebuild a league's stored PokemonStat aggregates from scratch by
        // re-scraping every reported match's stored replay log with the CURRENT
        // scraper. Non-destructive, matches, replays and standings are untouched;
        // only the derived per-mon stat rows (and the per-team presence denominators
        // they share) are recomputed. Run this after a scraper fix so the stats page
        // reflects corrected attribution without re-simulating the season.
        admin.MapPost("/leagues/{leagueId:int}/recompute-stats", async (
            int leagueId, ClaimsPrincipal me, AppDbContext db, MatchStatsRecorder recorder, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();

            var teamIds = await db.Teams.Where(t => t.LeagueId == leagueId).Select(t => t.Id).ToListAsync(ct);
            if (teamIds.Count == 0) return Results.NotFound(new { error = "No teams in that league." });

            // Zero the derived data: drop every PokemonStat for a pick on these teams,
            // and reset each team's season-presence denominator (BattleTurns). Save so
            // the re-apply below starts from a clean slate.
            var pickIds = await db.Picks.Where(p => teamIds.Contains(p.TeamId)).Select(p => p.Id).ToListAsync(ct);
            var stale = await db.PokemonStats.Where(s => pickIds.Contains(s.PickId)).ToListAsync(ct);
            db.PokemonStats.RemoveRange(stale);
            var teams = await db.Teams.Where(t => t.LeagueId == leagueId).ToListAsync(ct);
            foreach (var t in teams) t.BattleTurns = 0;
            await db.SaveChangesAsync(ct);

            // Re-apply every reported match's stored log (+1) with the fixed scraper.
            var matches = await db.Matches
                .Include(m => m.HomeTeam).Include(m => m.AwayTeam)
                .Where(m => m.LeagueId == leagueId && m.ReplayLog != null
                            && m.ReplayHomeSide != null && m.Result != MatchResult.Pending)
                .ToListAsync(ct);
            var applied = 0;
            foreach (var m in matches)
            {
                await recorder.ApplyAsync(m, m.ReplayHomeSide!, m.ReplayLog!, m.Result, +1, ct);
                // Save after EACH match: ApplyAsync reads the existing PokemonStat rows
                // back from the DB to accumulate onto, so a later match only sees an
                // earlier one's contribution once it's persisted. Batching to a single
                // save at the end collapses every mon to just its last game.
                await db.SaveChangesAsync(ct);
                applied++;
            }
            return Results.Ok(new { leagueId, matchesApplied = applied, statRowsCleared = stale.Count });
        });

        // Admin: ready ANY account into the not-yet-started draft (dummy or real
        // player), so a test roster can be filled without logging in as each. The
        // coach's own /ready endpoint only readies themselves; this lets the admin
        // do it on anyone's behalf.
        admin.MapPost("/drafts/{draftId:int}/participants/{discordId}", async (
            int draftId, string discordId, ClaimsPrincipal me, AppDbContext db, IDraftNotifier notifier, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();
            var draft = await db.Drafts.FindAsync([draftId], ct);
            if (draft is null) return Results.NotFound();
            if (draft.State != DraftState.NotStarted)
                return Results.BadRequest(new { error = "The draft has already started." });
            if (discordId == AuthApi.AdminDiscordId)
                return Results.BadRequest(new { error = "The oversee-admin can't be a player." });
            if (!await db.Users.AnyAsync(u => u.DiscordId == discordId, ct))
                return Results.NotFound(new { error = "No such account." });

            if (!await db.DraftParticipants.AnyAsync(p => p.DraftId == draftId && p.DiscordId == discordId, ct))
            {
                db.DraftParticipants.Add(new DraftParticipant { DraftId = draftId, DiscordId = discordId });
                await db.SaveChangesAsync(ct);
                await notifier.ReadyChangedAsync(draftId, ct);
            }
            return Results.Ok();
        });

        // Admin: un-ready an account from the not-yet-started draft.
        admin.MapDelete("/drafts/{draftId:int}/participants/{discordId}", async (
            int draftId, string discordId, ClaimsPrincipal me, AppDbContext db, IDraftNotifier notifier, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();
            var draft = await db.Drafts.FindAsync([draftId], ct);
            if (draft is null) return Results.NotFound();
            if (draft.State != DraftState.NotStarted)
                return Results.BadRequest(new { error = "The draft has already started." });

            var removed = await db.DraftParticipants
                .Where(p => p.DraftId == draftId && p.DiscordId == discordId)
                .ExecuteDeleteAsync(ct);
            if (removed > 0) await notifier.ReadyChangedAsync(draftId, ct);
            return Results.Ok();
        });

        // The users an admin may view as: everyone in the league roster (the same
        // left-hand player list), minus the admin themselves and the reserved
        // oversee-admin identity (which isn't a real player).
        admin.MapGet("/dummies", async (ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();
            var meId = me.DiscordId();
            var users = await db.Users
                .Where(u => u.DiscordId != AuthApi.AdminDiscordId && u.DiscordId != meId)
                .OrderBy(u => u.Username)
                .Select(u => new { discordId = u.DiscordId, username = u.Username })
                .ToListAsync(ct);
            return Results.Ok(users);
        });

        // Create a fresh dummy coach (a synthetic, non-Discord account) and, if the
        // draft hasn't started, ready it in so it joins the roster + draft. Admin-only
        // and dev-friendly: the way to fill a test league now that the fixed debug
        // slots are gone.
        admin.MapPost("/dummies", async (ClaimsPrincipal me, AppDbContext db, IDraftNotifier notifier, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();

            // Next free "dummy-N" id (N is 1-based and skips any that already exist).
            var taken = (await db.Users.Where(u => u.DiscordId.StartsWith("dummy-")).Select(u => u.DiscordId).ToListAsync(ct))
                .ToHashSet();
            var n = 1;
            while (taken.Contains($"dummy-{n}")) n++;
            var id = $"dummy-{n}";
            var user = new User { DiscordId = id, Username = $"Dummy {n}" };
            db.Users.Add(user);

            // Ready it into the current not-yet-started draft, so it shows as a readied
            // player and gets a team at Start (like a coach who readied up).
            var draft = await db.Drafts.OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
            var readied = false;
            if (draft is not null && draft.State == DraftState.NotStarted)
            {
                db.DraftParticipants.Add(new DraftParticipant { DraftId = draft.Id, DiscordId = id });
                readied = true;
            }

            await db.SaveChangesAsync(ct);
            await notifier.PlayersChangedAsync(ct); // roster grew, refresh everyone
            return Results.Ok(new { discordId = id, username = user.Username, readied });
        });

        // Bulk companion to Add dummy: ready EVERY existing dummy coach into the
        // current not-yet-started draft in one click, so a test roster fills without
        // adding them one at a time. Admin-only. Real Discord accounts (all-digit
        // snowflake ids) and the reserved oversee-admin are never touched.
        admin.MapPost("/dummies/ready-all", async (ClaimsPrincipal me, AppDbContext db, IDraftNotifier notifier, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();

            var draft = await db.Drafts.OrderBy(d => d.Id).FirstOrDefaultAsync(ct);
            if (draft is null || draft.State != DraftState.NotStarted)
                return Results.BadRequest(new { error = "The draft has already started." });

            // A dummy is any synthetic account: a non-numeric id (a real Discord id
            // is an all-digit snowflake), excluding the oversee-admin. EF can't
            // translate "has a non-digit char", so filter in memory.
            var dummyIds = (await db.Users.Select(u => u.DiscordId).ToListAsync(ct))
                .Where(id => id != AuthApi.AdminDiscordId && id.Length > 0 && id.Any(c => !char.IsDigit(c)))
                .ToList();

            var already = (await db.DraftParticipants
                    .Where(p => p.DraftId == draft.Id)
                    .Select(p => p.DiscordId)
                    .ToListAsync(ct))
                .ToHashSet();

            var toAdd = dummyIds.Where(id => !already.Contains(id)).ToList();
            foreach (var id in toAdd)
                db.DraftParticipants.Add(new DraftParticipant { DraftId = draft.Id, DiscordId = id });

            if (toAdd.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                await notifier.ReadyChangedAsync(draft.Id, ct);
            }
            return Results.Ok(new { readied = toAdd.Count });
        });

        // Mint a session for another user so the admin can browse as them. Admin-only.
        // Any account in the league can be viewed as (bar the reserved oversee-admin);
        // the caller is already an admin, so being handed a member's view is no
        // privilege escalation.
        admin.MapPost("/impersonate", async (
            ImpersonateRequest req, ClaimsPrincipal me, AppDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(req.DiscordId)) return Results.BadRequest(new { error = "No account given." });
            if (req.DiscordId == AuthApi.AdminDiscordId)
                return Results.BadRequest(new { error = "The oversee-admin identity can't be viewed as." });

            var target = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == req.DiscordId, ct);
            if (target is null) return Results.NotFound(new { error = "No such account." });

            var pair = await tokens.IssueAsync(target, "Admin view-as", ct);
            return Results.Ok(new
            {
                accessToken = pair.AccessToken,
                refreshToken = pair.RefreshToken,
                accessExpiresAt = pair.AccessExpiresAt,
                user = await AuthApi.BuildMeAsync(db, target, ct),
            });
        });
    }
}

public record ImpersonateRequest(string DiscordId);

public static class PrincipalExtensions
{
    public static string? DiscordId(this ClaimsPrincipal p) =>
        p.FindFirstValue(TokenService.DiscordIdClaim);

    /// <summary>
    /// The check that makes "not your turn" meaningful. Without it the engine's
    /// turn logic is bypassable by anyone willing to send a different teamId.
    /// </summary>
    public static async Task<bool> OwnsTeamAsync(this ClaimsPrincipal p, AppDbContext db, int teamId, CancellationToken ct)
    {
        var discordId = p.DiscordId();
        if (discordId is null) return false;
        return await db.Teams.AnyAsync(t => t.Id == teamId && t.CoachId == discordId, ct);
    }

    /// <summary>
    /// Admin against the live database, not the JWT's role claim. A token minted
    /// while a user was admin keeps its role for up to its lifetime; checking the
    /// DB means demoting someone (e.g. a debug coach that used to be admin) takes
    /// effect immediately for start/abort/rollback.
    /// </summary>
    public static async Task<bool> IsAdminAsync(this ClaimsPrincipal p, AppDbContext db, CancellationToken ct)
    {
        var discordId = p.DiscordId();
        if (discordId is null) return false;
        return await db.Users.AnyAsync(u => u.DiscordId == discordId && u.IsAdmin, ct);
    }
}
