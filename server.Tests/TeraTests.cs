using System.Net.Http.Json;
using System.Text.Json;

namespace DraftLeague.Web.Tests;

/// <summary>
/// The C-tier Tera-type mechanic: only C-tier options roll a Tera type, it's one
/// of the 19 known types, and the type a coach was offered is the one that sticks
/// to the pick — through the pick history and the team preview both.
/// </summary>
public class TeraTests : DraftScenarioBase
{
    // The 18 elemental types plus Stellar — mirrors DraftEngine.TeraTypes.
    private static readonly HashSet<string> KnownTeraTypes = new(StringComparer.Ordinal)
    {
        "Normal", "Fire", "Water", "Electric", "Grass", "Ice", "Fighting", "Poison", "Ground",
        "Flying", "Psychic", "Bug", "Rock", "Ghost", "Dragon", "Dark", "Steel", "Fairy", "Stellar",
    };

    /// <summary>Opens the given tier for whoever is on the clock and returns the offered options.</summary>
    private async Task<(int TeamId, HttpClient Client, List<JsonElement> Offered)> OpenTierAsync(
        HttpClient admin, int draftId, Dictionary<int, HttpClient> byTeam, int tier)
    {
        var s = await StateAsync(admin, draftId);
        var teamId = Int(s, "onClockTeamId");
        var client = byTeam[teamId];
        (await client.PostAsJsonAsync($"/api/drafts/{draftId}/offer", new { teamId, tier })).EnsureSuccessStatusCode();
        var offered = (await StateAsync(admin, draftId)).GetProperty("offered").EnumerateArray().ToList();
        return (teamId, client, offered);
    }

    [Fact]
    public async Task Every_C_tier_option_carries_a_known_tera_type()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var (_, _, offered) = await OpenTierAsync(admin, draftId, byTeam, C);

        Assert.NotEmpty(offered);
        foreach (var o in offered)
        {
            var tera = Str(o, "teraType");
            Assert.NotNull(tera);
            Assert.Contains(tera!, KnownTeraTypes);
        }
    }

    [Theory]
    [InlineData(S)]
    [InlineData(A)]
    [InlineData(B)]
    public async Task Non_C_tier_options_never_carry_a_tera_type(int tier)
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var (_, _, offered) = await OpenTierAsync(admin, draftId, byTeam, tier);

        Assert.NotEmpty(offered);
        Assert.All(offered, o => Assert.Null(Str(o, "teraType")));
    }

    [Fact]
    public async Task A_C_tier_picks_tera_type_survives_into_the_history()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var (teamId, client, offered) = await OpenTierAsync(admin, draftId, byTeam, C);

        // Take the first option and remember the Tera type it was offered with.
        var chosen = offered.First();
        var chosenId = Int(chosen, "pokemonEntryId");
        var offeredTera = Str(chosen, "teraType");

        (await client.PostAsJsonAsync($"/api/drafts/{draftId}/pick",
            new { teamId, pokemonEntryId = chosenId })).EnsureSuccessStatusCode();

        var pick = (await StateAsync(admin, draftId)).GetProperty("picks").EnumerateArray()
            .Single(p => Int(p, "pokemonEntryId") == chosenId);
        // The pick keeps the exact type it was offered with — not a fresh roll.
        Assert.Equal(offeredTera, Str(pick, "teraType"));
    }

    [Fact]
    public async Task The_team_preview_shows_a_C_tier_mons_tera_type()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("coach-1", "coach-2");
        var (teamId, client, offered) = await OpenTierAsync(admin, draftId, byTeam, C);

        var chosen = offered.First();
        var offeredTera = Str(chosen, "teraType");
        (await client.PostAsJsonAsync($"/api/drafts/{draftId}/pick",
            new { teamId, pokemonEntryId = Int(chosen, "pokemonEntryId") })).EnsureSuccessStatusCode();

        var coachId = (await StateAsync(admin, draftId)).GetProperty("teams").EnumerateArray()
            .First(t => Int(t, "id") == teamId).GetProperty("coachId").GetString()!;
        var team = await admin.GetFromJsonAsync<JsonElement>($"/api/players/{coachId}/team");

        var mon = team.GetProperty("mons").EnumerateArray().Single();
        Assert.Equal("C", Str(mon, "tier"));
        Assert.Equal(offeredTera, Str(mon, "teraType"));
    }
}
