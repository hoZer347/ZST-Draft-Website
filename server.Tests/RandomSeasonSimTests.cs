using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The dev-only <see cref="RandomSeasonSimulator"/>: a synthetic season — random
/// teams and a random valid draft. With realBattles:false it builds the draft +
/// schedule but plays no games, so results/stats come only from real battles (or
/// later-added replays) — the matches stay Pending, with no scores and no stats.
/// These assert that structure and that a fixed seed reproduces the same draft.
/// </summary>
public class RandomSeasonSimTests : DraftScenarioBase
{
    private async Task<int> DraftIdAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return (await db.Drafts.OrderBy(d => d.Id).FirstAsync()).Id;
    }

    [Fact]
    public async Task Random_season_builds_a_valid_draft_and_pending_schedule_without_battles()
    {
        var draftId = await DraftIdAsync();

        RandomSeasonSimulator.SimResult result;
        using (var scope = Factory.Services.CreateScope())
        {
            var sim = scope.ServiceProvider.GetRequiredService<RandomSeasonSimulator>();
            // realBattles:false — no battles, so the test stays fast and offline (no
            // Node/battle-server dependency).
            result = await sim.SimulateAsync(draftId, teamCount: 6, seed: 42, realBattles: false);
        }

        Assert.Equal(6, result.Teams);
        Assert.Equal(60, result.Picks); // 6 teams × (1+2+3+4)
        Assert.True(result.Matches > 0, "a multi-team season should schedule matches");
        Assert.False(result.RealBattles);

        using var check = Factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<AppDbContext>();

        Assert.Equal(DraftState.Complete, (await db.Drafts.FirstAsync(d => d.Id == draftId)).State);

        var picks = await db.Picks.Where(p => p.DraftId == draftId).ToListAsync();
        Assert.Equal(60, picks.Count);
        // The pool depletes — no mon drafted twice.
        Assert.Equal(picks.Count, picks.Select(p => p.PokemonEntryId).Distinct().Count());

        foreach (var team in picks.GroupBy(p => p.TeamId))
        {
            Assert.Equal(1, team.Count(p => p.Tier == Tier.S));
            Assert.Equal(2, team.Count(p => p.Tier == Tier.A));
            Assert.Equal(3, team.Count(p => p.Tier == Tier.B));
            Assert.Equal(4, team.Count(p => p.Tier == Tier.C));
        }
        // Tera is a C-tier-only mechanic.
        foreach (var p in picks)
            Assert.Equal(p.Tier == Tier.C, !string.IsNullOrEmpty(p.TeraType));

        // No battles → every match is Pending with no score, and nothing recorded.
        var leagueId = (await db.Drafts.FirstAsync(d => d.Id == draftId)).LeagueId;
        var matches = await db.Matches.Where(m => m.LeagueId == leagueId).ToListAsync();
        Assert.NotEmpty(matches);
        Assert.All(matches, m =>
        {
            Assert.Equal(MatchResult.Pending, m.Result);
            Assert.Null(m.HomeScore);
            Assert.Null(m.AwayScore);
        });

        var teams = await db.Teams.ToListAsync();
        Assert.Equal(6, teams.Count);
        Assert.All(teams, t => Assert.Equal(0, t.Wins + t.Losses + t.Draws)); // no games played

        Assert.Empty(await db.PokemonStats.Include(s => s.Pick)
            .Where(s => s.Pick.DraftId == draftId).ToListAsync());
    }

    [Fact]
    public async Task A_fixed_seed_reproduces_the_same_draft()
    {
        var draftId = await DraftIdAsync();

        async Task<List<(int Pick, int Mon)>> RunAsync(int seed)
        {
            using (var scope = Factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<RandomSeasonSimulator>()
                    .SimulateAsync(draftId, teamCount: 4, seed: seed, realBattles: false);

            using var read = Factory.Services.CreateScope();
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            return await db.Picks.Where(p => p.DraftId == draftId)
                .OrderBy(p => p.PickNumber)
                .Select(p => new ValueTuple<int, int>(p.PickNumber, p.PokemonEntryId))
                .ToListAsync();
        }

        var first = await RunAsync(123);
        var second = await RunAsync(123); // same seed → same picks (pool entry ids are stable)
        Assert.Equal(first, second);
    }
}
