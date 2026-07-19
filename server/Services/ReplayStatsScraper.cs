using DraftLeague.Web.Models;

namespace DraftLeague.Web.Services;

/// <summary>
/// Parses one Showdown battle log into per-mon stats for that game: kills,
/// deaths, damage dealt/taken (cumulative % of a full HP bar), self-heal,
/// ally-heal, and crits. Everything is attributed by battle slot (p1a…p2b) and
/// resolved to a drafted <see cref="Pick"/> via the supplied resolver.
///
/// HP in the log is a fraction — own side shows real HP (200/281), the opponent
/// shows a percent (88/100) — so working in "% of max" keeps both consistent.
///
/// Known limitation: Illusion (Hisuian Zoroark) disguises the active mon, so its
/// actions are misattributed to the disguise until the |replace| reveals it.
/// </summary>
public static class ReplayStatsScraper
{
    public sealed class GameStat
    {
        public int Kills, Deaths, Crits, ActiveTurns;
        public double Dealt, Taken, Recovered, Healed;
    }

    /// <summary>Per-mon stats for one game, plus that game's total turn count.</summary>
    public sealed record Result(Dictionary<Pick, GameStat> Stats, int Turns);

    private sealed class SlotState { public Pick? Pick; public double Hp = 1; }

    // The four active battle slots in doubles (singles just never fill p1b/p2b).
    private static readonly string[] ActiveSlots = ["p1a", "p1b", "p2a", "p2b"];

