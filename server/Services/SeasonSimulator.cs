using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// Development-only: wipes a league and rebuilds it as a finished season from
/// canned data (Data/sim-season.json), 14 teams with their full drafted
/// rosters, the draft marked Complete, and matches imported (and scored) from a
/// list of real Showdown replays. Lets the schedule/standings/team-page UI be
/// exercised without hand-drafting and hand-playing a whole season.
///
/// Matches are attributed by Showdown username: the canned data's "trainer" is
/// the account name, so each replay's |player| lines map straight to a team.
/// </summary>
public class SeasonSimulator(AppDbContext db, HttpClient http, ILogger<SeasonSimulator> log)
{
    public record SimResult(int Teams, int Picks, int Matches, int SkippedReplays);

    private record PickRow(
        [property: JsonPropertyName("pick")] int Pick,
        [property: JsonPropertyName("tier")] string Tier,
        [property: JsonPropertyName("pokemon")] string Pokemon,
        [property: JsonPropertyName("tera")] string? Tera,
        [property: JsonPropertyName("trainer")] string Trainer,
        [property: JsonPropertyName("team")] string Team);

    private record SeasonData(
        [property: JsonPropertyName("picks")] List<PickRow> Picks,
        [property: JsonPropertyName("replays")] List<string> Replays);

