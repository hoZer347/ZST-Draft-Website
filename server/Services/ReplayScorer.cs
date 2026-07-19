using System.Text.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// Turns a Pokémon Showdown replay URL into a scored result for a match.
///
/// The replay log names the two Showdown accounts, but those aren't our coach
/// names — so we don't trust them to say who won which side. Instead we read the
/// species each side brought and match them against the two teams' drafted
/// rosters: the side whose mons are a team's mons *is* that team. The winner line
/// then tells us which side won, and the faint count gives the mons-remaining
/// score.
/// </summary>
public class ReplayScorer(AppDbContext db, HttpClient http, ILogger<ReplayScorer> logger)
{
    public record Result(
        bool Ok,
        string? Error = null,
        MatchResult Outcome = MatchResult.Pending,
        int HomeScore = 0,
        int AwayScore = 0);

    /// <summary>
    /// Fetches and scores <paramref name="replayUrl"/> against the two teams in
    /// <paramref name="match"/>. Read-only: the caller persists the returned score.
    /// </summary>
    public async Task<Result> ScoreAsync(Match match, string replayUrl, CancellationToken ct = default)
    {
        if (!TryNormalize(replayUrl, out var jsonUrl))
            return new(false, "That doesn't look like a pokemonshowdown.com replay link.");

        string log;
        try
        {
            var body = await http.GetStringAsync(jsonUrl, ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("log", out var logProp))
                return new(false, "The replay had no battle log.");
            log = logProp.GetString() ?? "";
        }
        catch (Exception ex)
        {
            log = ""; // set for the compiler; the return below is the real path
            logger.LogWarning(ex, "Could not fetch replay {Url}", jsonUrl);
            return new(false, "Couldn't fetch that replay — check the link and try again.");
        }

        var parsed = ParseLog(log);
        if (parsed.Winner is null && !parsed.Tie)
            return new(false, "The replay doesn't look finished — no winner is recorded.");

        // Each team's drafted roster, as a set of normalised species ids. Both the
        // display name and the Showdown sprite slug are indexed, since either can
        // be what toID lands on for mega/regional forms.
        var homeIds = await RosterIdsAsync(match.HomeTeamId, ct);
        var awayIds = await RosterIdsAsync(match.AwayTeamId, ct);
        if (homeIds.Count == 0 || awayIds.Count == 0)
            return new(false, "Both teams need a drafted roster before a replay can be scored.");

        var p1 = parsed.Brought.GetValueOrDefault("p1") ?? [];
        var p2 = parsed.Brought.GetValueOrDefault("p2") ?? [];

        // Try both side→team assignments and keep whichever fits the rosters best.
        var straight = p1.Count(homeIds.Contains) + p2.Count(awayIds.Contains); // p1=home
        var swapped = p1.Count(awayIds.Contains) + p2.Count(homeIds.Contains);   // p1=away
        if (straight == 0 && swapped == 0)
            return new(false, "Couldn't match the replay's teams to this matchup — is it the right battle?");

        // The side id ("p1"/"p2") that played the home team.
        var homeSide = straight >= swapped ? "p1" : "p2";
        var awaySide = homeSide == "p1" ? "p2" : "p1";

        var homeBrought = parsed.Brought.GetValueOrDefault(homeSide)?.Count ?? 0;
        var awayBrought = parsed.Brought.GetValueOrDefault(awaySide)?.Count ?? 0;
        var homeScore = Math.Max(0, homeBrought - parsed.Faints.GetValueOrDefault(homeSide));
        var awayScore = Math.Max(0, awayBrought - parsed.Faints.GetValueOrDefault(awaySide));

        if (parsed.Tie)
            return new(true, Outcome: MatchResult.Draw, HomeScore: homeScore, AwayScore: awayScore);

        // Winner line carries a Showdown username; resolve it to a side, then to
        // home/away via the assignment we just chose.
        var winnerSide = parsed.Players.FirstOrDefault(kv =>
            string.Equals(kv.Value, parsed.Winner, StringComparison.OrdinalIgnoreCase)).Key;
        if (winnerSide is null)
            return new(false, "Couldn't tell which side won from the replay.");

        var outcome = winnerSide == homeSide ? MatchResult.HomeWin : MatchResult.AwayWin;
        return new(true, Outcome: outcome, HomeScore: homeScore, AwayScore: awayScore);
    }

