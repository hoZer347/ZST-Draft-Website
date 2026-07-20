using System.Text.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The dex-number clause under <see cref="RandomSeasonSimulator"/>: no team may
/// draft two forms of one species (two mons sharing a national dex number, e.g.
/// two Mega Raichu). These use a small, hand-built pool so the shared-dex forms
/// are guaranteed to be in play, and sweep many seeds so a broken clause can't
/// hide behind a lucky shuffle. Dex 0 is "unset" and never blocks.
///
/// The seed factory league runs S1/A2/B3/C4 with 2 teams, so each run drafts
/// 2 S, 4 A, 6 B and 8 C. Every pool below leaves comfortable slack over that so
/// the clause is always satisfiable (a pool with fewer distinct species than a
/// roster needs would force duplicates, which is a pool problem, not a bug).
/// </summary>
public class RandomSeasonDexClauseTests : DraftScenarioBase
{
    private record Mon(Tier Tier, int Dex, string Name);

    private async Task<(int DraftId, int LeagueId)> DraftAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var d = await db.Drafts.OrderBy(x => x.Id).FirstAsync();
        return (d.Id, d.LeagueId);
    }

    // Replace the league's whole pool with a controlled set.
    private async Task SeedPoolAsync(int leagueId, IEnumerable<Mon> mons)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Pokemon.RemoveRange(await db.Pokemon.Where(p => p.LeagueId == leagueId).ToListAsync());
        var i = 0;
        foreach (var m in mons)
            db.Pokemon.Add(new PokemonEntry { LeagueId = leagueId, Name = m.Name, Tier = m.Tier, DexNumber = m.Dex, Sprite = $"s{i++}" });
        await db.SaveChangesAsync();
    }

    private async Task<List<Pick>> RunAsync(int draftId, int seed)
    {
        using (var scope = Factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<RandomSeasonSimulator>()
                .SimulateAsync(draftId, teamCount: 2, seed: seed, realBattles: false);

        using var read = Factory.Services.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Picks.Include(p => p.PokemonEntry)
            .Where(p => p.DraftId == draftId).ToListAsync();
    }

    // A base pool of all-distinct species, sized with slack over a 2-team roster.
    private static List<Mon> BasePool()
    {
        var pool = new List<Mon>();
        for (var i = 0; i < 4; i++) pool.Add(new(Tier.S, 100 + i, $"S{i}"));   // need 2
        for (var i = 0; i < 6; i++) pool.Add(new(Tier.A, 200 + i, $"A{i}"));   // need 4
        for (var i = 0; i < 8; i++) pool.Add(new(Tier.B, 300 + i, $"B{i}"));   // need 6
        for (var i = 0; i < 10; i++) pool.Add(new(Tier.C, 400 + i, $"C{i}"));  // need 8
        return pool;
    }

    private static IEnumerable<int> PositiveDex(IEnumerable<Pick> picks) =>
        picks.Select(p => p.PokemonEntry.DexNumber).Where(d => d > 0);

    private static void AssertNoDuplicateSpeciesPerTeam(List<Pick> picks)
    {
        foreach (var team in picks.GroupBy(p => p.TeamId))
        {
            var dex = PositiveDex(team).ToList();
            Assert.Equal(dex.Count, dex.Distinct().Count());
        }
    }

    [Fact]
    public async Task No_team_holds_two_forms_of_one_species_over_many_seeds()
    {
        var (draftId, leagueId) = await DraftAsync();

        // Four Mega Raichu forms (dex 499) live in C alongside 10 distinct species.
        var pool = BasePool();
        for (var f = 0; f < 4; f++) pool.Add(new(Tier.C, 499, $"Raichu-Mega-{f}"));
        await SeedPoolAsync(leagueId, pool);

        var sawSharedForm = false;
        for (var seed = 0; seed < 30; seed++)
        {
            var picks = await RunAsync(draftId, seed);
            Assert.Equal(20, picks.Count); // 2 teams × (1+2+3+4), pool fully valid
            AssertNoDuplicateSpeciesPerTeam(picks);
            if (picks.Count(p => p.PokemonEntry.DexNumber == 499) >= 2) sawSharedForm = true;
        }

        // Non-vacuity: across the sweep the shared-dex forms really were drafted, so
        // the clause was actually exercised rather than trivially satisfied.
        Assert.True(sawSharedForm, "expected multiple shared-dex forms to be drafted across the seeds");
    }

    [Fact]
    public async Task The_passed_run_never_lists_two_forms_of_one_species()
    {
        var (draftId, leagueId) = await DraftAsync();

        var pool = BasePool();
        for (var f = 0; f < 5; f++) pool.Add(new(Tier.C, 499, $"Raichu-Mega-{f}"));
        await SeedPoolAsync(leagueId, pool);

        var sawOptions = false;
        for (var seed = 0; seed < 20; seed++)
        {
            var picks = await RunAsync(draftId, seed);
            foreach (var p in picks.Where(p => p.OtherOptions is not null))
            {
                var others = JsonSerializer.Deserialize<List<JsonElement>>(p.OtherOptions!)!;
                var dex = others.Select(o => o.GetProperty("dexNumber").GetInt32()).Where(d => d > 0).ToList();
                Assert.Equal(dex.Count, dex.Distinct().Count()); // no two forms among the options
                if (dex.Count > 0) sawOptions = true;
            }
        }
        Assert.True(sawOptions, "expected some picks to carry a passed run");
    }

    [Fact]
    public async Task The_clause_holds_across_tiers()
    {
        var (draftId, leagueId) = await DraftAsync();

        // Dex 555 has a form in BOTH A and C. A team that lands the A-form must never
        // also land the C-form (the held set spans tiers).
        var pool = BasePool();
        pool.Add(new(Tier.A, 555, "Cross-A"));
        pool.Add(new(Tier.C, 555, "Cross-C"));
        await SeedPoolAsync(leagueId, pool);

        var sawCrossTierInPlay = false;
        for (var seed = 0; seed < 40; seed++)
        {
            var picks = await RunAsync(draftId, seed);
            AssertNoDuplicateSpeciesPerTeam(picks);

            foreach (var team in picks.GroupBy(p => p.TeamId))
                Assert.True(team.Count(p => p.PokemonEntry.DexNumber == 555) <= 1);

            // Track a run where both 555 forms were drafted (across the two teams),
            // so we know the cross-tier case genuinely arose.
            if (picks.Count(p => p.PokemonEntry.DexNumber == 555) >= 2) sawCrossTierInPlay = true;
        }
        Assert.True(sawCrossTierInPlay, "expected both cross-tier forms to be drafted in some run");
    }

    [Fact]
    public async Task A_pool_of_unset_dex_species_never_blocks_and_fills_every_roster()
    {
        var (draftId, leagueId) = await DraftAsync();

        // Every C mon is a distinct species with an UNSET dex (0). Dex 0 must never
        // block, so a team happily holds four of them and the roster still fills.
        var pool = BasePool().Where(m => m.Tier != Tier.C).ToList();
        for (var i = 0; i < 10; i++) pool.Add(new(Tier.C, 0, $"Unset{i}"));
        await SeedPoolAsync(leagueId, pool);

        for (var seed = 0; seed < 10; seed++)
        {
            var picks = await RunAsync(draftId, seed);
            Assert.Equal(20, picks.Count);
            foreach (var team in picks.GroupBy(p => p.TeamId))
            {
                Assert.Equal(4, team.Count(p => p.PokemonEntry.Tier == Tier.C)); // roster filled
                Assert.Equal(4, team.Count(p => p.PokemonEntry.DexNumber == 0)); // all four are dex-0
            }
        }
    }

    [Fact]
    public async Task Every_mon_is_drafted_at_most_once_and_rosters_stay_correct()
    {
        var (draftId, leagueId) = await DraftAsync();

        var pool = BasePool();
        for (var f = 0; f < 3; f++) pool.Add(new(Tier.C, 499, $"Raichu-Mega-{f}"));
        await SeedPoolAsync(leagueId, pool);

        for (var seed = 0; seed < 15; seed++)
        {
            var picks = await RunAsync(draftId, seed);
            // No pool entry drafted twice.
            Assert.Equal(picks.Count, picks.Select(p => p.PokemonEntryId).Distinct().Count());
            // Exact per-tier quota per team.
            foreach (var team in picks.GroupBy(p => p.TeamId))
            {
                Assert.Equal(1, team.Count(p => p.PokemonEntry.Tier == Tier.S));
                Assert.Equal(2, team.Count(p => p.PokemonEntry.Tier == Tier.A));
                Assert.Equal(3, team.Count(p => p.PokemonEntry.Tier == Tier.B));
                Assert.Equal(4, team.Count(p => p.PokemonEntry.Tier == Tier.C));
            }
        }
    }
}
