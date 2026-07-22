using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DraftLeague.Web.Data;
using DraftLeague.Web.Models;
using DraftLeague.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Exercises the draft end to end: the roster is built from whoever signed in,
/// only the admin can start/abort, the snake order and tier limits are honoured,
/// picks leave the pool, undo/abort behave, and a timed-out pick lands on one of
/// the offered options.
///
/// Each test gets its own factory (and throwaway DB) so the set of signed-in
/// users, which IS the draft roster, never leaks between tests.
/// </summary>
public class DraftTests : IAsyncLifetime
{
    private readonly DraftLeagueFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await ((IAsyncLifetime)_factory).DisposeAsync();

    // Ordinals the offer endpoint expects (Tier enum order).
    private const int S = 0, A = 1, B = 2, C = 3;

    // ── helpers ──────────────────────────────────────────────────────────

    private async Task<int> DraftIdAsync(HttpClient client)
    {
        var drafts = await client.GetFromJsonAsync<JsonElement>("/api/drafts");
        return drafts.EnumerateArray().First().GetProperty("id").GetInt32();
    }

    private static Task<JsonElement> StateAsync(HttpClient client, int draftId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/drafts/{draftId}");

    private static int Int(JsonElement e, string prop) => e.GetProperty(prop).GetInt32();
    private static int? IntOrNull(JsonElement e, string prop) =>
        e.GetProperty(prop).ValueKind == JsonValueKind.Null ? null : e.GetProperty(prop).GetInt32();

    /// <summary>Signs in an admin + the given players, then starts the draft.</summary>
    private async Task<(HttpClient Admin, int DraftId, Dictionary<int, HttpClient> ByTeam)> StartWithAsync(params string[] playerIds)
    {
        var admin = await _factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);

        var clients = new List<(string Id, HttpClient Client)>();
        foreach (var id in playerIds)
        {
            var client = await _factory.SignedInAsAsync(id);
            // Roster is built from who readied up, so ready each player first.
            (await client.PostAsync($"/api/drafts/{draftId}/ready", null)).EnsureSuccessStatusCode();
            clients.Add((id, client));
        }

        (await admin.PostAsync($"/api/admin/drafts/{draftId}/start", null)).EnsureSuccessStatusCode();

        // Map each player's team id (assigned at Start, reported by the server).
        var byTeam = new Dictionary<int, HttpClient>();
        foreach (var (_, client) in clients)
        {
            var s = await StateAsync(client, draftId);
            byTeam[Int(s, "myTeamId")] = client;
        }
        return (admin, draftId, byTeam);
    }

    /// <summary>First tier (S→C) the on-clock team still owes a slot.</summary>
    private static int NextTier(JsonElement state, int teamId)
    {
        var used = new Dictionary<string, int> { ["S"] = 0, ["A"] = 0, ["B"] = 0, ["C"] = 0 };
        foreach (var p in state.GetProperty("picks").EnumerateArray())
            if (Int(p, "teamId") == teamId) used[p.GetProperty("tier").GetString()!]++;

        var allowed = new Dictionary<string, int>();
        foreach (var r in state.GetProperty("tierRules").EnumerateArray())
            allowed[r.GetProperty("tier").GetString()!] = Int(r, "slotsPerTeam");

        var order = new[] { "S", "A", "B", "C" };
        for (var i = 0; i < order.Length; i++)
            if (allowed[order[i]] - used[order[i]] > 0) return i;
        throw new InvalidOperationException("team has no remaining slots");
    }

    // ── roster is built from signed-in players ─────────────────────────────

    [Fact]
    public async Task Draft_has_no_teams_until_it_starts()
    {
        var client = await _factory.SignedInAsAsync("p1");
        var draftId = await DraftIdAsync(client);

        var s = await StateAsync(client, draftId);
        Assert.Equal("NotStarted", s.GetProperty("state").GetString());
        Assert.Empty(s.GetProperty("teams").EnumerateArray());
        Assert.Equal(0, Int(s, "totalPicks"));
    }

