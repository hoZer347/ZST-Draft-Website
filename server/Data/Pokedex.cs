using System.Text;
using System.Text.Json;
using DraftLeague.Web.Models;

namespace DraftLeague.Web.Data;

/// <summary>One draftable pokémon, as it appears in the source sheet.</summary>
public record PokemonRow(
    string Name, int Dex, string Tier, string? Sprite,
    int Hp, int Atk, int Def, int Spa, int Spd, int Spe,
    string? Type1, string? Type2, string? Ability1, string? Ability2, string? Hidden)
{
    public Tier TierEnum => Enum.Parse<Tier>(Tier);
}

/// <summary>
/// Loads the draft pool from two places: the committed snapshot the dev DB is
/// first seeded from (Data/pokemon-pool.json), and a small local supplement for
/// mons the source sheet doesn't carry (Data/pokemon-extra.json, e.g. AZ's
/// Eternal-Flower Floette). PokedexSync uses the same CSV parser against the
/// live sheet, merged with the same supplement.
/// </summary>
public static class Pokedex
{
    private static string DataPath(string file) => Path.Combine(AppContext.BaseDirectory, "Data", file);

    /// <summary>The seed snapshot plus the local supplement, deduped by name.</summary>
    public static IReadOnlyList<PokemonRow> LoadSeed() => Merge(LoadJson("pokemon-pool.json"), LoadExtra());

    /// <summary>Mons kept locally because the sheet doesn't have them.</summary>
    public static IReadOnlyList<PokemonRow> LoadExtra() => LoadJson("pokemon-extra.json");

    private static IReadOnlyList<PokemonRow> LoadJson(string file)
    {
        var path = DataPath(file);
        if (!File.Exists(path)) return [];
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<List<PokemonRow>>(stream, JsonOpts) ?? [];
    }

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>First occurrence of a name wins.</summary>
    public static IReadOnlyList<PokemonRow> Merge(params IEnumerable<PokemonRow>[] sources)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PokemonRow>();
        foreach (var row in sources.SelectMany(s => s).Select(Normalize))
            if (seen.Add(row.Name)) result.Add(row);
        return result;
    }

    /// <summary>
    /// The source sheet documents a few mons as an in-battle forme we never draft
    /// directly (Palafin's Hero forme). We draft the BASE forme, which becomes the
    /// battle forme via its ability, so remap those rows to the base name + sprite.
    /// Applied on every load path (seed and live re-sync) so the sheet can keep its
    /// own labels without ever re-introducing the forme into the pool.
    /// </summary>
    private static readonly Dictionary<string, (string Name, string Sprite)> FormOverrides =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Palafin-H"] = ("Palafin", "palafin"),
        };

    private static PokemonRow Normalize(PokemonRow r) =>
        FormOverrides.TryGetValue(r.Name, out var o) ? r with { Name = o.Name, Sprite = o.Sprite } : r;

    // ── CSV (the live sheet) ────────────────────────────────────────────

    /// <summary>
    /// Parses the sheet's CSV export into draftable rows (tiers S/A/B/C only,
    /// the sheet's Z/X rows aren't in the draft). Column order can change, so
    /// everything is looked up by header name.
    /// </summary>
    public static IReadOnlyList<PokemonRow> ParseCsv(string csv)
    {
        var rows = SplitCsv(csv);
        if (rows.Count < 2) return [];

        var header = rows[0];
        int Col(string name)
        {
            var i = Array.FindIndex(header, h => h.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (i < 0) throw new FormatException($"Sheet is missing the '{name}' column");
            return i;
        }
        int dex = Col("Dex"), tier = Col("Tier"), name = Col("Pokemon"), sprite = Col("Raw (Showdown)"),
            hp = Col("HP"), atk = Col("Atk"), def = Col("Def"), spa = Col("SpA"), spd = Col("SpD"), spe = Col("Spe"),
            t1 = Col("Type I"), t2 = Col("Type II"), a1 = Col("Ability I"), a2 = Col("Ability II"), ha = Col("Hidden Ability");

        var max = new[] { dex, tier, name, sprite, hp, atk, def, spa, spd, spe, t1, t2, a1, a2, ha }.Max();
        var result = new List<PokemonRow>();
        foreach (var r in rows.Skip(1))
        {
            if (r.Length <= max) continue;
            var t = r[tier].Trim();
            if (t is not ("S" or "A" or "B" or "C")) continue;
            var nm = r[name].Trim();
            if (nm.Length == 0) continue;
            result.Add(new PokemonRow(
                nm, Num(r[dex]), t, Str(r[sprite]),
                Num(r[hp]), Num(r[atk]), Num(r[def]), Num(r[spa]), Num(r[spd]), Num(r[spe]),
                Str(r[t1]), Str(r[t2]), Str(r[a1]), Str(r[a2]), Str(r[ha])));
        }
        return result;
    }

    private static int Num(string v)
    {
        v = v.Trim().Replace(",", "");
        return int.TryParse(v, out var n) ? n : (double.TryParse(v, out var d) ? (int)d : 0);
    }

    private static string? Str(string v) { v = v.Trim(); return v.Length == 0 ? null : v; }

    /// <summary>Minimal RFC-4180 CSV splitter, handles quoted fields, escaped
    /// quotes and quoted newlines, which Google's export produces.</summary>
    private static List<string[]> SplitCsv(string text)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else if (c == '"') inQuotes = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { /* handled by \n */ }
            else if (c == '\n') { row.Add(field.ToString()); field.Clear(); rows.Add(row.ToArray()); row = []; }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    // ── mapping to the entity ───────────────────────────────────────────

    public static PokemonEntry ToEntity(this PokemonRow r, int leagueId) => new()
    {
        LeagueId = leagueId,
        Name = r.Name, Tier = r.TierEnum, DexNumber = r.Dex, Sprite = r.Sprite,
        Hp = r.Hp, Atk = r.Atk, Def = r.Def, SpAtk = r.Spa, SpDef = r.Spd, Speed = r.Spe,
        Type1 = r.Type1, Type2 = r.Type2, Ability1 = r.Ability1, Ability2 = r.Ability2, HiddenAbility = r.Hidden,
    };

    /// <summary>Copies the sheet's fields onto an existing row (keeps its id and
    /// DraftedByTeamId, a re-sync must not un-draft a mon).</summary>
    public static void CopyInto(this PokemonRow r, PokemonEntry e)
    {
        e.Tier = r.TierEnum; e.DexNumber = r.Dex; e.Sprite = r.Sprite;
        e.Hp = r.Hp; e.Atk = r.Atk; e.Def = r.Def; e.SpAtk = r.Spa; e.SpDef = r.Spd; e.Speed = r.Spe;
        e.Type1 = r.Type1; e.Type2 = r.Type2; e.Ability1 = r.Ability1; e.Ability2 = r.Ability2; e.HiddenAbility = r.Hidden;
    }
}
