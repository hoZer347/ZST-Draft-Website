namespace DraftLeague.Web.Tests;

/// <summary>
/// Exhaustive attribution edge cases for <see cref="DraftLeague.Web.Services.ReplayStatsScraper"/>,
/// each a hand-authored log whose every HP number is known. Grouped by concern:
/// self-inflicted damage, [of]-tagged hitback, hazards, healing, weather, KO
/// credit, HP parsing, and Grassy Terrain. Logs use a distinct species per side so
/// results can be looked up by species (see <see cref="ReplayLogRunner"/>).
/// </summary>
public class ScraperAttributionTests
{
    // ── Self-inflicted damage → TakenSelf, credited to no dealer ─────────────

    [Fact]
    public void Recoil_is_self_damage_and_the_move_still_deals_to_the_target()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Talonflame, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Talonflame|Talonflame, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Talonflame|Brave Bird|p2a: Snorlax
            |-damage|p2a: Snorlax|70/100
            |-damage|p1a: Talonflame|85/100|[from] recoil
            |win|A
            """);
        Assert.Equal(30, run.Of("Talonflame").DealtDirect, 2);
        Assert.Equal(15, run.Of("Talonflame").TakenSelf, 2);
        Assert.Equal(0, run.Of("Talonflame").TakenIndirect, 2);
        Assert.Equal(30, run.Of("Snorlax").TakenDirect, 2);
    }

    [Fact]
    public void Life_orb_is_self_damage()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garchomp, M|item
            |poke|p2|Blissey, F|item
            |start
            |switch|p1a: Garchomp|Garchomp, M|100/100
            |switch|p2a: Blissey|Blissey, F|100/100
            |turn|1
            |move|p1a: Garchomp|Earthquake|p2a: Blissey
            |-damage|p2a: Blissey|60/100
            |-damage|p1a: Garchomp|90/100|[from] item: Life Orb
            |win|A
            """);
        Assert.Equal(40, run.Of("Garchomp").DealtDirect, 2);
        Assert.Equal(10, run.Of("Garchomp").TakenSelf, 2);
        Assert.Equal(0, run.Of("Garchomp").TakenIndirect, 2);
    }

    [Fact]
    public void Confusion_self_hit_is_self_damage()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gyarados, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Gyarados|Gyarados, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |-activate|p1a: Gyarados|confusion
            |-damage|p1a: Gyarados|80/100|[from] confusion
            |win|B
            """);
        Assert.Equal(20, run.Of("Gyarados").TakenSelf, 2);
        Assert.Equal(0, run.Of("Gyarados").TakenDirect, 2);
    }

    [Fact]
    public void Crash_damage_lands_on_the_user_as_self_damage()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Blaziken, M|item
            |poke|p2|Gengar, M|item
            |start
            |switch|p1a: Blaziken|Blaziken, M|100/100
            |switch|p2a: Gengar|Gengar, M|100/100
            |turn|1
            |move|p1a: Blaziken|High Jump Kick|p2a: Gengar|[miss]
            |-miss|p1a: Blaziken|p2a: Gengar
            |-damage|p1a: Blaziken|50/100
            |win|B
            """);
        Assert.Equal(50, run.Of("Blaziken").TakenSelf, 2);
        Assert.Equal(0, run.Of("Blaziken").TakenDirect, 2);
        Assert.Equal(0, run.Of("Gengar").DealtDirect, 2);
    }

    [Fact]
    public void A_move_hp_cost_substitute_is_self_damage()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Zapdos, M|item
            |poke|p2|Tyranitar, M|item
            |start
            |switch|p1a: Zapdos|Zapdos, M|100/100
            |switch|p2a: Tyranitar|Tyranitar, M|100/100
            |turn|1
            |move|p1a: Zapdos|Substitute|p1a: Zapdos
            |-start|p1a: Zapdos|Substitute
            |-damage|p1a: Zapdos|75/100
            |win|B
            """);
        Assert.Equal(25, run.Of("Zapdos").TakenSelf, 2);
        Assert.Equal(0, run.Of("Zapdos").TakenDirect, 2);
    }

    [Fact]
    public void Sticky_barb_chip_is_self_damage()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Ferrothorn, M|item
            |poke|p2|Gengar, M|item
            |start
            |switch|p1a: Ferrothorn|Ferrothorn, M|100/100
            |switch|p2a: Gengar|Gengar, M|100/100
            |turn|1
            |-damage|p1a: Ferrothorn|88/100|[from] item: Sticky Barb
            |win|B
            """);
        Assert.Equal(12, run.Of("Ferrothorn").TakenSelf, 2);
    }

    [Fact]
    public void Own_toxic_orb_poison_is_self_damage_not_credited_to_a_foe()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gliscor, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Gliscor|Gliscor, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |-status|p1a: Gliscor|tox|[from] item: Toxic Orb
            |turn|2
            |-damage|p1a: Gliscor|94/100|[from] psn
            |win|B
            """);
        Assert.Equal(6, run.Of("Gliscor").TakenSelf, 2);
        Assert.Equal(0, run.Of("Gliscor").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Snorlax").DealtIndirect, 2);
    }

    // ── Enemy-inflicted status is NOT self (kept credited to the inflictor) ───

    [Fact]
    public void Enemy_toxic_poison_is_the_inflictors_indirect_damage_not_self()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gengar, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Gengar|Gengar, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Gengar|Toxic|p2a: Snorlax
            |-status|p2a: Snorlax|tox
            |turn|2
            |-damage|p2a: Snorlax|94/100|[from] psn
            |win|A
            """);
        Assert.Equal(6, run.Of("Gengar").DealtIndirect, 2);
        Assert.Equal(6, run.Of("Snorlax").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Snorlax").TakenSelf, 2);
    }

    [Fact]
    public void Flame_body_burn_is_credited_to_the_ability_holder_via_of_not_self()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Rapidash, M|item
            |poke|p2|Tangrowth, M|item
            |start
            |switch|p1a: Rapidash|Rapidash, M|100/100
            |switch|p2a: Tangrowth|Tangrowth, M|100/100
            |turn|1
            |move|p2a: Tangrowth|Power Whip|p1a: Rapidash
            |-damage|p1a: Rapidash|70/100
            |-status|p2a: Tangrowth|brn|[from] ability: Flame Body|[of] p1a: Rapidash
            |turn|2
            |-damage|p2a: Tangrowth|88/100|[from] brn
            |win|A
            """);
        Assert.Equal(12, run.Of("Rapidash").DealtIndirect, 2);
        Assert.Equal(12, run.Of("Tangrowth").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Tangrowth").TakenSelf, 2);
    }

    // ── [of]-tagged hitback → the holder ─────────────────────────────────────

    [Fact]
    public void Rocky_helmet_chip_is_credited_to_the_holder()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Ferrothorn, M|item
            |poke|p2|Lopunny, F|item
            |start
            |switch|p1a: Ferrothorn|Ferrothorn, M|100/100
            |switch|p2a: Lopunny|Lopunny, F|100/100
            |turn|1
            |move|p2a: Lopunny|Close Combat|p1a: Ferrothorn
            |-damage|p1a: Ferrothorn|70/100
            |-damage|p2a: Lopunny|88/100|[from] item: Rocky Helmet|[of] p1a: Ferrothorn
            |win|B
            """);
        Assert.Equal(12, run.Of("Ferrothorn").DealtIndirect, 2);
        Assert.Equal(12, run.Of("Lopunny").TakenIndirect, 2);
        Assert.Equal(30, run.Of("Lopunny").DealtDirect, 2);
    }

    [Fact]
    public void Aftermath_credits_the_fainted_holder()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Weezing, M|item
            |poke|p2|Scizor, M|item
            |start
            |switch|p1a: Weezing|Weezing, M|100/100
            |switch|p2a: Scizor|Scizor, M|100/100
            |turn|1
            |move|p2a: Scizor|Bullet Punch|p1a: Weezing
            |-damage|p1a: Weezing|0 fnt
            |faint|p1a: Weezing
            |-damage|p2a: Scizor|75/100|[from] ability: Aftermath|[of] p1a: Weezing
            |win|B
            """);
        Assert.Equal(25, run.Of("Weezing").DealtIndirect, 2); // credited though it fainted
        Assert.Equal(25, run.Of("Scizor").TakenIndirect, 2);
        Assert.Equal(1, run.Of("Scizor").Kills);
        Assert.Equal(1, run.Of("Weezing").Deaths);
    }

    // ── Hazards: ownership survives faints, switches, and resets ──────────────

    [Fact]
    public void Toxic_spikes_poison_is_credited_to_the_setter()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Ferroseed, M|item
            |poke|p2|Skarmory, M|item
            |poke|p2|Garchomp, M|item
            |start
            |switch|p1a: Ferroseed|Ferroseed, M|100/100
            |switch|p2a: Skarmory|Skarmory, M|100/100
            |turn|1
            |move|p1a: Ferroseed|Toxic Spikes|p2a: Skarmory
            |-sidestart|p2: B|move: Toxic Spikes
            |turn|2
            |switch|p2a: Garchomp|Garchomp, M|100/100
            |-status|p2a: Garchomp|tox|[from] move: Toxic Spikes
            |turn|3
            |-damage|p2a: Garchomp|94/100|[from] psn
            |win|A
            """);
        Assert.Equal(6, run.Of("Ferroseed").DealtIndirect, 2);
        Assert.Equal(6, run.Of("Garchomp").TakenIndirect, 2);
    }

    [Fact]
    public void Hazard_kill_credits_the_setter_even_after_it_has_fainted()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Ferrothorn, M|item
            |poke|p1|Magnezone, M|item
            |poke|p2|Talonflame, M|item
            |poke|p2|Charizard, M|item
            |start
            |switch|p1a: Ferrothorn|Ferrothorn, M|100/100
            |switch|p2a: Talonflame|Talonflame, M|100/100
            |turn|1
            |move|p1a: Ferrothorn|Stealth Rock|p2a: Talonflame
            |-sidestart|p2: B|move: Stealth Rock
            |move|p2a: Talonflame|Brave Bird|p1a: Ferrothorn
            |-damage|p1a: Ferrothorn|0 fnt
            |faint|p1a: Ferrothorn
            |turn|2
            |switch|p1a: Magnezone|Magnezone, M|100/100
            |switch|p2a: Charizard|Charizard, M|100/100
            |-damage|p2a: Charizard|0 fnt|[from] Stealth Rock
            |faint|p2a: Charizard
            |win|B
            """);
        Assert.Equal(100, run.Of("Ferrothorn").DealtIndirect, 2);
        Assert.Equal(1, run.Of("Ferrothorn").Kills); // the SR kill, though Ferrothorn fainted turn 1
    }

    [Fact]
    public void A_reset_hazard_credits_the_new_setter_not_the_old()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Ferrothorn, M|item
            |poke|p1|Skarmory, M|item
            |poke|p2|Talonflame, M|item
            |poke|p2|Charizard, M|item
            |start
            |switch|p1a: Ferrothorn|Ferrothorn, M|100/100
            |switch|p2a: Talonflame|Talonflame, M|100/100
            |turn|1
            |move|p1a: Ferrothorn|Stealth Rock|p2a: Talonflame
            |-sidestart|p2: B|move: Stealth Rock
            |turn|2
            |move|p2a: Talonflame|Defog|p2a: Talonflame
            |-sideend|p2: B|Stealth Rock|[from] move: Defog
            |turn|3
            |switch|p1a: Skarmory|Skarmory, M|100/100
            |move|p1a: Skarmory|Stealth Rock|p2a: Talonflame
            |-sidestart|p2: B|move: Stealth Rock
            |turn|4
            |switch|p2a: Charizard|Charizard, M|100/100
            |-damage|p2a: Charizard|0 fnt|[from] Stealth Rock
            |faint|p2a: Charizard
            |win|A
            """);
        Assert.Equal(100, run.Of("Skarmory").DealtIndirect, 2);
        Assert.Equal(1, run.Of("Skarmory").Kills);
        Assert.Equal(0, run.Of("Ferrothorn").DealtIndirect, 2); // its rocks were cleared
        Assert.Equal(0, run.Of("Ferrothorn").Kills);
    }

    // ── Healing: drain's [of] is the victim, Wish is self, Heal Pulse a foe ───

    [Fact]
    public void Drain_heals_the_user_and_never_credits_the_of_victim()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Venusaur, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Venusaur|Venusaur, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p2a: Snorlax|Body Slam|p1a: Venusaur
            |-damage|p1a: Venusaur|60/100
            |move|p1a: Venusaur|Giga Drain|p2a: Snorlax
            |-damage|p2a: Snorlax|70/100
            |-heal|p1a: Venusaur|75/100|[from] drain|[of] p2a: Snorlax
            |win|A
            """);
        Assert.Equal(15, run.Of("Venusaur").Recovered, 2); // self-recovery
        Assert.Equal(0, run.Of("Venusaur").Healed, 2);
        Assert.Equal(0, run.Of("Snorlax").Healed, 2);     // the [of] victim is not a healer
        Assert.Equal(30, run.Of("Venusaur").DealtDirect, 2);
    }

    [Fact]
    public void Absorb_ability_heal_is_self_recovery_not_credited_to_the_of_attacker()
    {
        // Tyrantrum (p2b) Earthquakes, hitting its own ally Orthworm (p2a), whose
        // Earth Eater turns the Ground hit into a heal. Showdown's [of] on that heal
        // points to the mon whose move triggered it (Tyrantrum), NOT a healer — so
        // the heal is Orthworm's own self-recovery, never Tyrantrum ally-healing.
        // (Regression: this once credited Tyrantrum ~74% ally-heal over a game.)
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garchomp|item
            |poke|p1|Rotom|item
            |poke|p2|Tyrantrum|item
            |poke|p2|Orthworm|item
            |start
            |switch|p1a: Garchomp|Garchomp|100/100
            |switch|p1b: Rotom|Rotom|100/100
            |switch|p2a: Orthworm|Orthworm|50/100
            |switch|p2b: Tyrantrum|Tyrantrum|100/100
            |turn|1
            |move|p2b: Tyrantrum|Earthquake|p2a: Orthworm|[spread] p1a,p1b,p2a
            |-damage|p1a: Garchomp|60/100
            |-damage|p1b: Rotom|70/100
            |-heal|p2a: Orthworm|75/100|[from] ability: Earth Eater|[of] p2b: Tyrantrum
            |win|B
            """);
        Assert.Equal(25, run.Of("Orthworm").Recovered, 2);  // holder's own self-recovery
        Assert.Equal(0, run.Of("Tyrantrum").Healed, 2);     // the [of] attacker did NOT heal an ally
        Assert.Equal(0, run.Of("Orthworm").Healed, 2);
    }

    [Theory]
    [InlineData("Water Absorb", "Surf")]     // Water Absorb heals on a Water hit
    [InlineData("Volt Absorb", "Thunderbolt")] // Volt Absorb heals on an Electric hit
    [InlineData("Dry Skin", "Scald")]        // Dry Skin heals on a Water hit
    public void Damage_absorbing_ability_heal_is_self_recovery_not_ally_or_enemy(string ability, string move)
    {
        // A foe attacks the holder with the matching move type; the absorb ability
        // turns it into a heal whose [of] points to the ATTACKER. That HP is the
        // holder's own self-recovery — never credited as (enemy-)healing to the
        // attacker in [of]. Same class of bug as Earth Eater.
        var run = ReplayLogRunner.Run($"""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Jolteon|item
            |poke|p2|Vaporeon|item
            |start
            |switch|p1a: Jolteon|Jolteon|100/100
            |switch|p2a: Vaporeon|Vaporeon|60/100
            |turn|1
            |move|p1a: Jolteon|{move}|p2a: Vaporeon
            |-heal|p2a: Vaporeon|85/100|[from] ability: {ability}|[of] p1a: Jolteon
            |win|B
            """);
        Assert.Equal(25, run.Of("Vaporeon").Recovered, 2);   // holder's self-recovery
        Assert.Equal(0, run.Of("Vaporeon").Healed, 2);
        Assert.Equal(0, run.Of("Jolteon").HealedEnemy, 2);   // the [of] attacker did NOT heal anyone
        Assert.Equal(0, run.Of("Jolteon").Healed, 2);
    }

    [Fact]
    public void Wish_lands_as_self_recovery_not_credited_to_the_last_mover()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Jirachi, F|item
            |poke|p1|Celesteela, F|item
            |poke|p2|Gengar, M|item
            |start
            |switch|p1a: Jirachi|Jirachi, F|100/100
            |switch|p2a: Gengar|Gengar, M|100/100
            |turn|1
            |move|p1a: Jirachi|Wish|p1a: Jirachi
            |turn|2
            |switch|p1a: Celesteela|Celesteela, F|50/100
            |move|p2a: Gengar|Shadow Ball|p1a: Celesteela
            |-damage|p1a: Celesteela|40/100
            |-heal|p1a: Celesteela|90/100|[from] move: Wish|[wisher] Jirachi
            |win|A
            """);
        Assert.Equal(50, run.Of("Celesteela").Recovered, 2);
        Assert.Equal(0, run.Of("Gengar").Healed, 2);
    }

    [Fact]
    public void Heal_pulse_on_a_foe_is_enemy_healing()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Audino, F|item
            |poke|p2|Garchomp, M|item
            |start
            |switch|p1a: Audino|Audino, F|100/100
            |switch|p2a: Garchomp|Garchomp, M|100/100
            |turn|1
            |move|p1a: Audino|Seismic Toss|p2a: Garchomp
            |-damage|p2a: Garchomp|70/100
            |turn|2
            |move|p1a: Audino|Heal Pulse|p2a: Garchomp
            |-heal|p2a: Garchomp|100/100
            |win|B
            """);
        Assert.Equal(30, run.Of("Audino").HealedEnemy, 2);
        Assert.Equal(0, run.Of("Audino").Healed, 2);
        Assert.Equal(0, run.Of("Garchomp").Recovered, 2);
    }

    [Fact]
    public void Leftovers_is_self_recovery()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Toxapex, F|item
            |poke|p2|Gengar, M|item
            |start
            |switch|p1a: Toxapex|Toxapex, F|100/100
            |switch|p2a: Gengar|Gengar, M|100/100
            |turn|1
            |move|p2a: Gengar|Shadow Ball|p1a: Toxapex
            |-damage|p1a: Toxapex|80/100
            |-heal|p1a: Toxapex|86/100|[from] item: Leftovers
            |win|B
            """);
        Assert.Equal(6, run.Of("Toxapex").Recovered, 2);
        Assert.Equal(0, run.Of("Toxapex").Healed, 2);
    }

    [Fact]
    public void Leech_seed_damage_credits_the_seeder_and_its_heal_is_the_seeders_recovery()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Whimsicott, F|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Whimsicott|Whimsicott, F|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p2a: Snorlax|Body Slam|p1a: Whimsicott
            |-damage|p1a: Whimsicott|60/100
            |move|p1a: Whimsicott|Leech Seed|p2a: Snorlax
            |-start|p2a: Snorlax|move: Leech Seed
            |turn|2
            |-damage|p2a: Snorlax|88/100|[from] Leech Seed|[of] p1a: Whimsicott
            |-heal|p1a: Whimsicott|72/100|[from] Leech Seed|[silent]
            |win|A
            """);
        Assert.Equal(12, run.Of("Whimsicott").DealtIndirect, 2); // via [of]
        Assert.Equal(12, run.Of("Whimsicott").Recovered, 2);     // silent heal → self
        Assert.Equal(12, run.Of("Snorlax").TakenIndirect, 2);
    }

    // ── Weather is counted as taken but credited to no dealer ────────────────

    [Fact]
    public void Sandstorm_chip_is_taken_but_uncredited()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Tyranitar, M|item
            |poke|p2|Charizard, M|item
            |start
            |switch|p1a: Tyranitar|Tyranitar, M|100/100
            |switch|p2a: Charizard|Charizard, M|100/100
            |-weather|Sandstorm|[from] ability: Sand Stream|[of] p1a: Tyranitar
            |turn|1
            |-weather|Sandstorm|[upkeep]
            |-damage|p2a: Charizard|94/100|[from] Sandstorm
            |win|A
            """);
        Assert.Equal(6, run.Of("Charizard").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Charizard").TakenSelf, 2);
        Assert.Equal(0, run.Of("Tyranitar").DealtIndirect, 2); // weather has no credited dealer
    }

    // ── KO credit edge cases ─────────────────────────────────────────────────

    [Fact]
    public void A_friendly_fire_faint_is_not_a_kill_for_the_attacker()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Rillaboom, M|item
            |poke|p1|Incineroar, M|item
            |poke|p2|Garchomp, M|item
            |poke|p2|Landorus, M|item
            |start
            |switch|p1a: Rillaboom|Rillaboom, M|100/100
            |switch|p1b: Incineroar|Incineroar, M|100/100
            |switch|p2a: Garchomp|Garchomp, M|100/100
            |switch|p2b: Landorus|Landorus, M|100/100
            |turn|1
            |move|p2a: Garchomp|Earthquake|p1a: Rillaboom|[spread] p1a,p1b,p2b
            |-damage|p1a: Rillaboom|60/100
            |-damage|p1b: Incineroar|55/100
            |-damage|p2b: Landorus|0 fnt
            |faint|p2b: Landorus
            |win|A
            """);
        Assert.Equal(0, run.Of("Garchomp").Kills);            // KO'd its own ally — no credit
        Assert.Equal(100, run.Of("Garchomp").DealtAllyDirect, 2);
        Assert.Equal(85, run.Of("Garchomp").DealtDirect, 2);  // 40 + 45 to the two opponents
        Assert.Equal(1, run.Of("Landorus").Deaths);
    }

    [Fact]
    public void Explosion_user_faints_without_crediting_itself_a_kill()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gengar, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Gengar|Gengar, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Gengar|Explosion|p2a: Snorlax
            |-damage|p2a: Snorlax|20/100
            |faint|p1a: Gengar
            |win|B
            """);
        Assert.Equal(80, run.Of("Gengar").DealtDirect, 2);
        Assert.Equal(1, run.Of("Gengar").Deaths);
        Assert.Equal(0, run.Of("Gengar").Kills);
    }

    // ── HP parsing / guards ──────────────────────────────────────────────────

    [Fact]
    public void Real_hp_fractions_are_normalised_to_percent_of_max()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Blissey, F|item
            |poke|p2|Gengar, M|item
            |start
            |switch|p1a: Blissey|Blissey, F|651/651
            |switch|p2a: Gengar|Gengar, M|100/100
            |turn|1
            |move|p2a: Gengar|Shadow Ball|p1a: Blissey
            |-damage|p1a: Blissey|521/651
            |win|B
            """);
        // 130/651 of a bar ≈ 19.97%, matching the opponent's percentage view.
        Assert.Equal(19.97, run.Of("Blissey").TakenDirect, 2);
        Assert.Equal(19.97, run.Of("Gengar").DealtDirect, 2);
    }

    [Fact]
    public void Pain_split_sethp_records_no_damage_or_heal()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Rotom, F|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Rotom|Rotom, F|100/100
            |switch|p2a: Snorlax|Snorlax, M|651/651
            |turn|1
            |move|p1a: Rotom|Pain Split|p2a: Snorlax
            |-sethp|p2a: Snorlax|300/651|[from] move: Pain Split
            |-sethp|p1a: Rotom|60/100|[from] move: Pain Split
            |win|B
            """);
        Assert.Equal(0, run.Of("Rotom").TakenSelf, 2);
        Assert.Equal(0, run.Of("Rotom").Recovered, 2);
        Assert.Equal(0, run.Of("Snorlax").TakenDirect, 2);
        Assert.Equal(0, run.Of("Snorlax").TakenIndirect, 2);
    }

    [Fact]
    public void A_heal_at_full_hp_records_nothing()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Toxapex, F|item
            |poke|p2|Gengar, M|item
            |start
            |switch|p1a: Toxapex|Toxapex, F|100/100
            |switch|p2a: Gengar|Gengar, M|100/100
            |turn|1
            |-heal|p1a: Toxapex|100/100|[from] item: Leftovers
            |win|B
            """);
        Assert.Equal(0, run.Of("Toxapex").Recovered, 2);
    }

    // ── Grassy Terrain: credit survives the setter leaving the field ─────────

    [Fact]
    public void Grassy_terrain_credits_the_setter_even_after_it_switches_out()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Rillaboom, M|item
            |poke|p1|Incineroar, M|item
            |poke|p1|Amoonguss, M|item
            |poke|p2|Garchomp, M|item
            |poke|p2|Landorus, M|item
            |start
            |switch|p1a: Rillaboom|Rillaboom, M|100/100
            |-fieldstart|move: Grassy Terrain|[from] ability: Grassy Surge|[of] p1a: Rillaboom
            |switch|p1b: Incineroar|Incineroar, M|60/100
            |switch|p2a: Garchomp|Garchomp, M|100/100
            |switch|p2b: Landorus|Landorus, M|100/100
            |turn|1
            |move|p2a: Garchomp|Protect|p2a: Garchomp
            |switch|p1a: Amoonguss|Amoonguss, M|100/100
            |-heal|p1b: Incineroar|70/100|[from] Grassy Terrain
            |win|A
            """);
        Assert.Equal(10, run.Of("Rillaboom").Healed, 2);      // still credited off the field
        Assert.Equal(0, run.Of("Incineroar").Recovered, 2);
    }
}
