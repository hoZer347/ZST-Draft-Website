using DraftLeague.Web.Models;

namespace DraftLeague.Web.Data;

/// <summary>
/// Creates a small playable league so the draft can be exercised without
/// hand-building rows. Development only — wired up in Program.cs behind
/// an IsDevelopment check.
///
/// Teams and the pick order are deliberately NOT seeded: the draft lines up
/// whoever has actually signed in, built at Start (see DraftEngine.StartAsync).
/// </summary>
public static class DevSeed
{
    public static async Task<int> SeedAsync(AppDbContext db, CancellationToken ct = default)
    {
        if (db.Leagues.Any()) return db.Drafts.First().Id;

        var league = new League
        {
            Name = "Test Season 1",
            OwnerId = "owner-1",
            PickTimerSeconds = 86400, // 24h default; use /dev/…/expire to force a turn in testing
            SeasonWeeks = 8,
            TierRules =
            [
                new TierRule { Tier = Tier.S, SlotsPerTeam = 1, OptionsOffered = 3 },
                new TierRule { Tier = Tier.A, SlotsPerTeam = 2, OptionsOffered = 4 },
                new TierRule { Tier = Tier.B, SlotsPerTeam = 3, OptionsOffered = 5 },
                new TierRule { Tier = Tier.C, SlotsPerTeam = 4, OptionsOffered = 7 },
            ],
        };

        foreach (var p in Pokedex.LoadSeed())
            league.Pool.Add(p.ToEntity(0)); // LeagueId set by the League nav on save

        db.Leagues.Add(league);
        await db.SaveChangesAsync(ct);

        // Empty draft — teams and snake order are built from the signed-in
        // players when an admin presses Start.
        var draft = new Draft { LeagueId = league.Id };
        db.Drafts.Add(draft);
        await db.SaveChangesAsync(ct);
        return draft.Id;
    }
}