    /// <param name="resolve">(side "p1"/"p2", raw species) → the drafted Pick, or null.</param>
    public static Result Scrape(string log, Func<string, string, Pick?> resolve)
    {
        var stats = new Dictionary<Pick, GameStat>();
        GameStat Stat(Pick p) => stats.TryGetValue(p, out var g) ? g : stats[p] = new GameStat();

        var slots = new Dictionary<string, SlotState>();
        SlotState Slot(string id) => slots.TryGetValue(id, out var s) ? s : slots[id] = new SlotState();

        string? attacker = null;                             // slot of the move currently resolving
        var lastDamager = new Dictionary<string, string>();  // victim slot → attacker slot (for KO credit)
        var statusSource = new Dictionary<string, string>(); // afflicted slot → who inflicted it
        var turns = 0;

        foreach (var rawLine in log.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line[0] != '|') continue;
            var parts = line.Split('|');
            var cmd = parts.Length > 1 ? parts[1] : "";

            switch (cmd)
            {
                case "switch":
                case "drag":
                case "replace":
                {
                    if (parts.Length < 4) break;
                    var sid = SlotId(parts[2]);
                    var slot = Slot(sid);
                    slot.Pick = resolve(sid[..2], parts[3].Split(',')[0].Trim());
                    slot.Hp = parts.Length > 4 ? ParseHp(parts[4]) : 1;
                    if (slot.Pick is not null) Stat(slot.Pick); // switching in counts as "brought" (GP)
                    lastDamager.Remove(sid);
                    statusSource.Remove(sid);
                    break;
                }

                case "poke":
                    // Team preview lists all six brought mons — count each as
                    // played (GP), even one that never comes off the bench.
                    if (parts.Length > 3)
                    {
                        var brought = resolve(parts[2], parts[3].Split(',')[0].Trim());
                        if (brought is not null) Stat(brought);
                    }
                    break;

                case "turn":
                    // One turn elapsed; whoever is on the field this turn is
                    // "present" for it. Two slots per side, so a mon that never
                    // leaves gets one presence-turn per game turn.
                    turns++;
                    foreach (var sid in ActiveSlots)
                        if (slots.TryGetValue(sid, out var occ) && occ.Pick is not null)
                            Stat(occ.Pick).ActiveTurns++;
                    break;

                case "move":
                    attacker = parts.Length > 2 ? SlotId(parts[2]) : null;
                    break;

                case "-crit":
                    if (attacker is not null && slots.TryGetValue(attacker, out var a) && a.Pick is not null)
                        Stat(a.Pick).Crits++;
                    break;

                case "-status":
                {
                    // Remember who inflicted a status, so its residual chip can be
                    // credited. Item/ability self-status (Toxic Orb, Flame Body on
                    // the holder) has a [from] tag — don't credit an attacker then.
                    if (parts.Length > 2 && attacker is not null && Tags(parts).name is null)
                        statusSource[SlotId(parts[2])] = attacker;
                    break;
                }

                case "-damage":
                {
                    if (parts.Length < 4) break;
                    var sid = SlotId(parts[2]);
                    var slot = Slot(sid);
                    var delta = (slot.Hp - ParseHp(parts[3])) * 100;
                    slot.Hp = ParseHp(parts[3]);
                    if (delta <= 0) break;

                    var (_, from, ofSlot) = Tags(parts);
                    string? dealer =
                        from is null ? attacker :                          // direct move damage
                        IsResidual(from) ? statusSource.GetValueOrDefault(sid) : // psn/brn/hazard chip
                        ofSlot;                                            // Rocky Helmet, Rough Skin, Leech Seed…

                    if (slot.Pick is not null) Stat(slot.Pick).Taken += delta;
                    if (dealer is not null && dealer != sid &&
                        slots.TryGetValue(dealer, out var d) && d.Pick is not null)
                    {
                        Stat(d.Pick).Dealt += delta;
                        lastDamager[sid] = dealer;
                    }
                    break;
                }

                case "-heal":
                {
                    if (parts.Length < 4) break;
                    var sid = SlotId(parts[2]);
                    var slot = Slot(sid);
                    var heal = (ParseHp(parts[3]) - slot.Hp) * 100;
                    slot.Hp = ParseHp(parts[3]);
                    if (heal <= 0) break;

                    var (kind, name, ofSlot) = Tags(parts);
                    // Who to credit: a drain move heals its user (this slot); an
                    // explicit [of] names the healer; a field/spread heal move
                    // (Life Dew, Lunar Blessing, Pollen Puff) has no [of], so the
                    // caster is whoever just moved. Everything else — Recover,
                    // Roost, Leftovers, Regenerator, item/ability — is self.
                    string? healer =
                        name == "drain" ? sid
                        : ofSlot is not null ? ofSlot
                        : kind == "move" ? attacker
                        : sid;

                    if (healer is not null && healer != sid)
                    {
                        if (slots.TryGetValue(healer, out var h) && h.Pick is not null) Stat(h.Pick).Healed += heal;
                    }
                    else if (slot.Pick is not null)
                    {
                        Stat(slot.Pick).Recovered += heal;
                    }
                    break;
                }

                case "-sethp":
                    if (parts.Length > 3) Slot(SlotId(parts[2])).Hp = ParseHp(parts[3]);
                    break;

                case "faint":
                {
                    if (parts.Length < 3) break;
                    var sid = SlotId(parts[2]);
                    var slot = Slot(sid);
                    slot.Hp = 0;
                    if (slot.Pick is not null) Stat(slot.Pick).Deaths++;
                    if (lastDamager.TryGetValue(sid, out var killer) &&
                        slots.TryGetValue(killer, out var k) && k.Pick is not null &&
                        !ReferenceEquals(k.Pick, slot.Pick))
                        Stat(k.Pick).Kills++;
                    break;
                }
            }
        }
        return new Result(stats, turns);
    }

    /// <summary>"p1a: Nickname" → "p1a" (also handles a bare "p1a").</summary>
    private static string SlotId(string s)
    {
        var colon = s.IndexOf(':');
        return (colon >= 0 ? s[..colon] : s).Trim();
    }

    /// <summary>Passive chip that isn't a mon's direct hit.</summary>
    private static bool IsResidual(string from) => from is
        "psn" or "brn" or "tox" or "confusion" or "Leech Seed" or "Curse" or "Nightmare"
        or "Stealth Rock" or "Spikes" or "G-Max Steelsurge" or "sandstorm" or "hail"
        or "Salt Cure" or "Bad Dreams" or "trapped" or "powder";

    /// <summary>
    /// Pull the [from] effect and [of] slot. `kind` is the prefix (item/ability/
    /// move) or null for bare effects; `name` is the effect itself (e.g. "brn",
    /// "Rocky Helmet", "Life Dew"), null when there's no [from] tag at all.
    /// </summary>
    private static (string? kind, string? name, string? ofSlot) Tags(string[] parts)
    {
        string? kind = null, name = null, ofSlot = null;
        for (var i = 4; i < parts.Length; i++)
        {
            var t = parts[i].Trim();
            if (t.StartsWith("[from]"))
            {
                var v = t["[from]".Length..].Trim();
                var colon = v.IndexOf(':');
                if (colon >= 0) { kind = v[..colon].Trim(); name = v[(colon + 1)..].Trim(); }
                else { kind = null; name = v; }
            }
            else if (t.StartsWith("[of]"))
            {
                ofSlot = SlotId(t["[of]".Length..].Trim());
            }
        }
        return (kind, name, ofSlot);
    }

    /// <summary>"200/281" | "88/100" | "0 fnt" | "100/100 tox" → fraction 0–1.</summary>
    private static double ParseHp(string s)
    {
        var token = s.Trim().Split(' ')[0];
        var slash = token.IndexOf('/');
        if (slash < 0) return 0; // "0 fnt"
        return double.TryParse(token[..slash], out var cur) &&
               double.TryParse(token[(slash + 1)..], out var max) && max > 0
            ? cur / max : 0;
    }
}