    private async Task<HashSet<string>> RosterIdsAsync(int teamId, CancellationToken ct)
    {
        var mons = await db.Picks
            .Where(p => p.TeamId == teamId)
            .Select(p => new { p.PokemonEntry.Name, p.PokemonEntry.Sprite })
            .ToListAsync(ct);

        var ids = new HashSet<string>();
        foreach (var m in mons)
        {
            ids.Add(ToId(m.Name));
            if (!string.IsNullOrEmpty(m.Sprite)) ids.Add(ToId(m.Sprite));
        }
        ids.Remove("");
        return ids;
    }

    // ── log parsing ──────────────────────────────────────────────────────

    private record Parsed(
        string? Winner,
        bool Tie,
        Dictionary<string, string> Players,
        Dictionary<string, HashSet<string>> Brought,
        Dictionary<string, int> Faints);

    /// <summary>
    /// Walks the pipe-delimited battle log. Collects the two players, the species
    /// each side revealed (team-preview <c>|poke|</c> plus any <c>|switch|/|drag|</c>),
    /// each side's faint count, and the winner.
    /// </summary>
    private static Parsed ParseLog(string log)
    {
        var players = new Dictionary<string, string>();
        var brought = new Dictionary<string, HashSet<string>>();
        var faints = new Dictionary<string, int>();
        string? winner = null;
        var tie = false;

        HashSet<string> Side(string s) => brought.TryGetValue(s, out var set) ? set : brought[s] = [];

        foreach (var raw in log.Split('\n'))
        {
            if (raw.Length == 0 || raw[0] != '|') continue;
            var parts = raw.Split('|'); // parts[0] is the empty piece before the first '|'
            if (parts.Length < 2) continue;

            switch (parts[1])
            {
                case "player" when parts.Length >= 4 && parts[3].Length > 0:
                    players[parts[2]] = parts[3];
                    break;

                case "poke" when parts.Length >= 4:
                    Side(parts[2]).Add(SpeciesId(parts[3]));
                    break;

                case "switch" or "drag" when parts.Length >= 4:
                    // parts[2] is "p1a: Nickname"; the side is its first two chars.
                    Side(parts[2][..2]).Add(SpeciesId(parts[3]));
                    break;

                case "faint" when parts.Length >= 3 && parts[2].Length >= 2:
                    var side = parts[2][..2];
                    faints[side] = faints.GetValueOrDefault(side) + 1;
                    break;

                case "win" when parts.Length >= 3:
                    winner = parts[2].Trim();
                    break;

                case "tie":
                    tie = true;
                    break;
            }
        }

        return new Parsed(winner, tie, players, brought, faints);
    }

    /// <summary>Species out of a "Charizard-Mega-Y, L50, M" detail string.</summary>
    private static string SpeciesId(string detail) => ToId(detail.Split(',')[0]);

    /// <summary>Showdown's toID: lowercase, alphanumerics only. "Great Tusk" -> "greattusk".</summary>
    private static string ToId(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        var n = 0;
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    /// <summary>
    /// Accepts any replay.pokemonshowdown.com link and yields its <c>.json</c>
    /// form. Host-locked so a submitted URL can't point our fetch at something else.
    /// </summary>
    private static bool TryNormalize(string raw, out string jsonUrl)
    {
        jsonUrl = "";
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;
        if (!uri.Host.Equals("replay.pokemonshowdown.com", StringComparison.OrdinalIgnoreCase)) return false;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length <= 1) return false; // no battle id
        if (path.EndsWith(".log")) path = path[..^4];
        if (!path.EndsWith(".json")) path += ".json";
        jsonUrl = $"https://{uri.Host}{path}";
        return true;
    }
}
