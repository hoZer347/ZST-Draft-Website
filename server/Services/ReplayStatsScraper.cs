using DraftLeague.Web.Models;

namespace DraftLeague.Web.Services;

/// <summary>
/// Parses one Showdown battle log into per-mon stats for that game: kills,
/// deaths, damage dealt/taken (cumulative % of a full HP bar), self-heal,
/// ally-heal, and crits. Everything is attributed by battle slot (p1a…p2b) and
/// resolved to a drafted <see cref="Pick"/> via the supplied resolver.
///
/// Indirect damage counts too, credited to its source: entry hazards (Stealth
/// Rock, Spikes, Toxic Spikes, G-Max Steelsurge) to whoever set them, status
/// chip (poison/burn) to whoever inflicted it, damaging weather (Sandstorm/Hail)
/// to whoever set it, a Perish Song counter to whoever cast it, and contact/bind
/// effects (Rocky Helmet, Rough Skin, Leech Seed) to the [of] mon, and a faint
/// from any of these is a kill for that source, even after it has left the field.
///
/// HP in the log is a fraction, own side shows real HP (200/281), the opponent
/// shows a percent (88/100), so working in "% of max" keeps both consistent.
///
/// Known limitation: Illusion (Hisuian Zoroark) disguises the active mon, so its
/// actions are misattributed to the disguise until the |replace| reveals it.
/// </summary>
public static class ReplayStatsScraper
{
    public sealed class GameStat
    {
        public int Kills, Deaths, Crits, ActiveTurns;
        // Led the battle: was one of the initial active mons (switched in before
        // turn 1). Set on the resolved Pick, so a mon that mega-evolves later in
        // the game keeps its starter flag; the mega occupies the same slot and
        // resolves to the same Pick, so it is never re-counted as a fresh switch-in.
        public bool Started;
        // Terastallized at some point this game. Lets the UI grey a mon's Tera type
        // when it was never actually used.
        public bool Terastallized;
        // Finished the battle: was still on the field and NOT fainted when the game
        // ended (|win| / |tie|). The winning side's standing mons; a mon KO'd or
        // benched at the end doesn't count.
        public bool Finished;
        // Damage is split by source (direct = a mon's own move landing; indirect =
        // everything else it caused/suffered, hazards, status chip, weather, Rocky
        // Helmet, Leech Seed, recoil, Life Orb) and, for damage dealt / healing
        // given, by target side. Dealt*/Healed count opponents / allies (the useful
        // default); the Ally/Enemy buckets hold friendly-fire damage and enemy
        // healing separately, so the UI can fold them in only when asked.
        public double DealtDirect, DealtIndirect, TakenDirect, TakenIndirect, Recovered, Healed;
        public double DealtAllyDirect, DealtAllyIndirect, HealedEnemy;
        // Self-inflicted damage (recoil, Life Orb, own Toxic/Flame Orb, confusion,
        // crash, HP-cost moves) is kept out of Taken, it's a mon hurting itself.
        public double TakenSelf;
        public double Dealt => DealtDirect + DealtIndirect;
        public double Taken => TakenDirect + TakenIndirect;
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

        // The mon currently occupying a slot, or null. Used to resolve a source
        // slot to the Pick to credit.
        Pick? PickAt(string? id) => id is not null && slots.TryGetValue(id, out var s) ? s.Pick : null;

        // Swap two slot keys in a per-slot map (for Ally Switch), preserving the
        // "missing" state so a slot that had no entry doesn't gain a stale one.
        static void SwapKeys<T>(Dictionary<string, T> d, string a, string b)
        {
            var hasA = d.TryGetValue(a, out var va);
            var hasB = d.TryGetValue(b, out var vb);
            if (hasB) d[a] = vb!; else d.Remove(a);
            if (hasA) d[b] = va!; else d.Remove(b);
        }