    public async Task<SimResult> SimulateAsync(int draftId, CancellationToken ct = default)
    {
        var draft = await db.Drafts.Include(d => d.League).FirstOrDefaultAsync(d => d.Id == draftId, ct)
                    ?? throw new InvalidOperationException("Draft not found");
        var leagueId = draft.LeagueId;
        var data = LoadData();

        // ── wipe the league back to a blank slate ──────────────────────────
        // Order matters: matches reference teams with Restrict, so they go first.
        await db.Matches.Where(m => m.LeagueId == leagueId).ExecuteDeleteAsync(ct);
        await db.PokemonStats.Where(s => s.Pick.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.Picks.Where(p => p.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.DraftSlots.Where(s => s.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.DraftParticipants.Where(p => p.DraftId == draftId).ExecuteDeleteAsync(ct);
        await db.Pokemon.Where(p => p.LeagueId == leagueId && p.DraftedByTeamId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DraftedByTeamId, (int?)null), ct);
        await db.Teams.Where(t => t.LeagueId == leagueId).ExecuteDeleteAsync(ct);

        // ── teams (one per trainer, in first-seen order) ───────────────────
        // A canned trainer is bound to a real logged-in account when one exists,
        // so a member who has signed in owns their sim team (and sees it as "my
        // team") instead of a name-derived stand-in. Real accounts are matched by
        // normalised handle, the same ToId the stand-in id is built from, so
        // Discord handles like ".hozer"/"hoZer" and "mr.whale."/"mrwhale" still
        // line up. A stand-in is only fabricated for trainers nobody has claimed.
        var trainerOrder = data.Picks.Select(p => p.Trainer).Distinct().ToList();
        var teamByTrainer = new Dictionary<string, Team>();

        var realByHandle = (await db.Users.ToListAsync(ct))
            .Where(u => u.DiscordId != ToId(u.Username))   // a real account, not a prior stand-in
            .GroupBy(u => ToId(u.Username))
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var trainer in trainerOrder)
        {
            var row = data.Picks.First(p => p.Trainer == trainer);
            var handle = ToId(trainer);

            string coachId, coachName;
            if (realByHandle.TryGetValue(handle, out var real))
            {
                // Bind the real account; drop any leftover stand-in for this
                // trainer so the roster doesn't list them twice.
                coachId = real.DiscordId;
                coachName = real.Username;
                await db.Users.Where(u => u.DiscordId == handle).ExecuteDeleteAsync(ct);
            }
            else
            {
                // No account claimed yet, keep the name-derived stand-in so the
                // player list still shows them (upserted; a prior sim may exist).
                coachId = handle;
                coachName = trainer;
                var user = await db.Users.FirstOrDefaultAsync(u => u.DiscordId == handle, ct);
                if (user is null)
                    db.Users.Add(new User { DiscordId = handle, Username = trainer });
                else
                    user.Username = trainer;
            }

            // Team is shown by its coach's username / dummy name, no separate team name.
            var team = new Team { LeagueId = leagueId, Name = coachName, CoachId = coachId, CoachName = coachName };
            db.Teams.Add(team);
            teamByTrainer[trainer] = team;
        }
        await db.SaveChangesAsync(ct); // assign team + user ids

        // ── pool + picks ───────────────────────────────────────────────────
        var pool = await db.Pokemon.Where(p => p.LeagueId == leagueId).ToListAsync(ct);
        var poolByName = pool.ToDictionary(p => ToId(p.Name), p => p);

        foreach (var row in data.Picks.OrderBy(p => p.Pick))
        {
            if (!poolByName.TryGetValue(ToId(row.Pokemon), out var mon))
            {
                // Not in the sheet pool, create a display-only entry. Sprite is a
                // best-effort Showdown slug so most render; the season test cares
                // about results, not sprite fidelity.
                mon = new PokemonEntry
                {
                    LeagueId = leagueId,
                    Name = row.Pokemon,
                    Tier = ParseTier(row.Tier),
                    Sprite = SpriteSlug(row.Pokemon),
                };
                db.Pokemon.Add(mon);
                poolByName[ToId(row.Pokemon)] = mon;
            }

            var team = teamByTrainer[row.Trainer];
            db.Picks.Add(new Pick
            {
                DraftId = draftId,
                PickNumber = row.Pick,
                TeamId = team.Id,
                PokemonEntry = mon,
                Tier = ParseTier(row.Tier),
                TeraType = string.IsNullOrWhiteSpace(row.Tera) ? null : row.Tera,
            });
            mon.DraftedByTeam = team;
        }

        // ── mark the draft complete, with a snake order for appearances ────
        var teams = trainerOrder.Select(t => teamByTrainer[t]).ToList();
        var slotsPerRoster = draft.League.TierRules.Sum(r => r.SlotsPerTeam);
        var pos = 0;
        for (var round = 0; round < slotsPerRoster; round++)
        {
            var seq = round % 2 == 0 ? teams : Enumerable.Reverse(teams);
            foreach (var t in seq)
                db.DraftSlots.Add(new DraftSlot { DraftId = draftId, Position = pos++, TeamId = t.Id });
        }
        draft.CurrentIndex = pos;
        draft.State = DraftState.Complete;
        draft.PickDeadline = null;
        await db.SaveChangesAsync(ct);

        // ── matches from replays ───────────────────────────────────────────
        // Roster fingerprints: the Showdown usernames in a replay aren't our
        // trainer names, so we identify each side by matching its brought mons
        // (name + sprite slug) against the drafted rosters.
        var rosters = teams.ToDictionary(t => t, _ => new HashSet<string>());
        foreach (var row in data.Picks)
        {
            var mon = poolByName[ToId(row.Pokemon)];
            var set = rosters[teamByTrainer[row.Trainer]];
            set.Add(ToId(mon.Name));
            if (!string.IsNullOrEmpty(mon.Sprite)) set.Add(ToId(mon.Sprite));
        }

        // Resolve a replay's mons to drafted picks by base species (mega/base
        // share a base), and give each pick a fresh stat row to accumulate into.
        var picks = await db.Picks.Include(p => p.PokemonEntry).Where(p => p.DraftId == draftId).ToListAsync(ct);
        var pickByTeamBase = new Dictionary<int, Dictionary<string, Pick>>();
        var statByPick = new Dictionary<int, PokemonStat>();
        foreach (var p in picks)
        {
            if (!pickByTeamBase.TryGetValue(p.TeamId, out var map))
                pickByTeamBase[p.TeamId] = map = new Dictionary<string, Pick>();
            map[BaseId(p.PokemonEntry.Name)] = p;
            if (!string.IsNullOrEmpty(p.PokemonEntry.Sprite)) map[BaseId(p.PokemonEntry.Sprite)] = p;

            var stat = new PokemonStat { PickId = p.Id };
            db.PokemonStats.Add(stat);
            statByPick[p.Id] = stat;
        }

        var matches = 0;
        var skipped = 0;
        var index = 0;
        foreach (var url in data.Replays)
        {
            var m = await ImportReplayAsync(leagueId, url, rosters, pickByTeamBase, statByPick, week: index / 7 + 1, ct);
            if (m) matches++; else skipped++;
            index++;
        }
        await db.SaveChangesAsync(ct);

        log.LogInformation("Simulated season for league {League}: {Teams} teams, {Picks} picks, {Matches} matches ({Skipped} replays skipped)",
            leagueId, teams.Count, data.Picks.Count, matches, skipped);
        return new SimResult(teams.Count, data.Picks.Count, matches, skipped);
    }

    private async Task<bool> ImportReplayAsync(
        int leagueId, string url, Dictionary<Team, HashSet<string>> rosters,
        Dictionary<int, Dictionary<string, Pick>> pickByTeamBase, Dictionary<int, PokemonStat> statByPick,
        int week, CancellationToken ct)
    {
        string battleLog;
        try
        {
            var body = await http.GetStringAsync(url.TrimEnd('/') + ".json", ct);
            using var doc = JsonDocument.Parse(body);
            battleLog = doc.RootElement.TryGetProperty("log", out var l) ? l.GetString() ?? "" : "";
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Sim: couldn't fetch replay {Url}", url);
            return false;
        }

        var parsed = ParseLog(battleLog);
        if (parsed.Players.Count < 2) return false;

        // Identify each side by roster overlap. A confident match needs several
        // of the side's brought mons to be a single team's mons.
        Team? SideTeam(string side)
        {
            var brought = parsed.Brought.GetValueOrDefault(side);
            if (brought is null) return null;
            Team? best = null;
            var bestOverlap = 0;
            foreach (var (team, ids) in rosters)
            {
                var overlap = brought.Count(ids.Contains);
                if (overlap > bestOverlap) { bestOverlap = overlap; best = team; }
            }
            return bestOverlap >= 3 ? best : null;
        }

        var home = SideTeam("p1");
        var away = SideTeam("p2");
        if (home is null || away is null || home.Id == away.Id) return false;

        MatchResult result;
        if (parsed.Tie) result = MatchResult.Draw;
        else
        {
            var winSide = parsed.Players.FirstOrDefault(kv =>
                string.Equals(kv.Value, parsed.Winner, StringComparison.OrdinalIgnoreCase)).Key;
            if (winSide is null) return false;
            result = winSide == "p1" ? MatchResult.HomeWin : MatchResult.AwayWin;
        }

        // "6v6": mons standing = 6 − own faints. Approximate but plausible.
        var homeScore = Math.Max(0, 6 - parsed.Faints.GetValueOrDefault("p1"));
        var awayScore = Math.Max(0, 6 - parsed.Faints.GetValueOrDefault("p2"));

        db.Matches.Add(new Match
        {
            LeagueId = leagueId,
            Week = week,
            HomeTeamId = home.Id,
            AwayTeamId = away.Id,
            Result = result,
            HomeScore = homeScore,
            AwayScore = awayScore,
            ReplayUrl = url,
            ReportedAt = DateTimeOffset.UtcNow,
            ScheduledFor = null,
        });
        ApplyToStandings(home, away, result);

        // Scrape per-mon stats and fold them into each pick's running totals.
        var scraped = ReplayStatsScraper.Scrape(battleLog, (side, species) =>
        {
            var team = side == "p1" ? home : away;
            return pickByTeamBase.TryGetValue(team.Id, out var map) ? ReplayStatsScraper.ResolveInMap(map, species) : null;
        });
        // Season-presence denominator: the game's turn count (one field slot per
        // turn). A team's mons sum to 100% in singles and 200% in doubles, where
        // two mons are active each turn.
        home.BattleTurns += scraped.Turns;
        away.BattleTurns += scraped.Turns;
        foreach (var (pick, gs) in scraped.Stats)
        {
            if (!statByPick.TryGetValue(pick.Id, out var st)) continue;
            st.GamesPlayed++;
            st.Kills += gs.Kills;
            st.Deaths += gs.Deaths;
            st.Crits += gs.Crits;
            st.ActiveTurns += gs.ActiveTurns;
            st.PlayedTurns += scraped.Turns; // this game's turns count toward usage presence

            st.DamageDealtDirect += gs.DealtDirect;
            st.DamageDealtIndirect += gs.DealtIndirect;
            st.DamageDealtAllyDirect += gs.DealtAllyDirect;
            st.DamageDealtAllyIndirect += gs.DealtAllyIndirect;
            st.DamageTakenDirect += gs.TakenDirect;
            st.DamageTakenIndirect += gs.TakenIndirect;
            st.DamageTakenSelf += gs.TakenSelf;
            st.HpRecovered += gs.Recovered;
            st.HpHealed += gs.Healed;
            st.HpHealedEnemy += gs.HealedEnemy;
            if (result != MatchResult.Draw)
            {
                var isHome = pick.TeamId == home.Id;
                if (isHome ? result == MatchResult.HomeWin : result == MatchResult.AwayWin) st.Wins++;
                else st.Losses++;
            }
        }
        return true;
    }

    private static void ApplyToStandings(Team home, Team away, MatchResult result)
    {
        switch (result)
        {
            case MatchResult.HomeWin: home.Wins++; away.Losses++; break;
            case MatchResult.AwayWin: away.Wins++; home.Losses++; break;
            case MatchResult.Draw: home.Draws++; away.Draws++; break;
        }
    }

    // ── log parsing ────────────────────────────────────────────────────────

    private record Parsed(
        string? Winner, bool Tie,
        Dictionary<string, string> Players,
        Dictionary<string, HashSet<string>> Brought,
        Dictionary<string, int> Faints);

    private static Parsed ParseLog(string log)
    {
        string? winner = null;
        var tie = false;
        var players = new Dictionary<string, string>();
        var brought = new Dictionary<string, HashSet<string>>();
        var faints = new Dictionary<string, int>();

        foreach (var raw in log.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || line[0] != '|') continue;
            var parts = line.Split('|'); // parts[0] is empty (leading '|')

            switch (parts.Length > 1 ? parts[1] : "")
            {
                case "player" when parts.Length > 3 && !string.IsNullOrEmpty(parts[3]):
                    players[parts[2]] = parts[3];
                    break;
                // Team-preview line: |poke|p1|Cresselia, F|item. Gives the full
                // brought side, ideal for roster matching.
                case "poke" when parts.Length > 3:
                    var species = parts[3].Split(',')[0].Trim();
                    if (species.Length > 0)
                    {
                        if (!brought.TryGetValue(parts[2], out var set))
                            brought[parts[2]] = set = [];
                        set.Add(ToId(species));
                    }
                    break;
                case "win" when parts.Length > 2:
                    winner = parts[2].Trim();
                    break;
                case "tie":
                    tie = true;
                    break;
                case "faint" when parts.Length > 2 && parts[2].Length >= 2:
                    var side = parts[2][..2]; // "p1a: X" -> "p1"
                    faints[side] = faints.GetValueOrDefault(side) + 1;
                    break;
            }
        }
        return new Parsed(winner, tie, players, brought, faints);
    }

