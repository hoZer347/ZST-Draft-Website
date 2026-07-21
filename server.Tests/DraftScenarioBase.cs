using System.Net.Http.Json;
using System.Text.Json;

namespace DraftLeague.Web.Tests;

/// <summary>
/// Shared scaffolding for the feature suites below. Each suite gets its OWN
/// factory + throwaway DB (via <see cref="IAsyncLifetime"/>) because the draft
/// roster IS the set of signed-in users and the debug-slot claims live in a
/// per-app singleton, sharing either across tests would let one test's roster
/// leak into another's assertions.
///
/// The helpers mirror the private ones in <see cref="DraftTests"/> so every suite
/// reads the same way; they're centralised here rather than copied per file.
/// </summary>
public abstract class DraftScenarioBase : IAsyncLifetime
{
    protected readonly DraftLeagueFactory Factory = new();

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await ((IAsyncLifetime)Factory).DisposeAsync();

    // Ordinals the offer endpoint expects (Tier enum order).
    protected const int S = 0, A = 1, B = 2, C = 3;

    // ── json accessors ─────────────────────────────────────────────────────

    protected static int Int(JsonElement e, string prop) => e.GetProperty(prop).GetInt32();

    protected static int? IntOrNull(JsonElement e, string prop) =>
        e.GetProperty(prop).ValueKind == JsonValueKind.Null ? null : e.GetProperty(prop).GetInt32();

    protected static string? Str(JsonElement e, string prop) =>
        e.GetProperty(prop).ValueKind == JsonValueKind.Null ? null : e.GetProperty(prop).GetString();

    // ── draft flow ─────────────────────────────────────────────────────────

    protected async Task<int> DraftIdAsync(HttpClient client)
    {
        var drafts = await client.GetFromJsonAsync<JsonElement>("/api/drafts");
        return drafts.EnumerateArray().First().GetProperty("id").GetInt32();
    }

    protected static Task<JsonElement> StateAsync(HttpClient client, int draftId) =>
        client.GetFromJsonAsync<JsonElement>($"/api/drafts/{draftId}");

    /// <summary>Signs in an admin + the given players, then starts the draft.</summary>
    protected async Task<(HttpClient Admin, int DraftId, Dictionary<int, HttpClient> ByTeam)> StartWithAsync(params string[] playerIds)
    {
        var admin = await Factory.SignedInAsAsync("admin", admin: true);
        var draftId = await DraftIdAsync(admin);

        var clients = new List<(string Id, HttpClient Client)>();
        foreach (var id in playerIds)
        {
            var client = await Factory.SignedInAsAsync(id);
            // Readiness is opt-in now: the Start roster is built from who readied
            // up, so each player must ready before the draft can start.
            (await client.PostAsync($"/api/drafts/{draftId}/ready", null)).EnsureSuccessStatusCode();
            clients.Add((id, client));
        }

        (await admin.PostAsync($"/api/admin/drafts/{draftId}/start", null)).EnsureSuccessStatusCode();

        var byTeam = new Dictionary<int, HttpClient>();
        foreach (var (_, client) in clients)
        {
            var s = await StateAsync(client, draftId);
            byTeam[Int(s, "myTeamId")] = client;
        }
        return (admin, draftId, byTeam);
    }

    /// <summary>First tier (S→C) the given team still owes a slot.</summary>
    protected static int NextTier(JsonElement state, int teamId)
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

    /// <summary>The pokemon ids currently offered to whoever is on the clock.</summary>
    protected static async Task<List<int>> OfferedIdsAsync(HttpClient client, int draftId)
    {
        var s = await StateAsync(client, draftId);
        return s.GetProperty("offered").EnumerateArray().Select(o => Int(o, "pokemonEntryId")).ToList();
    }

    /// <summary>
    /// Drives one complete pick for whoever is on the clock: opens the tier they
    /// still owe and takes the first option. Returns the picked pokemon id.
    /// </summary>
    protected async Task<int> PickOnceAsync(HttpClient admin, int draftId, Dictionary<int, HttpClient> byTeam)
    {
        var s = await StateAsync(admin, draftId);
        var teamId = Int(s, "onClockTeamId");
        var client = byTeam[teamId];
        var tier = NextTier(s, teamId);

        (await client.PostAsJsonAsync($"/api/drafts/{draftId}/offer", new { teamId, tier })).EnsureSuccessStatusCode();
        var pickId = (await OfferedIdsAsync(admin, draftId)).First();
        (await client.PostAsJsonAsync($"/api/drafts/{draftId}/pick", new { teamId, pokemonEntryId = pickId }))
            .EnsureSuccessStatusCode();
        return pickId;
    }
}