    [Fact]
    public async Task Starting_lines_up_the_signed_in_players_in_a_snake_order()
    {
        var (admin, draftId, _) = await StartWithAsync("p1", "p2", "p3");

        var s = await StateAsync(admin, draftId);
        Assert.Equal("Running", s.GetProperty("state").GetString());
        Assert.Equal(3, s.GetProperty("teams").EnumerateArray().Count());
        // 3 teams × (1+2+3+4) picks each.
        Assert.Equal(30, Int(s, "totalPicks"));

        // Snake: round 0 forward, round 1 reversed.
        var order = s.GetProperty("order").EnumerateArray().Select(x => x.GetInt32()).ToList();
        Assert.Equal(order.Take(3).Reverse(), order.Skip(3).Take(3));
    }

    [Fact]
    public async Task Starting_with_no_players_is_refused()
    {
        var admin = await _factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);

        var res = await admin.PostAsync($"/api/admin/drafts/{draftId}/start", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task The_reserved_admin_is_not_drafted_as_a_player()
    {
        var (admin, draftId, _) = await StartWithAsync("p1");

        var s = await StateAsync(admin, draftId);
        // Only p1 is a team; the admin oversees but doesn't play.
        Assert.Single(s.GetProperty("teams").EnumerateArray());
        Assert.Null(IntOrNull(s, "myTeamId"));
        Assert.False(s.GetProperty("teams").EnumerateArray()
            .Any(t => t.GetProperty("coachId").GetString() == "admin"));
    }

    // ── only the admin can start / abort ───────────────────────────────────

    [Fact]
    public async Task A_normal_player_cannot_start_the_draft()
    {
        var player = await _factory.SignedInAsAsync("p1");
        var draftId = await DraftIdAsync(player);

        var res = await player.PostAsync($"/api/admin/drafts/{draftId}/start", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task A_demoted_admins_still_valid_token_cannot_start()
    {
        // Token minted while admin (carries the admin role claim)…
        var client = await _factory.SignedInAsAsync("ex-admin", admin: true);
        var draftId = await DraftIdAsync(client);

        // …but the account is demoted in the database.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var u = await db.Users.FirstAsync(x => x.DiscordId == "ex-admin");
            u.IsAdmin = false;
            await db.SaveChangesAsync();
        }

        // Admin is checked against the live DB, so the stale role no longer works.
        var res = await client.PostAsync($"/api/admin/drafts/{draftId}/start", null);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    // ── turn / ownership enforcement ───────────────────────────────────────

    [Fact]
    public async Task Offering_is_refused_for_a_team_you_do_not_own()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("p1", "p2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");
        var offClock = byTeam.Keys.First(t => t != onClock);

        // The on-clock client tries to offer for the OTHER team.
        var res = await byTeam[onClock].PostAsJsonAsync(
            $"/api/drafts/{draftId}/offer", new { teamId = offClock, tier = S });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task Offering_out_of_turn_for_your_own_team_is_rejected()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("p1", "p2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");
        var waiting = byTeam.First(kv => kv.Key != onClock);

        var res = await waiting.Value.PostAsJsonAsync(
            $"/api/drafts/{draftId}/offer", new { teamId = waiting.Key, tier = S });
        // Owns the team (not 403), but it isn't their turn.
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── a full draft ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_full_draft_completes_with_the_right_tiers_and_a_depleting_pool()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("p1", "p2");

        for (var guard = 0; guard < 100; guard++)
        {
            var s = await StateAsync(admin, draftId);
            if (s.GetProperty("state").GetString() == "Complete") break;

            var teamId = Int(s, "onClockTeamId");
            var client = byTeam[teamId];
            var tier = NextTier(s, teamId);

            (await client.PostAsJsonAsync($"/api/drafts/{draftId}/offer", new { teamId, tier }))
                .EnsureSuccessStatusCode();

            var s2 = await StateAsync(admin, draftId);
            var offered = s2.GetProperty("offered").EnumerateArray().ToList();
            var expected = tier switch { S => 3, A => 4, B => 5, C => 7, _ => 0 };
            Assert.Equal(expected, offered.Count); // S3/A4/B5/C7

            var pickId = Int(offered[0], "pokemonEntryId");
            (await client.PostAsJsonAsync($"/api/drafts/{draftId}/pick",
                new { teamId, pokemonEntryId = pickId })).EnsureSuccessStatusCode();
        }

        var final = await StateAsync(admin, draftId);
        Assert.Equal("Complete", final.GetProperty("state").GetString());

        var picks = final.GetProperty("picks").EnumerateArray().ToList();
        Assert.Equal(20, picks.Count); // 2 teams × 10

        // No mon drafted twice, the pool really depletes.
        var ids = picks.Select(p => Int(p, "pokemonEntryId")).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // Every team ends with exactly S1 / A2 / B3 / C4.
        foreach (var teamId in byTeam.Keys)
        {
            var mine = picks.Where(p => Int(p, "teamId") == teamId).ToList();
            Assert.Equal(1, mine.Count(p => p.GetProperty("tier").GetString() == "S"));
            Assert.Equal(2, mine.Count(p => p.GetProperty("tier").GetString() == "A"));
            Assert.Equal(3, mine.Count(p => p.GetProperty("tier").GetString() == "B"));
            Assert.Equal(4, mine.Count(p => p.GetProperty("tier").GetString() == "C"));
        }
    }

    // ── undo / abort ───────────────────────────────────────────────────────

    [Fact]
    public async Task Only_the_picker_or_an_admin_can_undo_the_last_pick()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("p1", "p2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");
        var picker = byTeam[onClock];
        var other = byTeam.First(kv => kv.Key != onClock).Value;

        // On-clock team makes a pick.
        var tier = NextTier(s, onClock);
        (await picker.PostAsJsonAsync($"/api/drafts/{draftId}/offer", new { teamId = onClock, tier })).EnsureSuccessStatusCode();
        var offered = (await StateAsync(admin, draftId)).GetProperty("offered").EnumerateArray().First();
        (await picker.PostAsJsonAsync($"/api/drafts/{draftId}/pick",
            new { teamId = onClock, pokemonEntryId = Int(offered, "pokemonEntryId") })).EnsureSuccessStatusCode();

        // Another player can't undo it.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await other.PostAsync($"/api/drafts/{draftId}/rollback", null)).StatusCode);

        // The picker can.
        (await picker.PostAsync($"/api/drafts/{draftId}/rollback", null)).EnsureSuccessStatusCode();
        var after = await StateAsync(admin, draftId);
        Assert.Empty(after.GetProperty("picks").EnumerateArray());
    }

    [Fact]
    public async Task Abort_is_admin_only_and_restores_the_pool()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("p1", "p2");

        // Make one pick.
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");
        var tier = NextTier(s, onClock);
        (await byTeam[onClock].PostAsJsonAsync($"/api/drafts/{draftId}/offer", new { teamId = onClock, tier })).EnsureSuccessStatusCode();
        var offered = (await StateAsync(admin, draftId)).GetProperty("offered").EnumerateArray().First();
        var pickedId = Int(offered, "pokemonEntryId");
        (await byTeam[onClock].PostAsJsonAsync($"/api/drafts/{draftId}/pick",
            new { teamId = onClock, pokemonEntryId = pickedId })).EnsureSuccessStatusCode();

        // A player can't abort.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await byTeam[onClock].PostAsync($"/api/admin/drafts/{draftId}/abort", null)).StatusCode);

        // The admin can, and it resets.
        (await admin.PostAsync($"/api/admin/drafts/{draftId}/abort", null)).EnsureSuccessStatusCode();
        var after = await StateAsync(admin, draftId);
        Assert.Equal("NotStarted", after.GetProperty("state").GetString());
        Assert.Empty(after.GetProperty("picks").EnumerateArray());

        // The mon that was drafted is back in the pool.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mon = await db.Pokemon.FirstAsync(p => p.Id == pickedId);
        Assert.Null(mon.DraftedByTeamId);
    }

