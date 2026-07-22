using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DraftLeague.Web.Services;

/// <summary>
/// Renders a stored team build (Showdown / pokepaste export text) as a self-contained
/// HTML page, the way pokepaste.es does: one card per mon with its sprite, item,
/// ability, tera, a little hexagon of its EV spread (nature-boosted stat in red, the
/// hindered one in blue) and its moves as type-coloured, category-badged rows, plus
/// the raw export and a copy button. Hosted on our own server (a plain link, no SPA).
///
/// The export is NOT re-authored, only shown: parsing is lenient and best-effort for
/// the card layout, and the verbatim export is always included underneath so nothing
/// is lost if a line doesn't fit the known shape. Sprites and the type/category badges
/// come from the same Showdown CDN the rest of the site uses.
/// </summary>
public static class BuildPageRenderer
{
    public sealed record MonSet(
        string Species, string? Item, string? Ability, string? Tera, string? Level,
        string? Nature, string? Evs, string? Ivs, bool Shiny,
        IReadOnlyList<string> Moves, IReadOnlyList<int> EvSpread,
        string SpriteUrl, string? SpriteFallback, string? Tier, string Raw);

    public sealed record TeamView(string CoachName, IReadOnlyList<MonSet> Sets, string RawExport);

    // Stat order used everywhere below: HP, Atk, Def, SpA, SpD, Spe.
    private static readonly string[] StatKeys = { "hp", "atk", "def", "spa", "spd", "spe" };

