using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The Draft Stats analytics endpoint: a read-only view derived entirely from the
/// picks + skips and their OtherOptions (the options offered but not taken). Drives
/// a full sim to produce picks, skips and passed-option snapshots, then checks the
/// endpoint's numbers reconcile with the raw draft data.
/// </summary>
public class DraftStatsTests : DraftScenarioBase
{
    private sealed record Opt(string? Name, string? Sprite, int DexNumber, string? Tier, string? TeraType);
    private static readonly JsonSerializerOptions J = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task Draft_stats_reconcile_with_the_picks_and_their_rejected_options()
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);

        int draftId, leagueId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var draft = await db.Drafts.OrderBy(d => d.Id).FirstAsync();
            draftId = draft.Id; leagueId = draft.LeagueId;
            await scope.ServiceProvider.GetRequiredService<RandomSeasonSimulator>()
                .SimulateAsync(draftId, teamCount: 6, seed: 7, realBattles: false);
        }

        // Expected values, computed straight from the DB.
        HashSet<string> draftedNames;
        Dictionary<string, int> rejections = new(StringComparer.OrdinalIgnoreCase);
        int picksWithTera;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var picks = await db.Picks.Include(p => p.PokemonEntry).Where(p => p.DraftId == draftId).ToListAsync();
            draftedNames = picks.Select(p => p.PokemonEntry.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            picksWithTera = picks.Count(p => !string.IsNullOrEmpty(p.TeraType));

            var skipJson = await db.DraftSkips
                .Where(s => s.DraftId == draftId && s.OtherOptions != null)
                .Select(s => s.OtherOptions!).ToListAsync();
            void Scan(string? json)
            {
                if (string.IsNullOrEmpty(json)) return;
                foreach (var o in JsonSerializer.Deserialize<List<Opt>>(json, J)!)
                    if (!string.IsNullOrEmpty(o.Name))
                        rejections[o.Name] = rejections.GetValueOrDefault(o.Name) + 1;
            }
            foreach (var p in picks) Scan(p.OtherOptions);
            foreach (var s in skipJson) Scan(s);
        }

        var res = await admin.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/draft-stats");
        var poke = res.GetProperty("pokemon");
        var tera = res.GetProperty("tera");

        Assert.Equal(draftedNames.Count, res.GetProperty("totalPicks").GetInt32());

        static HashSet<string> Names(JsonElement arr) =>
            arr.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Instant picks == drafted mons that were never once passed over.
        var expectedInstant = draftedNames.Where(n => rejections.GetValueOrDefault(n) == 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gotInstant = Names(poke.GetProperty("instantPicks"));
        Assert.Equal(expectedInstant, gotInstant);

        // Most rejected: sorted desc, and the top count matches the true maximum.
        var counts = poke.GetProperty("mostRejected").EnumerateArray()
            .Select(e => e.GetProperty("rejections").GetInt32()).ToList();
        Assert.Equal(counts.OrderByDescending(x => x).ToList(), counts);
        if (rejections.Count > 0)
        {
            Assert.NotEmpty(counts);
            Assert.Equal(rejections.Values.Max(), counts[0]);
        }

        // Instant picks are all drafted, so each carries a drafter name.
        foreach (var e in poke.GetProperty("instantPicks").EnumerateArray())
            Assert.Equal(JsonValueKind.String, e.GetProperty("trainer").ValueKind);

        // Put Me In Coach: every entry was drafted, was rejected at least once, has a
        // drafter, and never overlaps the instant picks.
        var putMeIn = poke.GetProperty("putMeInCoach");
        foreach (var e in putMeIn.EnumerateArray())
        {
            Assert.Contains(e.GetProperty("name").GetString()!, draftedNames);
            Assert.True(e.GetProperty("rejections").GetInt32() > 0);
            Assert.Equal(JsonValueKind.String, e.GetProperty("trainer").ValueKind);
        }
        Assert.Empty(gotInstant.Intersect(Names(putMeIn)));

        // Most rejected: a mon's trainer is present iff it was drafted (undrafted
        // mons show as "Undrafted" on the client, backed by a null trainer here).
        foreach (var e in poke.GetProperty("mostRejected").EnumerateArray())
        {
            var trainerSet = e.GetProperty("trainer").ValueKind == JsonValueKind.String;
            Assert.Equal(e.GetProperty("drafted").GetBoolean(), trainerSet);
        }

        // Tera: picked counts reconcile with the picks that carry a Tera type, and
        // every pick rate sits in [0, 1] over a non-empty sample.
        var teraPickedTotal = tera.GetProperty("mostPicked").EnumerateArray()
            .Sum(e => e.GetProperty("count").GetInt32());
        Assert.Equal(picksWithTera, teraPickedTotal);
        foreach (var e in tera.GetProperty("pickRate").EnumerateArray())
        {
            Assert.InRange(e.GetProperty("rate").GetDouble(), 0.0, 1.0);
            Assert.True(e.GetProperty("picked").GetInt32() + e.GetProperty("rejected").GetInt32() > 0);
        }
    }

    [Fact]
    public async Task A_coachs_own_drafted_mons_are_flagged_mine()
    {
        const string coachId = "111111111111111111"; // numeric id → a real coach who gets a team
        var coach = await Factory.SignedInAsAsync(coachId);

        int draftId, leagueId;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var draft = await db.Drafts.OrderBy(d => d.Id).FirstAsync();
            draftId = draft.Id; leagueId = draft.LeagueId;
            await scope.ServiceProvider.GetRequiredService<RandomSeasonSimulator>()
                .SimulateAsync(draftId, teamCount: 4, seed: 1, realBattles: false);
        }

        HashSet<string> myMons;
        using (var scope = Factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var team = await db.Teams.FirstAsync(t => t.CoachId == coachId);
            myMons = (await db.Picks.Include(p => p.PokemonEntry).Where(p => p.TeamId == team.Id)
                .Select(p => p.PokemonEntry.Name).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        Assert.NotEmpty(myMons); // the coach really got a roster

        var poke = (await coach.GetFromJsonAsync<JsonElement>($"/api/leagues/{leagueId}/draft-stats"))
            .GetProperty("pokemon");

        foreach (var listName in new[] { "instantPicks", "mostRejected", "putMeInCoach" })
            foreach (var e in poke.GetProperty(listName).EnumerateArray())
            {
                var name = e.GetProperty("name").GetString()!;
                var mine = e.GetProperty("mine").GetBoolean();
                // Anything flagged mine really is one of my mons…
                if (mine) Assert.Contains(name, myMons);
                // …and any of my DRAFTED mons that appear here are flagged.
                var drafted = !e.TryGetProperty("drafted", out var dp) || dp.GetBoolean();
                if (myMons.Contains(name) && drafted) Assert.True(mine, $"{name} should be flagged mine");
            }
    }
}