    // ── timeout ────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_timed_out_pick_lands_on_one_of_the_offered_options()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("p1", "p2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");

        // The coach opens a tier but never picks.
        (await byTeam[onClock].PostAsJsonAsync($"/api/drafts/{draftId}/offer", new { teamId = onClock, tier = S }))
            .EnsureSuccessStatusCode();
        var offeredIds = (await StateAsync(admin, draftId)).GetProperty("offered")
            .EnumerateArray().Select(o => Int(o, "pokemonEntryId")).ToHashSet();

        // Fire the clock's auto-pick directly (deterministic, no waiting).
        using (var scope = _factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<DraftEngine>();
            // preferSkip: false so we exercise the pick path directly (a real
            // timeout would auto-SKIP first while the team still holds skips).
            var result = await engine.AutoPickAsync(draftId, preferSkip: false);
            Assert.True(result.Ok);
        }

        var after = await StateAsync(admin, draftId);
        var pick = after.GetProperty("picks").EnumerateArray().Single();
        Assert.Contains(Int(pick, "pokemonEntryId"), offeredIds);
        Assert.True(pick.GetProperty("wasAutoPick").GetBoolean());
    }

    [Fact]
    public async Task An_auto_pick_with_no_tier_opened_still_snapshots_passed_options()
    {
        // A coach whose clock expires WITHOUT ever opening a tier had nothing
        // offered to snapshot, the feed used to show them with an empty "passed"
        // run. Now the engine samples the picked tier's remaining pool so an
        // auto-pick reads like a manual one.
        var (admin, draftId, _) = await StartWithAsync("p1", "p2");

        using (var scope = _factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<DraftEngine>();
            // preferSkip: false so this exercises the pick path (a real timeout
            // would auto-SKIP first while the team still holds skips).
            Assert.True((await engine.AutoPickAsync(draftId, preferSkip: false)).Ok);
        }

        var after = await StateAsync(admin, draftId);
        var pick = after.GetProperty("picks").EnumerateArray().Single();
        Assert.True(pick.GetProperty("wasAutoPick").GetBoolean());

        // otherOptions is a JSON string of the passed run.
        var othersRaw = pick.GetProperty("otherOptions");
        Assert.Equal(JsonValueKind.String, othersRaw.ValueKind);
        var others = JsonSerializer.Deserialize<List<JsonElement>>(othersRaw.GetString()!)!;
        Assert.NotEmpty(others);

        // Every passed option is the SAME tier as the pick and is NOT the pick itself.
        var pickedName = pick.GetProperty("name").GetString();
        var pickedTier = pick.GetProperty("tier").GetString();
        Assert.All(others, o =>
        {
            Assert.Equal(pickedTier, o.GetProperty("tier").GetString());
            Assert.NotEqual(pickedName, o.GetProperty("name").GetString());
        });
    }

    // ── one mega per team ────────────────────────────────────────────────────

    // Rigs tier B to a known 4-mon pool (3 megas + Snorlax) so the offer's random
    // sample is fully determined: B offers 5 >= 4, so every ELIGIBLE mon appears.
    // Optionally marks an unrelated mega as already drafted by a team.
    private async Task RigMegaBPoolAsync(int leagueId, int? megaHolder)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        // Clear the whole league pool (not just tier B): the seed's names are unique
        // per (LeagueId, Name) across ALL tiers, so a stray "Snorlax" elsewhere would
        // collide with the ones added here. The offer under test only reads tier B.
        db.Pokemon.RemoveRange(db.Pokemon.Where(p => p.LeagueId == leagueId));
        db.Pokemon.AddRange(
            new PokemonEntry { LeagueId = leagueId, Name = "M-Blastoise", Sprite = "blastoise-mega", Tier = Tier.B, DexNumber = 9 },
            new PokemonEntry { LeagueId = leagueId, Name = "M-Gengar", Sprite = "gengar-mega", Tier = Tier.B, DexNumber = 94 },
            new PokemonEntry { LeagueId = leagueId, Name = "M-Lopunny", Sprite = "lopunny-mega", Tier = Tier.B, DexNumber = 428 },
            new PokemonEntry { LeagueId = leagueId, Name = "Snorlax", Sprite = "snorlax", Tier = Tier.B, DexNumber = 143 });
        if (megaHolder is int team)
            db.Pokemon.Add(new PokemonEntry { LeagueId = leagueId, Name = "M-Held", Sprite = "held-mega", Tier = Tier.A, DexNumber = 9001, DraftedByTeamId = team });
        await db.SaveChangesAsync();
    }

    private static bool OfferedIsMega(JsonElement o) =>
        DraftEngine.IsMega(o.GetProperty("name").GetString()!, o.GetProperty("sprite").GetString());

    [Fact]
    public async Task Once_a_team_holds_a_mega_no_more_megas_are_offered()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("p1", "p2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");

        // The on-clock team already holds a mega; the B pool is 3 megas + 1 plain.
        // Without the cap all four would be offered; with it, only the non-mega is.
        await RigMegaBPoolAsync(Int(s, "leagueId"), megaHolder: onClock);

        (await byTeam[onClock].PostAsJsonAsync($"/api/drafts/{draftId}/offer",
            new { teamId = onClock, tier = B })).EnsureSuccessStatusCode();

        var offered = (await StateAsync(admin, draftId)).GetProperty("offered").EnumerateArray().ToList();
        Assert.NotEmpty(offered);
        Assert.DoesNotContain(offered, OfferedIsMega);                                    // no more megas
        Assert.Contains(offered, o => o.GetProperty("name").GetString() == "Snorlax");    // the plain mon still is
    }

    [Fact]
    public async Task A_team_with_no_mega_is_still_offered_megas()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("p1", "p2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");

        // Same rigged pool, but the team holds NO mega, so the cap doesn't apply and
        // the megas stay eligible (B offers 5 >= the 4-mon pool, so all four appear).
        await RigMegaBPoolAsync(Int(s, "leagueId"), megaHolder: null);

        (await byTeam[onClock].PostAsJsonAsync($"/api/drafts/{draftId}/offer",
            new { teamId = onClock, tier = B })).EnsureSuccessStatusCode();

        var offered = (await StateAsync(admin, draftId)).GetProperty("offered").EnumerateArray().ToList();
        Assert.Contains(offered, OfferedIsMega);
    }

    [Fact]
    public async Task Auto_pick_wont_give_a_second_mega_to_a_team_that_holds_one()
    {
        var (admin, draftId, byTeam) = await StartWithAsync("p1", "p2");
        var s = await StateAsync(admin, draftId);
        var onClock = Int(s, "onClockTeamId");
        var leagueId = Int(s, "leagueId");

        // The team holds a mega; the only mons left are C-tier megas (which auto-pick
        // reaches first) and one plain B mon. The cap must skip the C megas and land
        // the auto-pick on the plain mon rather than handing out a second mega.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Pokemon.RemoveRange(db.Pokemon.Where(p => p.LeagueId == leagueId));
            db.Pokemon.AddRange(
                new PokemonEntry { LeagueId = leagueId, Name = "M-Held", Sprite = "held-mega", Tier = Tier.A, DexNumber = 9001, DraftedByTeamId = onClock },
                new PokemonEntry { LeagueId = leagueId, Name = "M-Rayquaza", Sprite = "rayquaza-mega", Tier = Tier.C, DexNumber = 384 },
                new PokemonEntry { LeagueId = leagueId, Name = "M-Sableye", Sprite = "sableye-mega", Tier = Tier.C, DexNumber = 302 },
                new PokemonEntry { LeagueId = leagueId, Name = "Snorlax", Sprite = "snorlax", Tier = Tier.B, DexNumber = 143 });
            await db.SaveChangesAsync();
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<DraftEngine>();
            Assert.True((await engine.AutoPickAsync(draftId, preferSkip: false)).Ok);
        }

        var pick = (await StateAsync(admin, draftId)).GetProperty("picks").EnumerateArray().Single();
        Assert.Equal("Snorlax", pick.GetProperty("name").GetString()); // not a second mega
    }
}
