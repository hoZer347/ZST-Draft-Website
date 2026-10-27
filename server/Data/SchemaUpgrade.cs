using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Data;

/// <summary>
/// Adds columns introduced after a database was created. EnsureCreated only builds a
/// missing database, so an existing one would otherwise lack them. Idempotent.
/// </summary>
public static class SchemaUpgrade
{
    private static readonly (string Table, string Column, string Type)[] Columns =
    [
        ("Teams", "PlayoffSeed", "INTEGER"),
        ("Matches", "PlayoffRound", "INTEGER"),
        ("Matches", "PlayoffSlot", "INTEGER"),
    ];

    public static async Task ApplyAsync(AppDbContext db, CancellationToken ct = default)
    {
        foreach (var (table, column, type) in Columns)
        {
            var exists = await db.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info({table}) WHERE name = {column}")
                .SingleAsync(ct);
            if (exists == 0)
#pragma warning disable EF1002 // identifiers can't be parameters; these come from Columns above
                await db.Database.ExecuteSqlRawAsync($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {type} NULL", ct);
#pragma warning restore EF1002
        }
    }
}
