using System.Security.Claims;
using DraftLeague.Web.Data;
using DraftLeague.Web.Hubs;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Api;

public record SubmitReplayRequest(string ReplayUrl);
public record ShowdownReportRequest(string Log);

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
            // Teams are shown by their coach's username / dummy name — the league
            // doesn't use separate team names.
            var teamName = teams.ToDictionary(t => t.Id, t => t.CoachName);

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
                })
                .ToListAsync(ct);

            // "This week" is the earliest week that still has an unplayed match —
            // the front of the season. Everything before it is history.
            var currentWeek = matches
                .Where(m => m.result == nameof(MatchResult.Pending))
                .Select(m => (int?)m.Week)
                .Min();

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
                    m.AwayTeamId, awayName = teamName.GetValueOrDefault(m.AwayTeamId, $"Team {m.AwayTeamId}"),
                    m.result, m.HomeScore, m.AwayScore, m.ReplayUrl,
                    played = m.result != nameof(MatchResult.Pending),
                    mine = myTeamId != null && (m.HomeTeamId == myTeamId || m.AwayTeamId == myTeamId),
                }),
            });
        });

        // Submit a replay for one of your matches. The result — winner and score —
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
            // out of standings + stats first — from the STORED log, no re-fetch — then
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
        // reporting — a coach in the match, or an admin.
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
        // pending match between the two teams — no need to pick the match first.
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
        // match it was and record it automatically — no coach action needed.
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
            // Not a league game (teambuilder test, already recorded, unknown teams) —
            // acknowledge and move on rather than erroring.
            if (!report.Ok) return Results.Ok(new { recorded = false, reason = report.Reason });

            var match = await db.Matches
                .Include(m => m.HomeTeam).Include(m => m.AwayTeam)
                .FirstAsync(m => m.Id == report.MatchId, ct);
            await RecordReportAsync(db, recorder, hub, match, report, replayUrl: null, reporterId: null, ct);
            return Results.Ok(new { recorded = true, match.Id, result = match.Result.ToString() });
        });
        if (corsPolicy is not null) report.RequireCors(corsPolicy);
    }

    /// <summary>
    /// Renders a raw Showdown battle log with Showdown's OWN modern replay client
    /// (replays.js), not the older replay-embed. It's the exact page you get at
    /// replay.pokemonshowdown.com — same nav, header, controls, speed/sound, and
    /// download — because it loads the identical scripts and stylesheets from the
    /// CDN. The trick that avoids any backend: the client derives a "replay id"
    /// from the URL (everything after the final slash) and, if it finds inline
    /// &lt;script&gt; blocks keyed by that id, renders them directly instead of
    /// fetching. This endpoint is /api/matches/{id}/replay, so that id is always
    /// the literal "replay" — hence replaylog-replay / replaydata-replay below.
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
                 and controls together) to fill the room below the nav — capped so
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
    /// <see cref="MatchReporting.RecordReportAsync"/> — the one place a scored game is
    /// recorded, shared with the Showdown auto-report and the season simulator.
    /// </summary>
    private static Task RecordReportAsync(
        AppDbContext db, MatchStatsRecorder recorder, IHubContext<DraftHub> hub,
        Match match, ReplayScorer.AutoReport report, string? replayUrl, string? reporterId, CancellationToken ct) =>
        MatchReporting.RecordReportAsync(db, recorder, hub, match, report, replayUrl, reporterId, ct);

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
    /// RoundRobin repeats the cycle with home/away swapped each time round, so a long
    /// enough season plays each pair multiple times. Called automatically when the
    /// draft starts. No-op with fewer than two teams, or once results are in, so a
    /// re-start can't wipe a live season.
    /// </summary>
    /// <returns>The number of matches created (0 if it was skipped).</returns>
    public static async Task<int> GenerateAsync(AppDbContext db, int leagueId, int weeks, CancellationToken ct = default)
    {
        if (weeks < 1) weeks = 1;

        var teamIds = await db.Teams.Where(t => t.LeagueId == leagueId).Select(t => t.Id).ToListAsync(ct);
        if (teamIds.Count < 2) return 0;

        // Never disturb a season that's already producing results.
        if (await db.Matches.AnyAsync(m => m.LeagueId == leagueId && m.Result != MatchResult.Pending, ct))
            return 0;

        await db.Matches.Where(m => m.LeagueId == leagueId).ExecuteDeleteAsync(ct);

        // Weekly cadence off a clean anchor (midnight UTC today), so each week's
        // matches carry a plausible date the client can show.
        var seasonStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var rounds = RoundRobin(teamIds, weeks);
        for (var w = 0; w < rounds.Count; w++)
            foreach (var (home, away) in rounds[w])
                db.Matches.Add(new Match
                {
                    LeagueId = leagueId,
                    Week = w + 1,
                    HomeTeamId = home,
                    AwayTeamId = away,
                    ScheduledFor = seasonStart.AddDays(7 * w),
                });

        await db.SaveChangesAsync(ct);
        return rounds.Sum(x => x.Count);
    }

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