        string? attacker = null;                            // slot of the move currently resolving
        var lastKiller = new Dictionary<string, Pick>();    // victim slot → who gets the KO if it faints
        Pick? bondKiller = null;                            // a Destiny Bond just fired → credit the NEXT faint (its killer, dragged down) to this mon
        var bindOwner = new Dictionary<string, Pick>();     // partially-trapped slot → who trapped it (their bind chip carries no [of])
        var statusSource = new Dictionary<string, Pick>();  // afflicted slot → who inflicted the status
        var statusSelf = new HashSet<string>();             // slots whose status is self-inflicted (Toxic/Flame Orb)
        var hazardOwner = new Dictionary<string, Pick>();   // "side:hazardid" → who set the hazard
        (Pick pick, string side)? grassyTerrain = null;     // who set Grassy Terrain, and their side
        (Pick pick, string side)? weather = null;           // who set the current damaging weather, and their side
        var perishOwner = new Dictionary<string, (Pick pick, string side)>(); // slot with a perish counter → who cast it (and their side)
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
                    if (slot.Pick is not null)
                    {
                        Stat(slot.Pick); // switching in counts as "brought" (GP)
                        // The leads switch in before turn 1; mark them as starters.
                        // (A forced switch (drag) or an Illusion reveal (replace)
                        // only happens mid-game, so turns is already > 0 there.)
                        if (cmd == "switch" && turns == 0) Stat(slot.Pick).Started = true;
                    }
                    lastKiller.Remove(sid);
                    statusSource.Remove(sid);
                    statusSelf.Remove(sid);
                    bindOwner.Remove(sid); // the trap breaks when the mon leaves
                    perishOwner.Remove(sid); // its perish counter clears on switch-out
                    break;
                }

                case "swap":
                {
                    // Ally Switch trades the two active mons on a side (…a ↔ …b).
                    // Every per-slot fact must follow the mon to its new slot, or
                    // damage/heal after the switch is credited to the wrong Pick.
                    if (parts.Length < 3) break;
                    var side = SideOf(SlotId(parts[2]));
                    string slotA = side + "a", slotB = side + "b";
                    SwapKeys(slots, slotA, slotB);
                    SwapKeys(lastKiller, slotA, slotB);
                    SwapKeys(bindOwner, slotA, slotB);
                    SwapKeys(perishOwner, slotA, slotB);
                    SwapKeys(statusSource, slotA, slotB);
                    bool selfA = statusSelf.Contains(slotA), selfB = statusSelf.Contains(slotB);
                    if (selfB) statusSelf.Add(slotA); else statusSelf.Remove(slotA);
                    if (selfA) statusSelf.Add(slotB); else statusSelf.Remove(slotB);
                    break;
                }

                case "poke":
                    // Team preview lists all six brought mons, count each as
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

                case "-terastallize":
                    // |-terastallize|p1a: Name|Type: the mon in that slot used its Tera.
                    if (parts.Length > 2 && PickAt(SlotId(parts[2])) is { } tera)
                        Stat(tera).Terastallized = true;
                    break;

                case "-status":
                {
                    // Classify where a status came from, so its residual chip (and any
                    // KO it leads to) is credited to the opponent that inflicted it, or
                    // marked self-inflicted when it's the mon's own Toxic/Flame Orb.
                    if (parts.Length < 3) break;
                    var victimSlot = SlotId(parts[2]);
                    var (skind, sname, sof) = Tags(parts);
                    statusSource.Remove(victimSlot);
                    statusSelf.Remove(victimSlot);
                    if (sname is null && attacker is not null)
                    {
                        // Bare -status → the mon that just moved inflicted it.
                        var inflictor = PickAt(attacker);
                        if (inflictor is not null) statusSource[victimSlot] = inflictor;
                    }
                    else if (sname is not null && ToId(sname) == "toxicspikes")
                    {
                        // Poisoned on switch-in by Toxic Spikes, credit its setter.
                        var owner = hazardOwner.GetValueOrDefault(HazardKey(SideOf(victimSlot), "Toxic Spikes"));
                        if (owner is not null) statusSource[victimSlot] = owner;
                    }
                    else if (sof is not null)
                    {
                        // An opponent's contact ability (Flame Body, Poison Point,
                        // Effect Spore), the [of] mon inflicted it.
                        var inflictor = PickAt(sof);
                        if (inflictor is not null) statusSource[victimSlot] = inflictor;
                    }
                    else if (skind is "item" or "ability")
                    {
                        // Own Toxic Orb / Flame Orb (no [of]), self-inflicted, so its
                        // chip is the mon's own doing.
                        statusSelf.Add(victimSlot);
                    }
                    break;
                }