    // Move id -> (Type, Category), loaded once from the dumped Showdown move data.
    private static readonly Lazy<Dictionary<string, (string Type, string Cat)>> MoveData = new(() =>
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "moves.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
                map[p.Name] = (p.Value[0].GetString() ?? "", p.Value[1].GetString() ?? "");
        }
        catch { /* no data -> moves render plain, without type/category */ }
        return map;
    });

    private static (string Type, string Cat) MoveInfo(string name) =>
        MoveData.Value.TryGetValue(ToId(name), out var v) ? v : ("", "");

    // Item id -> its icon number on Showdown's itemicons sprite sheet.
    private static readonly Lazy<Dictionary<string, int>> ItemData = new(() =>
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "items.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject()) map[p.Name] = p.Value.GetInt32();
        }
        catch { /* no data -> items render without an icon */ }
        return map;
    });

    private static int ItemSprite(string name) => ItemData.Value.TryGetValue(ToId(name), out var n) ? n : -1;

    // Item id -> the forme it makes: (forme name, base species, sprite slug, dex). Covers
    // mega stones and forced-forme items (origin orbs/cores, drives, memories, ...).
    private static readonly Lazy<Dictionary<string, (string Forme, string Base, string Slug, int Num)>> FormeItems = new(() =>
    {
        var map = new Dictionary<string, (string, string, string, int)>(StringComparer.Ordinal);
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Data", "formeitems.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var p in doc.RootElement.EnumerateObject())
                map[p.Name] = (p.Value[0].GetString() ?? "", p.Value[1].GetString() ?? "", p.Value[2].GetString() ?? "", p.Value[3].GetInt32());
        }
        catch { /* no data -> megas stay as their base form */ }
        return map;
    });

    /// <summary>Tier sort key: S, A, B, C, then anything untiered.</summary>
    private static int TierRank(MonSet m) => m.Tier switch { "S" => 0, "A" => 1, "B" => 2, "C" => 3, _ => 4 };

    // Nature -> (boosted stat index, hindered stat index) in StatKeys order. Neutral
    // natures (Hardy, Docile, Serious, Bashful, Quirky) aren't listed. HP is never
    // affected, so a nature only ever touches Atk/Def/SpA/SpD/Spe.
    private static readonly Dictionary<string, (int Plus, int Minus)> Natures = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Adamant"] = (1, 3), ["Lonely"] = (1, 2), ["Brave"] = (1, 5), ["Naughty"] = (1, 4),
        ["Bold"] = (2, 1), ["Impish"] = (2, 3), ["Relaxed"] = (2, 5), ["Lax"] = (2, 4),
        ["Modest"] = (3, 1), ["Mild"] = (3, 2), ["Quiet"] = (3, 5), ["Rash"] = (3, 4),
        ["Calm"] = (4, 1), ["Gentle"] = (4, 2), ["Sassy"] = (4, 5), ["Careful"] = (4, 3),
        ["Timid"] = (5, 1), ["Hasty"] = (5, 2), ["Jolly"] = (5, 3), ["Naive"] = (5, 4),
    };

    /// <summary>Showdown's toID: lowercase, alphanumerics only.</summary>
    public static string ToId(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>
    /// Serebii's Legends Z-A mega artwork URL for a "-mega[x/y/z]" slug (3-digit national
    /// dex + a mega suffix), or null for a non-mega / a slug with no dex. Mirrors
    /// web/sprite.js serebiiMega so every surface shows the same mega image.
    /// </summary>
    private static string? SerebiiMega(string? slug, int? dex)
    {
        if (slug is null || dex is not int d) return null;
        var m = Regex.Match(slug, @"-mega([xyz]?)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var suffix = m.Groups[1].Value.ToLowerInvariant() switch { "x" => "-mx", "y" => "-my", "z" => "-mz", _ => "-m" };
        return $"https://www.serebii.net/legendsz-a/pokemon/{d:D3}{suffix}.png";
    }

    /// <summary>The sprite URL for a mon, mirroring the web client's spriteUrl().</summary>
    private static string SpriteUrl(string? slug, int? dex) =>
        string.IsNullOrEmpty(slug)
            ? (dex is int d ? $"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/{d}.png" : "")
            : Regex.IsMatch(slug, "^https?://", RegexOptions.IgnoreCase) ? slug
            : $"https://play.pokemonshowdown.com/sprites/gen5/{slug}.png";

    /// <summary>Parses an "EVs:" line into the six stat values (0 for any not listed).</summary>
    private static int[] ParseSpread(string? line)
    {
        var v = new int[6];
        if (string.IsNullOrWhiteSpace(line)) return v;
        foreach (var part in line.Split('/'))
        {
            var t = part.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length < 2 || !int.TryParse(t[0], out var n)) continue;
            var i = Array.IndexOf(StatKeys, t[1].Trim().ToLowerInvariant());
            if (i >= 0) v[i] = n;
        }
        return v;
    }

    /// <summary>
    /// Parses an export into per-mon sets for the card layout. <paramref name="spriteOf"/>
    /// maps a species id to the pool's (slug, dex) so sprites match the rest of the site.
    /// </summary>
    public static IReadOnlyList<MonSet> Parse(string export, Func<string, (string? Slug, int? Dex, string? Tier)> spriteOf)
    {
        var sets = new List<MonSet>();
        // Sets are separated by blank lines; a stray CRLF or trailing space is fine.
        foreach (var rawBlock in Regex.Split(export.Replace("\r\n", "\n").Trim(), @"\n\s*\n"))
        {
            var block = rawBlock.Trim();
            if (block.Length == 0) continue;
            var lines = block.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (lines.Count == 0) continue;

            // Header: "Nickname (Species) (M) @ Item", "Species @ Item" or "Species".
            var header = lines[0];
            string? item = null;
            var species = header;
            var at = header.IndexOf(" @ ", StringComparison.Ordinal);
            if (at >= 0) { item = header[(at + 3)..].Trim(); species = header[..at].Trim(); }
            species = Regex.Replace(species, @"\s*\((?:M|F)\)\s*$", ""); // drop a trailing gender
            var nick = Regex.Match(species, @"^.*\(([^)]+)\)\s*$");      // Nickname (Species)
            if (nick.Success) species = nick.Groups[1].Value.Trim();

            string? ability = null, tera = null, level = null, nature = null, evs = null, ivs = null;
            var shiny = false;
            var moves = new List<string>();
            foreach (var line in lines.Skip(1))
            {
                if (line.StartsWith("- ", StringComparison.Ordinal)) moves.Add(line[2..].Trim());
                else if (line.StartsWith("Ability: ", StringComparison.Ordinal)) ability = line[9..].Trim();
                else if (line.StartsWith("Tera Type: ", StringComparison.Ordinal)) tera = line[11..].Trim();
                else if (line.StartsWith("Level: ", StringComparison.Ordinal)) level = line[7..].Trim();
                else if (line.StartsWith("EVs: ", StringComparison.Ordinal)) evs = line[5..].Trim();
                else if (line.StartsWith("IVs: ", StringComparison.Ordinal)) ivs = line[5..].Trim();
                else if (line.Equals("Shiny: Yes", StringComparison.OrdinalIgnoreCase)) shiny = true;
                else if (line.EndsWith(" Nature", StringComparison.Ordinal)) nature = line[..^7].Trim();
            }

            // A base mon holding its own forme item (a mega stone, an origin orb/core,
            // ...) is shown AS that forme. Resolve its tier + sprite from the FORME's own
            // pool entry (keyed by the forme's sprite slug, e.g. "lucario-mega"), so when
            // BOTH the base and the mega are drafted we get the mega's, not the base's.
            var display = species;
            (string? Slug, int? Dex, string? Tier) r;
            if (item is not null && FormeItems.Value.TryGetValue(ToId(item), out var f) && ToId(f.Base) == ToId(species))
            {
                display = f.Forme;
                r = spriteOf(ToId(f.Slug));
                if (r.Slug is null && r.Dex is null) r = (f.Slug, f.Num, r.Tier);
            }
            else r = spriteOf(ToId(species));
            var (slug, dex, tier) = r;
            // A mega (base + stone) → Serebii's Legends Z-A mega artwork as the primary
            // sprite (the SAME image the web app and battle client use), since Showdown
            // has no gen-5 sprite for the newer megas and would otherwise fall to the
            // base-form dex sprite. Non-megas keep the normal sprite + PokeAPI base net.
            var serebiiMega = SerebiiMega(slug, dex);
            var url = serebiiMega ?? SpriteUrl(slug, dex);
            var fallback = serebiiMega is not null
                ? SpriteUrl(slug, dex)
                : (dex is int dd ? $"https://raw.githubusercontent.com/PokeAPI/sprites/master/sprites/pokemon/{dd}.png" : null);
            sets.Add(new MonSet(display, item, ability, tera, level, nature, evs, ivs, shiny,
                moves, ParseSpread(evs), url, fallback, tier, block));
        }
        return sets;
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    // ── the little EV hexagon ────────────────────────────────────────────────

    // Visual order clockwise from the top: HP, Atk, Def, Spe, SpD, SpA, the classic
    // summary-screen hexagon (physical stats on the right, special on the left).
    private static readonly int[] HexOrder = { 0, 1, 2, 5, 4, 3 };
    private static readonly string[] HexLabels = { "HP", "Atk", "Def", "Spe", "SpD", "SpA" };
    private const int MaxEv = 252;

    private static (double X, double Y) HexPoint(double frac, int corner)
    {
        const double cx = 66, cy = 57, r = 40;
        var ang = (-90 + 60 * corner) * Math.PI / 180.0;
        return (cx + r * frac * Math.Cos(ang), cy + r * frac * Math.Sin(ang));
    }

    private static string Poly(double frac)
    {
        var pts = Enumerable.Range(0, 6).Select(k => { var (x, y) = HexPoint(frac, k); return $"{x:0.#},{y:0.#}"; });
        return string.Join(" ", pts);
    }

    /// <summary>
    /// An SVG hexagon of the EV spread, or empty if the mon carries no EVs. The
    /// nature-boosted stat's label is drawn red and the hindered one blue.
    /// </summary>
    private static string EvHexagon(IReadOnlyList<int> ev, int plus, int minus)
    {
        if (ev.All(x => x <= 0)) return "";
        var sb = new StringBuilder();
        sb.Append("<svg class=\"ev-hex\" viewBox=\"0 0 132 116\" role=\"img\" aria-label=\"EV spread\">");
        sb.Append("<polygon class=\"hex-ring\" points=\"").Append(Poly(1)).Append("\"/>");
        sb.Append("<polygon class=\"hex-ring hex-mid\" points=\"").Append(Poly(0.5)).Append("\"/>");
        for (var k = 0; k < 6; k++)
        {
            var (x, y) = HexPoint(1, k);
            sb.Append("<line class=\"hex-spoke\" x1=\"66\" y1=\"57\" x2=\"").Append($"{x:0.#}").Append("\" y2=\"").Append($"{y:0.#}").Append("\"/>");
        }
        var valPts = Enumerable.Range(0, 6).Select(k =>
        {
            var (x, y) = HexPoint(Math.Clamp(ev[HexOrder[k]] / (double)MaxEv, 0, 1), k);
            return $"{x:0.#},{y:0.#}";
        });
        sb.Append("<polygon class=\"hex-val\" points=\"").Append(string.Join(" ", valPts)).Append("\"/>");
        for (var k = 0; k < 6; k++)
        {
            if (ev[HexOrder[k]] <= 0) continue;
            var (x, y) = HexPoint(Math.Clamp(ev[HexOrder[k]] / (double)MaxEv, 0, 1), k);
            sb.Append("<circle class=\"hex-dot\" r=\"2\" cx=\"").Append($"{x:0.#}").Append("\" cy=\"").Append($"{y:0.#}").Append("\"/>");
        }
        // Labels: stat name at each vertex, EV number under it when non-zero. The
        // nature-boosted/hindered axes get the plus/minus class for their colour.
        for (var k = 0; k < 6; k++)
        {
            var stat = HexOrder[k];
            var (lx, ly) = HexPoint(1.34, k);
            var val = ev[stat];
            var cls = stat == plus ? " plus" : stat == minus ? " minus" : val > 0 ? " on" : "";
            sb.Append("<text class=\"hex-lbl").Append(cls).Append("\" x=\"").Append($"{lx:0.#}").Append("\" y=\"").Append($"{ly:0.#}").Append("\">");
            sb.Append("<tspan x=\"").Append($"{lx:0.#}").Append("\">").Append(HexLabels[k]).Append("</tspan>");
            if (val > 0) sb.Append("<tspan class=\"hex-num\" x=\"").Append($"{lx:0.#}").Append("\" dy=\"9\">").Append(val).Append("</tspan>");
            sb.Append("</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    // ── the page ─────────────────────────────────────────────────────────────

    private const string Cdn = "https://play.pokemonshowdown.com/sprites";

    public static string Render(string pageTitle, IReadOnlyList<TeamView> teams)
    {
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append("<title>").Append(E(pageTitle)).Append("</title>");
        sb.Append("<style>").Append(Css).Append("</style></head><body>");
        sb.Append("<header class=\"page-head\"><div class=\"wrap\"><h1>").Append(E(pageTitle)).Append("</h1>");
        sb.Append("<p class=\"sub\">Team builds as brought to the game, hosted from the Draft League server.</p></div></header>");
        sb.Append("<main class=\"wrap\">");

        for (var ti = 0; ti < teams.Count; ti++)
        {
            var t = teams[ti];
            sb.Append("<section class=\"team\">");
            sb.Append("<div class=\"team-head\"><h2>").Append(E(t.CoachName)).Append("</h2>");
            sb.Append("<button class=\"copy\" type=\"button\" data-raw=\"raw").Append(ti).Append("\">Copy team</button></div>");

            sb.Append("<div class=\"mons\">");
            foreach (var m in t.Sets.OrderBy(TierRank))
            {
                var (plus, minus) = m.Nature is not null && Natures.TryGetValue(m.Nature, out var np) ? np : (-1, -1);
                var tiered = m.Tier is "S" or "A" or "B" or "C";

                sb.Append("<article class=\"mon").Append(tiered ? " tier-" + m.Tier!.ToLowerInvariant() : "").Append("\">");
                sb.Append("<div class=\"mon-top\">");
                sb.Append("<img class=\"sprite\" alt=\"\" loading=\"lazy\" src=\"").Append(E(m.SpriteUrl)).Append('"');
                if (m.SpriteFallback is not null)
                    sb.Append(" onerror=\"this.onerror=null;this.src='").Append(E(m.SpriteFallback)).Append("'\"");
                sb.Append('>');

                sb.Append("<div class=\"body\"><div class=\"title\">");
                sb.Append("<span class=\"species\">").Append(E(m.Species)).Append("</span>");
                // Tera type as its recoloured glyph icon, right after the name.
                if (m.Tera is not null)
                    sb.Append("<img class=\"tera-icon\" width=\"18\" height=\"18\" loading=\"lazy\" alt=\"Tera ").Append(E(m.Tera))
                      .Append("\" title=\"Tera ").Append(E(m.Tera)).Append("\" src=\"/type-icons/").Append(E(m.Tera.ToLowerInvariant())).Append(".png\">");
                if (m.Item is not null)
                {
                    sb.Append("<span class=\"item\">@ ").Append(E(m.Item));
                    var sn = ItemSprite(m.Item);
                    if (sn >= 0)
                        sb.Append("<span class=\"item-icon\" style=\"background-position:-").Append((sn % 16) * 24).Append("px -").Append((sn / 16) * 24).Append("px\"></span>");
                    sb.Append("</span>");
                }
                sb.Append("</div>"); // .title

                var meta = new List<string>();
                if (m.Ability is not null) meta.Add("Ability: " + E(m.Ability));
                if (m.Level is not null) meta.Add("Level " + E(m.Level));
                if (m.Shiny) meta.Add("Shiny");
                if (meta.Count > 0) sb.Append("<div class=\"meta\">").Append(string.Join(" &middot; ", meta)).Append("</div>");

                var line2 = new List<string>();
                if (m.Nature is not null) line2.Add(E(m.Nature) + " Nature");
                if (m.Ivs is not null) line2.Add("<span class=\"ivs\">IVs: " + E(m.Ivs) + "</span>");
                if (line2.Count > 0) sb.Append("<div class=\"spread\">").Append(string.Join(" &middot; ", line2)).Append("</div>");

                sb.Append("</div>"); // .body
                sb.Append("</div>"); // .mon-top

                // Second row: the EV hexagon beside the moves. Moves are stacked rows,
                // each tinted by its type with a type badge and a physical/special/status
                // category badge.
                var hex = EvHexagon(m.EvSpread, plus, minus);
                if (hex.Length > 0 || m.Moves.Count > 0)
                {
                    sb.Append("<div class=\"mon-mid\">").Append(hex);
                    if (m.Moves.Count > 0)
                    {
                        sb.Append("<div class=\"moves\">");
                        foreach (var mv in m.Moves)
                        {
                            var (type, cat) = MoveInfo(mv);
                            sb.Append("<div class=\"move").Append(type.Length > 0 ? " tc-" + type.ToLowerInvariant() : "").Append("\">");
                            if (type.Length > 0)
                                sb.Append("<img class=\"move-type\" alt=\"").Append(E(type)).Append("\" loading=\"lazy\" src=\"").Append(Cdn).Append("/types/").Append(E(type)).Append(".png\">");
                            sb.Append("<span class=\"move-name\">").Append(E(mv)).Append("</span>");
                            if (cat.Length > 0)
                                sb.Append("<img class=\"move-cat\" alt=\"").Append(E(cat)).Append("\" title=\"").Append(E(cat)).Append("\" loading=\"lazy\" src=\"").Append(Cdn).Append("/categories/").Append(E(cat)).Append(".png\">");
                            sb.Append("</div>");
                        }
                        sb.Append("</div>");
                    }
                    sb.Append("</div>"); // .mon-mid
                }
                sb.Append("</article>");
            }
            sb.Append("</div>");

            // Verbatim export, always shown so nothing the parser skipped is lost, and
            // it's the exact text to paste into Showdown's teambuilder import box.
            sb.Append("<details class=\"raw-wrap\"><summary>Raw export (import into Showdown)</summary>");
            sb.Append("<pre class=\"raw\" id=\"raw").Append(ti).Append("\">").Append(E(t.RawExport)).Append("</pre></details>");
            sb.Append("</section>");
        }

        sb.Append("</main>");
        sb.Append("<script>document.querySelectorAll('.copy').forEach(function(b){b.addEventListener('click',function(){")
          .Append("var t=document.getElementById(b.dataset.raw);navigator.clipboard.writeText(t.textContent).then(function(){")
          .Append("b.classList.add('ok');var o=b.textContent;b.textContent='Copied';setTimeout(function(){b.textContent=o;b.classList.remove('ok');},1300);});});});</script>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private const string Css = """
        :root { color-scheme: light dark;
          --bg:#f4f5f7; --card:#fff; --line:#e2e5ea; --text:#1b1d21; --muted:#6b7280; --accent:#e3350d; --ok:#1a9e5f;
          --plus:#e0362c; --minus:#2f7bd8; --hex-grid:#d7dbe1; --hex-fill:rgba(120,130,145,.05); }
        @media (prefers-color-scheme: dark) { :root {
          --bg:#14161b; --card:#1e2128; --line:#2c313a; --text:#e6e8ec; --muted:#9aa1ac; --accent:#ff6a4d; --ok:#37c980;
          --plus:#ff5a4d; --minus:#5aa2ff; --hex-grid:#333a45; --hex-fill:rgba(150,160,175,.06); } }
        * { box-sizing: border-box; }
        body { margin:0; background:var(--bg); color:var(--text); font:15px/1.45 -apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif; }
        .wrap { max-width:1040px; margin:0 auto; padding:0 16px; }
        .page-head { padding:22px 0 18px; border-bottom:1px solid var(--line);
          background:linear-gradient(180deg, color-mix(in srgb, var(--accent) 7%, transparent), transparent); }
        .page-head h1 { margin:0 0 4px; font-size:23px; letter-spacing:-.01em; }
        .page-head .sub { margin:0; color:var(--muted); font-size:13px; }
        /* main also carries .wrap (padding:0 16px); its class selector outranks a bare
           `main`, so the top/bottom gap MUST be set on `main.wrap` to actually apply. */
        main.wrap { padding-top:48px; padding-bottom:52px; }
        .team { margin:0 0 34px; }
        .team-head { display:flex; align-items:center; gap:12px; margin:0 0 26px; min-height:34px; }
        .team-head h2 { margin:0; font-size:19px; line-height:34px; }
        .team-head h2::before { content:""; display:inline-block; width:9px; height:9px; border-radius:50%; background:var(--accent); margin-right:9px; vertical-align:middle; }
        .copy { border:1px solid var(--line); background:var(--card); color:var(--text); border-radius:8px; height:34px; padding:0 15px; font-size:13px; font-weight:600; display:inline-flex; align-items:center; cursor:pointer; transition:.12s; }
        .copy:hover { border-color:var(--accent); color:var(--accent); }
        .copy.ok { border-color:var(--ok); color:var(--ok); }
        .mons { display:grid; grid-template-columns:repeat(auto-fill,minmax(340px,1fr)); gap:14px; align-items:start; }
        .mon { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:13px 14px; transition:transform .12s, box-shadow .12s; }
        .mon:hover { box-shadow:0 6px 18px -12px rgba(0,0,0,.4); transform:translateY(-1px); }
        /* Draft-tier effect: a tier-coloured left lip and a wash fading to the card. */
        .mon.tier-s{--tier:#f0850c}.mon.tier-a{--tier:#a335ee}.mon.tier-b{--tier:#2ea043}.mon.tier-c{--tier:#2d8fdb}
        .mon.tier-s,.mon.tier-a,.mon.tier-b,.mon.tier-c { border-left:4px solid var(--tier);
          background:linear-gradient(100deg, color-mix(in srgb, var(--tier) 13%, var(--card)) 0%, var(--card) 55%); }
        .mon-top { display:flex; gap:13px; align-items:center; }
        .sprite { width:96px; height:96px; object-fit:contain; image-rendering:pixelated; flex:0 0 auto; }
        .mon-mid { display:flex; gap:12px; align-items:center; margin-top:11px; }
        .body { min-width:0; flex:1 1 auto; }
        .title { display:flex; align-items:center; flex-wrap:wrap; gap:5px 7px; font-weight:700; font-size:15px; }
        .title .item { display:inline-flex; align-items:center; gap:3px; color:var(--muted); font-weight:400; font-size:13px; }
        .tera-icon { width:18px; height:18px; flex:0 0 auto; }
        .item-icon { display:inline-block; width:24px; height:24px; flex:0 0 auto; vertical-align:middle;
          background-image:url('https://play.pokemonshowdown.com/sprites/itemicons-sheet.png'); background-repeat:no-repeat; }
        .meta, .spread { color:var(--muted); font-size:12.5px; margin-top:3px; }
        .spread .ivs { font-size:11px; }
        .ev-hex { width:118px; height:auto; flex:0 0 auto; align-self:center; }
        .hex-ring { fill:var(--hex-fill); stroke:var(--hex-grid); stroke-width:1; }
        .hex-mid { fill:none; }
        .hex-spoke { stroke:var(--hex-grid); stroke-width:1; }
        .hex-val { fill:var(--accent); fill-opacity:.26; stroke:var(--accent); stroke-width:1.6; stroke-linejoin:round; }
        .hex-dot { fill:var(--accent); }
        .hex-lbl { fill:var(--muted); font-size:8.5px; font-weight:600; text-anchor:middle; dominant-baseline:central; }
        .hex-lbl.on { fill:var(--text); }
        .hex-num { fill:var(--muted); font-weight:700; }
        .hex-lbl.plus, .hex-lbl.plus .hex-num { fill:var(--plus); }
        .hex-lbl.minus, .hex-lbl.minus .hex-num { fill:var(--minus); }
        .moves { flex:1 1 auto; min-width:0; display:flex; flex-direction:column; gap:5px; }
        .move { display:flex; align-items:center; gap:8px; padding:5px 10px; min-height:28px; border-radius:8px;
          border:1px solid var(--line); border-left:4px solid var(--tcol,#8a929c);
          background:linear-gradient(90deg, color-mix(in srgb, var(--tcol,#8a929c) 32%, var(--card)) 0%, var(--card) 76%); }
        .move-type { height:14px; width:auto; flex:0 0 auto; image-rendering:auto; }
        .move-name { flex:1 1 auto; font-size:13px; font-weight:600; }
        .move-cat { height:15px; width:auto; flex:0 0 auto; }
        .raw-wrap { margin:14px 0 0; }
        .raw-wrap summary { cursor:pointer; color:var(--muted); font-size:13px; }
        .raw { overflow-x:auto; background:var(--card); border:1px solid var(--line); border-radius:9px; padding:12px; margin:8px 0 0; font:12.5px/1.4 ui-monospace,SFMono-Regular,Menlo,Consolas,monospace; }
        .tc-normal{--tcol:#9099a1}.tc-fire{--tcol:#ff9d55}.tc-water{--tcol:#4d90d5}.tc-electric{--tcol:#e0c020}
        .tc-grass{--tcol:#5bbf5a}.tc-ice{--tcol:#4bbbb0}.tc-fighting{--tcol:#ce4069}.tc-poison{--tcol:#ab6ac8}
        .tc-ground{--tcol:#d97845}.tc-flying{--tcol:#8aa5dd}.tc-psychic{--tcol:#fa7179}.tc-bug{--tcol:#8fb022}
        .tc-rock{--tcol:#b7a763}.tc-ghost{--tcol:#5269ac}.tc-dragon{--tcol:#0b6dc3}.tc-dark{--tcol:#595366}
        .tc-steel{--tcol:#5a8ea1}.tc-fairy{--tcol:#e07fd6}.tc-stellar{--tcol:#6a7bce}
        """;
}
