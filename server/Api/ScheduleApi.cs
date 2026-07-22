using System.Security.Claims;
using DraftLeague.Web.Data;
using DraftLeague.Web.Hubs;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Api;

public record SubmitReplayRequest(string ReplayUrl);
public record ShowdownReportRequest(string Log, string? P1Export = null, string? P2Export = null);

// Shapes of a season snapshot (see the GET .../snapshot endpoint), for restoring one.
public record SnapshotDto(SnapTeam[] Teams, SnapPick[] Picks, SnapMatch[] Matches);
public record SnapTeam(int Id, string CoachId, string CoachName);
public record SnapPick(int TeamId, int PickNumber, string Tier, string? TeraType, string Pokemon, string? Sprite, int DexNumber,
    string? OtherOptions = null, bool WasAutoPick = false);
public record SnapMatch(
    int Id, int Week, DateTimeOffset? ScheduledFor, int HomeTeamId, int AwayTeamId,
    string Result, int? HomeScore, int? AwayScore, string? ReplayUrl, string? ReplayLog,
    string? ReplayHomeSide, string? HomeTeamExport, string? AwayTeamExport);

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

        // Renders a headless-sim battle's stored log as an animated Showdown replay.
        // Anonymous on purpose: the schedule's "Watch replay" link is a plain <a>
        // that opens in a new tab with no auth header, so it can't sit behind
        // RequireAuthorization. Only sim battles carry a ReplayLog; real matches
        // link straight to Showdown.
        app.MapGet("/api/matches/{matchId:int}/replay", async (int matchId, AppDbContext db, CancellationToken ct) =>
        {
            var log = await db.Matches.Where(m => m.Id == matchId).Select(m => m.ReplayLog).FirstOrDefaultAsync(ct);
            return string.IsNullOrEmpty(log)
                ? Results.NotFound()
                : Results.Content(ReplayHtml(log), "text/html; charset=utf-8");
        });

        // Both teams' actual builds for a game, as pokepaste / Showdown import text.
        // Anonymous for the same reason as the replay link: it's a plain <a> that
        // opens in a new tab with no auth header. Only games with captured team data
        // have it (sim battles and Showdown-server-reported games).
        app.MapGet("/api/matches/{matchId:int}/paste", async (int matchId, string? team, AppDbContext db, CancellationToken ct) =>
        {
            var m = await db.Matches.Where(x => x.Id == matchId)
                .Select(x => new { x.HomeTeamExport, x.AwayTeamExport, x.HomeTeamId, x.AwayTeamId })
                .FirstOrDefaultAsync(ct);
            if (m is null) return Results.NotFound();

            var names = await db.Teams.Where(t => t.Id == m.HomeTeamId || t.Id == m.AwayTeamId)
                .ToDictionaryAsync(t => t.Id, t => t.CoachName, ct);

            // ?team=home|away returns just that side's build (the per-team link under
            // each scoresheet table); with no param it returns both (the combined link).
            var wantHome = !string.Equals(team, "away", StringComparison.OrdinalIgnoreCase);
            var wantAway = !string.Equals(team, "home", StringComparison.OrdinalIgnoreCase);

            var sb = new System.Text.StringBuilder();
            if (wantHome && m.HomeTeamExport is not null)
                sb.Append("=== ").Append(names.GetValueOrDefault(m.HomeTeamId, "Home")).Append(" ===\n\n").Append(m.HomeTeamExport.Trim()).Append("\n\n");
            if (wantAway && m.AwayTeamExport is not null)
                sb.Append("=== ").Append(names.GetValueOrDefault(m.AwayTeamId, "Away")).Append(" ===\n\n").Append(m.AwayTeamExport.Trim()).Append('\n');
            if (sb.Length == 0) return Results.NotFound(); // no build captured for the requested side
            return Results.Content(sb.ToString(), "text/plain; charset=utf-8");
        });

        // A pokepaste-style hosted PAGE for a game's team builds: one card per mon
        // (sprite, item, ability, spread, tera, moves) plus the raw export, rendered
        // server-side (see BuildPageRenderer). ?team=home|away shows one side, no param
        // shows both. Anonymous like the paste route: a plain link opened in a new tab.
        app.MapGet("/matches/{matchId:int}/teams", async (int matchId, string? team, AppDbContext db, CancellationToken ct) =>
        {
            var m = await db.Matches.Where(x => x.Id == matchId)
                .Select(x => new { x.LeagueId, x.Week, x.HomeTeamExport, x.AwayTeamExport, x.HomeTeamId, x.AwayTeamId })
                .FirstOrDefaultAsync(ct);
            if (m is null) return Results.NotFound();

            var names = await db.Teams.Where(t => t.Id == m.HomeTeamId || t.Id == m.AwayTeamId)
                .ToDictionaryAsync(t => t.Id, t => t.CoachName, ct);

            // Species id -> (sprite slug, dex) for the whole pool, so parsed sets get the
            // same sprites the rest of the site uses (name and slug both indexed, for forms).
            var pool = await db.Pokemon.Where(p => p.LeagueId == m.LeagueId)
                .Select(p => new { p.Name, p.Sprite, p.DexNumber, p.Tier }).ToListAsync(ct);
            var spriteBy = new Dictionary<string, (string?, int?, string?)>();
            foreach (var p in pool)
            {
                var v = ((string?)p.Sprite, (int?)p.DexNumber, (string?)p.Tier.ToString());
                spriteBy[BuildPageRenderer.ToId(p.Name)] = v;
                // Indexed by name AND sprite slug id, so a mega forme is findable by its
                // slug (e.g. "lucario-mega"), which is how the renderer resolves a mega
                // held via its stone to the forme's own tier/sprite, not the base's.
                if (!string.IsNullOrEmpty(p.Sprite)) spriteBy.TryAdd(BuildPageRenderer.ToId(p.Sprite), v);
            }
            (string?, int?, string?) SpriteOf(string id) => spriteBy.TryGetValue(id, out var v) ? v : (null, null, null);

            var wantHome = !string.Equals(team, "away", StringComparison.OrdinalIgnoreCase);
            var wantAway = !string.Equals(team, "home", StringComparison.OrdinalIgnoreCase);

            var views = new List<BuildPageRenderer.TeamView>();
            if (wantHome && m.HomeTeamExport is not null)
                views.Add(new(names.GetValueOrDefault(m.HomeTeamId, "Home"),
                    BuildPageRenderer.Parse(m.HomeTeamExport, SpriteOf), m.HomeTeamExport.Trim()));
            if (wantAway && m.AwayTeamExport is not null)
                views.Add(new(names.GetValueOrDefault(m.AwayTeamId, "Away"),
                    BuildPageRenderer.Parse(m.AwayTeamExport, SpriteOf), m.AwayTeamExport.Trim()));
            if (views.Count == 0) return Results.NotFound();

            // Title mirrors the coach's teambuilder team name for this match, "Week N
            // vs <opponent>" for a single side, or the full matchup for both.
            var home = names.GetValueOrDefault(m.HomeTeamId, "Home");
            var away = names.GetValueOrDefault(m.AwayTeamId, "Away");
            var title = (wantHome, wantAway) switch
            {
                (true, false) => $"Week {m.Week} vs {away}",
                (false, true) => $"Week {m.Week} vs {home}",
                _ => $"Week {m.Week}: {home} vs {away}",
            };
            return Results.Content(BuildPageRenderer.Render(title, views), "text/html; charset=utf-8");
        });

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
            // Teams are shown by their coach's username / dummy name, the league
            // doesn't use separate team names.
            var teamName = teams.ToDictionary(t => t.Id, t => t.CoachName);

            // Each coach's Discord avatar (for the outer edge of their match-card
            // side). Synthetic coaches (dummies / sim) and anyone without an avatar
            // won't be in the map, so their side just shows no icon.
            var coachIds = teams.Select(t => t.CoachId).ToList();
            var avatarByCoach = (await db.Users
                    .Where(u => coachIds.Contains(u.DiscordId) && u.AvatarHash != null)
                    .Select(u => new { u.DiscordId, u.AvatarHash })
                    .ToListAsync(ct))
                .ToDictionary(u => u.DiscordId, u => u.AvatarHash!);
            var teamAvatar = teams.ToDictionary(
                t => t.Id,
                t => avatarByCoach.TryGetValue(t.CoachId, out var hash)
                    ? $"https://cdn.discordapp.com/avatars/{t.CoachId}/{hash}.png"
                    : (string?)null);

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
                    hasPaste = m.HomeTeamExport != null || m.AwayTeamExport != null,
                    hasHomePaste = m.HomeTeamExport != null,
                    hasAwayPaste = m.AwayTeamExport != null,
                })
                .ToListAsync(ct);

            // "This week" is the earliest week that still has an unplayed match,
            // the front of the season. Everything before it is history.
            var currentWeek = matches
                .Where(m => m.result == nameof(MatchResult.Pending))
                .Select(m => (int?)m.Week)
                .Min();

            // Per-mon battle stats for each played match, scraped from its stored
            // log, for the compact strip under each score. Every brought mon gets a
            // row (team preview seeds even benched ones); a mon that never touched
            // the field reads as "did not participate" (played=false).
            var statsByMatch = await BattleStatsAsync(db, leagueId, teams.Select(t => t.Id).ToList(), matches
                .Where(m => m.result != nameof(MatchResult.Pending)).Select(m => m.Id).ToList(), ct);

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
                    homeAvatar = teamAvatar.GetValueOrDefault(m.HomeTeamId),
                    m.AwayTeamId, awayName = teamName.GetValueOrDefault(m.AwayTeamId, $"Team {m.AwayTeamId}"),
                    awayAvatar = teamAvatar.GetValueOrDefault(m.AwayTeamId),
                    m.result, m.HomeScore, m.AwayScore, m.ReplayUrl,
                    m.hasPaste, m.hasHomePaste, m.hasAwayPaste,
                    played = m.result != nameof(MatchResult.Pending),
                    mine = myTeamId != null && (m.HomeTeamId == myTeamId || m.AwayTeamId == myTeamId),
                    battleStats = statsByMatch.GetValueOrDefault(m.Id),
                }),
            });
        });

        // A snapshot of the season so far, for archiving/backup: every team's draft
        // picks, and every match's stored replay + captured team builds. Admin-only;
        // the client downloads it as a JSON file.
        api.MapGet("/leagues/{leagueId:int}/snapshot", async (
            int leagueId, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();
            var snap = await SeasonSnapshot.BuildAsync(db, leagueId, ct);
            return snap is null ? Results.NotFound() : Results.Ok(snap);
        });

        // Restore a season from a snapshot (the JSON the GET above produced). Admin-only.
        // Wipes the target league, then rebuilds its teams, draft picks, schedule and
        // recorded results, re-applying standings + per-mon stats from each stored log,
        // so the restored season is identical to the captured one. Team/match ids are
        // reassigned, so a stored local replay link is re-pointed at the new match id.
        api.MapPost("/leagues/{leagueId:int}/snapshot", async (
            int leagueId, SnapshotDto snap, ClaimsPrincipal me, AppDbContext db,
            MatchStatsRecorder recorder, IDraftNotifier notifier, CancellationToken ct) =>
        {
            if (!await me.IsAdminAsync(db, ct)) return Results.Forbid();
            var league = await db.Leagues.FirstOrDefaultAsync(l => l.Id == leagueId, ct);
            if (league is null) return Results.NotFound();
            var draft = await db.Drafts.FirstOrDefaultAsync(d => d.LeagueId == leagueId, ct);
            if (draft is null) return Results.BadRequest(new { error = "League has no draft to restore into." });

            // ── reset the league (same shape as an abort) ──
            await db.Matches.Where(m => m.LeagueId == leagueId).ExecuteDeleteAsync(ct);
            var oldTeamIds = await db.Teams.Where(t => t.LeagueId == leagueId).Select(t => t.Id).ToListAsync(ct);
            await db.Pokemon.Where(p => p.LeagueId == leagueId && p.DraftedByTeamId != null)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.DraftedByTeamId, (int?)null), ct);
            if (oldTeamIds.Count > 0)
            {
                await db.PokemonStats.Where(s => oldTeamIds.Contains(s.Pick.TeamId)).ExecuteDeleteAsync(ct);
                await db.Picks.Where(p => oldTeamIds.Contains(p.TeamId)).ExecuteDeleteAsync(ct);
                await db.DraftSkips.Where(s => oldTeamIds.Contains(s.TeamId)).ExecuteDeleteAsync(ct);
                await db.DraftSlots.Where(s => oldTeamIds.Contains(s.TeamId)).ExecuteDeleteAsync(ct);
                await db.Teams.Where(t => t.LeagueId == leagueId).ExecuteDeleteAsync(ct);
            }
            await db.DraftParticipants.Where(p => p.DraftId == draft.Id).ExecuteDeleteAsync(ct);
            await db.OfferedOptions.Where(o => o.DraftId == draft.Id).ExecuteDeleteAsync(ct);

            // ── coaches (Users) ──
            var snapCoachIds = snap.Teams.Select(t => t.CoachId).ToList();
            var users = await db.Users.Where(u => snapCoachIds.Contains(u.DiscordId)).ToDictionaryAsync(u => u.DiscordId, ct);
            foreach (var t in snap.Teams)
            {
                if (users.TryGetValue(t.CoachId, out var u)) u.Username = t.CoachName;
                else db.Users.Add(new User { DiscordId = t.CoachId, Username = t.CoachName });
            }

            // ── teams (new ids; map snapshot id -> new Team) ──
            var teamMap = new Dictionary<int, Team>();
            foreach (var t in snap.Teams)
            {
                var team = new Team { LeagueId = leagueId, Name = t.CoachName, CoachId = t.CoachId, CoachName = t.CoachName };
                db.Teams.Add(team);
                teamMap[t.Id] = team;
            }
            await db.SaveChangesAsync(ct); // assign team ids

            // ── picks: match each snapshot mon to the current pool (by sprite, then
            //    name, then dex) and mark it drafted ──
            var pool = await db.Pokemon.Where(p => p.LeagueId == leagueId).ToListAsync(ct);
            var bySprite = pool.Where(p => !string.IsNullOrEmpty(p.Sprite))
                .GroupBy(p => p.Sprite!).ToDictionary(g => g.Key, g => g.First());
            var byName = pool.GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.First());
            var byDex = pool.GroupBy(p => p.DexNumber).ToDictionary(g => g.Key, g => g.First());
            int matchedPicks = 0, missedPicks = 0;
            foreach (var p in snap.Picks)
            {
                if (!teamMap.TryGetValue(p.TeamId, out var team)) continue;
                var entry = p.Sprite is not null && bySprite.TryGetValue(p.Sprite, out var e1) ? e1
                    : byName.TryGetValue(p.Pokemon, out var e2) ? e2
                    : byDex.TryGetValue(p.DexNumber, out var e3) ? e3 : null;
                if (entry is null) { missedPicks++; continue; }
                db.Picks.Add(new Pick
                {
                    DraftId = draft.Id, TeamId = team.Id, PickNumber = p.PickNumber,
                    Tier = Enum.TryParse<Tier>(p.Tier, out var tier) ? tier : entry.Tier,
                    TeraType = p.TeraType, PokemonEntryId = entry.Id,
                    OtherOptions = p.OtherOptions, WasAutoPick = p.WasAutoPick,
                });
                entry.DraftedByTeamId = team.Id;
                matchedPicks++;
            }
            await db.SaveChangesAsync(ct); // picks exist before stats resolve mon -> pick

            // ── matches (Pending first; results/standings/stats applied after ids exist) ──
            var created = new List<(Match Match, SnapMatch Snap)>();
            foreach (var sm in snap.Matches)
            {
                if (!teamMap.TryGetValue(sm.HomeTeamId, out var home) || !teamMap.TryGetValue(sm.AwayTeamId, out var away)) continue;
                var match = new Match
                {
                    LeagueId = leagueId, Week = sm.Week, ScheduledFor = sm.ScheduledFor,
                    HomeTeamId = home.Id, AwayTeamId = away.Id, HomeTeam = home, AwayTeam = away,
                    HomeScore = sm.HomeScore, AwayScore = sm.AwayScore,
                    ReplayLog = sm.ReplayLog, ReplayHomeSide = sm.ReplayHomeSide,
                    HomeTeamExport = sm.HomeTeamExport, AwayTeamExport = sm.AwayTeamExport,
                    Result = MatchResult.Pending,
                };
                db.Matches.Add(match);
                created.Add((match, sm));
            }
            await db.SaveChangesAsync(ct); // assign match ids

            int recorded = 0;
            foreach (var (match, sm) in created)
            {
                // A stored local-renderer link must point at the NEW match id; a real
                // Showdown replay URL is kept as-is.
                match.ReplayUrl = !string.IsNullOrEmpty(sm.ReplayLog) ? $"/api/matches/{match.Id}/replay" : sm.ReplayUrl;
                if (!Enum.TryParse<MatchResult>(sm.Result, out var result) || result == MatchResult.Pending) continue;
                match.Result = result;
                MatchReporting.ApplyToStandings(match.HomeTeam, match.AwayTeam, result, +1);
                if (!string.IsNullOrEmpty(sm.ReplayLog) && sm.ReplayHomeSide is not null)
                {
                    await recorder.ApplyAsync(match, sm.ReplayHomeSide, sm.ReplayLog, result, +1, ct);
                    // Save between games so a mon's stats ACCUMULATE across its matches
                    // (the recorder reads the persisted rows) instead of inserting a
                    // duplicate per game.
                    await db.SaveChangesAsync(ct);
                }
                recorded++;
            }

            // The restored season is a finished draft.
            draft.State = DraftState.Complete;
            draft.PickDeadline = null;
            draft.CurrentIndex = 0;

            await db.SaveChangesAsync(ct);
            await notifier.DraftStateChangedAsync(draft.Id, DraftState.Complete, ct);
            await notifier.PlayersChangedAsync(ct);
            await notifier.ScheduleChangedAsync(leagueId, ct);
            return Results.Ok(new { teams = teamMap.Count, picks = matchedPicks, missedPicks, matches = created.Count, recorded });
        });

        // The scoreboard: team standings plus a handful of per-mon stat leaders,
        // all derived from the stored PokemonStat rows and the teams' match
        // records. Same numbers the Stats tab shows, rolled up into rankings.
        api.MapGet("/leagues/{leagueId:int}/scoreboard", async (
            int leagueId, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var league = await db.Leagues.FirstOrDefaultAsync(l => l.Id == leagueId, ct);
            if (league is null) return Results.NotFound();

            // Every team in the league, with its authoritative match record.
            var teams = await db.Teams
                .Where(t => t.LeagueId == leagueId)
                .Select(t => new { t.Id, t.CoachId, t.CoachName, t.Wins, t.Losses, t.Draws })
                .ToListAsync(ct);

            // The caller's own team, so the client can highlight their standings row
            // and their mons in the leaderboards.
            var myTeamId = teams.FirstOrDefault(t => t.CoachId == me.DiscordId())?.Id;

            // Discord avatar hashes for the coaches that have one, synthetic coaches
            // (dummies / sim) and anyone without an avatar simply won't be in here, so
            // the scoreboard shows an empty slot for them.
            var coachIds = teams.Select(t => t.CoachId).ToList();
            var avatarByCoach = (await db.Users
                    .Where(u => coachIds.Contains(u.DiscordId) && u.AvatarHash != null)
                    .Select(u => new { u.DiscordId, u.AvatarHash })
                    .ToListAsync(ct))
                .ToDictionary(u => u.DiscordId, u => u.AvatarHash!);

            // Every played mon in the league, with the raw fields the leaderboards
            // and the per-team KO rollups are built from. Mirrors /api/stats.
            var mons = await db.PokemonStats
                .Where(s => s.Pick.Team.LeagueId == leagueId && s.GamesPlayed > 0)
                .Select(s => new
                {
                    teamId = s.Pick.TeamId,
                    pokemon = s.Pick.PokemonEntry.Name,
                    sprite = s.Pick.PokemonEntry.Sprite,
                    dex = s.Pick.PokemonEntry.DexNumber,
                    tier = s.Pick.Tier.ToString(),
                    tera = s.Pick.TeraType,
                    trainer = s.Pick.Team.CoachName,
                    activeTurns = s.ActiveTurns,
                    teamTurns = s.Pick.Team.BattleTurns,
                    k = s.Kills,
                    d = s.Deaths,
                    dealt = s.DamageDealtDirect + s.DamageDealtIndirect,
                    taken = s.DamageTakenDirect + s.DamageTakenIndirect,
                    // Non-self team healing: HP restored to ALLIES only (Pollen Puff on
                    // an ally, Life Dew, ally Grassy Terrain). Excludes self-recovery
                    // (Rest/Recover/Leftovers/drain = HpRecovered) and healing given to
                    // enemies (HpHealedEnemy). Matches the Stats tab's "Ally-heal" column
                    // with "Healing to enemies" unchecked.
                    heal = s.HpHealed,
                })
                .ToListAsync(ct);

            // Per-team KO rollups for the standings tiebreakers.
            var koByTeam = mons
                .GroupBy(m => m.teamId)
                .ToDictionary(g => g.Key, g => (kos: g.Sum(x => x.k), deaths: g.Sum(x => x.d)));

            // Each team's MVP: the mon with the best KO differential (k - d), same
            // measure the team-page MVP badge uses. Only teams that have played
            // appear here; others get no MVP.
            var mvpByTeam = mons
                .GroupBy(m => m.teamId)
                .ToDictionary(g => g.Key, g => g
                    .OrderByDescending(x => x.k - x.d).ThenByDescending(x => x.k).ThenBy(x => x.pokemon)
                    .First());

            // Standings: every team, sorted by record differential (W - L), then
            // KO differential, then total wins, then total KOs. A team with no
            // games played still appears (all zeros), sorted to the bottom. Team id
            // is the final, stable tiebreak so the order is deterministic.
            var standings = teams
                .Select(t =>
                {
                    var ko = koByTeam.GetValueOrDefault(t.Id);
                    return new
                    {
                        teamId = t.Id,
                        discordId = t.CoachId,
                        avatarUrl = avatarByCoach.TryGetValue(t.CoachId, out var hash)
                            ? $"https://cdn.discordapp.com/avatars/{t.CoachId}/{hash}.png"
                            : null,
                        trainer = t.CoachName,
                        wins = t.Wins,
                        losses = t.Losses,
                        draws = t.Draws,
                        recordDiff = t.Wins - t.Losses,
                        koDiff = ko.kos - ko.deaths,
                        totalKos = ko.kos,
                        totalFaints = ko.deaths,
                        mvp = mvpByTeam.TryGetValue(t.Id, out var mv)
                            ? new { mv.pokemon, mv.sprite, mv.dex, mv.tier, mv.tera, kos = mv.k, faints = mv.d }
                            : null,
                    };
                })
                .OrderByDescending(s => s.recordDiff)
                .ThenByDescending(s => s.koDiff)
                .ThenByDescending(s => s.wins)
                .ThenByDescending(s => s.totalKos)
                .ThenBy(s => s.teamId)
                .ToList();

            // Leaderboards: up to the top 5 mons in each category, each with its
            // trainer. Every mon that PARTICIPATED (GamesPlayed > 0, the `mons`
            // filter) is ranked, including ones sitting at 0 in a category, their 0
            // still shows. Only ranks past the number of participants are left empty
            // for the client to grey out. Every ordering has explicit tiebreaks so
            // the result is deterministic.
            var presence = mons
                .Select(m => new { m, value = m.teamTurns > 0 ? (double)m.activeTurns / m.teamTurns : 0.0 })
                .OrderByDescending(x => x.value).ThenByDescending(x => x.m.activeTurns).ThenBy(x => x.m.pokemon)
                .Take(5)
                .Select(x => new { x.m.pokemon, x.m.trainer, x.m.teamId, x.m.sprite, x.m.dex, x.m.tier, x.m.tera, value = (double?)x.value })
                .ToList();

            var plusMinus = mons
                .Select(m => new { m, value = m.k - m.d })
                .OrderByDescending(x => x.value).ThenByDescending(x => x.m.k).ThenBy(x => x.m.pokemon)
                .Take(5)
                .Select(x => new { x.m.pokemon, x.m.trainer, x.m.teamId, x.m.sprite, x.m.dex, x.m.tier, x.m.tera, value = (double?)x.value })
                .ToList();

            var healing = mons
                .Select(m => new { m, value = m.heal })
                .OrderByDescending(x => x.value).ThenBy(x => x.m.pokemon)
                .Take(5)
                .Select(x => new { x.m.pokemon, x.m.trainer, x.m.teamId, x.m.sprite, x.m.dex, x.m.tier, x.m.tera, value = (double?)x.value })
                .ToList();

            // Damage ratio = (dealt + 1) / (taken + 1), matching the Stats tab. The +1
            // smoothing avoids dividing by zero, so a mon that never took damage just
            // gets a large finite ratio (dealt + 1) instead of infinity; the bigger
            // dealer among them still leads, with no infinity symbol to render.
            var damageRatio = mons
                .Select(m => new { m, ratio = (m.dealt + 1) / (m.taken + 1) })
                .OrderByDescending(x => x.ratio).ThenByDescending(x => x.m.dealt).ThenBy(x => x.m.pokemon)
                .Take(5)
                .Select(x => new { x.m.pokemon, x.m.trainer, x.m.teamId, x.m.sprite, x.m.dex, x.m.tier, x.m.tera,
                    value = (double?)x.ratio })
                .ToList();

            return Results.Ok(new
            {
                leagueId,
                myTeamId,
                standings,
                leaders = new { presence, plusMinus, healing, damageRatio },
            });
        });

        // Submit a replay for one of your matches. The result, winner and score,
        // is read off the replay itself, not taken on trust from the submitter.
        api.MapPost("/matches/{matchId:int}/replay", async (
            int matchId, SubmitReplayRequest req, ClaimsPrincipal me,
            AppDbContext db, ReplayScorer scorer, MatchStatsRecorder recorder,
            IHubContext<DraftHub> hub, CancellationToken ct) =>
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

            // Re-reporting is allowed (a corrected replay): back the current result
            // out of standings + stats first, from the STORED log, no re-fetch, then
            // apply the new one.
            await BackOutAsync(recorder, match, ct);

            match.Result = score.Outcome;
            match.HomeScore = score.HomeScore;
            match.AwayScore = score.AwayScore;
            match.ReplayUrl = req.ReplayUrl.Trim();
            match.ReplayLog = score.Log;          // stored so it can be backed out later
            match.ReplayHomeSide = score.HomeSide;
            match.ReportedByCoachId = myId;
            match.ReportedAt = DateTimeOffset.UtcNow;
            MatchReporting.ApplyToStandings(match.HomeTeam, match.AwayTeam, match.Result, +1);

            // Fold this replay's per-mon stats into the stats page. The scorer
            // already fetched and side-assigned the log, so no second fetch.
            if (score.Log is not null && score.HomeSide is not null)
                await recorder.ApplyAsync(match, score.HomeSide, score.Log, score.Outcome, +1, ct);

            await db.SaveChangesAsync(ct);
            await hub.Clients.All.SendAsync("scheduleChanged", new { leagueId = match.LeagueId }, ct);
            return Results.Ok(new
            {
                match.Id, result = match.Result.ToString(), match.HomeScore, match.AwayScore,
            });
        });

        // Remove a match's replay: back its result + stats out of the standings and
        // stats page, and return the match to Pending (no score). Same permissions as
        // reporting, a coach in the match, or an admin.
        api.MapDelete("/matches/{matchId:int}/replay", async (
            int matchId, ClaimsPrincipal me, AppDbContext db, MatchStatsRecorder recorder,
            IHubContext<DraftHub> hub, CancellationToken ct) =>
        {
            var match = await db.Matches
                .Include(m => m.HomeTeam).Include(m => m.AwayTeam)
                .FirstOrDefaultAsync(m => m.Id == matchId, ct);
            if (match is null) return Results.NotFound();

            var myId = me.DiscordId();
            var isPlayer = myId is not null && (match.HomeTeam.CoachId == myId || match.AwayTeam.CoachId == myId);
            if (!isPlayer && !await me.IsAdminAsync(db, ct)) return Results.Forbid();

            if (match.Result != MatchResult.Pending)
            {
                await BackOutAsync(recorder, match, ct);
                match.Result = MatchResult.Pending;
                match.HomeScore = null;
                match.AwayScore = null;
                match.ReplayUrl = null;
                match.ReplayLog = null;
                match.ReplayHomeSide = null;
                match.ReportedByCoachId = null;
                match.ReportedAt = null;
                await db.SaveChangesAsync(ct);
                await hub.Clients.All.SendAsync("scheduleChanged", new { leagueId = match.LeagueId }, ct);
            }
            return Results.Ok(new { match.Id, result = match.Result.ToString() });
        });

        // Paste a pokemonshowdown.com replay played anywhere (e.g. a coach who
        // prefers the official server) and we auto-attribute it to the most recent
        // pending match between the two teams, no need to pick the match first.
        api.MapPost("/replays/auto", async (
            SubmitReplayRequest req, ClaimsPrincipal me, AppDbContext db, ReplayScorer scorer,
            MatchStatsRecorder recorder, IHubContext<DraftHub> hub, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.ReplayUrl))
                return Results.BadRequest(new { error = "Paste a replay link first." });

            var report = await scorer.ReportFromUrlAsync(req.ReplayUrl, ct);
            if (!report.Ok) return Results.BadRequest(new { error = report.Reason });

            var match = await db.Matches
                .Include(m => m.HomeTeam).Include(m => m.AwayTeam)
                .FirstAsync(m => m.Id == report.MatchId, ct);

            // Only a coach in the identified match, or an admin, may report it.
            var myId = me.DiscordId();
            var isPlayer = myId is not null && (match.HomeTeam.CoachId == myId || match.AwayTeam.CoachId == myId);
            if (!isPlayer && !await me.IsAdminAsync(db, ct)) return Results.Forbid();

            await RecordReportAsync(db, recorder, hub, match, report, req.ReplayUrl.Trim(), myId, ct);
            return Results.Ok(new
            {
                recorded = true, match.Id, week = match.Week,
                result = match.Result.ToString(), match.HomeScore, match.AwayScore,
            });
        });

        // Server-to-server: our own Showdown server POSTs every finished battle's
        // log here (see battle-server chat plugin). We work out which scheduled
        // match it was and record it automatically, no coach action needed.
        // Not on the authenticated /api group (the plugin has no JWT); guarded by a
        // shared secret instead, so a random tunnel request can't forge results.
        var report = app.MapPost("/api/showdown/report", async (
            ShowdownReportRequest req, HttpContext ctx, AppDbContext db, ReplayScorer scorer,
            MatchStatsRecorder recorder, IHubContext<DraftHub> hub, IConfiguration cfg, CancellationToken ct) =>
        {
            var secret = cfg["Showdown:ReportSecret"];
            if (string.IsNullOrEmpty(secret) || ctx.Request.Headers["X-Report-Secret"] != secret)
                return Results.Unauthorized();
            if (string.IsNullOrWhiteSpace(req.Log)) return Results.BadRequest();

            var report = await scorer.ReportAsync(req.Log, ct);
            // Not a league game (teambuilder test, already recorded, unknown teams),
            // acknowledge and move on rather than erroring.
            if (!report.Ok) return Results.Ok(new { recorded = false, reason = report.Reason });

            var match = await db.Matches
                .Include(m => m.HomeTeam).Include(m => m.AwayTeam)
                .FirstAsync(m => m.Id == report.MatchId, ct);
            await RecordReportAsync(db, recorder, hub, match, report, replayUrl: null, reporterId: null, ct,
                p1Export: req.P1Export, p2Export: req.P2Export);
            return Results.Ok(new { recorded = true, match.Id, result = match.Result.ToString() });
        });
        if (corsPolicy is not null) report.RequireCors(corsPolicy);
    }

    /// <summary>
    /// Scrapes each played match's stored replay log into a compact per-mon strip
    /// (KOs, crits, that-game presence, ally healing) for the schedule cards. Every
    /// mon brought to a game gets a row (team preview seeds even benched ones), and
    /// a mon that never hit the field is flagged played=false so the client greys it.
    /// A malformed log is skipped rather than failing the whole schedule.
    /// </summary>
    private static async Task<Dictionary<int, List<object>>> BattleStatsAsync(
        AppDbContext db, int leagueId, List<int> teamIds, List<int> playedIds, CancellationToken ct)
    {
        var result = new Dictionary<int, List<object>>();
        if (playedIds.Count == 0) return result;

        var logs = await db.Matches
            .Where(m => playedIds.Contains(m.Id) && m.ReplayLog != null && m.ReplayHomeSide != null)
            .Select(m => new { m.Id, m.ReplayLog, m.ReplayHomeSide, m.HomeTeamId, m.AwayTeamId })
            .ToListAsync(ct);
        if (logs.Count == 0) return result;

        // Base-species → pick maps per team, so a log's "Salamence" resolves to a
        // pick drafted as "M-Salamence" on the right side. Loaded once for the league.
        var picks = await db.Picks
            .Include(p => p.PokemonEntry)
            .Where(p => teamIds.Contains(p.TeamId))
            .ToListAsync(ct);
        var byTeamBase = new Dictionary<int, Dictionary<string, Pick>>();
        foreach (var p in picks)
        {
            if (!byTeamBase.TryGetValue(p.TeamId, out var map)) byTeamBase[p.TeamId] = map = new();
            map[ReplayStatsScraper.BaseId(p.PokemonEntry.Name)] = p;
            if (!string.IsNullOrEmpty(p.PokemonEntry.Sprite))
                map[ReplayStatsScraper.BaseId(p.PokemonEntry.Sprite)] = p;
        }

        foreach (var lm in logs)
        {
            var homeSide = lm.ReplayHomeSide!;
            ReplayStatsScraper.Result scraped;
            try
            {
                scraped = ReplayStatsScraper.Scrape(lm.ReplayLog!, (side, species) =>
                {
                    var teamId = side == homeSide ? lm.HomeTeamId : lm.AwayTeamId;
                    return byTeamBase.TryGetValue(teamId, out var map)
                        ? ReplayStatsScraper.ResolveInMap(map, species) : null;
                });
            }
            catch { continue; }

            if (scraped.Stats.Count == 0) continue;
            var turns = scraped.Turns;
            result[lm.Id] = scraped.Stats
                // Home team first, then by tier (S -> C), then participants before
                // benched, starters, KOs and finally name.
                .OrderBy(kv => kv.Key.TeamId == lm.HomeTeamId ? 0 : 1)
                .ThenBy(kv => (int)kv.Key.Tier)
                .ThenByDescending(kv => kv.Value.Started || kv.Value.ActiveTurns > 0 || kv.Value.Kills > 0 || kv.Value.Deaths > 0)
                .ThenByDescending(kv => kv.Value.Started)
                .ThenByDescending(kv => kv.Value.Kills)
                .ThenBy(kv => kv.Key.PokemonEntry.Name)
                .Select(kv =>
                {
                    var g = kv.Value;
                    var played = g.Started || g.ActiveTurns > 0 || g.Kills > 0 || g.Deaths > 0 || g.Dealt > 0 || g.Taken > 0;
                    return (object)new
                    {
                        teamId = kv.Key.TeamId,
                        name = kv.Key.PokemonEntry.Name,
                        sprite = kv.Key.PokemonEntry.Sprite,
                        dex = kv.Key.PokemonEntry.DexNumber, // lets applySprite fall back to Serebii/PokeAPI art for custom megas with no gen-5 sprite (e.g. M-Barbaracle)
                        tier = kv.Key.Tier.ToString(),
                        tera = kv.Key.TeraType,
                        teraUsed = g.Terastallized, // false -> the client greys the Tera icon
                        played,
                        started = g.Started,
                        kos = g.Kills,
                        faints = g.Deaths,
                        dmg = (int)Math.Round(g.Dealt), // damage dealt to opponents, % of an HP bar
                        crits = g.Crits,
                        presence = turns > 0 ? (int)Math.Round(100.0 * g.ActiveTurns / turns) : 0,
                        heal = (int)Math.Round(g.Healed),
                    };
                })
                .ToList();
        }
        return result;
    }

    /// <summary>
    /// Renders a raw Showdown battle log with Showdown's OWN modern replay client
    /// (replays.js), not the older replay-embed. It's the exact page you get at
    /// replay.pokemonshowdown.com (same nav, header, controls, speed/sound, and
    /// download) because it loads the identical scripts and stylesheets from the
    /// CDN. The trick that avoids any backend: the client derives a "replay id"
    /// from the URL (everything after the final slash) and, if it finds inline
    /// &lt;script&gt; blocks keyed by that id, renders them directly instead of
    /// fetching. This endpoint is /api/matches/{id}/replay, so that id is always
    /// the literal "replay", hence replaylog-replay / replaydata-replay below.
    /// </summary>
    private static string ReplayHtml(string log)
    {
        // The client un-escapes `<\/` back to `</`, so escape every `</` in the log
        // (this also neutralises any literal </script that would close the block).
        var logData = log.Replace("</", "<\\/", StringComparison.Ordinal);

        // Page-header metadata (the "<format>: P1 vs. P2" title line + date). Pull
        // names and format straight from the log so it matches the battle.
        string? p1 = null, p2 = null, tier = null;
        foreach (var line in log.Split('\n'))
        {
            var f = line.Split('|');
            if (f.Length >= 4 && f[1] == "player")
            {
                if (f[2] == "p1") p1 = f[3];
                else if (f[2] == "p2") p2 = f[3];
            }
            else if (f.Length >= 3 && f[1] == "tier" && tier is null) tier = f[2];
        }
        // Default JSON escaping renders `<`/`>`/`&` as \uXXXX, so this can't break
        // out of the <script> even if a player names themselves "</script>".
        var meta = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = "replay",
            format = string.IsNullOrWhiteSpace(tier) ? "Draft League" : tier,
            players = new[] { p1 ?? "Player 1", p2 ?? "Player 2" },
            uploadtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            rating = (int?)null,
        });

        // Mirrors replay.pokemonshowdown.com's page: the four stylesheets, the PS
        // nav, an empty #main the app renders into, the full battle client + the
        // replay app scripts (all deferred, in dependency order), then the inline
        // log/data, then replays.js which mounts everything.
        return $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Battle replay</title>
            <link rel="stylesheet" href="https://pokemonshowdown.com/style/global.css" />
            <link rel="stylesheet" href="https://play.pokemonshowdown.com/style/font-awesome.css" />
            <link rel="stylesheet" href="https://play.pokemonshowdown.com/style/battle.css" />
            <link rel="stylesheet" href="https://play.pokemonshowdown.com/style/utilichart.css" />
            <style>
              /* The replay page's OWN layout CSS (its inline <style>, not in any
                 CDN sheet): centres and width-caps the app. Without it .bar-wrapper
                 is full-width and everything is left-justified. */
              @media (max-width:820px) {
                .battle { margin: 0 auto; }
                .battle-log { margin: 7px auto 0; max-width: 640px; height: 300px; position: static; }
              }
              .optgroup { display: inline-block; line-height: 22px; font-size: 10pt; vertical-align: top; }
              .optgroup .button { height: 25px; padding-top: 0; padding-bottom: 0; }
              .optgroup button.button { padding-left: 12px; padding-right: 12px; }
              .linklist { list-style: none; margin: 0.5em 0; padding: 0; }
              .linklist li { padding: 2px 0; }
              .sidebar { float: left; width: 320px; }
              .bar-wrapper { max-width: 1100px; margin: 0 auto; }
              .bar-wrapper.has-sidebar { max-width: 1430px; }
              .mainbar { margin: 0; padding-right: 1px; }
              .mainbar.has-sidebar { margin-left: 330px; }
              @media (min-width: 1511px) {
                .sidebar { width: 400px; }
                .bar-wrapper.has-sidebar { max-width: 1510px; }
                .mainbar.has-sidebar { margin-left: 410px; }
              }
              .section.first-section { margin-top: 9px; }
              .blocklink small { white-space: normal; }
              .button { vertical-align: middle; }
              .replay-controls { padding-top: 10px; }
              .replay-controls h1 { font-size: 16pt; font-weight: normal; color: #CCC; }
              .pagelink { text-align: center; }
              .pagelink a { width: 150px; }
              .textbox, .button { font-size: 11pt; vertical-align: middle; }
              @media (max-width: 450px) { .button { font-size: 9pt; } }
            </style>
            </head><body>
            <div><header><div class="nav-wrapper"><ul class="nav">
              <li><a class="button nav-first" href="https://pokemonshowdown.com/">Home</a></li>
              <li><a class="button" href="https://pokemonshowdown.com/dex/">Pok&eacute;dex</a></li>
              <li><a class="button cur" href="https://replay.pokemonshowdown.com/">Replay</a></li>
              <li><a class="button purplebutton" href="https://smogon.com/dex/" target="_blank" rel="noopener">Strategy</a></li>
              <li><a class="button nav-last purplebutton" href="https://smogon.com/forums/" target="_blank" rel="noopener">Forum</a></li>
              <li><a class="button greenbutton nav-first nav-last" href="https://play.pokemonshowdown.com/">Play</a></li>
            </ul></div></header>
            <div class="main" id="main"><noscript><section class="section">You need to enable JavaScript to use this page; sorry!</section></noscript></div>
            </div>
            <script defer nomodule src="https://play.pokemonshowdown.com/js/lib/ps-polyfill.js"></script>
            <script defer src="https://play.pokemonshowdown.com/js/lib/preact.min.js"></script>
            <script defer src="https://play.pokemonshowdown.com/config/config.js"></script>
            <script defer src="https://play.pokemonshowdown.com/js/lib/jquery-1.11.0.min.js"></script>
            <script defer src="https://play.pokemonshowdown.com/js/lib/html-sanitizer-minified.js"></script>
            <script defer src="https://play.pokemonshowdown.com/js/battle-sound.js"></script>
            <script defer src="https://play.pokemonshowdown.com/js/battledata.js"></script>
            <script defer src="https://play.pokemonshowdown.com/data/pokedex-mini.js"></script>
            <script defer src="https://play.pokemonshowdown.com/data/pokedex-mini-bw.js"></script>
            <script defer src="https://play.pokemonshowdown.com/data/graphics.js"></script>
            <script defer src="https://play.pokemonshowdown.com/data/pokedex.js"></script>
            <script defer src="https://play.pokemonshowdown.com/data/moves.js"></script>
            <script defer src="https://play.pokemonshowdown.com/data/abilities.js"></script>
            <script defer src="https://play.pokemonshowdown.com/data/items.js"></script>
            <script defer src="https://play.pokemonshowdown.com/data/teambuilder-tables.js"></script>
            <script defer src="https://play.pokemonshowdown.com/js/battle-tooltips.js"></script>
            <script defer src="https://play.pokemonshowdown.com/js/battle.js"></script>
            <script defer src="https://replay.pokemonshowdown.com/js/utils.js"></script>
            <script defer src="https://replay.pokemonshowdown.com/js/replays-battle.js"></script>
            <script defer src="https://replay.pokemonshowdown.com/js/replays-index.js"></script>
            <script type="text/plain" class="log" id="replaylog-replay">
            {{logData}}
            </script>
            <script type="application/json" class="data" id="replaydata-replay">
            {{meta}}
            </script>
            <script defer src="https://replay.pokemonshowdown.com/js/replays.js"></script>
            <script>
              /* The battle scene is a fixed 640x360 canvas, so on a big screen it
                 floats in a sea of empty space. Zoom the whole replay (battle, log
                 and controls together) to fill the room below the nav, capped so
                 it fits both dimensions and never leaks off screen. Re-runs as the
                 replay mounts asynchronously, and on resize. */
              (function () {
                function fit() {
                  var w = document.querySelector('.bar-wrapper');
                  if (!w) return;
                  w.style.zoom = '';
                  var top = w.getBoundingClientRect().top;
                  var availW = window.innerWidth - 12;
                  var availH = window.innerHeight - top - 12;
                  var natW = w.offsetWidth, natH = w.offsetHeight;
                  if (!natW || !natH) return;
                  var s = Math.min(availW / natW, availH / natH);
                  if (!isFinite(s) || s <= 0) return;
                  s = Math.max(1, Math.min(s, 2));
                  w.style.zoom = s;
                }
                var raf = 0;
                function schedule() { cancelAnimationFrame(raf); raf = requestAnimationFrame(fit); }
                window.addEventListener('resize', schedule);
                var n = 0, iv = setInterval(function () { schedule(); if (++n > 40) clearInterval(iv); }, 150);
              })();
            </script>
            </body></html>
            """;
    }

    /// <summary>
    /// Applies an auto-identified result to its (still-pending) match. Delegates to
    /// <see cref="MatchReporting.RecordReportAsync"/>, the one place a scored game is
    /// recorded, shared with the Showdown auto-report and the season simulator.
    /// </summary>
    private static Task RecordReportAsync(
        AppDbContext db, MatchStatsRecorder recorder, IHubContext<DraftHub> hub,
        Match match, ReplayScorer.AutoReport report, string? replayUrl, string? reporterId, CancellationToken ct,
        string? p1Export = null, string? p2Export = null) =>
        MatchReporting.RecordReportAsync(db, recorder, hub, match, report, replayUrl, reporterId, ct, p1Export, p2Export);

    /// <summary>
    /// Backs a match's currently-recorded result out of the standings and the stats
    /// page, using its STORED log (no re-fetch). No-op if the match is Pending.
    /// </summary>
    private static async Task BackOutAsync(MatchStatsRecorder recorder, Match match, CancellationToken ct)
    {
        if (match.Result == MatchResult.Pending) return;
        MatchReporting.ApplyToStandings(match.HomeTeam, match.AwayTeam, match.Result, -1);
        if (!string.IsNullOrEmpty(match.ReplayLog) && match.ReplayHomeSide is not null)
            await recorder.ApplyAsync(match, match.ReplayHomeSide, match.ReplayLog, match.Result, -1, ct);
    }

    /// <summary>
    /// Lays down the round-robin for a league's teams over <paramref name="weeks"/>
    /// weeks (the league's configured SeasonWeeks), replacing any existing schedule.
    /// The season is capped at ONE full single round-robin so no pair ever plays
    /// twice: that's (players - 1) weeks for an even count, or (players) weeks for an
    /// odd count, where each week a different player takes a bye and every player
    /// byes exactly once. The configured weeks only ever shortens the season below
    /// that cap. Called automatically when the draft starts. No-op with fewer than
    /// two teams, or once results are in, so a re-start can't wipe a live season.
    /// </summary>
    /// <returns>The number of matches created (0 if it was skipped).</returns>
    public static async Task<int> GenerateAsync(AppDbContext db, int leagueId, int weeks, CancellationToken ct = default)
    {
        if (weeks < 1) weeks = 1;

        var teamIds = await db.Teams.Where(t => t.LeagueId == leagueId).Select(t => t.Id).ToListAsync(ct);
        if (teamIds.Count < 2) return 0;

        // Never schedule more than one full single round-robin, so a matchup can't
        // repeat. An even roster completes in (n-1) weeks; an odd one needs n weeks
        // so every player gets their single bye. The configured weeks can only make
        // the season SHORTER than this, never longer.
        var maxWeeks = SingleRoundRobinWeeks(teamIds.Count);
        if (weeks > maxWeeks) weeks = maxWeeks;

        // Never disturb a season that's already producing results.
        if (await db.Matches.AnyAsync(m => m.LeagueId == leagueId && m.Result != MatchResult.Pending, ct))
            return 0;

        await db.Matches.Where(m => m.LeagueId == leagueId).ExecuteDeleteAsync(ct);

        // Each week's game is due by Sunday 23:59 (UTC) of that week. Anchor on the
        // first Sunday on or after today, then step one week per round; the client
        // renders this as the "Due by" stamp.
        var today = DateTime.UtcNow.Date;
        var daysToSunday = ((int)DayOfWeek.Sunday - (int)today.DayOfWeek + 7) % 7;
        var firstDue = new DateTimeOffset(today.AddDays(daysToSunday), TimeSpan.Zero)
            .AddHours(23).AddMinutes(59);
        var rounds = RoundRobin(teamIds, weeks);
        for (var w = 0; w < rounds.Count; w++)
            foreach (var (home, away) in rounds[w])
                db.Matches.Add(new Match
                {
                    LeagueId = leagueId,
                    Week = w + 1,
                    HomeTeamId = home,
                    AwayTeamId = away,
                    ScheduledFor = firstDue.AddDays(7 * w),
                });

        await db.SaveChangesAsync(ct);
        return rounds.Sum(x => x.Count);
    }

    /// <summary>
    /// Weeks in ONE full single round-robin for <paramref name="players"/> players:
    /// (players - 1) when even, players when odd (the extra week carries the byes, so
    /// each player sits out exactly once). This is the season-length cap.
    /// </summary>
    public static int SingleRoundRobinWeeks(int players) =>
        players < 2 ? 1 : (players % 2 == 0 ? players - 1 : players);

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

}
