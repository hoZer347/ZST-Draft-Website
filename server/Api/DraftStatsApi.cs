using System.Security.Claims;
using System.Text.Json;
using DraftLeague.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DraftLeague.Web.Api;

/// <summary>
/// Analytics over a league's draft: which mons were snapped up the instant they
/// were offered, which were passed over the most, and how Tera types fared among
/// chosen picks vs rejected options. Everything is derived from the picks + skips
/// and their OtherOptions snapshots, the options offered but not taken each turn,
/// so nothing here is stored separately; it's a read-only view over the draft.
/// </summary>
public static class DraftStatsApi
{
    // The shape stored in Pick.OtherOptions / DraftSkip.OtherOptions (camelCase JSON).
    private sealed record Option(string? Name, string? Sprite, int DexNumber, string? Tier, string? TeraType);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static void MapDraftStatsApi(this WebApplication app, string? corsPolicy = null)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        if (corsPolicy is not null) api.RequireCors(corsPolicy);

        api.MapGet("/leagues/{leagueId:int}/draft-stats", async (int leagueId, ClaimsPrincipal me, AppDbContext db, CancellationToken ct) =>
        {
            var draftId = await db.Drafts
                .Where(d => d.LeagueId == leagueId).Select(d => (int?)d.Id).FirstOrDefaultAsync(ct);
            if (draftId is null) return Results.NotFound();

            var myId = me.DiscordId(); // to flag the caller's own drafted mons

            var picks = await db.Picks
                .Where(p => p.DraftId == draftId)
                .Select(p => new
                {
                    p.PickNumber,
                    p.PokemonEntry.Name,
                    p.PokemonEntry.Sprite,
                    p.PokemonEntry.DexNumber,
                    Tier = p.Tier.ToString(),
                    p.TeraType,
                    p.OtherOptions,
                    Trainer = p.Team.CoachName,
                    p.Team.CoachId,
                })
                .ToListAsync(ct);
            var skips = await db.DraftSkips
                .Where(s => s.DraftId == draftId && s.OtherOptions != null)
                .Select(s => new { s.OtherOptions, s.AfterPickNumber })
                .ToListAsync(ct);

            var cmp = StringComparer.OrdinalIgnoreCase;

            // Every drafted mon (name -> its sprite, tier, dex, who drafted it, the
            // pick number it went at, and that coach's id for the "mine" flag).
            var drafted = new Dictionary<string, (string? Sprite, string Tier, int Dex, string? Trainer, int PickNo, string CoachId)>(cmp);
            foreach (var p in picks)
                if (!string.IsNullOrEmpty(p.Name)) drafted[p.Name] = (p.Sprite, p.Tier, p.DexNumber, p.Trainer, p.PickNumber, p.CoachId);

            bool Mine(string coachId) => myId is not null && coachId == myId;

            // Rejections: every mon that appeared in a passed run (a pick's or a skip's
            // offered-but-not-taken options), plus the Tera types rejected alongside.
            // lastRejection tracks the draft position of each mon's most recent
            // rejection (the pick number the offering pick/skip happened at).
            var rejections = new Dictionary<string, int>(cmp);
            var rejMeta = new Dictionary<string, (string? Sprite, string? Tier, int Dex)>(cmp);
            var lastRejection = new Dictionary<string, int>(cmp);
            var teraRejected = new Dictionary<string, int>(cmp);

            void Scan(string? optionsJson, int position)
            {
                if (string.IsNullOrEmpty(optionsJson)) return;
                List<Option>? opts;
                try { opts = JsonSerializer.Deserialize<List<Option>>(optionsJson, Json); }
                catch { return; }
                if (opts is null) return;
                foreach (var o in opts)
                {
                    if (!string.IsNullOrEmpty(o.Name))
                    {
                        rejections[o.Name] = rejections.GetValueOrDefault(o.Name) + 1;
                        if (!rejMeta.ContainsKey(o.Name)) rejMeta[o.Name] = (o.Sprite, o.Tier, o.DexNumber);
                        lastRejection[o.Name] = lastRejection.TryGetValue(o.Name, out var last)
                            ? Math.Max(last, position) : position;
                    }
                    if (!string.IsNullOrEmpty(o.TeraType))
                        teraRejected[o.TeraType] = teraRejected.GetValueOrDefault(o.TeraType) + 1;
                }
            }
            // A rejection's "position" is how many picks had been made when it was
            // offered: the offering pick's own number, or a skip's AfterPickNumber.
            foreach (var p in picks) Scan(p.OtherOptions, p.PickNumber);
            foreach (var s in skips) Scan(s.OtherOptions, s.AfterPickNumber);

            (string? Sprite, string? Tier, int Dex) MetaOf(string name) =>
                drafted.TryGetValue(name, out var d) ? (d.Sprite, d.Tier, d.Dex)
                : rejMeta.TryGetValue(name, out var r) ? r
                : (null, null, 0);

            // Tera types among CHOSEN picks.
            var teraPicked = picks
                .Where(p => !string.IsNullOrEmpty(p.TeraType))
                .GroupBy(p => p.TeraType!, cmp)
                .ToDictionary(g => g.Key, g => g.Count(), cmp);

            // Instant picks: drafted, yet never once passed over. Ordered purely by
            // how early they went in the draft (earliest pick first).
            var instantPicks = drafted
                .Where(d => rejections.GetValueOrDefault(d.Key) == 0)
                .OrderBy(d => d.Value.PickNo).ThenBy(d => d.Key, cmp)
                .Select(d => new { name = d.Key, sprite = d.Value.Sprite, dexNumber = d.Value.Dex, tier = d.Value.Tier, trainer = d.Value.Trainer, mine = Mine(d.Value.CoachId) })
                .ToList();

            // Most rejected across everything (drafted or not). Ties break on how early
            // each mon's LAST rejection happened (earliest last-rejection first).
            // Undrafted mons have a null trainer, which the client renders "Undrafted".
            var mostRejected = rejections
                .OrderByDescending(r => r.Value).ThenBy(r => lastRejection.GetValueOrDefault(r.Key)).ThenBy(r => r.Key, cmp)
                .Take(15)
                .Select(r =>
                {
                    var m = MetaOf(r.Key);
                    var isDrafted = drafted.TryGetValue(r.Key, out var dd);
                    return new { name = r.Key, sprite = m.Sprite, dexNumber = m.Dex, tier = m.Tier, trainer = isDrafted ? dd.Trainer : null, rejections = r.Value, drafted = isDrafted, mine = isDrafted && Mine(dd.CoachId) };
                })
                .ToList();

            // Put Me In Coach!!!: the most-passed-over mons that still got drafted.
            // Ties break on how LATE they were finally taken (latest pick first).
            var putMeInCoach = drafted
                .Select(d => new { Name = d.Key, d.Value.Sprite, d.Value.Tier, d.Value.Dex, d.Value.Trainer, d.Value.PickNo, d.Value.CoachId, Rej = rejections.GetValueOrDefault(d.Key) })
                .Where(x => x.Rej > 0)
                .OrderByDescending(x => x.Rej).ThenByDescending(x => x.PickNo).ThenBy(x => x.Name, cmp)
                .Take(15)
                .Select(x => new { name = x.Name, sprite = x.Sprite, dexNumber = x.Dex, tier = x.Tier, trainer = x.Trainer, rejections = x.Rej, mine = Mine(x.CoachId) })
                .ToList();

            // Undraftables: pool mons that never appeared in a single offer, not
            // drafted, and never once shown as an option to anyone. Ordered by tier
            // (S→A→B→C) then name. The mons that decided they weren't gonna show up.
            var appeared = new HashSet<string>(drafted.Keys, cmp);
            appeared.UnionWith(rejections.Keys);
            var undraftables = (await db.Pokemon
                    .Where(p => p.LeagueId == leagueId)
                    .OrderBy(p => p.Tier).ThenBy(p => p.Name)
                    .Select(p => new { p.Name, p.Sprite, p.DexNumber, Tier = p.Tier.ToString() })
                    .ToListAsync(ct))
                .Where(p => !string.IsNullOrEmpty(p.Name) && !appeared.Contains(p.Name))
                .Select(p => new { name = p.Name, sprite = p.Sprite, dexNumber = p.DexNumber, tier = p.Tier, trainer = (string?)null })
                .ToList();

            var teraMostPicked = teraPicked
                .OrderByDescending(t => t.Value).ThenBy(t => t.Key, cmp)
                .Select(t => new { type = t.Key, count = t.Value }).ToList();
            var teraMostRejected = teraRejected
                .OrderByDescending(t => t.Value).ThenBy(t => t.Key, cmp)
                .Select(t => new { type = t.Key, count = t.Value }).ToList();

            // Pick rate: of every time a type was offered (picked or rejected), the
            // share that was picked.
            var teraPickRate = teraPicked.Keys.Union(teraRejected.Keys, cmp)
                .Select(type =>
                {
                    var picked = teraPicked.GetValueOrDefault(type);
                    var rejected = teraRejected.GetValueOrDefault(type);
                    var total = picked + rejected;
                    return new { type, picked, rejected, rate = total == 0 ? 0.0 : (double)picked / total };
                })
                .OrderByDescending(t => t.rate).ThenByDescending(t => t.picked + t.rejected).ThenBy(t => t.type, cmp)
                .ToList();

            return Results.Ok(new
            {
                leagueId,
                totalPicks = picks.Count,
                pokemon = new { instantPicks, mostRejected, putMeInCoach, undraftables },
                tera = new { mostPicked = teraMostPicked, mostRejected = teraMostRejected, pickRate = teraPickRate },
            });
        });
    }
}