                case "-sidestart":
                {
                    // A hazard just went down. The setter is whoever just moved;
                    // remember them so the chip it does on switch-ins is credited.
                    if (parts.Length < 4 || attacker is null) break;
                    var owner = PickAt(attacker);
                    if (owner is not null) hazardOwner[HazardKey(SideOf(parts[2]), CondName(parts[3]))] = owner;
                    break;
                }

                case "-sideend":
                    // Hazard cleared (Rapid Spin, Defog…), drop its owner so a
                    // re-set later can't inherit stale credit.
                    if (parts.Length >= 4) hazardOwner.Remove(HazardKey(SideOf(parts[2]), CondName(parts[3])));
                    break;

                case "-fieldstart":
                {
                    // Grassy Terrain, set by the move (attacker) or Grassy Surge on
                    // switch-in (the [of] mon). Remember the setter and their side so
                    // the HP it heals their allies with is credited to them.
                    if (parts.Length < 3 || ToId(CondName(parts[2])) != "grassyterrain") break;
                    var setter = Tags(parts).ofSlot ?? attacker;
                    var owner = PickAt(setter);
                    if (owner is not null && setter is not null) grassyTerrain = (owner, SideOf(setter));
                    break;
                }

                case "-fieldend":
                    if (parts.Length >= 3 && ToId(CondName(parts[2])) == "grassyterrain") grassyTerrain = null;
                    break;

                case "-weather":
                {
                    // Sandstorm/Hail chip each turn is credited to whoever set the
                    // weather. A fresh set names its setter: a move (|-weather|Sandstorm|,
                    // setter = the mon that just moved) or an ability (|-weather|Sandstorm|
                    // [from] ability: Sand Stream|[of] p1a: Tyranitar). |[upkeep]| just
                    // ticks the existing weather (keep the owner); |none| clears it.
                    if (parts.Length < 3) break;
                    if (ToId(parts[2]) == "none") { weather = null; break; }
                    var upkeep = false;
                    for (var i = 3; i < parts.Length; i++)
                        if (parts[i].Trim() == "[upkeep]") { upkeep = true; break; }
                    if (upkeep) break;
                    var setter = Tags(parts).ofSlot ?? attacker;
                    var wOwner = PickAt(setter);
                    weather = wOwner is not null && setter is not null ? (wOwner, SideOf(setter)) : null;
                    break;
                }

