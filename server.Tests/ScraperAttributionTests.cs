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
    public void Recoil_tagged_capitalised_is_still_self_damage()
    {
        // Real replays tag recoil "Recoil" (capital), not the "recoil" the older test
        // above uses, it must still count as SELF, never as opponent damage taken.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Crobat, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Crobat|Crobat, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Crobat|Brave Bird|p2a: Snorlax
            |-damage|p2a: Snorlax|70/100
            |-damage|p1a: Crobat|82/100|[from] Recoil
            |win|A
            """);
        Assert.Equal(18, run.Of("Crobat").TakenSelf, 2);
        Assert.Equal(0, run.Of("Crobat").TakenDirect, 2);
        Assert.Equal(0, run.Of("Crobat").TakenIndirect, 2);
    }

    [Fact]
    public void Steel_beam_hp_cost_is_self_damage_not_indirect()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Sliggoo, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Sliggoo|Sliggoo, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Sliggoo|Steel Beam|p2a: Snorlax
            |-damage|p2a: Snorlax|60/100
            |-damage|p1a: Sliggoo|50/100|[from] steelbeam
            |win|B
            """);
        Assert.Equal(50, run.Of("Sliggoo").TakenSelf, 2);
        Assert.Equal(0, run.Of("Sliggoo").TakenIndirect, 2);
        Assert.Equal(40, run.Of("Sliggoo").DealtDirect, 2);
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

    [Fact]
    public void Explosion_hp_cost_is_self_damage_and_scores_no_kill_for_the_opponent()
    {
        // Explosion faints the user with no -damage line of its own; the HP it spends is
        // self-damage, and the opponent that chipped it earlier doesn't get the KO.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Coalossal, M|item
            |poke|p2|Gardevoir, F|item
            |start
            |switch|p1a: Coalossal|Coalossal, M|100/100
            |switch|p2a: Gardevoir|Gardevoir, F|100/100
            |turn|1
            |move|p2a: Gardevoir|Moonblast|p1a: Coalossal
            |-damage|p1a: Coalossal|40/100
            |move|p1a: Coalossal|Explosion|p2a: Gardevoir
            |-damage|p2a: Gardevoir|20/100
            |faint|p1a: Coalossal
            |win|B
            """);
        Assert.Equal(60, run.Of("Coalossal").TakenDirect, 2); // Moonblast
        Assert.Equal(40, run.Of("Coalossal").TakenSelf, 2);   // the HP Explosion spent
        Assert.Equal(80, run.Of("Coalossal").DealtDirect, 2); // Explosion hit Gardevoir
        Assert.Equal(1, run.Of("Coalossal").Deaths);
        Assert.Equal(0, run.Of("Gardevoir").Kills);           // self-KO: no kill for the foe
        Assert.Equal(1, run.Of("Coalossal").SelfKos);         // it took itself down
    }

    [Fact]
    public void Friendly_fire_KO_is_an_ally_ko_not_a_kill()
    {
        // A spread move that finishes your own ally is an AlliesKoed for the attacker,
        // not a Kill, and the ally's faint is still a Death.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garchomp, M|item
            |poke|p1|Munchlax, M|item
            |poke|p2|Gengar, M|item
            |start
            |switch|p1a: Garchomp|Garchomp, M|100/100
            |switch|p1b: Munchlax|Munchlax, M|8/100
            |switch|p2a: Gengar|Gengar, M|100/100
            |turn|1
            |move|p1a: Garchomp|Earthquake|p2a: Gengar|[spread] p1b,p2a
            |-damage|p1b: Munchlax|0 fnt
            |-damage|p2a: Gengar|60/100
            |faint|p1b: Munchlax
            |win|A
            """);
        Assert.Equal(1, run.Of("Garchomp").AlliesKoed);
        Assert.Equal(0, run.Of("Garchomp").Kills);
        Assert.Equal(8, run.Of("Garchomp").DealtAllyDirect, 2);
        Assert.Equal(1, run.Of("Munchlax").Deaths);
        Assert.Equal(0, run.Of("Munchlax").SelfKos);
    }

    [Fact]
    public void Final_gambit_hp_cost_is_self_damage()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Shuckle, M|item
            |poke|p2|Blissey, F|item
            |start
            |switch|p1a: Shuckle|Shuckle, M|100/100
            |switch|p2a: Blissey|Blissey, F|100/100
            |turn|1
            |move|p1a: Shuckle|Final Gambit|p2a: Blissey
            |-damage|p2a: Blissey|60/100
            |faint|p1a: Shuckle
            |win|B
            """);
        Assert.Equal(100, run.Of("Shuckle").TakenSelf, 2);  // its whole bar
        Assert.Equal(40, run.Of("Shuckle").DealtDirect, 2); // damage dealt to Blissey
        Assert.Equal(1, run.Of("Shuckle").Deaths);
        Assert.Equal(0, run.Of("Blissey").Kills);
    }

    [Fact]
    public void Ghost_curse_hp_cost_is_self_damage()
    {
        // A Ghost using Curse spends half its HP (a bare -damage on itself), which is
        // self, not indirect. The residual on the target is a separate line.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gengar, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Gengar|Gengar, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Gengar|Curse|p2a: Snorlax
            |-start|p2a: Snorlax|Curse|[of] p1a: Gengar
            |-damage|p1a: Gengar|50/100
            |win|B
            """);
        Assert.Equal(50, run.Of("Gengar").TakenSelf, 2);
        Assert.Equal(0, run.Of("Gengar").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Gengar").TakenDirect, 2);
    }

    [Fact]
    public void Curse_residual_is_credited_to_the_caster()
    {
        // The per-turn curse chip carries no [of]; the caster is remembered from the
        // -start, so it's the caster's indirect damage dealt (not left orphaned).
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gengar, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Gengar|Gengar, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Gengar|Curse|p2a: Snorlax
            |-start|p2a: Snorlax|Curse|[of] p1a: Gengar
            |-damage|p1a: Gengar|50/100
            |turn|2
            |-damage|p2a: Snorlax|75/100|[from] Curse
            |win|A
            """);
        Assert.Equal(25, run.Of("Gengar").DealtIndirect, 2);   // the curse chip
        Assert.Equal(25, run.Of("Snorlax").TakenIndirect, 2);
        Assert.Equal(50, run.Of("Gengar").TakenSelf, 2);       // the placement cost
    }

    [Fact]
    public void Salt_cure_residual_is_credited_to_the_user()
    {
        // Salt Cure's -start names no source; the mon that just moved applied it.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garganacl, M|item
            |poke|p2|Stakataka|item
            |start
            |switch|p1a: Garganacl|Garganacl, M|100/100
            |switch|p2a: Stakataka|Stakataka|100/100
            |turn|1
            |move|p1a: Garganacl|Salt Cure|p2a: Stakataka
            |-start|p2a: Stakataka|Salt Cure
            |-damage|p2a: Stakataka|88/100|[from] Salt Cure
            |turn|2
            |-damage|p2a: Stakataka|76/100|[from] Salt Cure
            |win|A
            """);
        Assert.Equal(24, run.Of("Garganacl").DealtIndirect, 2); // two 12% ticks
        Assert.Equal(24, run.Of("Stakataka").TakenIndirect, 2);
    }

    [Fact]
    public void Solar_power_chip_is_self_damage_even_though_it_tags_of_the_holder()
    {
        // Solar Power / Dry Skin tag [of] the SUFFERING holder, so the [of] mon is the
        // victim itself: self-damage, not indirect credited to a foe.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Tropius, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Tropius|Tropius, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |-weather|SunnyDay
            |turn|1
            |-damage|p1a: Tropius|88/100|[from] ability: Solar Power|[of] p1a: Tropius
            |win|B
            """);
        Assert.Equal(12, run.Of("Tropius").TakenSelf, 2);
        Assert.Equal(0, run.Of("Tropius").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Snorlax").DealtIndirect, 2);
    }

    [Fact]
    public void Mimikyu_disguise_break_is_self_damage()
    {
        // The disguise break costs Mimikyu 1/8, tagged "[from] pokemon: Mimikyu-Busted"
        // with no [of]: its own form mechanic, so self.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Mimikyu, F|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Mimikyu|Mimikyu, F|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p2a: Snorlax|Body Slam|p1a: Mimikyu
            |-activate|p1a: Mimikyu|ability: Disguise
            |-damage|p1a: Mimikyu|88/100|[from] pokemon: Mimikyu-Busted
            |win|B
            """);
        Assert.Equal(12, run.Of("Mimikyu").TakenSelf, 2);
        Assert.Equal(0, run.Of("Mimikyu").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Snorlax").DealtIndirect, 2);
    }

    [Fact]
    public void Perish_song_faint_is_indirect_damage_credited_to_the_caster()
    {
        // Perish Song fells mons from the counter with no -damage line: the HP each
        // loses is booked as indirect damage credited to the caster (the caster's own
        // is self), so the game's HP still reconciles.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gengar, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Gengar|Gengar, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Gengar|Perish Song|p1a: Gengar
            |-start|p1a: Gengar|perish3
            |-start|p2a: Snorlax|perish3
            |turn|2
            |-start|p1a: Gengar|perish0
            |-start|p2a: Snorlax|perish0
            |faint|p2a: Snorlax
            |faint|p1a: Gengar
            |win|A
            """);
        Assert.Equal(100, run.Of("Snorlax").TakenIndirect, 2);
        Assert.Equal(1, run.Of("Snorlax").Deaths);
        Assert.Equal(100, run.Of("Gengar").DealtIndirect, 2); // felled the enemy
        Assert.Equal(1, run.Of("Gengar").Kills);
        Assert.Equal(100, run.Of("Gengar").TakenSelf, 2);     // its own song felled it too
        Assert.Equal(1, run.Of("Gengar").SelfKos);
    }

    [Fact]
    public void Ohko_move_is_direct_damage_and_a_kill()
    {
        // Fissure / Sheer Cold / Horn Drill deal damage = the target's HP through the
        // normal path, so they emit a plain -damage to 0: direct damage + a kill.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Dugtrio, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Dugtrio|Dugtrio, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Dugtrio|Fissure|p2a: Snorlax
            |-damage|p2a: Snorlax|0 fnt
            |-ohko
            |faint|p2a: Snorlax
            |win|A
            """);
        Assert.Equal(100, run.Of("Dugtrio").DealtDirect, 2);
        Assert.Equal(100, run.Of("Snorlax").TakenDirect, 2);
        Assert.Equal(1, run.Of("Dugtrio").Kills);
        Assert.Equal(1, run.Of("Snorlax").Deaths);
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

    [Fact]
    public void Rough_skin_chip_is_credited_to_the_holder()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garchomp, M|item
            |poke|p2|Lopunny, F|item
            |start
            |switch|p1a: Garchomp|Garchomp, M|100/100
            |switch|p2a: Lopunny|Lopunny, F|100/100
            |turn|1
            |move|p2a: Lopunny|Close Combat|p1a: Garchomp
            |-damage|p1a: Garchomp|70/100
            |-damage|p2a: Lopunny|88/100|[from] ability: Rough Skin|[of] p1a: Garchomp
            |win|B
            """);
        Assert.Equal(12, run.Of("Garchomp").DealtIndirect, 2);
        Assert.Equal(12, run.Of("Lopunny").TakenIndirect, 2);
        Assert.Equal(30, run.Of("Lopunny").DealtDirect, 2);
        Assert.Equal(0, run.Of("Garchomp").Kills); // survived, no KO
    }

    [Fact]
    public void Iron_barbs_chip_is_credited_to_the_holder()
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
            |-damage|p2a: Lopunny|88/100|[from] ability: Iron Barbs|[of] p1a: Ferrothorn
            |win|B
            """);
        Assert.Equal(12, run.Of("Ferrothorn").DealtIndirect, 2);
        Assert.Equal(12, run.Of("Lopunny").TakenIndirect, 2);
        Assert.Equal(30, run.Of("Lopunny").DealtDirect, 2);
    }

    [Fact]
    public void Rough_skin_recoil_that_KOs_the_attacker_is_a_kill_for_the_holder()
    {
        // A contact ability whose chip finishes the attacker: the damage AND the KO go
        // to the holder (the [of] mon), not left uncredited.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garchomp, M|item
            |poke|p2|Lopunny, F|item
            |start
            |switch|p1a: Garchomp|Garchomp, M|100/100
            |switch|p2a: Lopunny|Lopunny, F|8/100
            |turn|1
            |move|p2a: Lopunny|Close Combat|p1a: Garchomp
            |-damage|p1a: Garchomp|70/100
            |-damage|p2a: Lopunny|0 fnt|[from] ability: Rough Skin|[of] p1a: Garchomp
            |faint|p2a: Lopunny
            |win|A
            """);
        Assert.Equal(8, run.Of("Garchomp").DealtIndirect, 2);
        Assert.Equal(8, run.Of("Lopunny").TakenIndirect, 2);
        Assert.Equal(1, run.Of("Garchomp").Kills);   // the recoil scored the KO
        Assert.Equal(1, run.Of("Lopunny").Deaths);
    }

    [Fact]
    public void Iron_barbs_recoil_that_KOs_the_attacker_is_a_kill_for_the_holder()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Ferrothorn, M|item
            |poke|p2|Lopunny, F|item
            |start
            |switch|p1a: Ferrothorn|Ferrothorn, M|100/100
            |switch|p2a: Lopunny|Lopunny, F|8/100
            |turn|1
            |move|p2a: Lopunny|Close Combat|p1a: Ferrothorn
            |-damage|p1a: Ferrothorn|70/100
            |-damage|p2a: Lopunny|0 fnt|[from] ability: Iron Barbs|[of] p1a: Ferrothorn
            |faint|p2a: Lopunny
            |win|A
            """);
        Assert.Equal(8, run.Of("Ferrothorn").DealtIndirect, 2);
        Assert.Equal(8, run.Of("Lopunny").TakenIndirect, 2);
        Assert.Equal(1, run.Of("Ferrothorn").Kills);
        Assert.Equal(1, run.Of("Lopunny").Deaths);
    }

    [Fact]
    public void Rocky_helmet_chip_that_KOs_the_attacker_is_a_kill_for_the_holder()
    {
        // Companion to the chip-only case above: when the Helmet recoil lands the KO,
        // the item's holder is credited the kill.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Ferrothorn, M|item
            |poke|p2|Lopunny, F|item
            |start
            |switch|p1a: Ferrothorn|Ferrothorn, M|100/100
            |switch|p2a: Lopunny|Lopunny, F|8/100
            |turn|1
            |move|p2a: Lopunny|Close Combat|p1a: Ferrothorn
            |-damage|p1a: Ferrothorn|70/100
            |-damage|p2a: Lopunny|0 fnt|[from] item: Rocky Helmet|[of] p1a: Ferrothorn
            |faint|p2a: Lopunny
            |win|A
            """);
        Assert.Equal(8, run.Of("Ferrothorn").DealtIndirect, 2);
        Assert.Equal(1, run.Of("Ferrothorn").Kills);
        Assert.Equal(1, run.Of("Lopunny").Deaths);
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

    [Fact]
    public void Spikes_chip_on_switch_in_is_credited_to_the_setter()
    {
        // Stealth Rock and Toxic Spikes are covered above; Spikes' grounded switch-in
        // chip (|-damage| [from] Spikes) is the third entry hazard, credited the same way.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Klefki, M|item
            |poke|p2|Snorlax, M|item
            |poke|p2|Garchomp, M|item
            |start
            |switch|p1a: Klefki|Klefki, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Klefki|Spikes|p2a: Snorlax
            |-sidestart|p2: B|Spikes
            |turn|2
            |switch|p2a: Garchomp|Garchomp, M|100/100
            |-damage|p2a: Garchomp|88/100|[from] Spikes
            |win|A
            """);
        Assert.Equal(12, run.Of("Klefki").DealtIndirect, 2);
        Assert.Equal(12, run.Of("Garchomp").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Garchomp").TakenSelf, 2);
    }

    // ── Magic Bounce / Magic Coat: reflected residual damage is the REFLECTOR's ──
    // When a reflectable move is bounced, Showdown re-emits it as a fresh |move| line
    // from the reflector (…|[from] ability: Magic Bounce, or |[from] Magic Coat), so
    // the mon that just "moved" is the reflector. Every hazard/status/seed it lays
    // therefore belongs to the reflector, on the ORIGINAL user's side, never to the
    // user who threw it. These lock that for every reflectable move that can chip.

    [Theory]
    [InlineData("Toxic", "tox")]        // badly poison (residual still tags [from] psn)
    [InlineData("Poison Powder", "psn")]
    [InlineData("Poison Gas", "psn")]
    [InlineData("Toxic Thread", "psn")]
    public void Reflected_poison_move_residual_is_the_reflectors_indirect_damage(string move, string status)
    {
        // Snorlax throws the poison move; Xatu's Magic Bounce sends it back onto
        // Snorlax. The poison chip is Xatu's indirect damage, NOT Snorlax's own.
        var run = ReplayLogRunner.Run($"""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Snorlax, M|item
            |poke|p2|Xatu, F|item
            |start
            |switch|p1a: Snorlax|Snorlax, M|100/100
            |switch|p2a: Xatu|Xatu, F|100/100
            |turn|1
            |move|p1a: Snorlax|{move}|p2a: Xatu
            |move|p2a: Xatu|{move}|p1a: Snorlax|[from] ability: Magic Bounce
            |-status|p1a: Snorlax|{status}
            |turn|2
            |-damage|p1a: Snorlax|94/100|[from] psn
            |win|B
            """);
        Assert.Equal(6, run.Of("Xatu").DealtIndirect, 2);    // the reflector owns the chip
        Assert.Equal(6, run.Of("Snorlax").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Snorlax").TakenSelf, 2);     // NOT self-inflicted
        Assert.Equal(0, run.Of("Snorlax").DealtIndirect, 2); // NOT the thrower's
    }

    [Fact]
    public void Reflected_will_o_wisp_burn_residual_is_the_reflectors_indirect_damage()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Snorlax, M|item
            |poke|p2|Xatu, F|item
            |start
            |switch|p1a: Snorlax|Snorlax, M|100/100
            |switch|p2a: Xatu|Xatu, F|100/100
            |turn|1
            |move|p1a: Snorlax|Will-O-Wisp|p2a: Xatu
            |move|p2a: Xatu|Will-O-Wisp|p1a: Snorlax|[from] ability: Magic Bounce
            |-status|p1a: Snorlax|brn
            |turn|2
            |-damage|p1a: Snorlax|94/100|[from] brn
            |win|B
            """);
        Assert.Equal(6, run.Of("Xatu").DealtIndirect, 2);
        Assert.Equal(6, run.Of("Snorlax").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Snorlax").TakenSelf, 2);
    }

    [Fact]
    public void Reflected_leech_seed_residual_is_the_reflectors_indirect_damage()
    {
        // The bounced seed lands on the thrower (Snorlax); its residual carries
        // [of] p2a: Xatu, so the drain is Xatu's indirect damage and self-recovery.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Snorlax, M|item
            |poke|p2|Xatu, F|item
            |start
            |switch|p1a: Snorlax|Snorlax, M|100/100
            |switch|p2a: Xatu|Xatu, F|100/100
            |turn|1
            |move|p1a: Snorlax|Leech Seed|p2a: Xatu
            |move|p2a: Xatu|Leech Seed|p1a: Snorlax|[from] ability: Magic Bounce
            |-start|p1a: Snorlax|move: Leech Seed
            |turn|2
            |-damage|p1a: Snorlax|88/100|[from] Leech Seed|[of] p2a: Xatu
            |-heal|p2a: Xatu|100/100|[from] Leech Seed|[silent]
            |win|B
            """);
        Assert.Equal(12, run.Of("Xatu").DealtIndirect, 2);
        Assert.Equal(12, run.Of("Snorlax").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Snorlax").DealtIndirect, 2);
    }

    [Fact]
    public void Reflected_stealth_rock_chip_is_the_reflectors_indirect_damage()
    {
        // Ferrothorn's Stealth Rock is bounced onto ITS OWN side (p1); the next p1
        // switch-in (Talonflame, 4x weak) takes the chip, credited to Xatu.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Ferrothorn, M|item
            |poke|p1|Talonflame, M|item
            |poke|p2|Xatu, F|item
            |start
            |switch|p1a: Ferrothorn|Ferrothorn, M|100/100
            |switch|p2a: Xatu|Xatu, F|100/100
            |turn|1
            |move|p1a: Ferrothorn|Stealth Rock|p2a: Xatu
            |move|p2a: Xatu|Stealth Rock|p1a: Ferrothorn|[from] ability: Magic Bounce
            |-sidestart|p1: A|move: Stealth Rock
            |turn|2
            |switch|p1a: Talonflame|Talonflame, M|100/100
            |-damage|p1a: Talonflame|50/100|[from] Stealth Rock
            |win|B
            """);
        Assert.Equal(50, run.Of("Xatu").DealtIndirect, 2);
        Assert.Equal(50, run.Of("Talonflame").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Ferrothorn").DealtIndirect, 2); // the thrower set nothing
    }

    [Fact]
    public void Reflected_spikes_chip_is_the_reflectors_indirect_damage()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Klefki, M|item
            |poke|p1|Snorlax, M|item
            |poke|p2|Xatu, F|item
            |start
            |switch|p1a: Klefki|Klefki, M|100/100
            |switch|p2a: Xatu|Xatu, F|100/100
            |turn|1
            |move|p1a: Klefki|Spikes|p2a: Xatu
            |move|p2a: Xatu|Spikes|p1a: Klefki|[from] ability: Magic Bounce
            |-sidestart|p1: A|Spikes
            |turn|2
            |switch|p1a: Snorlax|Snorlax, M|100/100
            |-damage|p1a: Snorlax|88/100|[from] Spikes
            |win|B
            """);
        Assert.Equal(12, run.Of("Xatu").DealtIndirect, 2);
        Assert.Equal(12, run.Of("Snorlax").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Klefki").DealtIndirect, 2);
    }

    [Fact]
    public void Reflected_toxic_spikes_poison_is_the_reflectors_indirect_damage()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Ferroseed, M|item
            |poke|p1|Snorlax, M|item
            |poke|p2|Xatu, F|item
            |start
            |switch|p1a: Ferroseed|Ferroseed, M|100/100
            |switch|p2a: Xatu|Xatu, F|100/100
            |turn|1
            |move|p1a: Ferroseed|Toxic Spikes|p2a: Xatu
            |move|p2a: Xatu|Toxic Spikes|p1a: Ferroseed|[from] ability: Magic Bounce
            |-sidestart|p1: A|move: Toxic Spikes
            |turn|2
            |switch|p1a: Snorlax|Snorlax, M|100/100
            |-status|p1a: Snorlax|tox|[from] move: Toxic Spikes
            |turn|3
            |-damage|p1a: Snorlax|94/100|[from] psn
            |win|B
            """);
        Assert.Equal(6, run.Of("Xatu").DealtIndirect, 2);
        Assert.Equal(6, run.Of("Snorlax").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Ferroseed").DealtIndirect, 2);
    }

    [Fact]
    public void Reflected_via_magic_coat_also_credits_the_reflector()
    {
        // Magic Coat (the move that mimics Magic Bounce) re-emits the bounced move the
        // same way, tagged |[from] Magic Coat. Stealth Rock stands in for all of them.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Ferrothorn, M|item
            |poke|p1|Talonflame, M|item
            |poke|p2|Espeon, F|item
            |start
            |switch|p1a: Ferrothorn|Ferrothorn, M|100/100
            |switch|p2a: Espeon|Espeon, F|100/100
            |turn|1
            |move|p2a: Espeon|Magic Coat|p2a: Espeon
            |-singleturn|p2a: Espeon|move: Magic Coat
            |move|p1a: Ferrothorn|Stealth Rock|p2a: Espeon
            |move|p2a: Espeon|Stealth Rock|p1a: Ferrothorn|[from] Magic Coat
            |-sidestart|p1: A|move: Stealth Rock
            |turn|2
            |switch|p1a: Talonflame|Talonflame, M|100/100
            |-damage|p1a: Talonflame|50/100|[from] Stealth Rock
            |win|B
            """);
        Assert.Equal(50, run.Of("Espeon").DealtIndirect, 2);
        Assert.Equal(50, run.Of("Talonflame").TakenIndirect, 2);
        Assert.Equal(0, run.Of("Ferrothorn").DealtIndirect, 2);
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
        // points to the mon whose move triggered it (Tyrantrum), NOT a healer, so
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
        // holder's own self-recovery, never credited as (enemy-)healing to the
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

    // ── Weather chip is credited to whoever set the weather ──────────────────

    [Fact]
    public void Sandstorm_chip_is_credited_to_the_ability_setter()
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
        Assert.Equal(6, run.Of("Tyranitar").DealtIndirect, 2); // credited to the sand setter
    }

    [Fact]
    public void Move_set_sandstorm_chip_is_credited_to_the_mover()
    {
        // Move-set weather carries no [of]; the setter is the mon that just used the
        // Sandstorm move. Its chip and any KO belong to that mover, even after it
        // has left the field.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Hippowdon, M|item
            |poke|p1|Excadrill, M|item
            |poke|p2|Charizard, M|item
            |start
            |switch|p1a: Hippowdon|Hippowdon, M|100/100
            |switch|p2a: Charizard|Charizard, M|100/100
            |turn|1
            |move|p1a: Hippowdon|Sandstorm|p1a: Hippowdon
            |-weather|Sandstorm
            |turn|2
            |switch|p1a: Excadrill|Excadrill, M|100/100
            |-weather|Sandstorm|[upkeep]
            |-damage|p2a: Charizard|0 fnt|[from] Sandstorm
            |faint|p2a: Charizard
            |win|A
            """);
        Assert.Equal(100, run.Of("Hippowdon").DealtIndirect, 2); // credited off the field
        Assert.Equal(1, run.Of("Hippowdon").Kills);              // the sand KO
        Assert.Equal(1, run.Of("Charizard").Deaths);
    }

    [Fact]
    public void Sandstorm_chip_on_the_setters_own_ally_is_friendly_fire_not_a_kill()
    {
        // Weather hits both sides. A non-immune mon on the sand-setter's own side
        // taking chip is friendly fire: it goes to the ally-damage bucket and its
        // faint never scores a kill for the setter.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Tyranitar, M|item
            |poke|p1|Charizard, M|item
            |poke|p2|Garchomp, M|item
            |poke|p2|Landorus, M|item
            |start
            |switch|p1a: Tyranitar|Tyranitar, M|100/100
            |switch|p1b: Charizard|Charizard, M|100/100
            |switch|p2a: Garchomp|Garchomp, M|100/100
            |switch|p2b: Landorus|Landorus, M|100/100
            |-weather|Sandstorm|[from] ability: Sand Stream|[of] p1a: Tyranitar
            |turn|1
            |-weather|Sandstorm|[upkeep]
            |-damage|p1b: Charizard|0 fnt|[from] Sandstorm
            |faint|p1b: Charizard
            |-damage|p2a: Garchomp|94/100|[from] Sandstorm
            |win|B
            """);
        Assert.Equal(6, run.Of("Tyranitar").DealtIndirect, 2);      // only the enemy chip
        Assert.Equal(100, run.Of("Tyranitar").DealtAllyIndirect, 2); // its own ally's chip
        Assert.Equal(0, run.Of("Tyranitar").Kills);                 // ally faint is not a kill
        Assert.Equal(1, run.Of("Charizard").Deaths);
    }

    // ── Perish Song: the counter's KO is credited to whoever cast it ─────────

    [Fact]
    public void Perish_song_ko_is_credited_to_the_caster()
    {
        // Perish Song sets a |perish3| counter on every active mon; it ticks down each
        // turn and the mon faints on |perish0| with no -damage of its own. That KO on a
        // foe is the caster's kill.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Politoed, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Politoed|Politoed, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Politoed|Perish Song|p1a: Politoed
            |-start|p1a: Politoed|perish3|[silent]
            |-start|p2a: Snorlax|perish3
            |turn|2
            |-start|p1a: Politoed|perish2
            |-start|p2a: Snorlax|perish2
            |turn|3
            |-start|p1a: Politoed|perish1
            |-start|p2a: Snorlax|perish1
            |turn|4
            |-start|p2a: Snorlax|perish0
            |faint|p2a: Snorlax
            |win|A
            """);
        Assert.Equal(1, run.Of("Politoed").Kills);   // the perish KO on the foe
        Assert.Equal(1, run.Of("Snorlax").Deaths);
    }

    [Fact]
    public void Perish_song_that_fells_the_casters_own_ally_is_not_a_kill()
    {
        // Perish Song also counts down the caster's side. A teammate that faints to the
        // caster's own Perish Song is friendly fire, never a kill for the caster (nor a
        // self-kill on the caster itself).
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Politoed|item
            |poke|p1|Whimsicott|item
            |poke|p2|Garchomp|item
            |poke|p2|Landorus|item
            |start
            |switch|p1a: Politoed|Politoed|100/100
            |switch|p1b: Whimsicott|Whimsicott|100/100
            |switch|p2a: Garchomp|Garchomp|100/100
            |switch|p2b: Landorus|Landorus|100/100
            |turn|1
            |move|p1a: Politoed|Perish Song|p1a: Politoed
            |-start|p1a: Politoed|perish3|[silent]
            |-start|p1b: Whimsicott|perish3
            |-start|p2a: Garchomp|perish3
            |-start|p2b: Landorus|perish3
            |turn|2
            |-start|p1b: Whimsicott|perish0
            |faint|p1b: Whimsicott
            |win|B
            """);
        Assert.Equal(0, run.Of("Politoed").Kills);   // its own ally faint is not a kill
        Assert.Equal(1, run.Of("Whimsicott").Deaths);
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
        Assert.Equal(0, run.Of("Garchomp").Kills);            // KO'd its own ally, no credit
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

    [Fact]
    public void Destiny_bond_credits_the_KO_to_the_bond_user_that_dragged_its_killer_down()
    {
        // Ceruledge Destiny Bonds, faints to Dondozo's Crunch, and takes Dondozo down
        // with it. Dondozo's faint carries NO -damage of its own, Showdown reports the
        // bond user's own faint, then the |-activate ... Destiny Bond, then the killer's
        // faint, so that KO must be credited to Ceruledge. (Regression: it was going
        // uncredited because there was no last-damage dealer to attribute it to.)
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Dondozo, M|item
            |poke|p2|Ceruledge, M|item
            |start
            |switch|p1a: Dondozo|Dondozo, M|100/100
            |switch|p2a: Ceruledge|Ceruledge, M|100/100
            |turn|1
            |move|p2a: Ceruledge|Destiny Bond|p2a: Ceruledge
            |-singlemove|p2a: Ceruledge|Destiny Bond
            |move|p1a: Dondozo|Crunch|p2a: Ceruledge
            |-supereffective|p2a: Ceruledge
            |-damage|p2a: Ceruledge|0 fnt
            |faint|p2a: Ceruledge
            |-activate|p2a: Ceruledge|move: Destiny Bond
            |faint|p1a: Dondozo
            |win|B
            """);
        Assert.Equal(1, run.Of("Ceruledge").Kills);   // dragged Dondozo down via Destiny Bond
        Assert.Equal(1, run.Of("Ceruledge").Deaths);
        Assert.Equal(1, run.Of("Dondozo").Kills);     // its Crunch still KO'd Ceruledge
        Assert.Equal(1, run.Of("Dondozo").Deaths);    // then it fainted to the bond
    }

    [Fact]
    public void Bind_chip_damage_and_its_KO_are_credited_to_the_trapper()
    {
        // Whirlpool's per-turn chip carries [partiallytrapped] and NO [of]; the trapper
        // (Lanturn) is named only in the -activate when the trap is set. That chip is
        // Lanturn's indirect damage, and a faint from it is Lanturn's KO. (Regression:
        // partial-trap chip + KO were going uncredited.)
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Lanturn, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Lanturn|Lanturn, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Lanturn|Whirlpool|p2a: Snorlax
            |-damage|p2a: Snorlax|90/100
            |-activate|p2a: Snorlax|move: Whirlpool|[of] p1a: Lanturn
            |-damage|p2a: Snorlax|84/100|[from] move: Whirlpool|[partiallytrapped]
            |turn|2
            |-damage|p2a: Snorlax|0 fnt|[from] move: Whirlpool|[partiallytrapped]
            |faint|p2a: Snorlax
            |win|A
            """);
        Assert.Equal(1, run.Of("Lanturn").Kills);              // the bind chip KO'd Snorlax
        Assert.Equal(1, run.Of("Snorlax").Deaths);
        Assert.Equal(10, run.Of("Lanturn").DealtDirect, 2);   // the Whirlpool hit itself
        Assert.Equal(90, run.Of("Lanturn").DealtIndirect, 2); // 6 + 84 bind chip
        Assert.Equal(10, run.Of("Snorlax").TakenDirect, 2);
        Assert.Equal(90, run.Of("Snorlax").TakenIndirect, 2);
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

    // ── Major status: attribution rides with the mon across a switch ─────────

    [Fact]
    public void Burn_chip_stays_credited_to_the_inflictor_after_the_victim_pivots_out()
    {
        // A burned mon that switches out and back keeps its burn (major status
        // persists across switches). Its chip after the pivot must still be
        // credited to the mon that burned it, not orphaned. Regression: status
        // used to be slot-keyed and cleared on switch-out, so every tick after the
        // first pivot went uncredited (dealt != taken).
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Talonflame, M|item
            |poke|p2|Snorlax, M|item
            |poke|p2|Gengar, M|item
            |start
            |switch|p1a: Talonflame|Talonflame, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Talonflame|Will-O-Wisp|p2a: Snorlax
            |-status|p2a: Snorlax|brn
            |-damage|p2a: Snorlax|94/100|[from] brn
            |turn|2
            |switch|p2a: Gengar|Gengar, M|100/100
            |turn|3
            |switch|p2a: Snorlax|Snorlax, M|94/100
            |-damage|p2a: Snorlax|88/100|[from] brn
            |win|A
            """);
        Assert.Equal(12, run.Of("Snorlax").TakenIndirect, 2);   // both ticks land on the victim
        Assert.Equal(12, run.Of("Talonflame").DealtIndirect, 2); // both credited to the burner, post-pivot too
    }

    // ── Weather: a bare re-announce must not steal ownership ─────────────────

    [Fact]
    public void Bare_weather_reannounce_keeps_the_original_setter_not_the_last_mover()
    {
        // Sand Stream sets sand (owner = Tyranitar). After Tapu Bulu moves, the engine
        // repeats a bare |-weather|Sandstorm. That must NOT reassign the sand to Tapu
        // Bulu (the last mover): otherwise Tapu Bulu's own sand chip resolves dealer ==
        // victim and books taken with no matching dealt (dealt != taken). It stays
        // Tyranitar's indirect damage.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Tapu Bulu, M|item
            |poke|p2|Tyranitar, M|item
            |start
            |switch|p1a: Tapu Bulu|Tapu Bulu, M|100/100
            |switch|p2a: Tyranitar|Tyranitar, M|100/100
            |-weather|Sandstorm|[from] ability: Sand Stream|[of] p2a: Tyranitar
            |turn|1
            |move|p1a: Tapu Bulu|Wood Hammer|p2a: Tyranitar
            |-damage|p2a: Tyranitar|70/100
            |-weather|Sandstorm
            |-damage|p1a: Tapu Bulu|94/100|[from] Sandstorm
            |win|B
            """);
        Assert.Equal(6, run.Of("Tapu Bulu").TakenIndirect, 2); // sand credited to the setter
        Assert.Equal(0, run.Of("Tapu Bulu").TakenSelf, 2);     // not self-damage
        Assert.Equal(6, run.Of("Tyranitar").DealtIndirect, 2); // Tyranitar still owns the sand
        Assert.Equal(30, run.Of("Tapu Bulu").DealtDirect, 2);
    }

    // ── Delayed mega: pre-mega damage lands on the mega form ────────────────

    [Fact]
    public void Damage_before_and_after_a_mega_evolution_all_lands_on_one_pick()
    {
        // A mon can hold its stone a few turns (see simulate-season.js mega timing),
        // taking hits as its BASE form, then mega-evolve. Base and mega forms share a
        // base id, so every point, before and after, belongs to the single drafted pick.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Alakazam, M|item
            |poke|p2|Tyranitar, M|item
            |start
            |switch|p1a: Alakazam|Alakazam, M|100/100
            |switch|p2a: Tyranitar|Tyranitar, M|100/100
            |turn|1
            |move|p2a: Tyranitar|Rock Slide|p1a: Alakazam
            |-damage|p1a: Alakazam|70/100
            |turn|2
            |detailschange|p1a: Alakazam|Alakazam-Mega, M
            |-mega|p1a: Alakazam|Alakazam|Alakazite
            |move|p1a: Alakazam|Psychic|p2a: Tyranitar
            |-damage|p2a: Tyranitar|60/100
            |turn|3
            |move|p2a: Tyranitar|Rock Slide|p1a: Alakazam
            |-damage|p1a: Alakazam|40/100
            |win|B
            """);
        // One row, holding the pre-mega hit (30) AND the post-mega hit (30). A split
        // into separate base/mega picks would leave "Alakazam" with only the first 30.
        Assert.Equal(60, run.Of("Alakazam").TakenDirect, 2);
        Assert.Equal(40, run.Of("Alakazam").DealtDirect, 2); // dealt as the mega form
        Assert.Equal(40, run.Of("Tyranitar").TakenDirect, 2);
    }

    // ── Battle-only forms resolve to the drafted base ───────────────────────

    [Fact]
    public void Battle_only_forms_resolve_to_their_drafted_base()
    {
        var palafin = new DraftLeague.Web.Models.Pick { Id = 1 };
        var rotomWash = new DraftLeague.Web.Models.Pick { Id = 2 };
        var map = new Dictionary<string, DraftLeague.Web.Models.Pick>
        {
            [DraftLeague.Web.Services.ReplayStatsScraper.BaseId("Palafin")] = palafin,
            [DraftLeague.Web.Services.ReplayStatsScraper.BaseId("Rotom-Wash")] = rotomWash,
        };
        // Zero-to-Hero renames Palafin to "Palafin-Hero" mid-battle; that form is
        // never drafted, so it must fall back to the drafted base and keep scoring.
        Assert.Same(palafin, DraftLeague.Web.Services.ReplayStatsScraper.ResolveInMap(map, "Palafin-Hero"));
        Assert.Same(palafin, DraftLeague.Web.Services.ReplayStatsScraper.ResolveInMap(map, "Palafin"));
        // A drafted distinct form matches directly and never collapses to a sibling.
        Assert.Same(rotomWash, DraftLeague.Web.Services.ReplayStatsScraper.ResolveInMap(map, "Rotom-Wash"));
        Assert.Null(DraftLeague.Web.Services.ReplayStatsScraper.ResolveInMap(map, "Rotom-Heat"));
    }

    // ── Nicknames never affect attribution (resolve by species, not nickname) ─
    // The scraper reads the species from a switch's DETAILS field (the "Garchomp, M"
    // after the slot), not from the "p1a: Nickname" label, which SlotId trims at the
    // colon. These lock that in: a nickname is present, duplicated within a team, and
    // duplicated across teams, and finally set to ANOTHER species' name to prove a
    // collision can't leak. ReplayLogRunner keys each Pick by the log species, so if
    // the scraper ever resolved by nickname the by-species lookups below would miss
    // and every assertion would read a zeroed GameStat.

    [Fact]
    public void A_nickname_does_not_change_attribution()
    {
        // Baseline: both mons carry a nickname unrelated to their species; damage is
        // still booked against the species, not the nickname.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garchomp, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Sharky|Garchomp, M|100/100
            |switch|p2a: BigBoy|Snorlax, M|100/100
            |turn|1
            |move|p1a: Sharky|Earthquake|p2a: BigBoy
            |-damage|p2a: BigBoy|60/100
            |win|A
            """);
        Assert.Equal(40, run.Of("Garchomp").DealtDirect, 2);
        Assert.Equal(40, run.Of("Snorlax").TakenDirect, 2);
    }

    [Fact]
    public void Duplicate_nicknames_on_one_team_still_split_by_species()
    {
        // Both p1 mons share the nickname "Chungus" and cycle through the same slot;
        // the differing species keeps their stats apart.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garchomp, M|item
            |poke|p1|Snorlax, M|item
            |poke|p2|Gengar, M|item
            |start
            |switch|p1a: Chungus|Garchomp, M|100/100
            |switch|p2a: Gengar|Gengar, M|100/100
            |turn|1
            |move|p1a: Chungus|Earthquake|p2a: Gengar
            |-damage|p2a: Gengar|50/100
            |move|p2a: Gengar|Shadow Ball|p1a: Chungus
            |-damage|p1a: Chungus|60/100
            |turn|2
            |switch|p1a: Chungus|Snorlax, M|100/100
            |move|p2a: Gengar|Shadow Ball|p1a: Chungus
            |-damage|p1a: Chungus|70/100
            |win|B
            """);
        Assert.Equal(50, run.Of("Garchomp").DealtDirect, 2); // its Earthquake
        Assert.Equal(40, run.Of("Garchomp").TakenDirect, 2); // the first Shadow Ball
        Assert.Equal(30, run.Of("Snorlax").TakenDirect, 2);  // the second, after the swap
        Assert.Equal(0, run.Of("Snorlax").DealtDirect, 2);   // never attacked
        Assert.Equal(70, run.Of("Gengar").DealtDirect, 2);   // 40 + 30
        Assert.Equal(50, run.Of("Gengar").TakenDirect, 2);
    }

    [Fact]
    public void Duplicate_nicknames_across_teams_do_not_cross_contaminate()
    {
        // The same nickname "Twin" on both sides: the slot side (p1a vs p2a) plus the
        // species keeps the two mons distinct, so neither steals the other's damage.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garchomp, M|item
            |poke|p2|Gengar, M|item
            |start
            |switch|p1a: Twin|Garchomp, M|100/100
            |switch|p2a: Twin|Gengar, M|100/100
            |turn|1
            |move|p1a: Twin|Earthquake|p2a: Twin
            |-damage|p2a: Twin|40/100
            |move|p2a: Twin|Shadow Ball|p1a: Twin
            |-damage|p1a: Twin|55/100
            |win|A
            """);
        Assert.Equal(60, run.Of("Garchomp").DealtDirect, 2);
        Assert.Equal(45, run.Of("Garchomp").TakenDirect, 2);
        Assert.Equal(60, run.Of("Gengar").TakenDirect, 2);
        Assert.Equal(45, run.Of("Gengar").DealtDirect, 2);
    }

    [Fact]
    public void A_nickname_that_collides_with_another_species_does_not_leak()
    {
        // Adversarial: a Gengar nicknamed "Snorlax" faces a real Snorlax. If the
        // scraper resolved by the label it would credit Gengar's hit to the Snorlax
        // pick; resolving by the details field keeps them separate.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gengar, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Snorlax|Gengar, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1a: Snorlax|Shadow Ball|p2a: Snorlax
            |-damage|p2a: Snorlax|50/100
            |move|p2a: Snorlax|Body Slam|p1a: Snorlax
            |-damage|p1a: Snorlax|65/100
            |win|A
            """);
        Assert.Equal(50, run.Of("Gengar").DealtDirect, 2);   // the nicknamed attacker
        Assert.Equal(35, run.Of("Gengar").TakenDirect, 2);
        Assert.Equal(50, run.Of("Snorlax").TakenDirect, 2);  // the real Snorlax
        Assert.Equal(35, run.Of("Snorlax").DealtDirect, 2);
    }

    // ── Self-KO must not be credited to the last enemy that touched the mon ───

    [Fact]
    public void Recoil_that_KOs_its_own_user_is_a_self_ko_not_the_last_enemys_kill()
    {
        // The m3 bug: a mon whittled by the foe, then felled by its OWN Take Down recoil.
        // The recoil is the most recent hit, so the faint is a self-KO; the foe that
        // landed the earlier chip must NOT be credited the kill.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Buzzwole, M|item
            |poke|p2|Enamorus, F|item
            |start
            |switch|p1a: Buzzwole|Buzzwole, M|100/100
            |switch|p2a: Enamorus|Enamorus, F|100/100
            |turn|1
            |move|p1a: Buzzwole|Ice Punch|p2a: Enamorus
            |-damage|p2a: Enamorus|40/100
            |move|p2a: Enamorus|Take Down|p1a: Buzzwole
            |-damage|p1a: Buzzwole|80/100
            |-damage|p2a: Enamorus|0 fnt|[from] Recoil
            |faint|p2a: Enamorus
            |win|A
            """);
        Assert.Equal(60, run.Of("Enamorus").TakenDirect, 2);  // Ice Punch
        Assert.Equal(40, run.Of("Enamorus").TakenSelf, 2);    // the recoil that felled it
        Assert.Equal(1, run.Of("Enamorus").SelfKos);
        Assert.Equal(1, run.Of("Enamorus").Deaths);
        Assert.Equal(0, run.Of("Buzzwole").Kills);            // NOT the foe's kill
    }

    [Fact]
    public void Struggle_recoil_that_KOs_its_user_is_a_self_ko()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Ursaring, M|item
            |poke|p2|Sinistcha, F|item
            |start
            |switch|p1a: Ursaring|Ursaring, M|100/100
            |switch|p2a: Sinistcha|Sinistcha, F|100/100
            |turn|1
            |move|p1a: Ursaring|Facade|p2a: Sinistcha
            |-damage|p2a: Sinistcha|30/100
            |move|p2a: Sinistcha|Struggle|p1a: Ursaring
            |-damage|p1a: Ursaring|85/100
            |-damage|p2a: Sinistcha|0 fnt|[from] recoil
            |faint|p2a: Sinistcha
            |win|A
            """);
        Assert.Equal(1, run.Of("Sinistcha").SelfKos);
        Assert.Equal(0, run.Of("Ursaring").Kills); // only earlier chip, not the lethal blow
    }

    [Fact]
    public void Life_orb_that_KOs_its_holder_is_a_self_ko_not_the_last_enemys_kill()
    {
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garchomp, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Garchomp|Garchomp, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p2a: Snorlax|Body Slam|p1a: Garchomp
            |-damage|p1a: Garchomp|40/100
            |move|p1a: Garchomp|Earthquake|p2a: Snorlax
            |-damage|p2a: Snorlax|60/100
            |-damage|p1a: Garchomp|0 fnt|[from] item: Life Orb
            |faint|p1a: Garchomp
            |win|B
            """);
        Assert.Equal(60, run.Of("Garchomp").TakenDirect, 2);  // Body Slam
        Assert.Equal(40, run.Of("Garchomp").TakenSelf, 2);    // Life Orb finished it
        Assert.Equal(1, run.Of("Garchomp").SelfKos);
        Assert.Equal(0, run.Of("Snorlax").Kills);
    }

    [Fact]
    public void Fatigue_confusion_self_hit_KO_is_a_self_ko()
    {
        // Thrash/Outrage/Petal Dance end in [fatigue] confusion; hitting yourself in that
        // confusion is self-damage even if a foe chipped you earlier this game.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Rillaboom, M|item
            |poke|p2|Dragonite, M|item
            |start
            |switch|p1a: Rillaboom|Rillaboom, M|100/100
            |switch|p2a: Dragonite|Dragonite, M|100/100
            |turn|1
            |move|p2a: Dragonite|Ice Beam|p1a: Rillaboom
            |-damage|p1a: Rillaboom|30/100
            |-start|p1a: Rillaboom|confusion|[fatigue]
            |turn|2
            |-activate|p1a: Rillaboom|confusion
            |-damage|p1a: Rillaboom|0 fnt|[from] confusion
            |faint|p1a: Rillaboom
            |win|B
            """);
        Assert.Equal(70, run.Of("Rillaboom").TakenDirect, 2); // Ice Beam
        Assert.Equal(30, run.Of("Rillaboom").TakenSelf, 2);   // the confusion hit
        Assert.Equal(1, run.Of("Rillaboom").SelfKos);
        Assert.Equal(0, run.Of("Dragonite").Kills);
    }

    [Fact]
    public void Confusion_from_an_enemy_move_is_that_enemys_damage_and_KO()
    {
        // Confuse Ray (not fatigue): the confused mon's self-hit belongs to the foe that
        // confused it, and a faint from it is the foe's kill.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gyarados, M|item
            |poke|p2|Alakazam, M|item
            |start
            |switch|p1a: Gyarados|Gyarados, M|100/100
            |switch|p2a: Alakazam|Alakazam, M|100/100
            |turn|1
            |move|p2a: Alakazam|Confuse Ray|p1a: Gyarados
            |-start|p1a: Gyarados|confusion
            |turn|2
            |-activate|p1a: Gyarados|confusion
            |-damage|p1a: Gyarados|0 fnt|[from] confusion
            |faint|p1a: Gyarados
            |win|B
            """);
        Assert.Equal(100, run.Of("Gyarados").TakenIndirect, 2); // credited to the confuser, not self
        Assert.Equal(0, run.Of("Gyarados").TakenSelf, 2);
        Assert.Equal(0, run.Of("Gyarados").SelfKos);
        Assert.Equal(100, run.Of("Alakazam").DealtIndirect, 2);
        Assert.Equal(1, run.Of("Alakazam").Kills);
    }

    [Fact]
    public void Confusion_from_an_ally_teeter_dance_is_an_ally_ko_not_a_kill()
    {
        // Teeter Dance confuses adjacent mons including your own ally. That ally's
        // confusion self-hit is friendly fire: the caster's ally damage and an AlliesKoed.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Gyarados, M|item
            |poke|p1|Dragonite, M|item
            |poke|p2|Snorlax, M|item
            |start
            |switch|p1a: Gyarados|Gyarados, M|100/100
            |switch|p1b: Dragonite|Dragonite, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |turn|1
            |move|p1b: Dragonite|Teeter Dance|p1a: Gyarados
            |-start|p1a: Gyarados|confusion
            |turn|2
            |-activate|p1a: Gyarados|confusion
            |-damage|p1a: Gyarados|0 fnt|[from] confusion
            |faint|p1a: Gyarados
            |win|B
            """);
        Assert.Equal(100, run.Of("Gyarados").TakenIndirect, 2);
        Assert.Equal(1, run.Of("Gyarados").Deaths);
        Assert.Equal(100, run.Of("Dragonite").DealtAllyIndirect, 2);
        Assert.Equal(1, run.Of("Dragonite").AlliesKoed);
        Assert.Equal(0, run.Of("Dragonite").Kills);
    }

    [Fact]
    public void Life_orb_tricked_on_by_a_foe_is_that_foes_damage_and_KO()
    {
        // A Life Orb Tricked onto a mon by the opponent: its recoil (and any KO) is the
        // trickster's, since they forced the item on. The holder never dealt it to itself.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garchomp, M|item
            |poke|p2|Rotom, M|item
            |start
            |switch|p1a: Garchomp|Garchomp, M|100/100
            |switch|p2a: Rotom|Rotom, M|100/100
            |turn|1
            |move|p2a: Rotom|Trick|p1a: Garchomp
            |-item|p1a: Garchomp|Life Orb|[from] move: Trick|[of] p2a: Rotom
            |-item|p2a: Rotom|Leftovers|[from] move: Trick|[of] p1a: Garchomp
            |turn|2
            |move|p1a: Garchomp|Earthquake|p2a: Rotom
            |-damage|p2a: Rotom|60/100
            |-damage|p1a: Garchomp|0 fnt|[from] item: Life Orb
            |faint|p1a: Garchomp
            |win|B
            """);
        Assert.Equal(100, run.Of("Garchomp").TakenIndirect, 2); // the tricked Life Orb, credited to Rotom
        Assert.Equal(0, run.Of("Garchomp").TakenSelf, 2);
        Assert.Equal(0, run.Of("Garchomp").SelfKos);
        Assert.Equal(100, run.Of("Rotom").DealtIndirect, 2);
        Assert.Equal(1, run.Of("Rotom").Kills);
    }

    // ── Toxic Spikes poison on a bare switch-in tox (real-log format) ─────────

    [Fact]
    public void Bare_switch_in_tox_with_toxic_spikes_down_is_the_layers_damage_and_KO()
    {
        // The m7 bug: real logs poison a switch-in with a BARE |-status|tox (no [from]),
        // which looks like a Toxic move. With Toxic Spikes down, credit the layer, and
        // the poison-death is the layer's kill, NOT the teammate that last moved.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Glimmora, M|item
            |poke|p1|Ferrothorn, M|item
            |poke|p2|Skarmory, M|item
            |poke|p2|Baxcalibur, M|item
            |start
            |switch|p1a: Glimmora|Glimmora, M|100/100
            |switch|p2a: Skarmory|Skarmory, M|100/100
            |turn|1
            |move|p1a: Glimmora|Toxic Spikes|p2a: Skarmory
            |-sidestart|p2: B|move: Toxic Spikes
            |turn|2
            |move|p1a: Glimmora|Toxic Spikes|p2a: Skarmory
            |-sidestart|p2: B|move: Toxic Spikes
            |turn|3
            |switch|p1a: Ferrothorn|Ferrothorn, M|100/100
            |move|p1a: Ferrothorn|Gyro Ball|p2a: Skarmory
            |-damage|p2a: Skarmory|0 fnt
            |faint|p2a: Skarmory
            |switch|p2a: Baxcalibur|Baxcalibur, M|100/100
            |-status|p2a: Baxcalibur|tox
            |turn|4
            |-damage|p2a: Baxcalibur|0 fnt|[from] psn
            |faint|p2a: Baxcalibur
            |win|A
            """);
        Assert.Equal(100, run.Of("Baxcalibur").TakenIndirect, 2);
        Assert.Equal(100, run.Of("Glimmora").DealtIndirect, 2); // the layer
        Assert.Equal(1, run.Of("Glimmora").Kills);
        Assert.Equal(0, run.Of("Ferrothorn").DealtIndirect, 2); // the teammate that last moved is NOT credited
        Assert.Equal(0, run.Of("Ferrothorn").AlliesKoed);
    }

    [Fact]
    public void A_toxic_move_still_credits_the_mover_not_the_toxic_spikes_layer()
    {
        // Guard the disambiguation: a bare tox on a mon that did NOT just switch in is a
        // Toxic move, credited to the mover, even with Toxic Spikes on the field.
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Glimmora, M|item
            |poke|p1|Grimmsnarl, M|item
            |poke|p2|Blissey, F|item
            |start
            |switch|p1a: Glimmora|Glimmora, M|100/100
            |switch|p2a: Blissey|Blissey, F|100/100
            |turn|1
            |move|p1a: Glimmora|Toxic Spikes|p2a: Blissey
            |-sidestart|p2: B|move: Toxic Spikes
            |turn|2
            |switch|p1a: Grimmsnarl|Grimmsnarl, M|100/100
            |move|p1a: Grimmsnarl|Toxic|p2a: Blissey
            |-status|p2a: Blissey|tox
            |turn|3
            |-damage|p2a: Blissey|94/100|[from] psn
            |win|A
            """);
        Assert.Equal(6, run.Of("Grimmsnarl").DealtIndirect, 2); // the Toxic mover
        Assert.Equal(0, run.Of("Glimmora").DealtIndirect, 2);   // not the T-Spikes layer
        Assert.Equal(6, run.Of("Blissey").TakenIndirect, 2);
    }

    // ── activeTurns must stop when a fainted mon's slot is never refilled ─────

    [Fact]
    public void A_fainted_mon_with_no_replacement_stops_counting_active_turns()
    {
        // Doubles, the away side brings only two mons. When one faints there is no bench
        // to refill its slot, so it must not keep accruing presence for the rest of the
        // game (the m19/m7 overcount).
        var run = ReplayLogRunner.Run("""
            |player|p1|A|1|
            |player|p2|B|2|
            |poke|p1|Garchomp, M|item
            |poke|p1|Gyarados, M|item
            |poke|p2|Snorlax, M|item
            |poke|p2|Munchlax, M|item
            |start
            |switch|p1a: Garchomp|Garchomp, M|100/100
            |switch|p1b: Gyarados|Gyarados, M|100/100
            |switch|p2a: Snorlax|Snorlax, M|100/100
            |switch|p2b: Munchlax|Munchlax, M|100/100
            |turn|1
            |move|p1a: Garchomp|Earthquake|p2a: Snorlax|[spread] p2a,p2b
            |-damage|p2a: Snorlax|0 fnt
            |-damage|p2b: Munchlax|60/100
            |faint|p2a: Snorlax
            |turn|2
            |move|p1a: Garchomp|Earthquake|p2b: Munchlax|[spread] p2b
            |-damage|p2b: Munchlax|30/100
            |turn|3
            |move|p1a: Garchomp|Earthquake|p2b: Munchlax|[spread] p2b
            |-damage|p2b: Munchlax|0 fnt
            |faint|p2b: Munchlax
            |win|A
            """);
        Assert.Equal(1, run.Of("Snorlax").ActiveTurns);   // only turn 1, not 1-3
        Assert.Equal(3, run.Of("Munchlax").ActiveTurns);  // on the field all three turns
        Assert.Equal(3, run.Of("Garchomp").ActiveTurns);
    }
}
