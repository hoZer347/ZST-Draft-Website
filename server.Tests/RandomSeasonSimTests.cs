using System.Text.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The dev-only <see cref="RandomSeasonSimulator"/>: a synthetic season driven
/// through the real <see cref="DraftEngine"/> (offer a tier, then pick/skip). With
/// realBattles:false it builds the draft + schedule but plays no games, so
/// results/stats come only from real battles (or later-added replays), the matches
/// stay Pending, with no scores and no stats. These assert that structure.
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
            // realBattles:false, no battles, so the test stays fast and offline (no
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

        var picks = await db.Picks.Include(p => p.PokemonEntry).Where(p => p.DraftId == draftId).ToListAsync();
        Assert.Equal(60, picks.Count);
        // The pool depletes, no mon drafted twice.
        Assert.Equal(picks.Count, picks.Select(p => p.PokemonEntryId).Distinct().Count());

        foreach (var team in picks.GroupBy(p => p.TeamId))
        {
            Assert.Equal(1, team.Count(p => p.Tier == Tier.S));
            Assert.Equal(2, team.Count(p => p.Tier == Tier.A));
            Assert.Equal(3, team.Count(p => p.Tier == Tier.B));
            Assert.Equal(4, team.Count(p => p.Tier == Tier.C));
        }
        // Tera fires for a C-tier pick unless the mon is barred (megas / Shedinja).
        foreach (var p in picks)
            Assert.Equal(
                p.Tier == Tier.C && !DraftEngine.TeraBarred(p.PokemonEntry.Name, p.PokemonEntry.Sprite),
                !string.IsNullOrEmpty(p.TeraType));

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
    public async Task Re_running_the_sim_rebuilds_a_fresh_complete_draft()
    {
        var draftId = await DraftIdAsync();

        async Task<int> RunAsync()
        {
            using (var scope = Factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<RandomSeasonSimulator>()
                    .SimulateAsync(draftId, teamCount: 4, seed: null, realBattles: false);

            using var read = Factory.Services.CreateScope();
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(DraftState.Complete, (await db.Drafts.FirstAsync(d => d.Id == draftId)).State);
            return await db.Picks.CountAsync(p => p.DraftId == draftId);
        }

        // Each run resets and drives a fresh full draft through the engine: 4 teams ×
        // (1+2+3+4) = 40 picks, and re-running wipes the prior one rather than piling on.
        Assert.Equal(40, await RunAsync());
        Assert.Equal(40, await RunAsync());
    }

    [Fact]
    public async Task Skips_are_scattered_through_the_draft_without_shrinking_rosters()
    {
        var draftId = await DraftIdAsync();

        RandomSeasonSimulator.SimResult result;
        using (var scope = Factory.Services.CreateScope())
            result = await scope.ServiceProvider.GetRequiredService<RandomSeasonSimulator>()
                .SimulateAsync(draftId, teamCount: 6, seed: 3, realBattles: false);

        Assert.True(result.Skips > 0, "expected some skips to be scattered");

        using var read = Factory.Services.CreateScope();
        var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
        var skips = await db.DraftSkips.Where(s => s.DraftId == draftId).ToListAsync();
        Assert.Equal(result.Skips, skips.Count);

        var picks = await db.Picks.Where(p => p.DraftId == draftId).ToListAsync();
        Assert.Equal(60, picks.Count); // rosters unchanged: 6 × (1+2+3+4)

        var teamIds = (await db.Teams.Select(t => t.Id).ToListAsync()).ToHashSet();
        foreach (var s in skips)
        {
            Assert.Contains(s.TeamId, teamIds);                // a real team
            Assert.InRange(s.AfterPickNumber, 0, picks.Count); // a valid feed position
        }
        foreach (var g in skips.GroupBy(s => s.TeamId))
            Assert.True(g.Count() <= DraftEngine.MaxSkipsPerTeam); // capped like the live draft
    }

    [Fact]
    public async Task Scattered_skips_include_both_voluntary_and_auto_and_carry_options()
    {
        var draftId = await DraftIdAsync();

        bool sawAuto = false, sawVoluntary = false, sawOptions = false;
        for (var seed = 0; seed < 15; seed++)
        {
            using (var scope = Factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<RandomSeasonSimulator>()
                    .SimulateAsync(draftId, teamCount: 6, seed: seed, realBattles: false);

            using var read = Factory.Services.CreateScope();
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            foreach (var s in await db.DraftSkips.Where(s => s.DraftId == draftId).ToListAsync())
            {
                if (s.WasAuto) sawAuto = true; else sawVoluntary = true;
                if (s.OtherOptions is not null) sawOptions = true;
            }
        }

        Assert.True(sawAuto, "expected some auto skips");
        Assert.True(sawVoluntary, "expected some voluntary skips");
        Assert.True(sawOptions, "expected some skips to carry a passed-options snapshot");
    }

    [Fact]
    public async Task Megas_and_shedinja_never_receive_a_tera_type()
    {
        int draftId, leagueId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var draft = await db.Drafts.OrderBy(d => d.Id).FirstAsync();
            draftId = draft.Id; leagueId = draft.LeagueId;

            // Controlled pool: keep enough distinct S/A/B, and rebuild C with regular
            // mons plus the three Tera-barred kinds, a mega by "M-" name, a mega by
            // "-mega" sprite, and Shedinja. Only C draws a Tera at all.
            db.Pokemon.RemoveRange(await db.Pokemon.Where(p => p.LeagueId == leagueId).ToListAsync());
            void Add(Tier t, int dex, string name, string sprite) =>
                db.Pokemon.Add(new PokemonEntry { LeagueId = leagueId, Name = name, Tier = t, DexNumber = dex, Sprite = sprite });
            for (var i = 0; i < 4; i++) Add(Tier.S, 100 + i, $"S{i}", $"s{i}");
            for (var i = 0; i < 6; i++) Add(Tier.A, 200 + i, $"A{i}", $"a{i}");
            for (var i = 0; i < 8; i++) Add(Tier.B, 300 + i, $"B{i}", $"b{i}");
            for (var i = 0; i < 10; i++) Add(Tier.C, 400 + i, $"C{i}", $"c{i}");   // regular → Tera allowed
            Add(Tier.C, 500, "M-Banette", "banette-mega");   // mega by name
            Add(Tier.C, 501, "Gardevoir", "gardevoir-mega"); // mega by sprite slug
            Add(Tier.C, 502, "Shedinja", "shedinja");        // Shedinja
            await db.SaveChangesAsync();
        }

        bool sawBarredDrafted = false, sawRegularTera = false, sawBarredInPassed = false;

        for (var seed = 0; seed < 25; seed++)
        {
            using (var scope = Factory.Services.CreateScope())
                await scope.ServiceProvider.GetRequiredService<RandomSeasonSimulator>()
                    .SimulateAsync(draftId, teamCount: 2, seed: seed, realBattles: false);

            using var read = Factory.Services.CreateScope();
            var db = read.ServiceProvider.GetRequiredService<AppDbContext>();
            var picks = await db.Picks.Include(p => p.PokemonEntry).Where(p => p.DraftId == draftId).ToListAsync();

            foreach (var p in picks)
            {
                if (DraftEngine.TeraBarred(p.PokemonEntry.Name, p.PokemonEntry.Sprite))
                {
                    Assert.Null(p.TeraType); // a barred mon is never given a Tera in a pick
                    sawBarredDrafted = true;
                }
                else if (p.PokemonEntry.Tier == Tier.C && p.TeraType is not null)
                    sawRegularTera = true;

                // The "passed" run must not hand a barred option a Tera type either.
                if (p.OtherOptions is null) continue;
                foreach (var o in JsonSerializer.Deserialize<List<JsonElement>>(p.OtherOptions)!)
                {
                    var name = o.GetProperty("name").GetString()!;
                    var sprite = o.TryGetProperty("sprite", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
                    var tera = o.TryGetProperty("teraType", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if (DraftEngine.TeraBarred(name, sprite))
                    {
                        Assert.Null(tera);
                        sawBarredInPassed = true;
                    }
                }
            }
        }

        // Non-vacuity: the barred mons really appeared (as picks and as options), and
        // Tera still fires for regular C mons.
        Assert.True(sawBarredDrafted, "expected a barred mon to be drafted across the seeds");
        Assert.True(sawRegularTera, "expected a regular C mon to receive a Tera type");
        Assert.True(sawBarredInPassed, "expected a barred mon to appear in a passed run");
    }
}
