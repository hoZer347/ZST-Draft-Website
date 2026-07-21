using DraftLeague.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Services;

/// <summary>
/// Re-pulls a league's draft pool from the source Google Sheet. Called when a
/// draft is started, so edits to the sheet (new mons, retiers, stat fixes) take
/// effect on the next draft without a redeploy.
///
/// Upsert only, matched by name, existing rows are updated and new ones added.
/// Nothing is deleted: a mon already drafted must not vanish mid-history, and a
/// re-sync must never un-draft one. The local supplement (pokemon-extra.json) is
/// merged in for mons the sheet doesn't carry.
/// </summary>
public class PokedexSync(AppDbContext db, HttpClient http, IConfiguration config, ILogger<PokedexSync> log)
{
    /// <returns>How many rows were added or updated (0 if the sync was skipped).</returns>
    public async Task<int> RefreshAsync(int leagueId, CancellationToken ct = default)
    {
        var url = config["Pokedex:SheetCsvUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            log.LogInformation("Pokédex sync skipped: no sheet URL configured");
            return 0;
        }

        IReadOnlyList<PokemonRow> rows;
        try
        {
            var csv = await http.GetStringAsync(url, ct);
            rows = Pokedex.Merge(Pokedex.ParseCsv(csv), Pokedex.LoadExtra());
        }
        catch (Exception ex)
        {
            // Never block a draft on the sheet being unreachable or malformed,
            // the existing pool is still perfectly playable.
            log.LogWarning(ex, "Pokédex sync: could not fetch/parse the sheet; keeping the existing pool");
            return 0;
        }
        if (rows.Count == 0)
        {
            log.LogWarning("Pokédex sync: sheet had no draftable rows; keeping the existing pool");
            return 0;
        }

        var existing = await db.Pokemon
            .Where(p => p.LeagueId == leagueId)
            .ToDictionaryAsync(p => p.Name, ct);

        int added = 0, updated = 0;
        foreach (var row in rows)
        {
            if (existing.TryGetValue(row.Name, out var entity)) { row.CopyInto(entity); updated++; }
            else { db.Pokemon.Add(row.ToEntity(leagueId)); added++; }
        }

        await db.SaveChangesAsync(ct);

        // Drop pool mons the sheet no longer lists, a ban (or retier out of
        // S/A/B/C) removes the row from the draftable set, so it should leave the
        // local pool too. A mon that's already been drafted is spared, so a
        // mid-season ban never rewrites history.
        var draftable = rows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var removed = await db.Pokemon
            .Where(p => p.LeagueId == leagueId && p.DraftedByTeamId == null && !draftable.Contains(p.Name))
            .ExecuteDeleteAsync(ct);

        log.LogInformation("Pokédex sync: {Added} added, {Updated} updated, {Removed} removed for league {League}",
            added, updated, removed, leagueId);
        return added + updated + removed;
    }
}
