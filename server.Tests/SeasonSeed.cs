using DraftLeague.Web.Data;
using DraftLeague.Web.Models;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Seeds a minimal scored-season scenario straight into the DB for the scoring /
/// stats-recorder tests: a league, two teams (home + away) with fully drafted
/// rosters, and one pending match between them. No HTTP, no draft flow.
/// </summary>
internal static class SeasonSeed
{
    public sealed record Seeded(League League, Team Home, Team Away, Match Match, Dictionary<string, Pick> Picks);

    public static async Task<Seeded> SeasonAsync(
        AppDbContext db,
        (string CoachId, string Name) home, string[] homeMons,
        (string CoachId, string Name) away, string[] awayMons)
    {
        var league = new League { Name = "Seed League", OwnerId = "owner" };
        db.Leagues.Add(league);
        await db.SaveChangesAsync();

        var homeTeam = new Team { LeagueId = league.Id, Name = home.Name, CoachName = home.Name, CoachId = home.CoachId };
        var awayTeam = new Team { LeagueId = league.Id, Name = away.Name, CoachName = away.Name, CoachId = away.CoachId };
        db.Teams.AddRange(homeTeam, awayTeam);
        await db.SaveChangesAsync();

        var draft = new Draft { LeagueId = league.Id };
        db.Drafts.Add(draft);
        await db.SaveChangesAsync();

        var picks = new Dictionary<string, Pick>();
        var n = 0;
        void Roster(Team team, string[] mons)
        {
            foreach (var mon in mons)
            {
                var entry = new PokemonEntry
                {
                    LeagueId = league.Id, Name = mon, Tier = Tier.C,
                    Sprite = mon.ToLowerInvariant(), DraftedByTeamId = team.Id,
                };
                db.Pokemon.Add(entry);
                var pick = new Pick { DraftId = draft.Id, PickNumber = ++n, TeamId = team.Id, PokemonEntry = entry, Tier = Tier.C };
                db.Picks.Add(pick);
                picks[mon.ToLowerInvariant()] = pick;
            }
        }
        Roster(homeTeam, homeMons);
        Roster(awayTeam, awayMons);
        await db.SaveChangesAsync();

        var match = new Match
        {
            LeagueId = league.Id, Week = 1,
            HomeTeamId = homeTeam.Id, AwayTeamId = awayTeam.Id,
            HomeTeam = homeTeam, AwayTeam = awayTeam,
        };
        db.Matches.Add(match);
        await db.SaveChangesAsync();

        return new Seeded(league, homeTeam, awayTeam, match, picks);
    }
}
