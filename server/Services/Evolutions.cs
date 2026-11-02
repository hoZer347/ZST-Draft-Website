using System.Text.Json;

namespace DraftLeague.Web.Services;

/// <summary>
/// Pre-evolutions by national dex number, read from the Showdown client's pokedex.
/// Keyed by dex number rather than form, so every form of a species shares one line
/// (Goodra and Goodra-Hisui both reach back to Sliggoo and Goomy).
/// </summary>
public class Evolutions(IWebHostEnvironment env)
{
    private readonly Lazy<Dictionary<int, HashSet<int>>> _prevos = new(() => Load(Path.Combine(
        env.ContentRootPath, "..", "battle-server", "client", "play.pokemonshowdown.com", "data", "pokedex.json")), LazyThreadSafetyMode.PublicationOnly);

    /// <summary>Dex numbers of every mon any of <paramref name="dex"/> evolves from, at any depth.</summary>
    public HashSet<int> PreEvolutionsOf(IEnumerable<int> dex)
    {
        var found = new HashSet<int>();
        var queue = new Queue<int>(dex);
        while (queue.TryDequeue(out var num))
            if (_prevos.Value.TryGetValue(num, out var prevos))
                foreach (var prevo in prevos)
                    if (found.Add(prevo)) queue.Enqueue(prevo);
        return found;
    }

    private static Dictionary<int, HashSet<int>> Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var dex = doc.RootElement;
        var prevos = new Dictionary<int, HashSet<int>>();
        foreach (var mon in dex.EnumerateObject())
        {
            if (!mon.Value.TryGetProperty("prevo", out var prevo)
                || !dex.TryGetProperty(BuildPageRenderer.ToId(prevo.GetString()!), out var from)) continue;
            var num = mon.Value.GetProperty("num").GetInt32();
            if (!prevos.TryGetValue(num, out var set)) prevos[num] = set = [];
            set.Add(from.GetProperty("num").GetInt32());
        }
        return prevos;
    }
}