                case "-start":
                {
                    // Perish Song / Perish Body counter. The initial |-start|slot|perish3|
                    // names no source, but it is emitted right after the Perish Song move
                    // (or Perish Body contact) with the mover still set as `attacker`, so
                    // the current mover owns it. When the counter reaches |perish0| the mon
                    // faints this turn with no -damage of its own, so arm the KO here
                    // (cross-side only: a Perish Song that fells the caster's own ally, or
                    // the caster itself, is friendly fire / self, not a kill).
                    if (parts.Length < 4) break;
                    var pslot = SlotId(parts[2]);
                    var pname = ToId(parts[3]);
                    if (pname == "perish3")
                    {
                        var pOwner = PickAt(attacker);
                        if (pOwner is not null && attacker is not null) perishOwner[pslot] = (pOwner, SideOf(attacker));
                    }
                    else if (pname == "perish0" && perishOwner.TryGetValue(pslot, out var po)
                             && po.side != SideOf(pslot) && !ReferenceEquals(po.pick, PickAt(pslot)))
                    {
                        lastKiller[pslot] = po.pick;
                    }
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
                    // Classify the source. Direct = a move landing (no [from]). Self =
                    // the mon hurting itself (recoil, Life Orb, own Toxic/Flame Orb,
                    // confusion, or a move's HP cost / crash that lands on the user).
                    // Otherwise indirect, credited to: an entry hazard's setter (even
                    // after they've left); the status inflictor; or the [of] mon of a
                    // contact/bind effect (Rocky Helmet, Rough Skin, Leech Seed).
                    // dealerSlot is kept only where the source is on the field, to
                    // tell ally from enemy.
                    var direct = from is null;
                    Pick? dealer; string? dealerSlot; bool self;
                    if (direct)
                    {
                        dealer = PickAt(attacker); dealerSlot = attacker;
                        self = ReferenceEquals(dealer, slot.Pick); // a move's HP cost / crash on the user
                    }
                    else if (IsSelfSource(from!)) { dealer = null; dealerSlot = null; self = true; }
                    else if (IsHazard(from!)) { dealer = hazardOwner.GetValueOrDefault(HazardKey(SideOf(sid), from!)); dealerSlot = null; self = false; }
                    else if (from is "psn" or "brn")
                    {
                        if (statusSource.TryGetValue(sid, out var inflictor)) { dealer = inflictor; dealerSlot = null; self = false; }
                        else { dealer = null; dealerSlot = null; self = statusSelf.Contains(sid); } // own Toxic/Flame Orb
                    }
                    else if (IsWeather(from!))
                    {
                        // Weather chips every non-immune mon on the field, so unlike a
                        // hazard it can hit the setter's own ally: tag the source with the
                        // setter's side (a sentinel slot) so friendly weather damage lands
                        // in the ally bucket and never scores a kill.
                        dealer = weather?.pick;
                        dealerSlot = weather is { } w ? w.side + "a" : null;
                        self = false;
                    }
                    else if (IsResidual(from!)) { dealer = statusSource.GetValueOrDefault(sid); dealerSlot = null; self = false; }
                    else if (IsBindMove(ToId(from!))) { dealer = bindOwner.GetValueOrDefault(sid); dealerSlot = null; self = false; }
                    else { dealer = PickAt(ofSlot); dealerSlot = ofSlot; self = false; }

                    if (slot.Pick is not null)
                    {
                        var v = Stat(slot.Pick);
                        if (self) v.TakenSelf += delta;
                        else if (direct) v.TakenDirect += delta;
                        else v.TakenIndirect += delta;
                    }
                    if (!self && dealer is not null && !ReferenceEquals(dealer, slot.Pick))
                    {
                        // Friendly fire (a spread move hitting your own ally) is kept
                        // apart so it never inflates damage-to-opponents, and never
                        // scores a kill.
                        var ally = dealerSlot is not null && SameSide(dealerSlot, sid);
                        var d = Stat(dealer);
                        if (ally)
                        {
                            if (direct) d.DealtAllyDirect += delta; else d.DealtAllyIndirect += delta;
                        }
                        else
                        {
                            if (direct) d.DealtDirect += delta; else d.DealtIndirect += delta;
                            lastKiller[sid] = dealer; // this source gets the KO if the mon faints
                        }
                    }
                    break;
                }

