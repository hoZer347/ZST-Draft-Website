using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Fuzz the draft with a *randomly generated season*: every coach opens a random
/// tier they still owe and takes a random offered option, with the clock
/// occasionally timing out into an auto-pick. Seeds are fixed so a failure is
/// reproducible.
///
/// Rather than assert a scripted outcome, each run checks the invariants that
/// must hold for ANY valid season, the roster shape, a depleting pool, the
/// snake order, C-tier Tera assignment, and that the "passed options" snapshot
/// shown in the pick feed matches what was really offered.
/// </summary>
public class RandomSeasonTests : DraftScenarioBase
{
    public static IEnumerable<object[]> Seasons()
    {
        // (seed, playerCount), a spread of roster sizes across several seeds.
        yield return [1, 2];
        yield return [7, 3];
        yield return [42, 4];
        yield return [99, 5];
        yield return [123, 3];
    }

    [Theory]
    [MemberData(nameof(Seasons))]
    public async Task A_randomly_generated_season_satisfies_every_invariant(int seed, int players)
    {
        var ids = Enumerable.Range(1, players).Select(i => $"p{i}").ToArray();
        var (admin, draftId, byTeam) = await StartWithAsync(ids);
        var rng = new Random(seed);

        var slotsPerRoster = 0; // 1+2+3+4, read from tierRules below
        {
            var s0 = await StateAsync(admin, draftId);
            foreach (var r in s0.GetProperty("tierRules").EnumerateArray())
                slotsPerRoster += Int(r, "slotsPerTeam");
        }
        var expectedPicks = players * slotsPerRoster;

        // Drive the whole draft with randomised choices.
        for (var guard = 0; guard <= expectedPicks + 5; guard++)
        {
            var s = await StateAsync(admin, draftId);
            if (s.GetProperty("state").GetString() == "Complete") break;

            var teamId = Int(s, "onClockTeamId");
            var tier = RandomOwedTier(s, teamId, rng);

            (await byTeam[teamId].PostAsJsonAsync($"/api/drafts/{draftId}/offer", new { teamId, tier }))
                .EnsureSuccessStatusCode();

            // ~1 pick in 6 times out into an auto-pick; the rest pick a random
            // offered option themselves.
            if (rng.Next(6) == 0)
            {
                using var scope = Factory.Services.CreateScope();
                var engine = scope.ServiceProvider.GetRequiredService<DraftEngine>();
                // preferSkip: false so a timed-out turn fills a pick here rather
                // than auto-skipping (this loop is modelling picks, not deferrals).
                Assert.True((await engine.AutoPickAsync(draftId, preferSkip: false)).Ok);
            }
            else
            {
                var offered = (await StateAsync(admin, draftId)).GetProperty("offered").EnumerateArray().ToList();
                var choice = offered[rng.Next(offered.Count)];
                (await byTeam[teamId].PostAsJsonAsync($"/api/drafts/{draftId}/pick",
                    new { teamId, pokemonEntryId = Int(choice, "pokemonEntryId") })).EnsureSuccessStatusCode();
            }
        }

        var final = await StateAsync(admin, draftId);
        AssertSeasonInvariants(final, players, slotsPerRoster);
    }