    // ── helpers ──────────────────────────────────────────────────────────

    /// <summary>Showdown-style id: lowercase, alphanumerics only.</summary>
    private static string ToId(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    private static Tier ParseTier(string t) => Enum.Parse<Tier>(t, ignoreCase: true);

    /// <summary>
    /// Species id with the mega suffix stripped, so a mon drafted as "M-Salamence"
    /// (sprite "salamence-mega") and the replay's base "Salamence" both key to the
    /// same pick. Regional/other forms are kept, they're distinct draft picks.
    /// </summary>
    private static string BaseId(string species)
    {
        var id = ToId(species);
        if (id.EndsWith("megax") || id.EndsWith("megay")) return id[..^5];
        if (id.EndsWith("mega")) return id[..^4];
        return id;
    }

    // Regional / form suffixes → the Showdown sprite slug tail.
    private static readonly Dictionary<string, string> FormSuffix = new()
    {
        ["A"] = "alola", ["G"] = "galar", ["H"] = "hisui", ["PC"] = "paldeacombat",
        ["T"] = "therian", ["W"] = "wellspring", ["C"] = "cornerstone",
        ["S"] = "sky", ["D"] = "defense", ["B"] = "bloodmoon",
    };

    /// <summary>
    /// Best-effort Showdown gen5 sprite slug for a display name. Handles "M-"
    /// megas, "-Incarnate" (I → base), a few regional/form suffixes, and numbered
    /// forms (Zygarde-50). Anything unusual just toIds the base, a wrong sprite,
    /// not a crash, which is fine for a test season.
    /// </summary>
    private static string SpriteSlug(string name)
    {
        if (name.StartsWith("M-", StringComparison.Ordinal))
            return ToId(name[2..]) + "-mega";

        var dash = name.LastIndexOf('-');
        if (dash > 0)
        {
            var baseName = name[..dash];
            var suffix = name[(dash + 1)..];
            if (suffix is "I") return ToId(baseName);                 // Incarnate = base form
            if (int.TryParse(suffix, out _)) return ToId(baseName);   // Zygarde-50
            if (FormSuffix.TryGetValue(suffix, out var tail)) return ToId(baseName) + "-" + tail;
        }
        return ToId(name);
    }

    private static SeasonData LoadData()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "sim-season.json");
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<SeasonData>(stream)
               ?? throw new InvalidOperationException("sim-season.json is empty or malformed");
    }
}
