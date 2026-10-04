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
    /// <summary>
    /// Outcome of a sync. Ok is false when the pool was left untouched (no sheet
    /// configured, unreachable, or empty), so a caller that shows the result can
    /// tell "nothing changed" apart from "nothing happened".
    /// </summary>
    public record Result(bool Ok, int Changed, string? Error = null);

    /// <returns>How many rows were added, updated or removed, and whether the sync ran at all.</returns>
    public async Task<Result> RefreshAsync(int leagueId, CancellationToken ct = default)
    {
        var url = config["Pokedex:SheetCsvUrl"];
        if (string.IsNullOrWhiteSpace(url))
        {
            log.LogInformation("Pokédex sync skipped: no sheet URL configured");
            return new Result(false, 0, "No source sheet is configured");
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
            return new Result(false, 0, "Could not read the source sheet");
        }
        if (rows.Count == 0)
        {
            log.LogWarning("Pokédex sync: sheet had no draftable rows; keeping the existing pool");
            return new Result(false, 0, "The source sheet had no draftable rows");
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
        // local pool too.
        //
        // Two kinds of row are SPARED, because both Picks and OfferedOptions hold an
        // ON DELETE CASCADE foreign key to Pokemon, so deleting one of those rows here
        // would silently take draft state with it:
        //   • an already-drafted mon (DraftedByTeamId, and belt-and-braces any mon with
        //     a Pick), so a mid-season ban never rewrites history, and
        //   • a mon currently OFFERED to whoever is on the clock, so refreshing during a
        //     live draft can't yank an option out from under them mid-pick.
        // A sheet edit that bans an offered mon therefore lands on the next sync, once
        // that pick is made. Everything else about the draft (state, clock, picks,
        // teams, readiness) is untouched by this method.
        var draftable = rows.Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var removed = await db.Pokemon
            .Where(p => p.LeagueId == leagueId
                && p.DraftedByTeamId == null
                && !draftable.Contains(p.Name)
                && !db.Picks.Any(pick => pick.PokemonEntryId == p.Id)
                && !db.OfferedOptions.Any(o => o.PokemonEntryId == p.Id))
            .ExecuteDeleteAsync(ct);

        log.LogInformation("Pokédex sync: {Added} added, {Updated} updated, {Removed} removed for league {League}",
            added, updated, removed, leagueId);
        return new Result(true, added + updated + removed);
    }
}