    /// <summary>Checks that hold for any completed random season.</summary>
    private static void AssertSeasonInvariants(JsonElement state, int players, int slotsPerRoster)
    {
        Assert.Equal("Complete", state.GetProperty("state").GetString());

        var picks = state.GetProperty("picks").EnumerateArray().ToList();
        Assert.Equal(players * slotsPerRoster, picks.Count);

        // The pool really depletes, no mon drafted twice.
        var ids = picks.Select(p => Int(p, "pokemonEntryId")).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // Pick numbers are a clean 1..N with no gaps or repeats.
        Assert.Equal(Enumerable.Range(1, picks.Count),
            picks.Select(p => Int(p, "pickNumber")).OrderBy(x => x));

        var allowed = new Dictionary<string, int>();
        foreach (var r in state.GetProperty("tierRules").EnumerateArray())
            allowed[r.GetProperty("tier").GetString()!] = Int(r, "slotsPerTeam");
        var offeredFor = new Dictionary<string, int>();
        foreach (var r in state.GetProperty("tierRules").EnumerateArray())
            offeredFor[r.GetProperty("tier").GetString()!] = Int(r, "optionsOffered");

        var teamIds = state.GetProperty("teams").EnumerateArray().Select(t => Int(t, "id")).ToList();
        Assert.Equal(players, teamIds.Count);

        foreach (var teamId in teamIds)
        {
            var mine = picks.Where(p => Int(p, "teamId") == teamId).ToList();
            // Exactly its per-tier quota, no more, no less.
            foreach (var (tier, quota) in allowed)
                Assert.Equal(quota, mine.Count(p => p.GetProperty("tier").GetString() == tier));
        }

        foreach (var p in picks)
        {
            var tier = p.GetProperty("tier").GetString()!;
            var tera = Str(p, "teraType");
            var name = p.GetProperty("name").GetString()!;

            // Tera is a C-tier-only mechanic, and even in C, megas and Shedinja
            // are barred from one (see DraftEngine.TeraBarred). Megas all carry the
            // "M-" name prefix.
            var teraBarred = name.StartsWith("M-", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Shedinja", StringComparison.OrdinalIgnoreCase);
            if (tier == "C" && !teraBarred)
                Assert.False(string.IsNullOrEmpty(tera), "C-tier pick must have a Tera type");
            else
                Assert.True(string.IsNullOrEmpty(tera), $"{tier}-tier pick must not have a Tera type (barred={teraBarred})");

            // The "passed options" snapshot, when present, must be the rest of the
            // options actually offered that turn: one fewer than the tier's offer
            // count, all the same tier, and never the mon that was taken.
            var othersJson = Str(p, "otherOptions");
            if (!string.IsNullOrEmpty(othersJson))
            {
                using var doc = JsonDocument.Parse(othersJson);
                var others = doc.RootElement.EnumerateArray().ToList();
                Assert.Equal(offeredFor[tier] - 1, others.Count);
                var pickedName = p.GetProperty("name").GetString();
                foreach (var o in others)
                {
                    Assert.Equal(tier, o.GetProperty("tier").GetString());
                    Assert.NotEqual(pickedName, o.GetProperty("name").GetString());
                }
            }
        }

        AssertSnakeOrder(state, players, slotsPerRoster);
    }

    /// <summary>The laid-down order is a proper snake: each round is the roster
    /// forward, then reversed, alternating.</summary>
    private static void AssertSnakeOrder(JsonElement state, int players, int slotsPerRoster)
    {
        var order = state.GetProperty("order").EnumerateArray().Select(x => x.GetInt32()).ToList();
        var round0 = order.Take(players).ToList();
        Assert.Equal(players, round0.Distinct().Count()); // first round hits every team once

        for (var round = 0; round * players + players <= order.Count && round < slotsPerRoster; round++)
        {
            var seg = order.Skip(round * players).Take(players).ToList();
            var expected = round % 2 == 0 ? round0 : Enumerable.Reverse(round0).ToList();
            Assert.Equal(expected, seg);
        }
    }

    /// <summary>A random tier this team still owes at least one slot in.</summary>
    private static int RandomOwedTier(JsonElement state, int teamId, Random rng)
    {
        var used = new Dictionary<string, int> { ["S"] = 0, ["A"] = 0, ["B"] = 0, ["C"] = 0 };
        foreach (var p in state.GetProperty("picks").EnumerateArray())
            if (Int(p, "teamId") == teamId) used[p.GetProperty("tier").GetString()!]++;

        var allowed = new Dictionary<string, int>();
        foreach (var r in state.GetProperty("tierRules").EnumerateArray())
            allowed[r.GetProperty("tier").GetString()!] = Int(r, "slotsPerTeam");

        var order = new[] { "S", "A", "B", "C" };
        var owed = order.Where((t, _) => allowed[t] - used[t] > 0).ToList();
        var pick = owed[rng.Next(owed.Count)];
        return Array.IndexOf(order, pick);
    }
}