                case "-activate":
                {
                    if (parts.Length < 4) break;
                    var actSlot = SlotId(parts[2]);
                    var effect = parts[3];
                    // Destiny Bond triggering: the mon that just KO'd this slot's
                    // occupant is taken down with it, and that killer faints with NO
                    // -damage of its own (so lastKiller can't credit it). Showdown emits
                    // the Destiny Bond user's own faint, then this activate, then the
                    // killer's faint — so remember the DB user and credit the very next
                    // faint to it.
                    if (ToId(effect).Contains("destinybond")) { bondKiller = PickAt(actSlot); break; }
                    // Partial-trap set/refresh: |-activate|victim|move: Whirlpool|[of]
                    // binder. The per-turn bind chip that follows carries no [of], so
                    // remember the trapper here to credit its damage (and any KO).
                    if (effect.StartsWith("move:") && IsBindMove(ToId(effect["move:".Length..]))
                        && Tags(parts).ofSlot is { } of && PickAt(of) is { } binder)
                        bindOwner[actSlot] = binder;
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
                    var silent = false;
                    for (var i = 4; i < parts.Length; i++)
                        if (parts[i].Trim() == "[silent]") { silent = true; break; }

                    // Who to credit. Grassy Terrain (from the move or Grassy Surge)
                    // heals every grounded mon each turn: the HP it restores to the
                    // setter's teammates is ally-healing credited to the setter, while
                    // its own tick and the opponents' are self-recovery. Otherwise:
                    // Showdown emits a BARE |-heal| (no [from]/[of]) for a move's
                    // healing, including the ally half of Life Dew / Lunar Blessing /
                    // Pollen Puff / Heal Pulse, so a tag-less heal on someone other
                    // than the mon that just moved is that mover healing an ally; a
                    // tag-less heal on the mover itself (Recover, Roost) is self.
                    // Anything with a [from] is a self source (Leftovers, Regenerator,
                    // Aqua Ring, Wish…) except drain, whose [of] is the victim not a
                    // healer. [silent] heals (Leech Seed, Rest) fire at end of turn
                    // with a stale attacker, so are never credited to it.
                    // An ABSORB ability (Water/Volt Absorb, Earth Eater, Dry Skin)
                    // heals its own holder when struck by the matching move type: its
                    // [of] is the ATTACKER whose move triggered it, NOT a healer, so
                    // like drain the HP is self-recovery. (Only these; an ally-healing
                    // ability such as Hospitality instead puts the healer in [of], so
                    // it must fall through to the [of] branch, not self.)
                    // Resolve the healer and their side. Healing someone other than
                    // the healer is credited as ally-healing (same side) or enemy-
                    // healing (Heal Pulse on a foe, your Grassy Terrain topping them
                    // up); a healerSide of null means it resolved to self-recovery.
                    Pick? healerPick; string? healerSide;
                    if (name is not null && ToId(name) == "grassyterrain" && grassyTerrain is not null && !ReferenceEquals(grassyTerrain.Value.pick, slot.Pick))
                    {
                        healerPick = grassyTerrain.Value.pick; healerSide = grassyTerrain.Value.side;
                    }
                    else if (silent || name == "drain" || (kind == "ability" && IsAbsorbHealAbility(name)))
                    {
                        healerPick = slot.Pick; healerSide = null; // drain / absorb-ability heal / Leech Seed / Rest → self
                    }
                    else if (ofSlot is not null)
                    {
                        healerPick = PickAt(ofSlot); healerSide = ofSlot is null ? null : SideOf(ofSlot);
                    }
                    else if (name is null && attacker is not null && attacker != sid)
                    {
                        healerPick = PickAt(attacker); healerSide = SideOf(attacker); // a move healing someone else
                    }
                    else
                    {
                        healerPick = slot.Pick; healerSide = null; // Recover, Leftovers, Regenerator, Wish… → self
                    }

                    if (healerPick is not null && healerSide is not null && !ReferenceEquals(healerPick, slot.Pick))
                    {
                        var s = Stat(healerPick);
                        if (healerSide == SideOf(sid)) s.Healed += heal; else s.HealedEnemy += heal;
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
                    // A Destiny Bond drag-down takes precedence: that killer faints with
                    // no -damage of its own, so lastKiller can't credit it. bondKiller was
                    // set on the DB user's -activate (which comes AFTER the DB user's own
                    // faint), so it applies to exactly this next faint. Consume it here.
                    var killer = bondKiller;
                    bondKiller = null;
                    if (killer is null) lastKiller.TryGetValue(sid, out killer);
                    if (killer is not null && slot.Pick is not null && !ReferenceEquals(killer, slot.Pick))
                        Stat(killer).Kills++;
                    break;
                }

                case "win":
                case "tie":
                    // The game is over: everyone still on the field and not fainted
                    // (Hp > 0, a faint sets it to 0) "finished" the battle.
                    foreach (var sid in ActiveSlots)
                        if (slots.TryGetValue(sid, out var occ) && occ.Pick is not null && occ.Hp > 0)
                            Stat(occ.Pick).Finished = true;
                    break;
            }
        }
        return new Result(stats, turns);
    }

    /// <summary>
    /// The HP-restoring "absorb" abilities: they heal their HOLDER when hit by a move
    /// of a matching type (Water Absorb / Dry Skin, Water; Volt Absorb, Electric;
    /// Earth Eater, Ground). Their -heal carries [of] the attacker whose move
    /// triggered it, NOT a healer, so the HP is self-recovery. Ability heals that
    /// really do heal another mon (e.g. Hospitality) are deliberately NOT here.
    /// </summary>
    private static bool IsAbsorbHealAbility(string? name) => name is not null && ToId(name) is
        "waterabsorb" or "voltabsorb" or "eartheater" or "dryskin";

    /// <summary>Showdown-style id: lowercase, alphanumerics only.</summary>
    public static string ToId(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        var n = 0;
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) buf[n++] = char.ToLowerInvariant(c);
        return new string(buf[..n]);
    }

    /// <summary>
    /// Species id with the mega suffix stripped, so a mon drafted as "M-Salamence"
    /// (sprite "salamence-mega") and the replay's base "Salamence" both key to the
    /// same pick. Regional/other forms are kept, they're distinct draft picks.
    /// </summary>
    public static string BaseId(string species)
    {
        var id = ToId(species);
        if (id.EndsWith("megax") || id.EndsWith("megay")) return id[..^5];
        if (id.EndsWith("mega")) return id[..^4];
        return id;
    }

    /// <summary>True if two slot ids share a side (p1a/p1b, or p2a/p2b).</summary>
    private static bool SameSide(string a, string b) =>
        a.Length >= 2 && b.Length >= 2 && a[0] == b[0] && a[1] == b[1];

    /// <summary>"p1a: Nickname" → "p1a" (also handles a bare "p1a").</summary>
    private static string SlotId(string s)
    {
        var colon = s.IndexOf(':');
        return (colon >= 0 ? s[..colon] : s).Trim();
    }

    /// <summary>An entry hazard that chips a mon on switch-in, credited to its setter.</summary>
    private static bool IsHazard(string from) => from is
        "Stealth Rock" or "Spikes" or "G-Max Steelsurge";

    /// <summary>
    /// Partial-trap moves whose per-turn chip is credited to the trapper. Their
    /// -damage carries no [of] (only "[partiallytrapped]"), so the trapper is picked
    /// up from the "-activate ... [of]" when the trap is set (see bindOwner).
    /// </summary>
    private static bool IsBindMove(string id) => id is
        "whirlpool" or "firespin" or "sandtomb" or "infestation" or "magmastorm"
        or "bind" or "wrap" or "clamp" or "thundercage" or "snaptrap" or "gmaxcentiferno" or "gmaxsandcastle";

    /// <summary>
    /// Damage a mon inflicts on itself. Recoil (Showdown tags it "Recoil" OR "recoil",
    /// hence the case-insensitive compare); Life Orb / Sticky Barb / Black Sludge are
    /// the holder's own item; confusion is a self-hit; Steel Beam / Mind Blown /
    /// Chloroblast are the user's own HP-cost. Own-Orb poison/burn is caught separately
    /// via statusSelf, and a move's plain HP cost / crash via the direct-damage-on-the-
    /// user check, both also self.
    /// </summary>
    private static bool IsSelfSource(string from) => ToId(from) is
        "recoil" or "lifeorb" or "stickybarb" or "confusion" or "blacksludge"
        or "steelbeam" or "mindblown" or "chloroblast";

    /// <summary>
    /// Damaging weather, credited to whoever set it (tracked in `weather`): Sandstorm
    /// and Hail chip every non-immune mon each turn. Snow (gen 9) does no damage, so
    /// it never appears as a [from] on -damage and isn't listed.
    /// </summary>
    private static bool IsWeather(string from) => from is "Sandstorm" or "Hail";

    /// <summary>
    /// Tag-less chip we currently leave uncredited to a dealer (still counted as the
    /// victim's indirect damage taken): Curse/Nightmare/Salt Cure and partial-trap
    /// "trapped". Poison/burn are handled before this (their inflictor is remembered,
    /// or they're self via statusSelf); weather is credited via IsWeather. NOT listed
    /// here, so they fall through to the [of] branch, are effects that name their
    /// source: Rocky Helmet, Rough Skin/Iron Barbs, Aftermath, Bad Dreams, Liquid
    /// Ooze, Leech Seed.
    /// </summary>
    private static bool IsResidual(string from) => from is
        "Curse" or "Nightmare" or "Salt Cure" or "trapped";

    /// <summary>"p1a: Nickname" | "p1: Alice" | "p1a" → the side "p1"/"p2".</summary>
    private static string SideOf(string s) { var id = SlotId(s); return id.Length >= 2 ? id[..2] : id; }

    /// <summary>"move: Stealth Rock" | "Spikes" → the bare effect name.</summary>
    private static string CondName(string cond)
    {
        cond = cond.Trim();
        var colon = cond.IndexOf(':');
        return colon >= 0 ? cond[(colon + 1)..].Trim() : cond;
    }

    /// <summary>Key for the per-side hazard owner map, e.g. ("p1", "Stealth Rock") → "p1:stealthrock".</summary>
    private static string HazardKey(string side, string name) => side + ":" + ToId(name);

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
